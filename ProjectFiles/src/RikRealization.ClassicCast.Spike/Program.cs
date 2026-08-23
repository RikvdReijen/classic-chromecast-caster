using System.Text.Json;
using RikRealization.ClassicCast.Protocol.Channel;
using RikRealization.ClassicCast.Protocol.Discovery;
using RikRealization.ClassicCast.Protocol.Media;
using RikRealization.ClassicCast.Protocol.Rtp;
using RikRealization.ClassicCast.Protocol.Streaming;
using RikRealization.ClassicCast.Media;

// Milestones 1 and 2 of the roadmap:
//   1. find a classic Chromecast, hold a CASTV2 session, launch the mirroring receiver.
//   2. negotiate a Cast Streaming session over OFFER/ANSWER and get a UDP port back.
//   3. encrypt and packetise a test pattern onto that port, and get a picture on screen.
//   4. replace the test pattern with a live capture of the desktop, hardware encoded.

var options = SpikeOptions.Parse(args);
if (options is null) return 2;

Console.OutputEncoding = System.Text.Encoding.UTF8;
Banner();

if (options.PrintOffer)
{
    // Offline: render the OFFER we would send, so the wire format can be eyeballed
    // against Open Screen's reference sample without going near a device.
    var sample = StreamOffer.CreateMirroringOffer(
        options.Resolution, options.TargetDelay,
        includeAudio: !options.NoAudio,
        useAndroidRtpHack: !options.LegacyPayloadTypes);

    using var rendered = JsonDocument.Parse(sample.ToJson("1"));
    Console.WriteLine(JsonSerializer.Serialize(rendered,
        new JsonSerializerOptions { WriteIndented = true }));
    return 0;
}

if (options.HlsSelfTest)
{
    // Exercises the fallback's encoder, HTTP server and playlist without a device, so the
    // half that does not need a Chromecast can still be verified when one is not around.
    var selfTestDisplay = DisplayEnumerator.Enumerate().First();
    using var source = new GdiFrameSource(selfTestDisplay);

    var address = HlsBroadcaster.LocalAddressFor(System.Net.IPAddress.Parse("8.8.8.8"));
    await using var broadcaster = HlsBroadcaster.Start(
        options.FfmpegPath, source, address,
        new MirroringOptions { FrameRate = 15, BitRateKbps = 2000, Scale = (960, 540) });

    Console.WriteLine($"Serving {broadcaster.PlaylistUrl}");
    Console.WriteLine($"Capturing {selfTestDisplay}");

    using var pumpQuit = new CancellationTokenSource(TimeSpan.FromSeconds(12));
    int frames = 0;
    try
    {
        while (!pumpQuit.IsCancellationRequested)
        {
            var frame = source.TryCapture(TimeSpan.FromMilliseconds(50));
            if (frame is not null) { await broadcaster.SubmitAsync(frame, pumpQuit.Token); frames++; }
            await Task.Delay(66, pumpQuit.Token);
        }
    }
    catch (OperationCanceledException) { }

    Console.WriteLine($"Captured {frames} frames; playlist written: {broadcaster.IsPlaylistReady}");

    // Fetching over HTTP is the part that matters: a receiver can only play what the
    // server actually hands out.
    using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
    try
    {
        string playlist = await http.GetStringAsync(broadcaster.PlaylistUrl);
        var lines = playlist.ReplaceLineEndings("\n").Split('\n');
        var segment = lines.FirstOrDefault(l => l.Trim().EndsWith(".ts"));

        Console.WriteLine($"Playlist fetched over HTTP: {lines.Length} lines");

        if (segment is null)
        {
            Console.WriteLine("The playlist named no segments.");
            return 1;
        }

        var baseUri = new Uri(broadcaster.PlaylistUrl);
        var bytes = await http.GetByteArrayAsync(new Uri(baseUri, segment.Trim()));
        Console.WriteLine($"Segment {segment.Trim()} fetched: {bytes.Length / 1024} KiB");

        return bytes.Length > 0 ? 0 : 1;
    }
    catch (Exception ex)
    {
        Console.WriteLine($"HTTP fetch failed: {ex.Message}");
        return 1;
    }
}

if (options.ListDisplays)
{
    foreach (var monitor in DisplayEnumerator.Enumerate())
        Console.WriteLine($"  [{monitor.Index}] {monitor}");
    return 0;
}

using var quit = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; quit.Cancel(); };

Console.WriteLine($"Browsing _googlecast._tcp.local for {options.Timeout.TotalSeconds:0}s ...\n");
var devices = await CastDiscovery.DiscoverAsync(options.Timeout, quit.Token);

if (devices.Count == 0)
{
    Console.WriteLine("No Cast devices found.");
    Console.WriteLine("  - Same subnet as the Chromecast? mDNS does not cross routers.");
    Console.WriteLine("  - A VPN (Tailscale, WireGuard) can swallow multicast. Try disabling it.");
    Console.WriteLine("  - Windows Firewall must allow inbound UDP 5353 for this process.");
    return 1;
}

Console.WriteLine($"Found {devices.Count} device(s):\n");
foreach (var (device, index) in devices.Select((d, i) => (d, i)))
{
    Console.WriteLine($"  [{index}] {device.FriendlyName}");
    Console.WriteLine($"        model    {device.Model}");
    Console.WriteLine($"        address  {device.Address}:{device.Port}");
    Console.WriteLine($"        id       {device.Id}");
    Console.WriteLine($"        video    {(device.HasVideoOutput ? "yes" : "no (audio-only)")}");
    if (device.Txt.TryGetValue("ve", out var ve)) Console.WriteLine($"        proto    {ve}");
    Console.WriteLine();
}

var target = SelectTarget(devices, options.Device);
if (target is null)
{
    Console.WriteLine($"No device matched \"{options.Device}\".");
    return 1;
}

if (options.Hls)
{
    // The fallback path: Default Media Receiver playing HLS over HTTP. Seconds of
    // latency, but it needs nothing from the device beyond what every one of them ships.
    var displays = DisplayEnumerator.Enumerate();
    var captureDisplay = displays.FirstOrDefault(d => d.Index == options.DisplayIndex) ?? displays[0];

    Console.WriteLine();
    Console.WriteLine($"HLS fallback: capturing {captureDisplay}");

    await using var hls = await HlsCastSession.StartAsync(
        target, CaptureTarget.FromDisplay(captureDisplay),
        new MirroringOptions
        {
            FrameRate = options.FrameRate,
            BitRateKbps = options.BitRateKbps,
            FfmpegPath = options.FfmpegPath,
            Scale = options.Scale ?? DefaultEncodeSize(captureDisplay.Width, captureDisplay.Height),
        },
        quit.Token);

    Console.WriteLine($"  serving   {hls.PlaylistUrl}");
    Console.WriteLine("  The receiver is playing it. Ctrl+C to stop.");
    Console.WriteLine();

    var hlsStart = DateTimeOffset.UtcNow;
    try
    {
        while (!quit.IsCancellationRequested)
        {
            await Task.Delay(1000, quit.Token);
            double seconds = (DateTimeOffset.UtcNow - hlsStart).TotalSeconds;
            Console.Write("\r  " + hls.FramesSent + " frames captured " +
                          $"({hls.FramesSent / Math.Max(seconds, 0.001):F1} fps)   ");
        }
    }
    catch (OperationCanceledException) { }

    Console.WriteLine();
    Console.WriteLine();
    Console.WriteLine($"  Sent {hls.FramesSent} frames over HLS.");
    return 0;
}

Console.WriteLine($"Connecting to {target} ...");
await using var session = await CastSession.OpenAsync(target, quit.Token);
session.Closed += ex => Console.WriteLine($"\n[channel closed] {ex?.Message ?? "by peer"}");
if (options.Verbose)
    session.MessageReceived += m => Console.WriteLine($"  <- [{Short(m.Namespace)}] {m.PayloadUtf8}");

Console.WriteLine("Connected. Requesting receiver status ...\n");
var status = await session.GetStatusAsync(quit.Token);
PrintStatus(status);

if (!options.Launch)
{
    Console.WriteLine("\nPass --launch to start the mirroring receiver, or --negotiate to");
    Console.WriteLine("launch it and negotiate a streaming session.");
    return 0;
}

var running = status.Applications.FirstOrDefault(a =>
    a.AppId.Equals(CastSession.MirroringAppId, StringComparison.OrdinalIgnoreCase));

if (running is not null)
{
    Console.WriteLine($"\nMirroring receiver is already running (session {running.SessionId}).");
    Console.WriteLine("Another sender may hold it. Re-launching to take over the session.");
}

Console.WriteLine($"\nLaunching mirroring receiver {CastSession.MirroringAppId} ...\n");
ReceiverApplication app;
try
{
    app = await session.LaunchAsync(CastSession.MirroringAppId, quit.Token);
}
catch (Exception ex)
{
    Console.WriteLine($"Launch failed: {ex.Message}");
    return 1;
}

Console.WriteLine($"  app          {app.DisplayName} ({app.AppId})");
Console.WriteLine($"  sessionId    {app.SessionId}");
Console.WriteLine($"  transportId  {app.TransportId}");
Console.WriteLine($"  statusText   {app.StatusText}");
Console.WriteLine($"  namespaces   {string.Join(", ", app.Namespaces.Select(Short))}");

if (!options.Negotiate)
{
    Console.WriteLine("\nPass --negotiate to send an OFFER on the webrtc namespace.");
    await HoldAsync(session, app, quit.Token);
    return 0;
}

// ---- capture setup, before the offer ------------------------------------------------
// The OFFER has to advertise the resolution we will actually encode, so the capture
// source must exist before we negotiate.

IFrameSource? frameSource = null;
FfmpegH264Encoder? encoder = null;
PipelineTiming? captureTiming = null;

if (options.Mirror)
{
    var displays = DisplayEnumerator.Enumerate();
    if (displays.Count == 0)
    {
        Console.WriteLine("No displays found to capture.");
        return 1;
    }

    var captureTarget = displays.FirstOrDefault(d => d.Index == options.DisplayIndex) ?? displays[0];

    // Desktop Duplication keeps the frame on the GPU and is several times faster, but it
    // is not always available — remote sessions and some drivers refuse it — so GDI stays
    // as the fallback that always works.
    if (options.ForceGdi)
    {
        frameSource = new GdiFrameSource(captureTarget);
    }
    else
    {
        try
        {
            frameSource = new DesktopDuplicationFrameSource(captureTarget);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  Desktop Duplication unavailable: {ex.Message}");
            Console.WriteLine("  Falling back to GDI.");
            frameSource = new GdiFrameSource(captureTarget);
        }
    }

    var scale = options.Scale ?? DefaultEncodeSize(captureTarget.Width, captureTarget.Height);
    options = options with { Resolution = new CastResolution(scale.Width, scale.Height) };

    Console.WriteLine();
    Console.WriteLine($"Capturing {captureTarget}");
    Console.WriteLine($"  method     {frameSource.Description}");
    Console.WriteLine($"  encoding   {scale.Width}x{scale.Height} @ {options.FrameRate} fps, " +
                      $"{options.BitRateKbps} kbps");
}

// ---- milestone 2 --------------------------------------------------------------------

var offer = StreamOffer.CreateMirroringOffer(
    resolution: options.Resolution,
    targetDelay: options.TargetDelay,
    includeAudio: !options.NoAudio,
    useAndroidRtpHack: !options.LegacyPayloadTypes);

Console.WriteLine($"\nSending OFFER on {Short(CastSession.NsWebRtc)}:");
Console.WriteLine($"  castMode       mirroring");
Console.WriteLine($"  targetDelay    {options.TargetDelay.TotalMilliseconds:0} ms " +
                  $"(Open Screen defaults to 400 ms)");
Console.WriteLine($"  payload types  {(options.LegacyPayloadTypes ? "spec" : "Chrome-compatible")}");
foreach (var stream in offer.Streams)
{
    string codec = stream switch
    {
        CastVideoStream v => $"{v.Codec.WireName(),-5} {string.Join(" ", v.Resolutions)}",
        CastAudioStream a => $"{a.Codec.WireName(),-5} {a.Channels}ch {a.BitRate / 1000}kbps",
        _ => "?",
    };
    Console.WriteLine($"  [{stream.Index}] {stream.SourceType,-13} {codec}");
}
Console.WriteLine();

NegotiatedStreamingSession negotiated;
try
{
    negotiated = await CastStreamingNegotiator.NegotiateAsync(
        session, app, target.Address, offer, quit.Token);
}
catch (CastNegotiationException ex)
{
    Console.WriteLine($"Negotiation failed: {ex.Message}");
    Console.WriteLine("\nIf the receiver rejected the payload types, try --legacy-payload-types.");
    return 1;
}
catch (TimeoutException ex)
{
    Console.WriteLine($"Negotiation timed out: {ex.Message}");
    return 1;
}

Console.WriteLine("ANSWER received.\n");
Console.WriteLine($"  RTP destination  {negotiated.Destination}");
Console.WriteLine($"  accepted streams {negotiated.Accepted.Count} of {offer.Streams.Count}");
foreach (var accepted in negotiated.Accepted)
    Console.WriteLine($"    {accepted}");

if (negotiated.Answer.Display is { } display)
{
    Console.WriteLine($"\n  receiver display");
    if (display.Dimensions is { } d) Console.WriteLine($"    dimensions   {d} @ {display.FrameRate ?? "?"}");
    if (display.AspectRatio is not null) Console.WriteLine($"    aspectRatio  {display.AspectRatio}");
    if (display.Scaling is not null) Console.WriteLine($"    scaling      {display.Scaling}");
}

if (negotiated.Answer.Constraints is { } c)
{
    Console.WriteLine($"\n  receiver constraints (these bound the encoder config)");
    if (c.MaxDimensions is { } md) Console.WriteLine($"    maxDimensions      {md} @ {c.MaxFrameRate ?? "?"}");
    if (c.MaxPixelsPerSecond is { } pps) Console.WriteLine($"    maxPixelsPerSecond {pps:N0}");
    if (c.MaxBitRate is { } mb) Console.WriteLine($"    maxBitRate         {mb / 1000:N0} kbps");
    if (c.MaxDelay is { } delay)
    {
        Console.WriteLine($"    maxDelay           {delay.TotalMilliseconds:0} ms");
        if (options.TargetDelay > delay)
            Console.WriteLine($"    !! our targetDelay exceeds the receiver's ceiling");
    }
}

if (!options.SendTestPattern && !options.Mirror)
{
    Console.WriteLine($"""

        Milestone 2 complete. The receiver is listening for RTP on
        {negotiated.Destination}, expecting the streams above.

        Pass --send-test-pattern for a canned pattern, or --mirror for the live desktop.
        """);

    await HoldAsync(session, app, quit.Token);
    return 0;
}

// ---- milestones 3 and 4: pixels on the screen ---------------------------------------

var videoStream = negotiated.Video?.Stream as CastVideoStream;
if (videoStream is null)
{
    Console.WriteLine("The receiver accepted no video stream; nothing to send.");
    return 1;
}

if (videoStream.Codec != CastVideoCodec.H264)
{
    Console.WriteLine($"The receiver chose {videoStream.Codec.WireName()}, but only H.264 is");
    Console.WriteLine("implemented on the sending side.");
    return 1;
}

// Both modes end up producing H.264 access units; only the source differs.
Func<CancellationToken, Task<IReadOnlyList<H264AccessUnit>>> nextFrames;

if (options.Mirror)
{
    var source = frameSource!;
    try
    {
        encoder = FfmpegH264Encoder.Start(options.FfmpegPath, new EncoderSettings
        {
            Width = source.Width,
            Height = source.Height,
            FrameRate = options.FrameRate,
            BitRate = options.BitRateKbps * 1000,
            ScaleTo = (options.Resolution.Width, options.Resolution.Height),
        });
    }
    catch (Exception ex)
    {
        Console.WriteLine($"\nCould not start an encoder: {ex.Message}");
        Console.WriteLine($"Is ffmpeg on the path, or at --ffmpeg <path>?");
        return 1;
    }

    Console.WriteLine($"  encoder    {encoder.Codec} " +
                      $"({(encoder.IsHardware ? "hardware" : "software")})");

    var activeEncoder = encoder;

    // The whole project is a latency argument, so the pipeline measures itself.
    captureTiming = new PipelineTiming();
    var timing = captureTiming;

    // Half a frame interval of silence is enough to conclude the encoder has finished a
    // picture. Waiting for the next one instead would add a whole frame of latency.
    var idleFlush = TimeSpan.FromMilliseconds(500.0 / options.FrameRate);

    nextFrames = async ct =>
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var captured = source.TryCapture(TimeSpan.FromMilliseconds(50));
        double captureMs = clock.Elapsed.TotalMilliseconds;

        clock.Restart();
        if (captured is not null) await activeEncoder.SubmitAsync(captured, ct);
        double submitMs = clock.Elapsed.TotalMilliseconds;

        timing.Record(captureMs, submitMs);
        return activeEncoder.Drain(idleFlush);
    };
}
else
{
    if (!File.Exists(options.TestPatternPath))
    {
        Console.WriteLine($"Test pattern not found: {options.TestPatternPath}");
        Console.WriteLine("Generate it with:  bash tools/make-test-pattern.sh");
        return 1;
    }

    Console.WriteLine($"\nLoading {Path.GetFileName(options.TestPatternPath)} ...");
    var accessUnits = H264AnnexBStream.ParseFile(options.TestPatternPath);
    if (accessUnits.Count == 0)
    {
        Console.WriteLine("No H.264 access units found. Is the file an Annex-B elementary stream?");
        return 1;
    }

    Console.WriteLine($"  {accessUnits.Count} frames, " +
                      $"{accessUnits.Count(u => u.IsKeyFrame)} key frames, " +
                      $"{accessUnits.Sum(u => (long)u.Length) / 1024:N0} KiB");

    int patternIndex = 0;
    nextFrames = _ => Task.FromResult<IReadOnlyList<H264AccessUnit>>(
        new[] { accessUnits[patternIndex++ % accessUnits.Count] });
}

using var videoCrypto = new FrameCrypto(videoStream.AesKey, videoStream.AesIvMask);
await using var transport = new CastStreamingTransport(negotiated.Destination);
var video = transport.AddStream(videoStream.Ssrc, videoStream.RtpPayloadType, videoCrypto);

// ---- milestone 5: system audio -------------------------------------------------------

var audioSpec = negotiated.Audio?.Stream as CastAudioStream;
IAudioSource? audioSource = null;
OpusAudioEncoder? opus = null;
FrameCrypto? audioCrypto = null;
CastRtpStream? audio = null;
Task? audioPump = null;
long audioFramesSent = 0;
long audioCheckpointAdvances = 0;
int audioLastCheckpoint = -1;
string? audioError = null;

if (options.Mirror && audioSpec is not null)
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

        // Track the audio checkpoint separately: the receiver acknowledges each stream
        // on its own, and only its own checkpoint proves audio is being consumed.
        audio.FeedbackReceived += fb =>
        {
            if (fb.CheckpointFrameId is not { } checkpoint) return;
            if (Interlocked.Exchange(ref audioLastCheckpoint, checkpoint) != checkpoint)
                Interlocked.Increment(ref audioCheckpointAdvances);
        };

        Console.WriteLine($"  audio      {audioSource.Description}");
        Console.WriteLine($"  opus       {audioSpec.BitRate / 1000} kbps, " +
                          $"{audioSource.FrameSamples * 1000 / audioSource.SampleRate} ms frames");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"  audio      unavailable: {ex.Message}");
        audioSource?.Dispose();
        audioSource = null;
        opus?.Dispose();
        opus = null;
    }
}
else if (options.Mirror)
{
    Console.WriteLine("  audio      the receiver accepted no audio stream");
}

int feedbackCount = 0, nackedPackets = 0, fullFrameNacks = 0, pictureLossCount = 0;
int checkpointAdvances = 0;
byte? lastCheckpoint = null;
TimeSpan? receiverPlayoutDelay = null;
TimeSpan? lastReportedDelay = null;
var feedbackLock = new object();

var delayController = new PlayoutDelayController(options.TargetDelay);

video.FeedbackReceived += fb =>
{
    Interlocked.Increment(ref feedbackCount);
    delayController.RecordFeedback(fb);
    lock (feedbackLock)
    {
        nackedPackets += fb.NackedPacketCount;
        fullFrameNacks += fb.FullFrameNackCount;
        if (fb.PictureLossIndicated) pictureLossCount++;
        if (fb.PlayoutDelay is { } delay) receiverPlayoutDelay = delay;

        if (fb.CheckpointFrameId is { } checkpoint)
        {
            if (lastCheckpoint != checkpoint)
            {
                checkpointAdvances++;
                delayController.RecordCheckpointAdvanced();
            }
            lastCheckpoint = checkpoint;
        }
    }

    // The receiver echoes the delay it is actually applying. Watching it change is the
    // only way to know an adaptive-latency extension was honoured rather than ignored.
    if (fb.PlayoutDelay is { } reported && reported != lastReportedDelay)
    {
        lastReportedDelay = reported;
        Console.WriteLine();
        Console.WriteLine($"  [receiver now applying {reported.TotalMilliseconds:F0} ms playout delay]");
    }
};
transport.StartReceiving();

Console.WriteLine($"\nStreaming to {transport.Destination} from {transport.LocalEndPoint}");
Console.WriteLine($"  ssrc          {videoStream.Ssrc}");
Console.WriteLine($"  payload type  {videoStream.RtpPayloadType}");
Console.WriteLine($"  encryption    AES-128-CTR, key from the OFFER\n");

// The receiver has no timeline until it gets a Sender Report, so lead with one.
// With two streams these reports are also what ties audio to video: each carries its own
// RTP timestamp against a shared wall clock, and the receiver aligns them from that.
var streamStart = DateTimeOffset.UtcNow;
await video.SendSenderReportAsync(0, streamStart, quit.Token);

if (audio is not null && audioSource is not null && opus is not null)
{
    var audioStream = audio;
    var pcmSource = audioSource;
    var opusEncoder = opus;

    audioPump = Task.Run(async () =>
    {
        uint audioFrameId = 0;
        uint audioRtpTimestamp = 0;
        var audioLastReport = DateTimeOffset.UtcNow;

        // Opus frames are 20 ms, which is also the cadence Cast expects.
        using var audioTimer = new PeriodicTimer(TimeSpan.FromMilliseconds(20));

        try
        {
            await audioStream.SendSenderReportAsync(0, streamStart, quit.Token);

            while (await audioTimer.WaitForNextTickAsync(quit.Token))
            {
                var pcm = pcmSource.ReadFrame();
                var packet = opusEncoder.Encode(pcm);

                await audioStream.SendFrameAsync(new EncodedFrame
                {
                    FrameId = audioFrameId,
                    // Opus packets carry no inter-frame prediction, so every audio frame
                    // stands alone and references itself. A lost one costs 20 ms, not a
                    // stalled stream the way a lost video reference frame would.
                    ReferencedFrameId = audioFrameId,
                    IsKeyFrame = true,
                    RtpTimestamp = audioRtpTimestamp,
                    Data = packet,
                }, quit.Token);

                audioFrameId++;

                // The audio clock counts samples, not wall time. Deriving it from elapsed
                // time instead would let rounding drift against the video clock.
                audioRtpTimestamp += (uint)pcmSource.FrameSamples;
                Interlocked.Increment(ref audioFramesSent);

                var audioNow = DateTimeOffset.UtcNow;
                if (audioNow - audioLastReport >= TimeSpan.FromMilliseconds(250))
                {
                    await audioStream.SendSenderReportAsync(audioRtpTimestamp, audioNow, quit.Token);
                    audioLastReport = audioNow;
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { audioError = ex.Message; }
    });
}

uint frameId = 0;
var lastReport = DateTimeOffset.UtcNow;
int framesSent = 0;
int stalledTicks = 0;
int captureTicks = 0;

// How far ahead of the receiver's checkpoint we allow ourselves to get.
const uint InFlightFrameWindow = 60;

var frameInterval = TimeSpan.FromTicks(TimeSpan.TicksPerSecond / options.FrameRate);
using var frameTimer = new PeriodicTimer(frameInterval);

var lastControlTick = DateTimeOffset.UtcNow;
TimeSpan pendingDelayChange = TimeSpan.Zero;

Console.WriteLine("Sending. Ctrl+C to stop.\n");
try
{
    while (await frameTimer.WaitForNextTickAsync(quit.Token))
    {
        captureTicks++;

        // Never outrun the receiver. Frame ids are 8 bits on the wire, so the window a
        // receiver can reason about is bounded; pushing past its checkpoint just means
        // everything after it is discarded and NACKed forever.
        if (video.LastCheckpointFrameId is { } checkpointByte)
        {
            uint checkpoint = (frameId & 0xFFFFFF00u) | checkpointByte;
            if (checkpoint > frameId) checkpoint -= 256;

            if (frameId - checkpoint >= InFlightFrameWindow)
            {
                stalledTicks++;
                continue;
            }
        }

        IReadOnlyList<H264AccessUnit> units;
        try
        {
            units = await nextFrames(quit.Token);
        }
        catch (OperationCanceledException) { break; }
        catch (EncoderFailedException ex)
        {
            // ffmpeg shares this console, so Ctrl+C reaches it too and it exits a moment
            // before we do. On the way out that is expected, not a fault.
            if (quit.IsCancellationRequested) break;

            Console.WriteLine();
            Console.WriteLine($"  Encoder failed: {ex.Message}");
            break;
        }

        foreach (var unit in units)
        {
            var captureTime = DateTimeOffset.UtcNow;

            // Timestamps come from the clock rather than a frame counter: a live encoder
            // does not produce exactly one picture per tick.
            uint rtpTimestamp = (uint)((captureTime - streamStart).TotalSeconds * 90_000);

            bool isKeyFrame = unit.IsKeyFrame || frameId == 0;

            await video.SendFrameAsync(new EncodedFrame
            {
                FrameId = frameId,
                // A key frame references itself; that is how the receiver knows it can
                // begin decoding here rather than waiting for something earlier.
                ReferencedFrameId = isKeyFrame ? frameId : frameId - 1,
                IsKeyFrame = isKeyFrame,
                RtpTimestamp = rtpTimestamp,
                Data = unit.Data,
                // A pending change rides out on the next frame and is then cleared, so
                // the extension is sent once rather than on every frame.
                NewPlayoutDelay = pendingDelayChange,
            }, quit.Token);

            pendingDelayChange = TimeSpan.Zero;
            frameId++;
            framesSent++;
            delayController.RecordFrameSent();
        }

        var now = DateTimeOffset.UtcNow;
        if (now - lastReport >= TimeSpan.FromMilliseconds(250))
        {
            uint rtpNow = (uint)((now - streamStart).TotalSeconds * 90_000);
            await video.SendSenderReportAsync(rtpNow, now, quit.Token);
            lastReport = now;
        }

        // One control interval per second: often enough to react to a link going bad,
        // rarely enough that one unlucky burst does not move the target.
        if (!options.FixedDelay && now - lastControlTick >= TimeSpan.FromSeconds(1))
        {
            lastControlTick = now;
            if (delayController.Tick() is { } newDelay) pendingDelayChange = newDelay;
        }

        if (captureTicks % options.FrameRate == 0)
        {
            double seconds = (now - streamStart).TotalSeconds;
            double kbps = video.OctetsSent * 8 / 1000.0 / Math.Max(seconds, 0.001);

            string dropped = encoder is null || encoder.DroppedFrames == 0
                ? ""
                : $"{encoder.DroppedFrames} dropped  ";

            Console.Write($"\r  {framesSent,6} sent  {framesSent / Math.Max(seconds, 0.001),5:F1} fps  " +
                          $"{kbps,6:F0} kbps  ack {lastCheckpoint?.ToString() ?? "-",-4} " +
                          $"({checkpointAdvances} adv)  {nackedPackets} nack  " +
                          $"{video.RetransmittedPackets} resent  {dropped}" +
                          $"delay {delayController.Current.TotalMilliseconds:F0}ms   ");
        }
    }
}
catch (OperationCanceledException) { }

double elapsed = (DateTimeOffset.UtcNow - streamStart).TotalSeconds;

Console.WriteLine();
Console.WriteLine();
Console.WriteLine($"  Sent          {framesSent} frames in {elapsed:F1}s " +
                  $"({framesSent / Math.Max(elapsed, 0.001):F1} fps), {video.PacketsSent} packets, " +
                  $"{video.OctetsSent / 1024.0 / 1024.0:F1} MiB payload");
if (captureTiming is not null)
{
    Console.WriteLine($"  Capture       {captureTiming.AverageCaptureMs:F1} ms per frame " +
                      $"(peak {captureTiming.PeakCaptureMs:F1} ms)");
    Console.WriteLine($"  Encode feed   {captureTiming.AverageSubmitMs:F1} ms per frame " +
                      $"(peak {captureTiming.PeakSubmitMs:F1} ms)");
}
if (audio is not null)
{
    long audioFrames = Interlocked.Read(ref audioFramesSent);
    Console.WriteLine($"  Audio         {audioFrames} Opus frames " +
                      $"({audioFrames / Math.Max(elapsed, 0.001):F1}/s, " +
                      $"{audio.OctetsSent * 8 / 1000.0 / Math.Max(elapsed, 0.001):F0} kbps)");

    long audioAdvances = Interlocked.Read(ref audioCheckpointAdvances);
    int audioCheckpoint = Volatile.Read(ref audioLastCheckpoint);
    Console.WriteLine($"                acked frame " +
                      $"{(audioCheckpoint < 0 ? "never" : audioCheckpoint.ToString())}, " +
                      $"after {audioAdvances} advances, " +
                      $"{audio.RetransmittedPackets} packets resent");

    if (audioSource is not null && audioSource.SilentFrames > 0)
        Console.WriteLine($"                {audioSource.SilentFrames} frames were silence " +
                          "(nothing was playing)");

    if (audioError is not null)
        Console.WriteLine($"  Audio error   {audioError}");
}
Console.WriteLine($"  RTCP in       {feedbackCount} datagrams");
Console.WriteLine($"  Checkpoint    frame {lastCheckpoint?.ToString() ?? "never reported"}" +
                  $", after {checkpointAdvances} advances");
Console.WriteLine($"  NACKs         {nackedPackets} packets, {fullFrameNacks} whole frames");
Console.WriteLine($"  Retransmitted {video.RetransmittedPackets} packets");
Console.WriteLine($"  Held back     {stalledTicks} frame slots waiting for the receiver");
Console.WriteLine($"  Picture loss  {pictureLossCount}");
if (receiverPlayoutDelay is { } finalDelay)
    Console.WriteLine($"  Playout delay {finalDelay.TotalMilliseconds:0} ms (the receiver's own figure)");

Console.WriteLine($"  Delay control started at {options.TargetDelay.TotalMilliseconds:0} ms, " +
                  $"ended at {delayController.Current.TotalMilliseconds:0} ms " +
                  $"after {delayController.Adjustments} adjustments" +
                  $"{(options.FixedDelay ? " (adaptation disabled)" : "")}");

if (encoder is not null && encoder.DroppedFrames > 0)
    Console.WriteLine($"  Dropped       {encoder.DroppedFrames} frames the encoder could not keep up with");

if (encoder is not null && encoder.Diagnostics.Count > 0)
{
    Console.WriteLine("\n  encoder said:");
    foreach (var line in encoder.Diagnostics.Take(6)) Console.WriteLine($"    {line}");
}

Console.WriteLine();
if (checkpointAdvances > framesSent / 4)
{
    Console.WriteLine("  The checkpoint advanced repeatedly, so the receiver completed and");
    Console.WriteLine("  accepted frames continuously. That is decoded video on the screen.");
}
else if (feedbackCount > 0)
{
    Console.WriteLine("  The receiver replied but its checkpoint barely moved, so it was not");
    Console.WriteLine("  completing frames. Little or nothing would have appeared.");
}
else
{
    Console.WriteLine("  No RTCP came back, so the receiver never engaged with the stream.");
}

if (audioPump is not null)
{
    try { await audioPump; }
    catch (OperationCanceledException) { }
}

opus?.Dispose();
audioSource?.Dispose();
audioCrypto?.Dispose();
encoder?.Dispose();
frameSource?.Dispose();

Console.WriteLine("\nStopping the receiver app ...");
try { await session.StopAsync(app.SessionId, CancellationToken.None); }
catch (Exception ex) { Console.WriteLine($"  (stop failed: {ex.Message})"); }

return 0;


// ---- helpers -----------------------------------------------------------------------

static async Task HoldAsync(CastSession session, ReceiverApplication app, CancellationToken ct)
{
    Console.WriteLine("\nHolding the session open. Ctrl+C to stop and release the device.\n");
    try { await Task.Delay(Timeout.Infinite, ct); }
    catch (OperationCanceledException) { }

    Console.WriteLine("Stopping the receiver app ...");
    try { await session.StopAsync(app.SessionId, CancellationToken.None); }
    catch (Exception ex) { Console.WriteLine($"  (stop failed: {ex.Message})"); }
}

static void Banner()
{
    Console.WriteLine("Classic Chromecast caster — protocol spike");
    Console.WriteLine("by Rik Realization · http://rikrealization.com/");
    Console.WriteLine(new string('-', 62));
}

static CastDevice? SelectTarget(IReadOnlyList<CastDevice> devices, string? wanted)
{
    if (string.IsNullOrWhiteSpace(wanted)) return devices[0];

    if (int.TryParse(wanted, out int index) && index >= 0 && index < devices.Count)
        return devices[index];

    return devices.FirstOrDefault(d =>
        d.FriendlyName.Contains(wanted, StringComparison.OrdinalIgnoreCase) ||
        d.Address.ToString() == wanted ||
        d.Id == wanted);
}

static void PrintStatus(ReceiverStatus status)
{
    if (status.VolumeLevel is { } level)
        Console.WriteLine($"  volume       {level:P0}{(status.Muted == true ? " (muted)" : "")}");

    if (status.Applications.Count == 0)
    {
        Console.WriteLine("  running app  none (idle)");
        return;
    }

    foreach (var app in status.Applications)
        Console.WriteLine($"  running app  {app}");
}

/// <summary>
/// Picks an encode size for a captured display. Full 4K would blow past what a classic
/// puck can decode and what the link can carry, so anything larger is halved until it
/// fits inside 1080p, which keeps the aspect ratio exact.
/// </summary>
static (int Width, int Height) DefaultEncodeSize(int width, int height)
{
    while (width > 1920 || height > 1080)
    {
        width /= 2;
        height /= 2;
    }

    // H.264 wants even dimensions for 4:2:0 chroma.
    return (width - (width % 2), height - (height % 2));
}

static string Short(string ns) => ns.Replace("urn:x-cast:com.google.cast.", "");

file sealed record SpikeOptions(
    TimeSpan Timeout,
    string? Device,
    bool Launch,
    bool Negotiate,
    TimeSpan TargetDelay,
    CastResolution Resolution,
    bool NoAudio,
    bool LegacyPayloadTypes,
    bool Verbose,
    bool PrintOffer,
    bool SendTestPattern,
    string TestPatternPath,
    int FrameRate,
    bool Mirror,
    int DisplayIndex,
    (int Width, int Height)? Scale,
    int BitRateKbps,
    string FfmpegPath,
    bool ListDisplays,
    bool ForceGdi,
    bool FixedDelay,
    bool Hls,
    bool HlsSelfTest)
{
    public static SpikeOptions? Parse(string[] args)
    {
        var timeout = TimeSpan.FromSeconds(5);
        var targetDelay = TimeSpan.FromMilliseconds(60);
        var resolution = new CastResolution(1920, 1080);
        string? device = null;
        bool launch = false, negotiate = false, noAudio = false, legacy = false, verbose = false;
        bool printOffer = false, sendTestPattern = false;
        string testPatternPath = Path.Combine("assets", "testpattern-1280x720.h264");
        int frameRate = 30;
        bool mirror = false, listDisplays = false;
        int displayIndex = 0, bitRateKbps = 4000;
        (int Width, int Height)? scale = null;
        string ffmpegPath = "ffmpeg";
        bool forceGdi = false, fixedDelay = false, hls = false, hlsSelfTest = false;

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--timeout" when i + 1 < args.Length && double.TryParse(args[++i], out var s):
                    timeout = TimeSpan.FromSeconds(s);
                    break;

                case "--device" when i + 1 < args.Length:
                    device = args[++i];
                    break;

                case "--delay" when i + 1 < args.Length && double.TryParse(args[++i], out var ms):
                    targetDelay = TimeSpan.FromMilliseconds(ms);
                    break;

                case "--resolution" when i + 1 < args.Length:
                {
                    var parts = args[++i].Split('x', 'X');
                    if (parts.Length != 2 ||
                        !int.TryParse(parts[0], out int w) || !int.TryParse(parts[1], out int h))
                    {
                        Console.WriteLine("--resolution expects WIDTHxHEIGHT, e.g. 1920x1080");
                        return null;
                    }
                    resolution = new CastResolution(w, h);
                    break;
                }

                case "--launch": launch = true; break;
                case "--negotiate": launch = true; negotiate = true; break;
                case "--no-audio": noAudio = true; break;
                case "--print-offer": printOffer = true; break;
                case "--list-displays": listDisplays = true; break;
                case "--gdi": forceGdi = true; break;
                case "--fixed-delay": fixedDelay = true; break;
                case "--hls": hls = true; break;
                case "--hls-selftest": hlsSelfTest = true; break;

                case "--mirror":
                    mirror = true;
                    launch = true;
                    negotiate = true;
                    break;

                case "--display" when i + 1 < args.Length && int.TryParse(args[++i], out var index):
                    displayIndex = index;
                    break;

                case "--bitrate" when i + 1 < args.Length && int.TryParse(args[++i], out var kbps):
                    bitRateKbps = kbps;
                    break;

                case "--ffmpeg" when i + 1 < args.Length:
                    ffmpegPath = args[++i];
                    break;

                case "--scale" when i + 1 < args.Length:
                {
                    var parts = args[++i].Split('x', 'X');
                    if (parts.Length != 2 ||
                        !int.TryParse(parts[0], out int sw) || !int.TryParse(parts[1], out int sh))
                    {
                        Console.WriteLine("--scale expects WIDTHxHEIGHT, e.g. 1280x720");
                        return null;
                    }
                    scale = (sw, sh);
                    break;
                }

                case "--send-test-pattern":
                    sendTestPattern = true;
                    launch = true;
                    negotiate = true;
                    // The canned pattern carries no audio; negotiating a stream we never
                    // feed would leave the receiver waiting on it.
                    noAudio = true;
                    // Negotiating an audio stream we never feed invites the receiver to
                    // stall waiting for it, so the pattern goes out video-only.
                    noAudio = true;
                    if (i + 1 < args.Length && !args[i + 1].StartsWith("--"))
                        testPatternPath = args[++i];
                    break;

                case "--fps" when i + 1 < args.Length && int.TryParse(args[++i], out var fps):
                    if (fps is < 1 or > 60)
                    {
                        Console.WriteLine("--fps must be between 1 and 60.");
                        return null;
                    }
                    frameRate = fps;
                    break;
                case "--legacy-payload-types": legacy = true; break;
                case "-v":
                case "--verbose": verbose = true; break;

                case "-h":
                case "--help":
                    Console.WriteLine("""
                        Usage: castspike [options]

                          --timeout <seconds>     How long to browse for devices. Default 5.
                          --device <name|ip|#>    Which device to use. Default: the first.
                          --launch                Launch the built-in mirroring receiver.
                          --negotiate             Launch, then send an OFFER. Implies --launch.
                          --delay <ms>            Starting playout delay. Default 60. The
                                                  controller adapts from there.
                          --fixed-delay           Do not adapt the playout delay.
                          --resolution <WxH>      Top resolution to offer. Default 1920x1080.
                          --no-audio              Offer video only.
                          --print-offer           Print the OFFER JSON and exit. No network.
                          --send-test-pattern [f] Negotiate, then stream an H.264 test
                                                  pattern. Implies --negotiate --no-audio.
                          --fps <n>               Frame rate. Default 30.
                          --mirror                Capture and cast the live desktop.
                                                  Implies --negotiate --no-audio.
                          --list-displays         Show capturable displays and exit.
                          --display <n>           Which display to mirror. Default 0.
                          --scale <WxH>           Encode at this size instead of native.
                          --bitrate <kbps>        Video bitrate. Default 4000.
                          --ffmpeg <path>         ffmpeg executable. Default "ffmpeg".
                          --gdi                   Force GDI capture instead of Desktop
                                                  Duplication, for comparison.
                          --hls                   Use the Default Media Receiver and HLS
                                                  instead of Cast Streaming. Seconds of
                                                  latency; works on any Cast device.
                          --legacy-payload-types  Use spec RTP payload types instead of the
                                                  Chrome-compatible AndroidTV values.
                          -v, --verbose           Dump unsolicited messages.
                          -h, --help              This text.
                        """);
                    return null;

                default:
                    Console.WriteLine($"Unknown argument: {args[i]}  (try --help)");
                    return null;
            }
        }

        return new SpikeOptions(timeout, device, launch, negotiate,
            targetDelay, resolution, noAudio, legacy, verbose, printOffer,
            sendTestPattern, testPatternPath, frameRate,
            mirror, displayIndex, scale, bitRateKbps, ffmpegPath, listDisplays, forceGdi, fixedDelay, hls, hlsSelfTest);
    }
}

/// <summary>Running averages for the capture and encode-feed stages.</summary>
file sealed class PipelineTiming
{
    private readonly object _lock = new();
    private double _captureTotal, _submitTotal;
    private int _samples;

    public double PeakCaptureMs { get; private set; }
    public double PeakSubmitMs { get; private set; }

    public void Record(double captureMs, double submitMs)
    {
        lock (_lock)
        {
            _captureTotal += captureMs;
            _submitTotal += submitMs;
            _samples++;
            if (captureMs > PeakCaptureMs) PeakCaptureMs = captureMs;
            if (submitMs > PeakSubmitMs) PeakSubmitMs = submitMs;
        }
    }

    public double AverageCaptureMs { get { lock (_lock) return _samples == 0 ? 0 : _captureTotal / _samples; } }
    public double AverageSubmitMs { get { lock (_lock) return _samples == 0 ? 0 : _submitTotal / _samples; } }
}
