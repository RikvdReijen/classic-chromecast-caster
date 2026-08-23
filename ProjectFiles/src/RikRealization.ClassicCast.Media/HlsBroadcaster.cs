using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace RikRealization.ClassicCast.Media;

/// <summary>
/// Serves a live HLS stream of a capture source over the local network.
///
/// This is the fallback path. It gives up everything the Cast Streaming route was built
/// for — expect seconds of latency rather than tens of milliseconds — in exchange for
/// working on any Cast device at all, because it plays through the Default Media Receiver
/// that every one of them ships with. Worth having for a device that will not negotiate
/// mirroring, and useless for anything interactive.
///
/// The HTTP server is a hand-rolled TcpListener rather than HttpListener, which on Windows
/// needs an administrator to reserve the URL prefix before it will bind to anything but
/// localhost. Serving four static files does not justify demanding elevation.
/// </summary>
public sealed class HlsBroadcaster : IAsyncDisposable
{
    private readonly string _directory;
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private readonly Process _ffmpeg;

    private Task? _acceptLoop;
    private bool _disposed;

    private HlsBroadcaster(string directory, TcpListener listener, Process ffmpeg, IPAddress address, int port)
    {
        _directory = directory;
        _listener = listener;
        _ffmpeg = ffmpeg;

        PlaylistUrl = $"http://{address}:{port}/live.m3u8";
    }

    public string PlaylistUrl { get; }

    /// <summary>
    /// Starts encoding and serving. <paramref name="localAddress"/> must be the address
    /// the Chromecast can reach us on, which is the one facing it rather than any VPN or
    /// container adapter that happens to sort first.
    /// </summary>
    public static HlsBroadcaster Start(
        string ffmpegPath, IFrameSource source, IPAddress localAddress, MirroringOptions options)
    {
        string directory = Path.Combine(Path.GetTempPath(), "classiccast-hls-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(directory);

        var listener = new TcpListener(localAddress, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;

        var ffmpeg = StartEncoder(ffmpegPath, source, directory, options);

        var broadcaster = new HlsBroadcaster(directory, listener, ffmpeg, localAddress, port);
        broadcaster._acceptLoop = Task.Run(() => broadcaster.AcceptLoopAsync(broadcaster._cts.Token));
        return broadcaster;
    }

    private static Process StartEncoder(
        string ffmpegPath, IFrameSource source, string directory, MirroringOptions options)
    {
        var scale = options.Scale ?? (source.Width, source.Height);

        var arguments = new List<string>
        {
            "-hide_banner", "-loglevel", "error", "-nostdin",
            "-f", "rawvideo", "-pix_fmt", "bgra",
            "-s", $"{source.Width}x{source.Height}",
            "-r", options.FrameRate.ToString(),
            "-i", "-",
            "-an",
        };

        if (scale.Item1 != source.Width || scale.Item2 != source.Height)
            arguments.AddRange(new[] { "-vf", $"scale={scale.Item1}:{scale.Item2}:flags=fast_bilinear" });

        arguments.AddRange(new[]
        {
            "-c:v", "libx264", "-preset", "veryfast", "-tune", "zerolatency",
            "-profile:v", "main", "-pix_fmt", "yuv420p",
            "-b:v", (options.BitRateKbps * 1000).ToString(),
            // One-second segments with a key frame at every boundary. Shorter would cut
            // latency further, but classic receivers handle short segments badly.
            "-g", options.FrameRate.ToString(),
            "-f", "hls",
            "-hls_time", "1",
            "-hls_list_size", "6",
            "-hls_flags", "delete_segments+omit_endlist+independent_segments",
            "-hls_segment_filename", Path.Combine(directory, "seg%05d.ts"),
            Path.Combine(directory, "live.m3u8"),
        });

        var startInfo = new ProcessStartInfo(ffmpegPath)
        {
            RedirectStandardInput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);

        return Process.Start(startInfo)
            ?? throw new InvalidOperationException("Could not start ffmpeg for HLS.");
    }

    /// <summary>Feeds one captured frame into the encoder.</summary>
    public async Task SubmitAsync(CapturedFrame frame, CancellationToken ct = default)
    {
        int rowBytes = frame.Width * 4;
        var stream = _ffmpeg.StandardInput.BaseStream;

        if (frame.Stride == rowBytes)
        {
            await stream.WriteAsync(frame.Pixels.AsMemory(0, rowBytes * frame.Height), ct);
        }
        else
        {
            for (int y = 0; y < frame.Height; y++)
                await stream.WriteAsync(frame.Pixels.AsMemory(y * frame.Stride, rowBytes), ct);
        }

        await stream.FlushAsync(ct);
    }

    /// <summary>True once ffmpeg has written a playlist worth handing to a receiver.</summary>
    public bool IsPlaylistReady =>
        File.Exists(Path.Combine(_directory, "live.m3u8")) &&
        Directory.GetFiles(_directory, "*.ts").Length >= 2;

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await _listener.AcceptTcpClientAsync(ct); }
            catch (OperationCanceledException) { return; }
            catch (SocketException) { continue; }
            catch (ObjectDisposedException) { return; }

            _ = Task.Run(() => ServeAsync(client, ct), ct);
        }
    }

    private async Task ServeAsync(TcpClient client, CancellationToken ct)
    {
        using (client)
        {
            try
            {
                using var stream = client.GetStream();
                var buffer = new byte[2048];
                int read = await stream.ReadAsync(buffer, ct);
                if (read <= 0) return;

                string request = Encoding.ASCII.GetString(buffer, 0, read);
                string[] parts = request.Split(' ');
                if (parts.Length < 2) return;

                // Only the file name is honoured, so a crafted path cannot escape the
                // temporary directory this serves.
                string name = Path.GetFileName(parts[1].TrimStart('/'));
                string path = Path.Combine(_directory, name);

                if (name.Length == 0 || !File.Exists(path))
                {
                    await WriteAsync(stream, "HTTP/1.1 404 Not Found\r\nContent-Length: 0\r\n\r\n", ct);
                    return;
                }

                var content = await File.ReadAllBytesAsync(path, ct);
                string type = name.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase)
                    ? "application/vnd.apple.mpegurl"
                    : "video/mp2t";

                var header =
                    "HTTP/1.1 200 OK\r\n" +
                    $"Content-Type: {type}\r\n" +
                    $"Content-Length: {content.Length}\r\n" +
                    "Access-Control-Allow-Origin: *\r\n" +
                    "Cache-Control: no-cache\r\n" +
                    "Connection: close\r\n\r\n";

                await WriteAsync(stream, header, ct);
                await stream.WriteAsync(content, ct);
            }
            catch (OperationCanceledException) { }
            catch (IOException) { }
            catch (SocketException) { }
        }
    }

    private static Task WriteAsync(NetworkStream stream, string text, CancellationToken ct) =>
        stream.WriteAsync(Encoding.ASCII.GetBytes(text), ct).AsTask();

    /// <summary>
    /// Picks the local address that can actually reach the given device, by asking the
    /// routing table rather than guessing. On a machine with VPN, Docker and Tailscale
    /// adapters, the first address enumerated is rarely the right one.
    /// </summary>
    public static IPAddress LocalAddressFor(IPAddress device)
    {
        using var probe = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        probe.Connect(device, 9);   // no packets are sent by connecting a UDP socket
        return ((IPEndPoint)probe.LocalEndPoint!).Address;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        await _cts.CancelAsync();
        _listener.Stop();

        try { _ffmpeg.StandardInput.Close(); } catch { }
        try { if (!_ffmpeg.WaitForExit(1500)) _ffmpeg.Kill(); } catch { }
        _ffmpeg.Dispose();

        if (_acceptLoop is not null)
        {
            try { await _acceptLoop; } catch (OperationCanceledException) { }
        }

        _cts.Dispose();

        try { Directory.Delete(_directory, recursive: true); } catch { }
    }
}
