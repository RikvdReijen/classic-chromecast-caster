using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading.Channels;
using RikRealization.ClassicCast.Protocol.Media;

namespace RikRealization.ClassicCast.Media;

/// <summary>Raised when the encoder process dies, carrying whatever it printed.</summary>
public sealed class EncoderFailedException : Exception
{
    public EncoderFailedException(string message, IReadOnlyList<string> diagnostics, Exception? inner)
        : base(diagnostics.Count == 0
            ? message
            : message + Environment.NewLine + string.Join(Environment.NewLine, diagnostics), inner)
    {
        Diagnostics = diagnostics;
    }

    public IReadOnlyList<string> Diagnostics { get; }
}

public sealed record EncoderSettings
{
    public required int Width { get; init; }
    public required int Height { get; init; }
    public required int FrameRate { get; init; }
    public int BitRate { get; init; } = 4_000_000;

    /// <summary>
    /// Seconds between key frames. Cast has no way to request an IDR from an ffmpeg
    /// subprocess mid-stream, so this doubles as the recovery interval after a picture
    /// loss — the main reason an in-process encoder is worth building later.
    /// </summary>
    public int KeyFrameIntervalSeconds { get; init; } = 2;

    /// <summary>Encode at a lower resolution than captured. Null keeps capture size.</summary>
    public (int Width, int Height)? ScaleTo { get; init; }

    public int OutputWidth => ScaleTo?.Width ?? Width;
    public int OutputHeight => ScaleTo?.Height ?? Height;
}

/// <summary>
/// H.264 encoding through an ffmpeg subprocess, preferring a hardware encoder.
///
/// This is a real hardware encode — NVENC and QuickSync are the first candidates — but it
/// pays for a process hop and a raw-frame pipe. It exists to get the whole pipeline live
/// and measurable; an in-process Media Foundation or NVENC encoder removes the hop and
/// gains on-demand key frames.
/// </summary>
public sealed class FfmpegH264Encoder : IDisposable
{
    /// <summary>Encoders to try, best first. Availability is only known by trying.</summary>
    private static readonly (string Codec, string[] Arguments)[] Candidates =
    {
        ("h264_nvenc", new[]
        {
            "-preset", "llhp",        // low latency, high performance
            "-zerolatency", "1",
            "-delay", "0",
            "-rc", "cbr",
            "-bf", "0",
        }),
        ("h264_qsv", new[]
        {
            "-preset", "veryfast",
            "-look_ahead", "0",
            // One frame in flight inside the encoder. Anything deeper is latency we
            // cannot get back.
            "-async_depth", "1",
            "-bf", "0",
        }),
        ("libx264", new[]
        {
            "-preset", "ultrafast",
            "-tune", "zerolatency",
            "-bf", "0",
        }),
    };

    private readonly Process _process;
    private readonly Stream _input;
    private readonly H264StreamSplitter _splitter = new();
    private readonly Queue<H264AccessUnit> _ready = new();
    private readonly object _readyLock = new();
    private readonly Task _readerTask;
    private readonly Task _writerTask;
    private readonly CancellationTokenSource _cts = new();
    private readonly List<string> _stderr = new();

    // Capture and the pipe write are independent, so they run concurrently rather than in
    // series. Writing a 1080p frame costs more than capturing one, and making the capture
    // loop wait for it was throwing away most of a frame interval.
    private readonly Channel<byte[]> _pending;
    private readonly ConcurrentBag<byte[]> _bufferPool = new();
    private readonly int _frameByteCount;

    private long _droppedFrames;
    private volatile Exception? _writeFailure;

    private DateTimeOffset _lastOutput = DateTimeOffset.UtcNow;
    private bool _disposed;

    private FfmpegH264Encoder(Process process, string codec, EncoderSettings settings)
    {
        _process = process;
        _input = process.StandardInput.BaseStream;
        Codec = codec;
        Settings = settings;

        _frameByteCount = settings.Width * settings.Height * 4;

        // A short queue on purpose. Buffering more would only convert a slow encoder into
        // latency, and for live mirroring a dropped frame beats a late one.
        _pending = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(3)
        {
            SingleReader = true,
            FullMode = BoundedChannelFullMode.Wait,
        });

        _readerTask = Task.Run(() => ReadOutputAsync(_cts.Token));
        _writerTask = Task.Run(() => WriteInputAsync(_cts.Token));

        process.ErrorDataReceived += (_, e) =>
        {
            if (string.IsNullOrWhiteSpace(e.Data)) return;
            lock (_stderr) { if (_stderr.Count < 40) _stderr.Add(e.Data); }
        };
        process.BeginErrorReadLine();
    }

    public string Codec { get; }
    public EncoderSettings Settings { get; }
    public bool IsHardware => Codec is "h264_nvenc" or "h264_qsv" or "h264_amf";
    public IReadOnlyList<string> Diagnostics { get { lock (_stderr) return _stderr.ToArray(); } }

    /// <summary>
    /// Picks an encoder and starts it.
    ///
    /// Each candidate is proved with a throwaway encode of a few synthetic frames first.
    /// Being listed by <c>ffmpeg -encoders</c> means only that the build has the code, not
    /// that this machine can run it: NVENC in particular fails outright when the installed
    /// driver is newer than the API the ffmpeg build was compiled against, and it does so
    /// late enough that simply watching for an early exit misses it.
    /// </summary>
    public static FfmpegH264Encoder Start(string ffmpegPath, EncoderSettings settings)
    {
        var failures = new List<string>();

        foreach (var (codec, arguments) in Candidates)
        {
            string probeFailure = Probe(ffmpegPath, codec, arguments);
            if (probeFailure.Length > 0)
            {
                failures.Add($"{codec}: {probeFailure}");
                continue;
            }

            var process = TryStart(ffmpegPath, codec, arguments, settings, out string failure);
            if (process is not null) return new FfmpegH264Encoder(process, codec, settings);
            failures.Add($"{codec}: {failure}");
        }

        throw new InvalidOperationException(
            "No usable H.264 encoder. Tried:" + Environment.NewLine +
            string.Join(Environment.NewLine, failures));
    }

    /// <summary>
    /// Encodes a fraction of a second of synthetic video to nowhere. Returns an empty
    /// string when the codec works, or the reason it does not.
    /// </summary>
    private static string Probe(string ffmpegPath, string codec, string[] codecArguments)
    {
        var startInfo = new ProcessStartInfo(ffmpegPath)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        foreach (var argument in new[]
                 {
                     "-hide_banner", "-loglevel", "error", "-nostdin",
                     "-f", "lavfi", "-i", "color=c=black:s=640x360:r=30:d=0.2",
                     "-c:v", codec,
                 })
            startInfo.ArgumentList.Add(argument);

        foreach (var argument in codecArguments) startInfo.ArgumentList.Add(argument);
        foreach (var argument in new[] { "-b:v", "2000000", "-f", "null", "-" })
            startInfo.ArgumentList.Add(argument);

        try
        {
            using var process = Process.Start(startInfo);
            if (process is null) return "process did not start";

            string error = process.StandardError.ReadToEnd();
            if (!process.WaitForExit(10_000))
            {
                try { process.Kill(); } catch { }
                return "probe timed out";
            }

            if (process.ExitCode == 0) return "";

            string firstLine = error
                .ReplaceLineEndings("\n")
                .Split('\n')
                .FirstOrDefault(l => l.Trim().Length > 0)?.Trim()
                ?? $"exit code {process.ExitCode}";

            return firstLine.Length > 120 ? firstLine[..120] : firstLine;
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    private static Process? TryStart(
        string ffmpegPath, string codec, string[] codecArguments,
        EncoderSettings settings, out string failure)
    {
        var arguments = new List<string>
        {
            "-hide_banner", "-loglevel", "error", "-nostdin",
            "-f", "rawvideo",
            "-pix_fmt", "bgra",
            "-s", $"{settings.Width}x{settings.Height}",
            "-r", settings.FrameRate.ToString(),
            "-i", "-",
            "-an",
        };

        // Skip the filter entirely when the sizes match: a no-op swscale pass still costs
        // a full frame copy per frame.
        if (settings.ScaleTo is { } scale &&
            (scale.Width != settings.Width || scale.Height != settings.Height))
            arguments.AddRange(new[] { "-vf", $"scale={scale.Width}:{scale.Height}:flags=fast_bilinear" });

        arguments.AddRange(new[] { "-c:v", codec });
        arguments.AddRange(codecArguments);
        arguments.AddRange(new[]
        {
            "-b:v", settings.BitRate.ToString(),
            "-maxrate", ((int)(settings.BitRate * 1.2)).ToString(),
            "-bufsize", (settings.BitRate / 2).ToString(),
            "-g", (settings.FrameRate * settings.KeyFrameIntervalSeconds).ToString(),
            "-profile:v", "main",
            // QuickSync works in NV12 natively and will convert anyway, announcing it as
            // a warning; asking for it directly skips the round trip.
            "-pix_fmt", codec == "h264_qsv" ? "nv12" : "yuv420p",
            "-f", "h264",
            "-flush_packets", "1",
            "-",
        });

        var startInfo = new ProcessStartInfo(ffmpegPath)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);

        Process? process = null;
        try
        {
            process = Process.Start(startInfo);
            if (process is null)
            {
                failure = "process did not start";
                return null;
            }

            // A codec that this machine cannot actually run dies almost immediately.
            if (process.WaitForExit(700))
            {
                failure = $"exited with code {process.ExitCode}";
                process.Dispose();
                return null;
            }

            failure = "";
            return process;
        }
        catch (Exception ex)
        {
            failure = ex.Message;
            try { process?.Kill(); } catch { }
            process?.Dispose();
            return null;
        }
    }

    /// <summary>Frames discarded because the encoder could not keep up.</summary>
    public long DroppedFrames => Interlocked.Read(ref _droppedFrames);

    /// <summary>
    /// Queues one captured frame. Returns as soon as the pixels have been copied, leaving
    /// the pipe write to happen alongside the next capture. The frame source hands back a
    /// buffer it reuses, so the copy is not optional.
    /// </summary>
    public Task SubmitAsync(CapturedFrame frame, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_writeFailure is { } failure)
            throw new EncoderFailedException(
                "The encoder stopped accepting frames.", Diagnostics, failure);

        var buffer = Rent();
        int rowBytes = Settings.Width * 4;

        if (frame.Stride == rowBytes)
        {
            Buffer.BlockCopy(frame.Pixels, 0, buffer, 0, _frameByteCount);
        }
        else
        {
            for (int y = 0; y < Settings.Height; y++)
                Buffer.BlockCopy(frame.Pixels, y * frame.Stride, buffer, y * rowBytes, rowBytes);
        }

        if (!_pending.Writer.TryWrite(buffer))
        {
            // The encoder is behind. Dropping the frame is the right congestion response:
            // queueing it would show up as latency the viewer cannot get back.
            Return(buffer);
            Interlocked.Increment(ref _droppedFrames);
        }

        return Task.CompletedTask;
    }

    private byte[] Rent() =>
        _bufferPool.TryTake(out var buffer) ? buffer : new byte[_frameByteCount];

    private void Return(byte[] buffer)
    {
        if (_bufferPool.Count < 6) _bufferPool.Add(buffer);
    }

    private async Task WriteInputAsync(CancellationToken ct)
    {
        try
        {
            while (await _pending.Reader.WaitToReadAsync(ct))
            {
                while (_pending.Reader.TryRead(out var buffer))
                {
                    await _input.WriteAsync(buffer.AsMemory(0, _frameByteCount), ct);
                    await _input.FlushAsync(ct);
                    Return(buffer);
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
        catch (Exception ex)
        {
            // ffmpeg closed the pipe, which means it died. Its stderr says why, and that
            // is far more useful to the caller than "the pipe has been ended".
            _writeFailure = ex;
        }
    }

    /// <summary>
    /// Takes any encoded frames produced so far. <paramref name="idleFlush"/> is how long
    /// the encoder must have been silent before a buffered picture is treated as finished,
    /// which avoids waiting for the next frame just to learn this one ended.
    /// </summary>
    public IReadOnlyList<H264AccessUnit> Drain(TimeSpan idleFlush)
    {
        var units = new List<H264AccessUnit>();

        lock (_readyLock)
        {
            while (_ready.Count > 0) units.Add(_ready.Dequeue());

            if (units.Count == 0 &&
                _splitter.PendingBytes > 0 &&
                DateTimeOffset.UtcNow - _lastOutput >= idleFlush &&
                _splitter.FlushPending() is { } pending)
            {
                units.Add(pending);
            }
        }

        return units;
    }

    private async Task ReadOutputAsync(CancellationToken ct)
    {
        var buffer = new byte[1 << 16];
        var stream = _process.StandardOutput.BaseStream;

        while (!ct.IsCancellationRequested)
        {
            int read;
            try { read = await stream.ReadAsync(buffer, ct); }
            catch (OperationCanceledException) { return; }
            catch (IOException) { return; }
            catch (ObjectDisposedException) { return; }

            if (read == 0) return;   // ffmpeg closed its output

            lock (_readyLock)
            {
                foreach (var unit in _splitter.Push(buffer.AsSpan(0, read)))
                    _ready.Enqueue(unit);
                _lastOutput = DateTimeOffset.UtcNow;
            }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _cts.Cancel();
        _pending.Writer.TryComplete();

        try { _writerTask.Wait(500); } catch { }
        try { _input.Close(); } catch { }
        try { if (!_process.WaitForExit(1000)) _process.Kill(); } catch { }
        try { _readerTask.Wait(500); } catch { }

        _cts.Dispose();
        _process.Dispose();
    }
}
