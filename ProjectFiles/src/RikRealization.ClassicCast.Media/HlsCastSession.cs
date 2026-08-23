using RikRealization.ClassicCast.Protocol.Channel;
using RikRealization.ClassicCast.Protocol.Discovery;

namespace RikRealization.ClassicCast.Media;

/// <summary>
/// Casts through the Default Media Receiver instead of the mirroring one.
///
/// Every Cast device ships with this receiver, so it works where mirroring negotiation
/// fails — but it plays a URL, which means HLS, which means seconds of latency instead of
/// tens of milliseconds. It exists to be the thing that still works, not the thing to use.
/// </summary>
public sealed class HlsCastSession : IAsyncDisposable
{
    private readonly CastSession _session;
    private readonly IFrameSource _frameSource;

    /// <summary>Set once the receiver has accepted the stream, so teardown can stop it.</summary>
    private ReceiverApplication? _app;
    private readonly HlsBroadcaster _broadcaster;
    private readonly CancellationTokenSource _cts = new();

    private Task? _pump;
    private long _framesSent;
    private bool _disposed;

    private HlsCastSession(
        CastSession session, IFrameSource frameSource,
        HlsBroadcaster broadcaster, CaptureTarget target)
    {
        _session = session;
        _frameSource = frameSource;
        _broadcaster = broadcaster;
        Target = target;
    }

    public CaptureTarget Target { get; }
    public string PlaylistUrl => _broadcaster.PlaylistUrl;
    public long FramesSent => Interlocked.Read(ref _framesSent);

    public event Action<string>? Faulted;

    public static async Task<HlsCastSession> StartAsync(
        CastDevice device, CaptureTarget target, MirroringOptions options,
        CancellationToken ct = default)
    {
        if (target.IsAudioOnly)
            throw new NotSupportedException("The HLS fallback carries video; use mirroring for audio only.");

        var session = await CastSession.OpenAsync(device, ct);

        IFrameSource? frameSource = null;
        HlsBroadcaster? broadcaster = null;

        try
        {
            frameSource = target.Window is { } window
                ? new WindowFrameSource(window)
                : new GdiFrameSource(target.Display
                    ?? throw new InvalidOperationException("The target names nothing to capture."));

            // Ask the routing table which of our addresses faces the device: on a machine
            // with VPN, Docker and Tailscale adapters the first one enumerated is rarely
            // the one the Chromecast can reach.
            var localAddress = HlsBroadcaster.LocalAddressFor(device.Address);
            broadcaster = HlsBroadcaster.Start(options.FfmpegPath, frameSource, localAddress, options);

            var hls = new HlsCastSession(session, frameSource, broadcaster, target);

            // Frames have to flow before there is a playlist to hand over, so the pump
            // starts first and the receiver is told about the stream once it exists.
            hls.StartPump();
            await hls.WaitForPlaylistAsync(TimeSpan.FromSeconds(15), ct);

            var app = await session.LaunchAsync(CastSession.DefaultMediaReceiverAppId, ct);
            await session.LoadMediaAsync(
                app, broadcaster.PlaylistUrl, "application/vnd.apple.mpegurl", ct: ct);

            hls._app = app;
            return hls;
        }
        catch
        {
            if (broadcaster is not null) await broadcaster.DisposeAsync();
            frameSource?.Dispose();
            await session.DisposeAsync();
            throw;
        }
    }

    private void StartPump() => _pump = Task.Run(() => PumpAsync(_cts.Token));

    private async Task WaitForPlaylistAsync(TimeSpan timeout, CancellationToken ct)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;

        while (DateTimeOffset.UtcNow < deadline)
        {
            if (_broadcaster.IsPlaylistReady) return;
            await Task.Delay(200, ct);
        }

        throw new TimeoutException("ffmpeg produced no HLS segments to serve.");
    }

    private async Task PumpAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(1000.0 / 30));

        try
        {
            while (await timer.WaitForNextTickAsync(ct))
            {
                var frame = _frameSource.TryCapture(TimeSpan.FromMilliseconds(50));
                if (frame is null) continue;

                await _broadcaster.SubmitAsync(frame, ct);
                Interlocked.Increment(ref _framesSent);
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

        if (_pump is not null)
        {
            try { await _pump; } catch (OperationCanceledException) { }
        }

        if (_app is not null)
        {
            try { await _session.StopAsync(_app.SessionId, CancellationToken.None); } catch { }
        }

        await _broadcaster.DisposeAsync();
        _frameSource.Dispose();
        await _session.DisposeAsync();
        _cts.Dispose();
    }
}
