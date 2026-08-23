using System.Collections.Concurrent;
using System.Text.Json;
using RikRealization.ClassicCast.Protocol.Discovery;

namespace RikRealization.ClassicCast.Protocol.Channel;

/// <summary>
/// A live CASTV2 session: virtual connections, the heartbeat the device requires, and
/// request/response correlation by <c>requestId</c>.
/// </summary>
public sealed class CastSession : IAsyncDisposable
{
    public const string NsConnection = "urn:x-cast:com.google.cast.tp.connection";
    public const string NsHeartbeat  = "urn:x-cast:com.google.cast.tp.heartbeat";
    public const string NsReceiver   = "urn:x-cast:com.google.cast.receiver";
    public const string NsWebRtc     = "urn:x-cast:com.google.cast.webrtc";
    public const string NsMedia      = "urn:x-cast:com.google.cast.media";

    /// <summary>
    /// The built-in "Chrome Mirroring" receiver. Because it ships on the device, using it
    /// needs no Cast Developer Console registration and no hosted receiver page — which is
    /// what makes low-latency mirroring to classic pucks possible at all.
    /// </summary>
    public const string MirroringAppId = "0F5096E8";

    /// <summary>
    /// The Default Media Receiver, which ships on every Cast device and plays a URL.
    /// The fallback path when a device will not negotiate mirroring.
    /// </summary>
    public const string DefaultMediaReceiverAppId = "CC1AD845";

    private const string SenderId = "sender-0";
    private const string PlatformReceiverId = "receiver-0";

    private readonly CastChannel _channel;
    private readonly CancellationTokenSource _cts = new();
    private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonElement>> _pending = new();
    private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonElement>> _pendingSeq = new();
    private readonly HashSet<string> _virtualConnections = new(StringComparer.Ordinal);
    private readonly object _connectionLock = new();

    private Task? _readLoop;
    private Task? _heartbeatLoop;
    private int _requestId;
    private int _seqNum;

    private CastSession(CastChannel channel) => _channel = channel;

    /// <summary>Raised for messages that are not responses to one of our requests.</summary>
    public event Action<CastMessage>? MessageReceived;

    /// <summary>Raised when the read loop stops, with the reason if it was an error.</summary>
    public event Action<Exception?>? Closed;

    public static Task<CastSession> OpenAsync(CastDevice device, CancellationToken ct = default) =>
        OpenAsync(device.Address.ToString(), device.Port, ct);

    public static async Task<CastSession> OpenAsync(
        string host, int port = 8009, CancellationToken ct = default)
    {
        var channel = await CastChannel.ConnectAsync(host, port, ct);
        var session = new CastSession(channel);

        session._readLoop = Task.Run(() => session.ReadLoopAsync(session._cts.Token));
        session._heartbeatLoop = Task.Run(() => session.HeartbeatLoopAsync(session._cts.Token));

        await session.ConnectToAsync(PlatformReceiverId, ct);
        return session;
    }

    /// <summary>
    /// Opens a virtual connection to a destination. Required before any other namespace
    /// will be accepted for that destination, and required again for an app's transport
    /// once it has been launched.
    /// </summary>
    public async Task ConnectToAsync(string destinationId, CancellationToken ct = default)
    {
        lock (_connectionLock)
        {
            if (!_virtualConnections.Add(destinationId)) return;
        }

        await SendAsync(destinationId, NsConnection,
            """{"type":"CONNECT","userAgent":"ClassicChromecastCaster","connType":0,"origin":{}}""", ct);
    }

    public Task<ReceiverStatus> GetStatusAsync(CancellationToken ct = default) =>
        RequestReceiverStatusAsync("""{"type":"GET_STATUS","requestId":%ID%}""", ct);

    /// <summary>
    /// Launches a receiver app and returns it once the device reports it running. Also
    /// opens the virtual connection to the app's transport, so the caller can immediately
    /// negotiate on <see cref="NsWebRtc"/>.
    /// </summary>
    public async Task<ReceiverApplication> LaunchAsync(
        string appId, CancellationToken ct = default)
    {
        var status = await RequestReceiverStatusAsync(
            $$"""{"type":"LAUNCH","appId":"{{appId}}","requestId":%ID%}""", ct);

        var app = status.Applications.FirstOrDefault(a =>
            string.Equals(a.AppId, appId, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException(
                $"The device accepted LAUNCH for {appId} but did not report it running.");

        await ConnectToAsync(app.TransportId, ct);
        return app;
    }

    public async Task StopAsync(string sessionId, CancellationToken ct = default) =>
        await SendAsync(PlatformReceiverId, NsReceiver,
            $$"""{"type":"STOP","sessionId":"{{sessionId}}","requestId":{{NextRequestId()}}}""", ct);

    /// <summary>Sends a request carrying a <c>requestId</c> and awaits the matching reply.</summary>
    public async Task<JsonElement> SendRequestAsync(
        string destinationId, string ns, string payloadTemplate, CancellationToken ct = default)
    {
        int id = NextRequestId();
        var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;

        try
        {
            await SendAsync(destinationId, ns,
                payloadTemplate.Replace("%ID%", id.ToString()), ct);

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            await using var _ = timeout.Token.Register(() =>
                tcs.TrySetException(new TimeoutException(
                    $"No reply to request {id} on {ns}."))).ConfigureAwait(false);

            return await tcs.Task;
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    /// <summary>
    /// Sends a Cast Streaming message on the webrtc namespace and awaits its reply.
    /// This half of the protocol correlates on <c>seqNum</c>, not <c>requestId</c> — the
    /// two message families share a channel but not a numbering scheme.
    /// Occurrences of <c>%SEQ%</c> in the template are replaced with the sequence number.
    /// </summary>
    public async Task<JsonElement> SendStreamingRequestAsync(
        string destinationId, string payloadTemplate, CancellationToken ct = default)
    {
        int seq = Interlocked.Increment(ref _seqNum);
        var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingSeq[seq] = tcs;

        try
        {
            await SendAsync(destinationId, NsWebRtc,
                payloadTemplate.Replace("%SEQ%", seq.ToString()), ct);

            // Negotiation makes the receiver allocate a socket and spin up its pipeline,
            // which is slower than a status query. Give it more room than a plain request.
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(20));
            await using var _ = timeout.Token.Register(() =>
                tcs.TrySetException(new TimeoutException(
                    $"No reply to Cast Streaming message seqNum {seq}."))).ConfigureAwait(false);

            return await tcs.Task;
        }
        finally
        {
            _pendingSeq.TryRemove(seq, out _);
        }
    }

    /// <summary>
    /// Tells a media receiver to play a URL. Used only by the HLS fallback; the mirroring
    /// receiver is driven through the webrtc namespace instead.
    /// </summary>
    public async Task LoadMediaAsync(
        ReceiverApplication app, string url, string contentType,
        string title = "Classic Chromecast caster", CancellationToken ct = default)
    {
        await ConnectToAsync(app.TransportId, ct);

        // Built by hand rather than as a raw literal: the nested JSON ends in three closing
        // braces, which an interpolated raw string cannot express unambiguously.
        string media =
            "{\"contentId\":\"" + url + "\"," +
            "\"contentType\":\"" + contentType + "\"," +
            "\"streamType\":\"LIVE\"," +
            "\"metadata\":{\"metadataType\":0,\"title\":\"" + title + "\"}}";

        string payload =
            "{\"type\":\"LOAD\",\"requestId\":%ID%,\"autoplay\":true,\"currentTime\":0," +
            "\"media\":" + media + "}";

        var reply = await SendRequestAsync(app.TransportId, NsMedia, payload, ct);

        if (reply.TryGetProperty("type", out var type) &&
            type.GetString() is "LOAD_FAILED" or "LOAD_CANCELLED" or "INVALID_REQUEST")
        {
            string reason = reply.TryGetProperty("reason", out var r) ? r.GetString() ?? "unknown" : "unknown";
            throw new InvalidOperationException($"The receiver refused to load the stream: {reason}.");
        }
    }

    public Task SendAsync(string destinationId, string ns, string payload, CancellationToken ct = default) =>
        _channel.SendAsync(new CastMessage
        {
            SourceId = SenderId,
            DestinationId = destinationId,
            Namespace = ns,
            PayloadUtf8 = payload,
        }, ct);

    // ---- internals -----------------------------------------------------------------

    private int NextRequestId() => Interlocked.Increment(ref _requestId);

    private async Task<ReceiverStatus> RequestReceiverStatusAsync(
        string payloadTemplate, CancellationToken ct)
    {
        var reply = await SendRequestAsync(PlatformReceiverId, NsReceiver, payloadTemplate, ct);

        if (reply.TryGetProperty("type", out var type) &&
            type.GetString() is "LAUNCH_ERROR" or "INVALID_REQUEST")
        {
            string reason = reply.TryGetProperty("reason", out var r) ? r.GetString() ?? "unknown" : "unknown";
            throw new InvalidOperationException($"Receiver rejected the request: {reason}.");
        }

        return ReceiverStatus.Parse(reply);
    }

    private async Task ReadLoopAsync(CancellationToken ct)
    {
        Exception? failure = null;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var message = await _channel.ReceiveAsync(ct);
                if (message is null) break;

                if (message.Namespace == NsHeartbeat)
                {
                    if (message.PayloadUtf8?.Contains("\"PING\"", StringComparison.Ordinal) == true)
                        await SendAsync(message.SourceId, NsHeartbeat, """{"type":"PONG"}""", ct);
                    continue;
                }

                if (message.PayloadUtf8 is { } json &&
                    TryCompletePending(message.Namespace, json)) continue;

                MessageReceived?.Invoke(message);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { failure = ex; }
        finally
        {
            var reason = failure ?? new IOException("The Cast channel closed.");
            foreach (var pending in _pending.Values) pending.TrySetException(reason);
            foreach (var pending in _pendingSeq.Values) pending.TrySetException(reason);

            Closed?.Invoke(failure);
        }
    }

    private bool TryCompletePending(string ns, string json)
    {
        JsonElement root;
        try { root = JsonDocument.Parse(json).RootElement.Clone(); }
        catch (JsonException) { return false; }

        if (root.ValueKind != JsonValueKind.Object) return false;

        // Cast Streaming replies are numbered independently of receiver requests.
        if (ns == NsWebRtc)
        {
            if (!root.TryGetProperty("seqNum", out var seqElement)) return false;
            if (!seqElement.TryGetInt32(out int seq)) return false;
            return _pendingSeq.TryRemove(seq, out var seqTcs) && seqTcs.TrySetResult(root);
        }

        if (!root.TryGetProperty("requestId", out var idElement)) return false;
        if (!idElement.TryGetInt32(out int id) || id == 0) return false;

        // Broadcast status updates also carry requestId 0, which we deliberately ignore
        // above so they surface as events rather than completing somebody's request.
        return _pending.TryRemove(id, out var tcs) && tcs.TrySetResult(root);
    }

    private async Task HeartbeatLoopAsync(CancellationToken ct)
    {
        // The device drops senders that go quiet. Chrome pings every five seconds.
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
                await SendAsync(PlatformReceiverId, NsHeartbeat, """{"type":"PING"}""", ct);
        }
        catch (OperationCanceledException) { }
        catch (Exception) { /* the read loop reports the real failure */ }
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync();

        try
        {
            if (_readLoop is not null) await _readLoop;
            if (_heartbeatLoop is not null) await _heartbeatLoop;
        }
        catch (OperationCanceledException) { }

        _cts.Dispose();
        await _channel.DisposeAsync();
    }
}
