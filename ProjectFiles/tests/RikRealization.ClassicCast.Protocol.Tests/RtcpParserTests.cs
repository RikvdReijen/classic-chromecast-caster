using System.Buffers.Binary;
using RikRealization.ClassicCast.Protocol.Rtp;
using Xunit;

namespace RikRealization.ClassicCast.Protocol.Tests;

/// <summary>
/// The receiver's feedback is the only signal that frames are actually being completed,
/// and its NACK list drives retransmission. Misreading it is the difference between a
/// working stream and one that stalls forever, so the layout is pinned here.
/// </summary>
public class CastRtcpParserTests
{
    private const byte PayloadSpecific = 206;
    private const byte SubtypeFeedback = 15;
    private const byte SubtypePictureLoss = 1;

    private const uint DefaultMediaSsrc = 0x2222_2222;

    /// <summary>Builds a Cast feedback RTCP packet with the given loss fields.</summary>
    private static byte[] Feedback(
        byte checkpoint, ushort playoutDelayMs, params (byte Frame, ushort Packet, byte Bits)[] losses) =>
        Feedback(DefaultMediaSsrc, checkpoint, playoutDelayMs, losses);

    private static byte[] Feedback(
        uint mediaSsrc, byte checkpoint, ushort playoutDelayMs,
        params (byte Frame, ushort Packet, byte Bits)[] losses)
    {
        int payloadSize = 16 + losses.Length * 4;
        var packet = new byte[4 + payloadSize];

        packet[0] = 0b1000_0000 | SubtypeFeedback;
        packet[1] = PayloadSpecific;
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(2), (ushort)(payloadSize / 4));

        BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(4), 0x1111_1111);   // receiver ssrc
        BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(8), mediaSsrc);      // our media ssrc
        "CAST"u8.CopyTo(packet.AsSpan(12));

        packet[16] = checkpoint;
        packet[17] = (byte)losses.Length;
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(18), playoutDelayMs);

        for (int i = 0; i < losses.Length; i++)
        {
            int offset = 20 + i * 4;
            packet[offset] = losses[i].Frame;
            BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(offset + 1), losses[i].Packet);
            packet[offset + 3] = losses[i].Bits;
        }

        return packet;
    }

    [Fact]
    public void Reads_the_checkpoint_and_playout_delay()
    {
        var feedback = CastRtcpParser.Parse(Feedback(checkpoint: 42, playoutDelayMs: 120));

        Assert.Equal((byte)42, feedback.CheckpointFrameId);
        Assert.Equal(TimeSpan.FromMilliseconds(120), feedback.PlayoutDelay);
        Assert.Empty(feedback.Nacks);
        Assert.True(feedback.HasFeedback);
    }

    [Fact]
    public void Reads_a_single_packet_nack()
    {
        var feedback = CastRtcpParser.Parse(
            Feedback(10, 120, (Frame: 11, Packet: 3, Bits: 0)));

        var nack = Assert.Single(feedback.Nacks);
        Assert.Equal(new PacketNack(11, 3), nack);
        Assert.False(nack.IsWholeFrame);
        Assert.Equal(1, feedback.NackedPacketCount);
    }

    [Fact]
    public void Bit_vector_names_further_missing_packets()
    {
        // Bits 0 and 2 set means packets 4 and 6 are also missing, alongside packet 3.
        var feedback = CastRtcpParser.Parse(
            Feedback(10, 120, (Frame: 11, Packet: 3, Bits: 0b0000_0101)));

        Assert.Equal(
            new[] { new PacketNack(11, 3), new PacketNack(11, 4), new PacketNack(11, 6) },
            feedback.Nacks);
    }

    [Fact]
    public void All_packets_lost_is_reported_as_a_whole_frame()
    {
        var feedback = CastRtcpParser.Parse(
            Feedback(10, 120, (Frame: 12, Packet: CastRtcpParser.AllPacketsLost, Bits: 0xFF)));

        var nack = Assert.Single(feedback.Nacks);
        Assert.True(nack.IsWholeFrame);
        Assert.Equal(1, feedback.FullFrameNackCount);
        Assert.Equal(0, feedback.NackedPacketCount);

        // The bit vector is meaningless for a whole-frame NACK and must not be expanded.
        Assert.Single(feedback.Nacks);
    }

    [Fact]
    public void Walks_every_packet_in_a_compound_datagram()
    {
        // A receiver report first, then the Cast feedback. Stopping at the first packet
        // would miss the part that matters.
        var receiverReport = new byte[] { 0x80, 201, 0x00, 0x01, 0, 0, 0, 0 };
        var datagram = receiverReport.Concat(Feedback(77, 200)).ToArray();

        var feedback = CastRtcpParser.Parse(datagram);

        Assert.Equal((byte)77, feedback.CheckpointFrameId);
        Assert.Equal(new byte[] { 201, PayloadSpecific }, feedback.PacketTypes);
    }

    [Fact]
    public void Picture_loss_indicator_is_detected()
    {
        var pli = new byte[] { 0b1000_0000 | SubtypePictureLoss, PayloadSpecific, 0x00, 0x02,
                               0, 0, 0, 0, 0, 0, 0, 0 };

        Assert.True(CastRtcpParser.Parse(pli).PictureLossIndicated);
    }

    [Fact]
    public void Payload_without_the_cast_marker_is_ignored()
    {
        var packet = Feedback(42, 120);
        packet[12] = (byte)'X';                 // corrupt the 'CAST' word

        var feedback = CastRtcpParser.Parse(packet);

        Assert.Null(feedback.CheckpointFrameId);
        Assert.False(feedback.HasFeedback);
    }

    [Fact]
    public void Truncated_and_malformed_datagrams_do_not_throw()
    {
        var packet = Feedback(42, 120, (Frame: 1, Packet: 2, Bits: 0));

        for (int length = 0; length < packet.Length; length++)
        {
            var truncated = packet.AsSpan(0, length).ToArray();
            var exception = Record.Exception(() => CastRtcpParser.Parse(truncated));
            Assert.Null(exception);
        }
    }

    [Fact]
    public void Media_ssrc_identifies_which_stream_the_feedback_is_about()
    {
        // One UDP port carries both audio and video, so routing depends entirely on this.
        var feedback = CastRtcpParser.Parse(Feedback(0xABCD_1234, checkpoint: 9, playoutDelayMs: 120));

        Assert.Equal(0xABCD_1234u, feedback.MediaSsrc);
    }

    [Fact]
    public void Ssrc_above_int_max_is_not_truncated()
    {
        var feedback = CastRtcpParser.Parse(Feedback(uint.MaxValue - 1, 3, 120));

        Assert.Equal(uint.MaxValue - 1, feedback.MediaSsrc);
    }

    [Fact]
    public void Feedback_without_the_cast_marker_reports_no_media_ssrc()
    {
        var packet = Feedback(5, 120);
        packet[12] = (byte)'X';

        Assert.Null(CastRtcpParser.Parse(packet).MediaSsrc);
    }

    [Fact]
    public void Non_rtcp_input_yields_nothing()
    {
        // An RTP packet arriving on the shared port must not parse as feedback.
        var rtp = new byte[] { 0x80, 96, 0x00, 0x01, 0, 0, 0, 0, 0, 0, 0, 0 };

        var feedback = CastRtcpParser.Parse(rtp);
        Assert.Null(feedback.CheckpointFrameId);
    }
}
