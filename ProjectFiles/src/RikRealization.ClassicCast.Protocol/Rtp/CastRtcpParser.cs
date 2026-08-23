using System.Buffers.Binary;

namespace RikRealization.ClassicCast.Protocol.Rtp;

/// <summary>
/// A packet the receiver says it never got. <see cref="PacketId"/> of
/// <see cref="CastRtcpParser.AllPacketsLost"/> means the whole frame is missing.
/// </summary>
public readonly record struct PacketNack(byte FrameId, ushort PacketId)
{
    public bool IsWholeFrame => PacketId == CastRtcpParser.AllPacketsLost;

    public override string ToString() =>
        IsWholeFrame ? $"frame {FrameId} (all)" : $"frame {FrameId} packet {PacketId}";
}

/// <summary>
/// What a receiver told us in one compound RTCP datagram.
/// </summary>
public sealed record CastReceiverFeedback
{
    /// <summary>
    /// Low 8 bits of the highest frame the receiver has completely received. Every frame
    /// up to and including this one is done. If this advances, frames are landing.
    /// </summary>
    public byte? CheckpointFrameId { get; init; }

    /// <summary>The delay the receiver is currently applying, which it may adapt.</summary>
    public TimeSpan? PlayoutDelay { get; init; }

    /// <summary>Exactly which packets the receiver wants retransmitted.</summary>
    public IReadOnlyList<PacketNack> Nacks { get; init; } = Array.Empty<PacketNack>();

    /// <summary>Individual packets the receiver is asking us to retransmit.</summary>
    public int NackedPacketCount => Nacks.Count(n => !n.IsWholeFrame);

    /// <summary>Frames the receiver has given up on entirely.</summary>
    public int FullFrameNackCount => Nacks.Count(n => n.IsWholeFrame);

    /// <summary>
    /// The media SSRC this feedback is about. One UDP port carries both audio and video,
    /// so this is what tells them apart.
    /// </summary>
    public uint? MediaSsrc { get; init; }

    /// <summary>The receiver lost its reference picture and needs a fresh key frame.</summary>
    public bool PictureLossIndicated { get; init; }

    /// <summary>RTCP packet types seen in this datagram, for diagnostics.</summary>
    public IReadOnlyList<byte> PacketTypes { get; init; } = Array.Empty<byte>();

    public bool HasFeedback => CheckpointFrameId.HasValue;
}

/// <summary>
/// Walks a compound RTCP datagram and pulls out the Cast-specific feedback.
///
/// Cast packs several RTCP packets into one datagram, so parsing only the first would
/// miss the part that matters: the application-defined feedback carrying the checkpoint
/// frame id and the NACK list.
/// </summary>
public static class CastRtcpParser
{
    private const byte PayloadSpecific = 206;
    private const byte ApplicationDefined = 204;

    private const byte SubtypePictureLossIndicator = 1;
    private const byte SubtypeFeedback = 15;

    /// <summary>The ASCII word 'CAST' that marks a Cast feedback payload.</summary>
    private const uint CastIdentifier = 0x43_41_53_54;

    private const int FeedbackHeaderSize = 16;
    private const int LossFieldSize = 4;

    /// <summary>A packet id of 0xFFFF in a loss field means "the whole frame is missing".</summary>
    public const ushort AllPacketsLost = 0xFFFF;

    public static CastReceiverFeedback Parse(ReadOnlySpan<byte> datagram)
    {
        var types = new List<byte>();
        byte? checkpoint = null;
        TimeSpan? playoutDelay = null;
        var nacks = new List<PacketNack>();
        bool pictureLoss = false;
        uint? mediaSsrc = null;

        int offset = 0;
        while (offset + 4 <= datagram.Length)
        {
            byte firstByte = datagram[offset];
            if ((firstByte & 0b1100_0000) != 0b1000_0000) break;   // not RTCP version 2

            byte packetType = datagram[offset + 1];
            int payloadWords = BinaryPrimitives.ReadUInt16BigEndian(datagram[(offset + 2)..]);
            int packetLength = 4 + payloadWords * 4;

            if (packetLength <= 0 || offset + packetLength > datagram.Length) break;

            types.Add(packetType);

            // For these types the report-count field carries a Cast subtype instead.
            byte subtype = (byte)(firstByte & 0b0001_1111);
            var payload = datagram.Slice(offset + 4, packetLength - 4);

            if (packetType is PayloadSpecific or ApplicationDefined)
            {
                if (subtype == SubtypePictureLossIndicator)
                {
                    pictureLoss = true;
                }
                else if (subtype == SubtypeFeedback &&
                         TryParseFeedback(payload, out byte cp, out var delay, out uint ssrc, nacks))
                {
                    checkpoint = cp;
                    playoutDelay = delay;
                    mediaSsrc = ssrc;
                }
            }

            offset += packetLength;
        }

        return new CastReceiverFeedback
        {
            CheckpointFrameId = checkpoint,
            PlayoutDelay = playoutDelay,
            MediaSsrc = mediaSsrc,
            Nacks = nacks,
            PictureLossIndicated = pictureLoss,
            PacketTypes = types,
        };
    }

    private static bool TryParseFeedback(
        ReadOnlySpan<byte> payload, out byte checkpoint, out TimeSpan playoutDelay,
        out uint mediaSsrc, List<PacketNack> nacks)
    {
        checkpoint = 0;
        playoutDelay = TimeSpan.Zero;
        mediaSsrc = 0;
        if (payload.Length < FeedbackHeaderSize) return false;

        // Layout: receiver SSRC, our media SSRC, then the 'CAST' marker.
        if (BinaryPrimitives.ReadUInt32BigEndian(payload[8..]) != CastIdentifier) return false;

        mediaSsrc = BinaryPrimitives.ReadUInt32BigEndian(payload[4..]);

        checkpoint = payload[12];
        int lossFieldCount = payload[13];
        playoutDelay = TimeSpan.FromMilliseconds(
            BinaryPrimitives.ReadUInt16BigEndian(payload[14..]));

        var lossFields = payload[FeedbackHeaderSize..];
        for (int i = 0; i < lossFieldCount; i++)
        {
            int start = i * LossFieldSize;
            if (start + LossFieldSize > lossFields.Length) break;

            byte frameId = lossFields[start];
            ushort packetId = BinaryPrimitives.ReadUInt16BigEndian(lossFields[(start + 1)..]);
            byte bits = lossFields[start + 3];

            nacks.Add(new PacketNack(frameId, packetId));
            if (packetId == AllPacketsLost) continue;

            // Each set bit names a further missing packet, counting up from packetId.
            for (int bit = 0; bit < 8; bit++)
                if ((bits & (1 << bit)) != 0)
                    nacks.Add(new PacketNack(frameId, (ushort)(packetId + bit + 1)));
        }

        return true;
    }
}
