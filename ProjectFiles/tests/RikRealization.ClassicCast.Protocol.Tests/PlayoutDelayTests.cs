using System.Buffers.Binary;
using RikRealization.ClassicCast.Protocol.Rtp;
using Xunit;

namespace RikRealization.ClassicCast.Protocol.Tests;

/// <summary>
/// The playout delay is a literal addition to end-to-end latency, so the controller is
/// what decides how good the product feels. It must back off fast enough to avoid stutter
/// and recover slowly enough not to oscillate.
/// </summary>
public class PlayoutDelayControllerTests
{
    private static PlayoutDelayController Controller(double initialMs = 100) =>
        new(TimeSpan.FromMilliseconds(initialMs),
            minimum: TimeSpan.FromMilliseconds(40),
            maximum: TimeSpan.FromMilliseconds(400),
            increaseStep: TimeSpan.FromMilliseconds(40),
            decreaseStep: TimeSpan.FromMilliseconds(10));

    private static CastReceiverFeedback Loss(int nackedPackets, int wholeFrames = 0, bool pli = false)
    {
        var nacks = new List<PacketNack>();
        for (int i = 0; i < nackedPackets; i++) nacks.Add(new PacketNack(1, (ushort)i));
        for (int i = 0; i < wholeFrames; i++) nacks.Add(new PacketNack(2, CastRtcpParser.AllPacketsLost));

        return new CastReceiverFeedback
        {
            CheckpointFrameId = 1,
            Nacks = nacks,
            PictureLossIndicated = pli,
        };
    }

    /// <summary>
    /// Runs one bad interval. Backing off needs two in a row, so most tests want this
    /// called twice.
    /// </summary>
    private static TimeSpan? BadInterval(PlayoutDelayController controller, CastReceiverFeedback loss)
    {
        SendFrames(controller, 30);
        controller.RecordFeedback(loss);
        return controller.Tick();
    }

    /// <summary>A healthy interval: frames sent and the receiver acknowledging them.</summary>
    private static void SendFrames(PlayoutDelayController controller, int count)
    {
        for (int i = 0; i < count; i++)
        {
            controller.RecordFrameSent();
            controller.RecordCheckpointAdvanced();
        }
    }

    [Fact]
    public void A_clean_interval_does_not_change_anything_immediately()
    {
        var controller = Controller();
        SendFrames(controller, 30);

        Assert.Null(controller.Tick());
        Assert.Equal(TimeSpan.FromMilliseconds(100), controller.Current);
    }

    [Fact]
    public void Ordinary_link_noise_does_not_raise_the_delay()
    {
        // Measured from a real, healthy Wi-Fi stream: roughly 9% of frames lose a packet
        // and 1.5% need a full resend, and retransmission repairs all of it. An earlier
        // threshold treated this as congestion and drove the delay to its ceiling on a
        // stream that was working perfectly.
        var controller = Controller();

        for (int interval = 0; interval < 3; interval++)
        {
            SendFrames(controller, 30);
            controller.RecordFeedback(Loss(nackedPackets: 3, wholeFrames: 0));
            controller.Tick();
        }

        Assert.Equal(TimeSpan.FromMilliseconds(100), controller.Current);
        Assert.Equal(0, controller.Adjustments);
    }

    [Fact]
    public void A_stalled_checkpoint_raises_the_delay()
    {
        // The receiver is not completing frames at all, which is the unambiguous signal.
        var controller = Controller();

        // Frames go out but nothing is ever acknowledged, in two successive intervals.
        for (int i = 0; i < 30; i++) controller.RecordFrameSent();
        Assert.Null(controller.Tick());

        for (int i = 0; i < 30; i++) controller.RecordFrameSent();
        Assert.Equal(TimeSpan.FromMilliseconds(140), controller.Tick());
    }

    [Fact]
    public void One_bad_interval_is_not_enough_to_back_off()
    {
        // A single burst of interference is transient. Because a raise costs four times
        // what a recovery earns back, reacting to isolated bad intervals ratchets the
        // delay upward on a link that is basically fine - which is what it did in
        // practice before this rule existed.
        var controller = Controller();

        Assert.Null(BadInterval(controller, Loss(20)));
        Assert.Equal(TimeSpan.FromMilliseconds(100), controller.Current);
    }

    [Fact]
    public void A_good_interval_clears_the_bad_streak()
    {
        var controller = Controller();

        BadInterval(controller, Loss(20));
        SendFrames(controller, 30);
        controller.Tick();

        Assert.Null(BadInterval(controller, Loss(20)));
        Assert.Equal(TimeSpan.FromMilliseconds(100), controller.Current);
    }

    [Fact]
    public void Sustained_loss_raises_the_delay()
    {
        var controller = Controller();

        BadInterval(controller, Loss(nackedPackets: 20));    // far past ordinary noise
        var raised = BadInterval(controller, Loss(nackedPackets: 20));

        Assert.Equal(TimeSpan.FromMilliseconds(140), raised);
        Assert.Equal(1, controller.Adjustments);
    }

    [Fact]
    public void Picture_loss_raises_the_delay_on_its_own()
    {
        // Losing the reference picture is unambiguous evidence the link is not coping.
        var controller = Controller();
        BadInterval(controller, Loss(nackedPackets: 0, pli: true));

        Assert.Equal(TimeSpan.FromMilliseconds(140),
            BadInterval(controller, Loss(nackedPackets: 0, pli: true)));
    }

    [Fact]
    public void Whole_frame_losses_weigh_more_than_single_packets()
    {
        // One abandoned frame out of 100 is under a naive 2% packet threshold, but it is
        // a far worse symptom than a couple of late packets.
        var controller = Controller();
        BadInterval(controller, Loss(nackedPackets: 0, wholeFrames: 10));

        Assert.NotNull(BadInterval(controller, Loss(nackedPackets: 0, wholeFrames: 10)));
    }

    [Fact]
    public void Recovery_needs_several_clean_intervals()
    {
        var controller = Controller();

        // Three clean intervals are not enough.
        for (int i = 0; i < 3; i++)
        {
            SendFrames(controller, 30);
            Assert.Null(controller.Tick());
        }

        SendFrames(controller, 30);
        Assert.Equal(TimeSpan.FromMilliseconds(90), controller.Tick());
    }

    [Fact]
    public void Backing_off_is_faster_than_recovering()
    {
        // Stutter is instantly visible; a little extra latency is not. One bad interval
        // must cost more than one good one earns back.
        var controller = Controller();

        BadInterval(controller, Loss(20));
        var afterLoss = BadInterval(controller, Loss(20))!.Value;

        for (int i = 0; i < 4; i++)
        {
            SendFrames(controller, 30);
            controller.Tick();
        }

        Assert.True(afterLoss - controller.Current < TimeSpan.FromMilliseconds(40),
            "four clean intervals should not undo one bad one");
    }

    [Fact]
    public void Loss_resets_the_recovery_streak()
    {
        var controller = Controller();

        for (int i = 0; i < 3; i++)
        {
            SendFrames(controller, 30);
            controller.Tick();
        }

        // Bad intervals here must restart the count, not let the next one recover.
        BadInterval(controller, Loss(20));
        BadInterval(controller, Loss(20));

        SendFrames(controller, 30);
        Assert.Null(controller.Tick());
    }

    [Fact]
    public void The_delay_stays_within_its_bounds()
    {
        var controller = Controller(initialMs: 380);

        for (int i = 0; i < 20; i++) BadInterval(controller, Loss(30));

        Assert.Equal(TimeSpan.FromMilliseconds(400), controller.Current);

        var low = Controller(initialMs: 45);
        for (int i = 0; i < 40; i++)
        {
            SendFrames(low, 30);
            low.Tick();
        }

        Assert.Equal(TimeSpan.FromMilliseconds(40), low.Current);
    }

    [Fact]
    public void No_change_at_a_bound_is_not_counted_as_an_adjustment()
    {
        var controller = Controller(initialMs: 400);

        BadInterval(controller, Loss(30));
        BadInterval(controller, Loss(30));

        Assert.Equal(0, controller.Adjustments);
    }
}

public class AdaptiveLatencyExtensionTests
{
    private const uint Ssrc = 0x11223344;

    private static EncodedFrame Frame(TimeSpan newDelay) => new()
    {
        FrameId = 7,
        ReferencedFrameId = 7,
        IsKeyFrame = true,
        RtpTimestamp = 1000,
        Data = new byte[8],
        NewPlayoutDelay = newDelay,
    };

    [Fact]
    public void No_change_means_no_extension()
    {
        var packetizer = new CastRtpPacketizer(96, Ssrc);
        var packet = packetizer.GeneratePacket(Frame(TimeSpan.Zero), new byte[8], 0, 1);

        Assert.Equal(0, packet[12] & 0b0011_1111);   // extension count
        Assert.Equal(CastRtpPacketizer.BaseHeaderSize + 8, packet.Length);
    }

    [Fact]
    public void A_delay_change_is_carried_as_extension_type_one()
    {
        var packetizer = new CastRtpPacketizer(96, Ssrc);
        var packet = packetizer.GeneratePacket(
            Frame(TimeSpan.FromMilliseconds(250)), new byte[8], 0, 1);

        Assert.Equal(1, packet[12] & 0b0011_1111);

        ushort header = BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(19));
        Assert.Equal(1, header >> 10);            // extension type
        Assert.Equal(2, header & 0x3FF);          // two bytes of payload
        Assert.Equal(250, BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(21)));

        Assert.Equal(CastRtpPacketizer.BaseHeaderSize
                     + CastRtpPacketizer.AdaptiveLatencyHeaderSize + 8, packet.Length);
    }

    [Fact]
    public void Only_the_first_packet_of_a_frame_carries_the_change()
    {
        var packetizer = new CastRtpPacketizer(96, Ssrc);
        var payload = new byte[packetizer.MaxPayloadSize * 2];
        var frame = Frame(TimeSpan.FromMilliseconds(120));

        var first = packetizer.GeneratePacket(frame, payload, 0, 2);
        var second = packetizer.GeneratePacket(frame, payload, 1, 2);

        Assert.Equal(1, first[12] & 0b0011_1111);
        Assert.Equal(0, second[12] & 0b0011_1111);
    }

    [Fact]
    public void A_packet_carrying_the_extension_still_fits_the_mtu()
    {
        // The payload budget always reserves room for the extension, so the first packet
        // of a full frame cannot overrun the MTU when a delay change lands on it.
        var packetizer = new CastRtpPacketizer(96, Ssrc);
        var payload = new byte[packetizer.MaxPayloadSize * 3];

        var packet = packetizer.GeneratePacket(
            Frame(TimeSpan.FromMilliseconds(400)), payload, 0, 3);

        Assert.True(packet.Length <= CastRtpPacketizer.MaxPacketSizeIpv4,
            $"packet was {packet.Length} bytes");
    }
}
