using System.Diagnostics;
using RikRealization.ClassicCast.Protocol.Channel;
using RikRealization.ClassicCast.Protocol.Discovery;
using RikRealization.ClassicCast.Protocol.Media;
using RikRealization.ClassicCast.Protocol.Rtp;
using RikRealization.ClassicCast.Protocol.Streaming;

namespace RikRealization.ClassicCast.Media;

/// <summary>What to capture: a whole display, one window, or nothing but sound.</summary>
public sealed record CaptureTarget
{
    private CaptureTarget(string label) => Label = label;

    public string Label { get; }
    public DisplayInfo? Display { get; private init; }
    public WindowInfo? Window { get; private init; }
    public bool IsAudioOnly { get; private init; }

    public static CaptureTarget FromDisplay(DisplayInfo display) =>
        new($"Display {display.Index + 1} ({display.Width}x{display.Height})") { Display = display };

    public static CaptureTarget FromWindow(WindowInfo window) =>
        new(window.Title) { Window = window };

    public static CaptureTarget AudioOnly() =>
        new("Audio Only") { IsAudioOnly = true };
}

public sealed record MirroringOptions
{
    public int FrameRate { get; init; } = 30;
    public int BitRateKbps { get; init; } = 4000;
    public TimeSpan TargetDelay { get; init; } = TimeSpan.FromMilliseconds(60);
    public int KeyFrameIntervalSeconds { get; init; } = 2;
    public bool IncludeAudio { get; init; } = true;
    public bool PreferGdiCapture { get; init; }
    public string FfmpegPath { get; init; } = "ffmpeg";
    public (int Width, int Height)? Scale { get; init; }

    /// <summary>
    /// Settings for a source nobody is interacting with, such as a slide deck or a
    /// dashboard. Latency stops mattering and smoothness starts to, so the jitter buffer
    /// gets deep, the frame rate drops, and the saved bitrate goes into per-frame quality
    /// because text on a board wants sharpness rather than temporal resolution.
    /// </summary>
    public static MirroringOptions Signage => new()
    {
        FrameRate = 15,
        TargetDelay = TimeSpan.FromMilliseconds(400),
        BitRateKbps = 3000,
        KeyFrameIntervalSeconds = 4,
    };
}

/// <summary>A snapshot of how the session is doing, for the interface to display.</summary>
public sealed record MirroringStats
{
    public double FramesPerSecond { get; init; }
    public double KilobitsPerSecond { get; init; }
    public TimeSpan PlayoutDelay { get; init; }
    public long FramesSent { get; init; }
    public long AudioFramesSent { get; init; }
    public long RetransmittedPackets { get; init; }
    public long DroppedFrames { get; init; }
    public string CaptureMethod { get; init; } = "";
    public string Encoder { get; init; } = "";
    public bool AudioActive { get; init; }
}

/// <summary>
/// Everything needed to mirror one display to one Chromecast, from opening the control
/// channel to pumping encrypted RTP, behind a start/stop pair.
///
/// The spike drives the same layers directly, with far more instrumentation, because its
/// job is diagnosing the protocol. This exists so the application does not have to know
/// any of that.
/// </summary>
public sealed class MirroringSession : IAsyncDisposable
{
    private readonly CastSession _session;
    private readonly ReceiverApplication _app;
    private readonly CastStreamingTransport _transport;
    private readonly CastRtpStream? _video;
    private readonly FrameCrypto? _videoCrypto;
    private readonly IFrameSource? _frameSource;
    private readonly FfmpegH264Encoder? _encoder;
    private readonly PlayoutDelayController _delayController;
    private readonly CancellationTokenSource _cts = new();

    private readonly CastRtpStream? _audio;
    private readonly FrameCrypto? _audioCrypto;
    private readonly IAudioSource? _audioSource;
    private readonly OpusAudioEncoder? _opus;

    private Task? _videoPump;
    private Task? _audioPump;
    private long _audioFramesSent;
    private bool _disposed;

    private MirroringSession(
        CastSession session, ReceiverApplication app, CastStreamingTransport transport,
        CastRtpStream? video, FrameCrypto? videoCrypto, IFrameSource? frameSource,
        FfmpegH264Encoder? encoder, PlayoutDelayController delayController,
        CastRtpStream? audio, FrameCrypto? audioCrypto,
        IAudioSource? audioSource, OpusAudioEncoder? opus,
        MirroringOptions options, CastDevice device, CaptureTarget target)
    {
        _session = session;
        _app = app;
        _transport = transport;
        _video = video;
        _videoCrypto = videoCrypto;
        _frameSource = frameSource;
        _encoder = encoder;
        _delayController = delayController;
        _audio = audio;
        _audioCrypto = audioCrypto;
        _audioSource = audioSource;
        _opus = opus;

        Options = options;
        Device = device;
        Target = target;
    }

    public MirroringOptions Options { get; }
    public CastDevice Device { get; }
    public CaptureTarget Target { get; }

    /// <summary>Raised when the session ends unexpectedly, with a reason to show.</summary>
    public event Action<string>? Faulted;

    public static async Task<MirroringSession> StartAsync(
        CastDevice device, CaptureTarget target, MirroringOptions options,
        CancellationToken ct = default)
    {
        var session = await CastSession.OpenAsync(device, ct);

        IFrameSource? frameSource = null;
        FfmpegH264Encoder? encoder = null;
        CastStreamingTransport? transport = null;

        try
        {
            bool wantsVideo = !target.IsAudioOnly;

            frameSource = wantsVideo ? CreateFrameSource(target, options.PreferGdiCapture) : null;

            var scale = frameSource is null
                ? (Width: 640, Height: 360)   // never encoded, but the offer needs a size
                : options.Scale ?? FitForCast(frameSource.Width, frameSource.Height);

            var app = await session.LaunchAsync(CastSession.MirroringAppId, ct);

            var offer = StreamOffer.CreateMirroringOffer(
                new CastResolution(scale.Width, scale.Height),
                options.TargetDelay,
                includeAudio: options.IncludeAudio || target.IsAudioOnly,
                includeVideo: wantsVideo);

            var negotiated = await CastStreamingNegotiator.NegotiateAsync(
                session, app, device.Address, offer, ct);

            CastVideoStream? videoSpec = null;
            if (wantsVideo)
            {
                videoSpec = negotiated.Video?.Stream as CastVideoStream
                    ?? throw new CastNegotiationException("The receiver accepted no video stream.");

                if (videoSpec.Codec != CastVideoCodec.H264)
                    throw new CastNegotiationException(
                        $"The receiver chose {videoSpec.Codec.WireName()}, which is not implemented.");

                encoder = FfmpegH264Encoder.Start(options.FfmpegPath, new EncoderSettings
                {
                    Width = frameSource!.Width,
                    Height = frameSource.Height,
                    FrameRate = options.FrameRate,
                    BitRate = options.BitRateKbps * 1000,
                    KeyFrameIntervalSeconds = options.KeyFrameIntervalSeconds,
                    ScaleTo = scale,
                });
            }

            transport = new CastStreamingTransport(negotiated.Destination);

            FrameCrypto? videoCrypto = null;
            CastRtpStream? video = null;
            if (videoSpec is not null)
            {
                videoCrypto = new FrameCrypto(videoSpec.AesKey, videoSpec.AesIvMask);
                video = transport.AddStream(videoSpec.Ssrc, videoSpec.RtpPayloadType, videoCrypto);
            }

            CastRtpStream? audio = null;
            FrameCrypto? audioCrypto = null;
            IAudioSource? audioSource = null;
            OpusAudioEncoder? opus = null;

            if (negotiated.Audio?.Stream is CastAudioStream audioSpec &&
                (options.IncludeAudio || target.IsAudioOnly))
            {
                try
                {
                    audioSource = new WasapiLoopbackAudioSource();
                    audioSource.Start();

                    opus = new OpusAudioEncoder(
                        audioSource.SampleRate, audioSource.Channels,
                        audioSpec.BitRate, audioSource.FrameSamples);

                    audioCrypto = new FrameCrypto(audioSpec.AesKey, audioSpec.AesIvMask);
                    audio = transport.AddStream(audioSpec.Ssrc, audioSpec.RtpPayloadType, audioCrypto);
                }
                catch when (!target.IsAudioOnly)
                {
                    // Alongside video, audio is a bonus and not worth failing the cast for.
                    // In an audio-only session it is the entire point, so there the
                    // exception is left to propagate.
                    opus?.Dispose();
                    audioSource?.Dispose();
                    audioSource = null;
                    opus = null;
                    audio = null;
                }
            }

            var mirroring = new MirroringSession(
                session, app, transport, video, videoCrypto, frameSource, encoder,
                new PlayoutDelayController(options.TargetDelay),
                audio, audioCrypto, audioSource, opus, options, device, target);

            mirroring.Start();
            return mirroring;
        }
        catch
        {
            if (transport is not null) await transport.DisposeAsync();
            encoder?.Dispose();
            frameSource?.Dispose();
            await session.DisposeAsync();
            throw;
        }
    }

    private static IFrameSource CreateFrameSource(CaptureTarget target, bool preferGdi)
    {
        if (target.Window is { } window) return new WindowFrameSource(window);

        var display = target.Display
            ?? throw new InvalidOperationException("The target names nothing to capture.");

        if (preferGdi) return new GdiFrameSource(display);

        try { return new DesktopDuplicationFrameSource(display); }
        catch { return new GdiFrameSource(display); }
    }

    /// <summary>Halves the capture size until it is something a classic puck can decode.</summary>
    private static (int Width, int Height) FitForCast(int width, int height)
    {
        while (width > 1920 || height > 1080) { width /= 2; height /= 2; }
        return (width - (width % 2), height - (height % 2));
    }

    private void Start()
    {
        // Whichever stream exists drives the delay controller; in an audio-only session
        // the audio stream is the only source of feedback there is.
        var primary = _video ?? _audio;
        if (primary is not null)
        {
            primary.FeedbackReceived += feedback =>
            {
                _delayController.RecordFeedback(feedback);
                if (feedback.CheckpointFrameId is not null) _delayController.RecordCheckpointAdvanced();
                if (feedback.PlayoutDelay is { } delay) ReceiverPlayoutDelay = delay;
            };
        }

        _transport.StartReceiving();

        if (_video is not null) _videoPump = Task.Run(() => VideoLoopAsync(_cts.Token));
        if (_audio is not null) _audioPump = Task.Run(() => AudioLoopAsync(_cts.Token));
    }

    public TimeSpan ReceiverPlayoutDelay { get; private set; }

    public MirroringStats Snapshot()
    {
        double seconds = Math.Max(_clock.Elapsed.TotalSeconds, 0.001);

        return new MirroringStats
        {
            FramesPerSecond = FramesSent / seconds,
            KilobitsPerSecond =
                ((_video?.OctetsSent ?? 0) + (_audio?.OctetsSent ?? 0)) * 8 / 1000.0 / seconds,
            PlayoutDelay = ReceiverPlayoutDelay == TimeSpan.Zero
                ? _delayController.Current
                : ReceiverPlayoutDelay,
            FramesSent = FramesSent,
            AudioFramesSent = Interlocked.Read(ref _audioFramesSent),
            RetransmittedPackets = _video?.RetransmittedPackets ?? 0,
            DroppedFrames = _encoder?.DroppedFrames ?? 0,
            CaptureMethod = _frameSource?.Description ?? "audio only",
            Encoder = _encoder is null
                ? "opus"
                : _encoder.Codec + (_encoder.IsHardware ? " (hardware)" : " (software)"),
            AudioActive = _audio is not null,
        };
    }

    public long FramesSent { get; private set; }

    private readonly Stopwatch _clock = Stopwatch.StartNew();

    private async Task VideoLoopAsync(CancellationToken ct)
    {
        var start = DateTimeOffset.UtcNow;
        var idleFlush = TimeSpan.FromMilliseconds(500.0 / Options.FrameRate);
        var interval = TimeSpan.FromTicks(TimeSpan.TicksPerSecond / Options.FrameRate);

        uint frameId = 0;
        var lastReport = start;
        var lastControlTick = start;
        var pendingDelayChange = TimeSpan.Zero;

        using var timer = new PeriodicTimer(interval);

        try
        {
            await _video!.SendSenderReportAsync(0, start, ct);

            while (await timer.WaitForNextTickAsync(ct))
            {
                // Never outrun the receiver: frame ids are 8 bits, so its window is bounded.
                if (_video!.LastCheckpointFrameId is { } checkpointByte)
                {
                    uint checkpoint = (frameId & 0xFFFFFF00u) | checkpointByte;
                    if (checkpoint > frameId) checkpoint -= 256;
                    if (frameId - checkpoint >= 60) continue;
                }

                var captured = _frameSource!.TryCapture(TimeSpan.FromMilliseconds(50));
                if (captured is not null) await _encoder!.SubmitAsync(captured, ct);

                foreach (var unit in _encoder!.Drain(idleFlush))
                {
                    var now = DateTimeOffset.UtcNow;
                    bool isKeyFrame = unit.IsKeyFrame || frameId == 0;

                    await _video!.SendFrameAsync(new EncodedFrame
                    {
                        FrameId = frameId,
                        ReferencedFrameId = isKeyFrame ? frameId : frameId - 1,
                        IsKeyFrame = isKeyFrame,
                        RtpTimestamp = (uint)((now - start).TotalSeconds * 90_000),
                        Data = unit.Data,
                        NewPlayoutDelay = pendingDelayChange,
                    }, ct);

                    pendingDelayChange = TimeSpan.Zero;
                    frameId++;
                    FramesSent++;
                    _delayController.RecordFrameSent();
                }

                var tick = DateTimeOffset.UtcNow;
                if (tick - lastReport >= TimeSpan.FromMilliseconds(250))
                {
                    await _video!.SendSenderReportAsync(
                        (uint)((tick - start).TotalSeconds * 90_000), tick, ct);
                    lastReport = tick;
                }

                if (tick - lastControlTick >= TimeSpan.FromSeconds(1))
                {
                    lastControlTick = tick;
                    if (_delayController.Tick() is { } newDelay) pendingDelayChange = newDelay;
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Faulted?.Invoke(ex.Message); }
    }

    private async Task AudioLoopAsync(CancellationToken ct)
    {
        var start = DateTimeOffset.UtcNow;
        uint frameId = 0;
        uint rtpTimestamp = 0;
        var lastReport = start;

        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(20));

        try
        {
            await _audio!.SendSenderReportAsync(0, start, ct);

            while (await timer.WaitForNextTickAsync(ct))
            {
                var packet = _opus!.Encode(_audioSource!.ReadFrame());

                await _audio.SendFrameAsync(new EncodedFrame
                {
                    FrameId = frameId,
                    // Opus packets are independent, so every frame stands alone.
                    ReferencedFrameId = frameId,
                    IsKeyFrame = true,
                    RtpTimestamp = rtpTimestamp,
                    Data = packet,
                }, ct);

                frameId++;
                rtpTimestamp += (uint)_audioSource.FrameSamples;
                Interlocked.Increment(ref _audioFramesSent);

                var now = DateTimeOffset.UtcNow;
                if (now - lastReport >= TimeSpan.FromMilliseconds(250))
                {
                    await _audio.SendSenderReportAsync(rtpTimestamp, now, ct);
                    lastReport = now;
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Faulted?.Invoke(ex.Message); }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        await _cts.CancelAsync();

        foreach (var pump in new[] { _videoPump, _audioPump })
        {
            if (pump is null) continue;
            try { await pump; } catch (OperationCanceledException) { }
        }

        try { await _session.StopAsync(_app.SessionId, CancellationToken.None); } catch { }

        await _transport.DisposeAsync();

        _opus?.Dispose();
        _audioSource?.Dispose();
        _audioCrypto?.Dispose();
        _encoder?.Dispose();
        _frameSource?.Dispose();
        _videoCrypto?.Dispose();

        await _session.DisposeAsync();
        _cts.Dispose();
    }
}
