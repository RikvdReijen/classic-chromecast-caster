using System.Buffers.Binary;

namespace RikRealization.ClassicCast.Protocol.Rtp;

/// <summary>One encoded frame, ready to be encrypted and packetised.</summary>
public sealed record EncodedFrame
{
    /// <summary>
    /// Monotonic frame counter. Only the low 8 bits go on the wire, but the full value
    /// keys the encryption, so it must never wrap in practice or restart mid-session.
    /// </summary>
    public required uint FrameId { get; init; }

    /// <summary>
    /// The frame this one decodes against. Key frames reference themselves, which is how
    /// a receiver recognises it can start decoding here.
    /// </summary>
    public required uint ReferencedFrameId { get; init; }

    public required bool IsKeyFrame { get; init; }

    /// <summary>Presentation time in the stream's timebase — 90 kHz for video.</summary>
    public required uint RtpTimestamp { get; init; }

    /// <summary>The encoded bitstream. For H.264 this is Annex-B with start codes.</summary>
    public required ReadOnlyMemory<byte> Data { get; init; }

    /// <summary>
    /// A new receiver playout delay to take effect from this frame, or zero to leave it
    /// alone. Carried as an RTP header extension on the frame's first packet, which is the
    /// only way to change the delay without renegotiating the whole session.
    /// </summary>
    public TimeSpan NewPlayoutDelay { get; init; } = TimeSpan.Zero;
}

/// <summary>
/// Builds Cast RTP packets. The layout is a standard 12-byte RTP header followed by a
/// Cast-specific header, per Open Screen's <c>rtp_defines.h</c>:
/// <code>
///  byte 0      V=2, no padding, no extension, no CSRCs      (0x80)
///  byte 1      marker bit (last packet of frame) | payload type (7 bits)
///  bytes 2-3   sequence number
///  bytes 4-7   RTP timestamp
///  bytes 8-11  sender SSRC
///  byte 12     key-frame bit | has-reference-frame-id bit | extension count (6 bits)
///  byte 13     frame id, low 8 bits
///  bytes 14-15 packet id within the frame
///  bytes 16-17 highest packet id in the frame
///  byte 18     referenced frame id, low 8 bits
///  bytes 19-22 optional adaptive-latency extension, first packet of a frame only
/// </code>
/// </summary>
public sealed class CastRtpPacketizer
{
    /// <summary>Ethernet MTU less the IPv4 and UDP headers.</summary>
    public const int MaxPacketSizeIpv4 = 1500 - 20 - 8;

    /// <summary>
    /// Header size with the reference frame id always present. Open Screen's packetizer
    /// sets that bit unconditionally, so we match it rather than saving one byte.
    /// </summary>
    public const int BaseHeaderSize = 19;

    private const byte RequiredFirstByte = 0b1000_0000;
    private const byte MarkerBitMask = 0b1000_0000;
    private const byte PayloadTypeMask = 0b0111_1111;
    private const byte KeyFrameBitMask = 0b1000_0000;
    private const byte HasReferenceFrameIdBitMask = 0b0100_0000;

    /// <summary>Extension type 1, carrying a two-byte playout delay in milliseconds.</summary>
    private const int AdaptiveLatencyExtensionType = 1;
    private const int ExtensionDataSizeFieldBits = 10;
    public const int AdaptiveLatencyHeaderSize = 4;

    private readonly byte _payloadType;
    private readonly uint _senderSsrc;
    private readonly int _maxPacketSize;

    private ushort _sequenceNumber;

    public CastRtpPacketizer(int payloadType, uint senderSsrc, int maxPacketSize = MaxPacketSizeIpv4)
    {
        if (payloadType is < 0 or > 127)
            throw new ArgumentOutOfRangeException(nameof(payloadType), "RTP payload type is 7 bits.");
        if (maxPacketSize <= BaseHeaderSize)
            throw new ArgumentOutOfRangeException(nameof(maxPacketSize));

        _payloadType = (byte)(payloadType & PayloadTypeMask);
        _senderSsrc = senderSsrc;
        _maxPacketSize = maxPacketSize;
    }

    /// <summary>
    /// Payload room per packet. The adaptive-latency extension is always allowed for, so
    /// a frame that carries one cannot overrun the MTU on its first packet. Four bytes per
    /// packet is a cheap price for never having to special-case the size.
    /// </summary>
    public int MaxPayloadSize => _maxPacketSize - BaseHeaderSize - AdaptiveLatencyHeaderSize;

    /// <summary>Sequence number the next packet will carry. Exposed for diagnostics.</summary>
    public ushort NextSequenceNumber => _sequenceNumber;

    /// <summary>
    /// How many packets a payload of this size splits into. Always at least one, so a
    /// zero-length frame still produces a packet the receiver can account for.
    /// </summary>
    public int ComputePacketCount(int payloadLength) =>
        payloadLength <= 0 ? 1 : (payloadLength + MaxPayloadSize - 1) / MaxPayloadSize;

    /// <summary>Builds one packet of an already-encrypted frame.</summary>
    public byte[] GeneratePacket(
        EncodedFrame frame, ReadOnlySpan<byte> encryptedPayload, int packetId, int packetCount)
    {
        if (packetId < 0 || packetId >= packetCount)
            throw new ArgumentOutOfRangeException(nameof(packetId));
        if (packetCount > ushort.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(packetCount), "Frame needs too many packets.");

        bool isLastPacket = packetId == packetCount - 1;

        // A delay change rides on the first packet only; repeating it would be redundant
        // and the receiver acts on the first one it sees.
        bool includeLatencyChange = packetId == 0 && frame.NewPlayoutDelay > TimeSpan.Zero;
        int headerSize = BaseHeaderSize + (includeLatencyChange ? AdaptiveLatencyHeaderSize : 0);

        int chunkStart = MaxPayloadSize * packetId;
        int chunkLength = isLastPacket
            ? Math.Max(0, encryptedPayload.Length - chunkStart)
            : MaxPayloadSize;

        var packet = new byte[headerSize + chunkLength];
        var buffer = packet.AsSpan();

        // ---- RTP header ----
        buffer[0] = RequiredFirstByte;
        buffer[1] = (byte)((isLastPacket ? MarkerBitMask : 0) | _payloadType);
        BinaryPrimitives.WriteUInt16BigEndian(buffer[2..], _sequenceNumber++);
        BinaryPrimitives.WriteUInt32BigEndian(buffer[4..], frame.RtpTimestamp);
        BinaryPrimitives.WriteUInt32BigEndian(buffer[8..], _senderSsrc);

        // ---- Cast header ----
        // The low 6 bits are an extension count, which is 1 exactly when this packet
        // carries a playout delay change and 0 otherwise.
        buffer[12] = (byte)((frame.IsKeyFrame ? KeyFrameBitMask : 0)
                            | HasReferenceFrameIdBitMask
                            | (includeLatencyChange ? 1 : 0));   // low 6 bits: extension count
        buffer[13] = (byte)(frame.FrameId & 0xFF);
        BinaryPrimitives.WriteUInt16BigEndian(buffer[14..], (ushort)packetId);
        BinaryPrimitives.WriteUInt16BigEndian(buffer[16..], (ushort)(packetCount - 1));
        buffer[18] = (byte)(frame.ReferencedFrameId & 0xFF);

        if (includeLatencyChange)
        {
            int milliseconds = (int)frame.NewPlayoutDelay.TotalMilliseconds;
            if (milliseconds > ushort.MaxValue) milliseconds = ushort.MaxValue;

            // Type in the upper 6 bits, payload size in the lower 10.
            BinaryPrimitives.WriteUInt16BigEndian(buffer[19..],
                (ushort)((AdaptiveLatencyExtensionType << ExtensionDataSizeFieldBits) | sizeof(ushort)));
            BinaryPrimitives.WriteUInt16BigEndian(buffer[21..], (ushort)milliseconds);
        }

        if (chunkLength > 0)
            encryptedPayload.Slice(chunkStart, chunkLength).CopyTo(buffer[headerSize..]);

        return packet;
    }
}
