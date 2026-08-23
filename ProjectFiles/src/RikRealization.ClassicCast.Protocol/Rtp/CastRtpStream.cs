namespace RikRealization.ClassicCast.Protocol.Rtp;

/// <summary>
/// One media stream inside a Cast Streaming session — audio or video.
///
/// Owns its own frame numbering, encryption, retransmission buffer and Sender Reports.
/// The two streams of a session are independent in every way except the socket they
/// share, which the transport owns.
/// </summary>
public sealed class CastRtpStream
{
    private readonly CastStreamingTransport _transport;
    private readonly CastRtpPacketizer _packetizer;
    private readonly FrameCrypto _crypto;

    /// <summary>
    /// Packets of recently sent frames, indexed by the low 8 bits of the frame id — the
    /// same width the wire and the NACKs use, so the buffer naturally holds exactly the
    /// range the receiver can still ask about.
    /// </summary>
    private readonly byte[][]?[] _sentFrames = new byte[][]?[256];

    private long _packetsSent;
    private long _octetsSent;
    private long _retransmittedPackets;
    private int _lastCheckpoint = -1;

    internal CastRtpStream(
        CastStreamingTransport transport, uint ssrc, int payloadType, FrameCrypto crypto)
    {
        _transport = transport;
        _crypto = crypto;
        _packetizer = new CastRtpPacketizer(payloadType, ssrc);

        Ssrc = ssrc;
        PayloadType = payloadType;
    }

    public uint Ssrc { get; }
    public int PayloadType { get; }

    public long PacketsSent => Interlocked.Read(ref _packetsSent);
    public long OctetsSent => Interlocked.Read(ref _octetsSent);
    public long RetransmittedPackets => Interlocked.Read(ref _retransmittedPackets);

    /// <summary>
    /// Low 8 bits of the last checkpoint the receiver reported for this stream, or null
    /// before any feedback. Callers use it to avoid outrunning the receiver.
    /// </summary>
    public byte? LastCheckpointFrameId
    {
        get
        {
            int value = Volatile.Read(ref _lastCheckpoint);
            return value < 0 ? null : (byte)value;
        }
    }

    /// <summary>
    /// Resend packets the receiver reports missing. Without this a single lost packet
    /// stalls the stream permanently: the receiver cannot complete the frame, every later
    /// frame references it, and it NACKs forever.
    /// </summary>
    public bool AutoRetransmit { get; set; } = true;

    public event Action<CastReceiverFeedback>? FeedbackReceived;

    /// <summary>Encrypts, packetises and transmits one frame.</summary>
    public async Task SendFrameAsync(EncodedFrame frame, CancellationToken ct = default)
    {
        var encrypted = _crypto.Encrypt(frame.FrameId, frame.Data.Span);
        int packetCount = _packetizer.ComputePacketCount(encrypted.Length);

        var packets = new byte[packetCount][];
        for (int packetId = 0; packetId < packetCount; packetId++)
            packets[packetId] = _packetizer.GeneratePacket(frame, encrypted, packetId, packetCount);

        // Publish before sending: a NACK can arrive while we are still transmitting the
        // burst, and it must find the packets already retained.
        _sentFrames[frame.FrameId & 0xFF] = packets;

        await _transport.SendBatchAsync(packets, length =>
        {
            Interlocked.Increment(ref _packetsSent);
            Interlocked.Add(ref _octetsSent, length - CastRtpPacketizer.BaseHeaderSize);
        }, ct);
    }

    /// <summary>
    /// Sends an RTCP Sender Report for this stream. The receiver cannot schedule playout
    /// without one, and with two streams these reports are also what ties audio and video
    /// to a common wall clock — they are the whole basis of A/V sync.
    /// </summary>
    public async Task SendSenderReportAsync(
        uint rtpTimestamp, DateTimeOffset referenceTime, CancellationToken ct = default)
    {
        var report = RtcpSenderReport.Build(
            Ssrc, referenceTime, rtpTimestamp, (uint)PacketsSent, (uint)OctetsSent);

        await _transport.SendAsync(report, ct);
    }

    /// <summary>Resends the packets named in a NACK list, where we still hold them.</summary>
    public async Task RetransmitAsync(
        IEnumerable<PacketNack> nacks, CancellationToken ct = default)
    {
        var toSend = new List<byte[]>();

        foreach (var nack in nacks)
        {
            var packets = _sentFrames[nack.FrameId];
            if (packets is null) continue;      // already aged out of the buffer

            if (nack.IsWholeFrame) toSend.AddRange(packets);
            else if (nack.PacketId < packets.Length) toSend.Add(packets[nack.PacketId]);
        }

        if (toSend.Count == 0) return;

        await _transport.SendBatchAsync(
            toSend, _ => Interlocked.Increment(ref _retransmittedPackets), ct);
    }

    internal async Task HandleFeedbackAsync(CastReceiverFeedback feedback, CancellationToken ct)
    {
        if (feedback.CheckpointFrameId is { } checkpoint)
            Volatile.Write(ref _lastCheckpoint, checkpoint);

        FeedbackReceived?.Invoke(feedback);

        if (AutoRetransmit && feedback.Nacks.Count > 0)
        {
            try { await RetransmitAsync(feedback.Nacks, ct); }
            catch (OperationCanceledException) { }
            catch (System.Net.Sockets.SocketException) { /* the next NACK will ask again */ }
        }
    }
}
