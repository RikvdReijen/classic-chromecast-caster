using System.Buffers.Binary;
using System.Security.Cryptography;
using RikRealization.ClassicCast.Protocol.Rtp;
using Xunit;

namespace RikRealization.ClassicCast.Protocol.Tests;

public class FrameCryptoTests
{
    private static readonly byte[] Key = Convert.FromHexString("040d756791711fd3adb939066e6d8690");
    private static readonly byte[] IvMask = Convert.FromHexString("9ff0f022a959150e70a2d05a6c184aed");

    [Fact]
    public void Round_trips()
    {
        using var crypto = new FrameCrypto(Key, IvMask);
        var plaintext = RandomNumberGenerator.GetBytes(5000);

        var cipher = crypto.Encrypt(42, plaintext);
        Assert.NotEqual(plaintext, cipher);
        Assert.Equal(plaintext, crypto.Decrypt(42, cipher));
    }

    [Fact]
    public void Nonce_is_the_frame_id_big_endian_at_offset_eight_xor_the_mask()
    {
        // The risky part of the scheme is nonce derivation, so verify it independently
        // rather than by round-tripping (which would pass even if the nonce were wrong).
        const uint frameId = 0x01020304;

        var expectedNonce = new byte[16];
        BinaryPrimitives.WriteUInt32BigEndian(expectedNonce.AsSpan(8), frameId);
        for (int i = 0; i < 16; i++) expectedNonce[i] ^= IvMask[i];

        using var aes = Aes.Create();
        aes.Key = Key;
        aes.Mode = CipherMode.ECB;
        aes.Padding = PaddingMode.None;
        var expectedKeystream = aes.EncryptEcb(expectedNonce, PaddingMode.None);

        // Encrypting zeroes yields the raw keystream.
        using var crypto = new FrameCrypto(Key, IvMask);
        var keystream = crypto.Encrypt(frameId, new byte[16]);

        Assert.Equal(expectedKeystream, keystream);
    }

    [Fact]
    public void Different_frames_use_different_keystreams()
    {
        using var crypto = new FrameCrypto(Key, IvMask);
        var zeroes = new byte[32];

        Assert.NotEqual(crypto.Encrypt(1, zeroes), crypto.Encrypt(2, zeroes));
    }

    [Fact]
    public void Counter_advances_between_blocks()
    {
        // A stuck counter would repeat the keystream every 16 bytes — catastrophic, and
        // invisible to a round-trip test.
        using var crypto = new FrameCrypto(Key, IvMask);
        var keystream = crypto.Encrypt(7, new byte[32]);

        Assert.NotEqual(keystream[..16], keystream[16..]);
    }

    [Fact]
    public void Partial_final_block_is_supported()
    {
        using var crypto = new FrameCrypto(Key, IvMask);
        var plaintext = RandomNumberGenerator.GetBytes(19);   // not a block multiple

        Assert.Equal(19, crypto.Encrypt(3, plaintext).Length);
        Assert.Equal(plaintext, crypto.Decrypt(3, crypto.Encrypt(3, plaintext)));
    }

    [Theory]
    [InlineData(15)]
    [InlineData(17)]
    public void Wrong_key_or_mask_length_is_rejected(int length)
    {
        var bad = new byte[length];
        Assert.Throws<ArgumentException>(() => new FrameCrypto(bad, IvMask));
        Assert.Throws<ArgumentException>(() => new FrameCrypto(Key, bad));
    }
}

public class CastRtpPacketizerTests
{
    private const uint Ssrc = 0x11223344;

    private static EncodedFrame Frame(bool keyFrame, int length, uint frameId = 5) => new()
    {
        FrameId = frameId,
        ReferencedFrameId = keyFrame ? frameId : frameId - 1,
        IsKeyFrame = keyFrame,
        RtpTimestamp = 0xAABBCCDD,
        Data = new byte[length],
    };

    [Fact]
    public void Header_layout_matches_the_cast_rtp_spec()
    {
        var packetizer = new CastRtpPacketizer(96, Ssrc);
        var payload = new byte[10];
        var packet = packetizer.GeneratePacket(Frame(true, 10), payload, 0, 1);

        Assert.Equal(0x80, packet[0]);                                    // V=2, no padding
        Assert.Equal(0x80 | 96, packet[1]);                               // marker set, PT 96
        Assert.Equal(0, BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(2)));
        Assert.Equal(0xAABBCCDD, BinaryPrimitives.ReadUInt32BigEndian(packet.AsSpan(4)));
        Assert.Equal(Ssrc, BinaryPrimitives.ReadUInt32BigEndian(packet.AsSpan(8)));

        // Key-frame bit and has-reference-frame-id bit, no extensions.
        Assert.Equal(0b1100_0000, packet[12]);
        Assert.Equal(5, packet[13]);                                      // frame id low byte
        Assert.Equal(0, BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(14)));
        Assert.Equal(0, BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(16)));  // max packet id
        Assert.Equal(5, packet[18]);                                      // key frame refs itself
        Assert.Equal(CastRtpPacketizer.BaseHeaderSize + 10, packet.Length);
    }

    [Fact]
    public void Delta_frame_clears_the_key_frame_bit()
    {
        var packetizer = new CastRtpPacketizer(96, Ssrc);
        var packet = packetizer.GeneratePacket(Frame(false, 4), new byte[4], 0, 1);

        Assert.Equal(0b0100_0000, packet[12]);   // reference bit only
        Assert.Equal(4, packet[18]);             // references the previous frame
    }

    [Fact]
    public void Marker_bit_is_set_only_on_the_final_packet()
    {
        var packetizer = new CastRtpPacketizer(96, Ssrc);
        int payloadLength = packetizer.MaxPayloadSize * 3;
        var payload = new byte[payloadLength];
        int count = packetizer.ComputePacketCount(payloadLength);

        Assert.Equal(3, count);

        for (int i = 0; i < count; i++)
        {
            var packet = packetizer.GeneratePacket(Frame(true, payloadLength), payload, i, count);
            bool isLast = i == count - 1;

            Assert.Equal(isLast, (packet[1] & 0x80) != 0);
            Assert.Equal(i, BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(14)));
            Assert.Equal(count - 1, BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(16)));
        }
    }

    [Fact]
    public void Packets_reassemble_into_the_original_payload()
    {
        var packetizer = new CastRtpPacketizer(96, Ssrc);
        var payload = RandomNumberGenerator.GetBytes(packetizer.MaxPayloadSize * 2 + 137);
        int count = packetizer.ComputePacketCount(payload.Length);

        var reassembled = new List<byte>();
        for (int i = 0; i < count; i++)
        {
            var packet = packetizer.GeneratePacket(Frame(true, payload.Length), payload, i, count);
            Assert.True(packet.Length <= CastRtpPacketizer.MaxPacketSizeIpv4);
            reassembled.AddRange(packet.Skip(CastRtpPacketizer.BaseHeaderSize));
        }

        Assert.Equal(payload, reassembled);
    }

    [Fact]
    public void Sequence_numbers_increment_across_frames()
    {
        var packetizer = new CastRtpPacketizer(96, Ssrc);

        for (ushort expected = 0; expected < 5; expected++)
        {
            var packet = packetizer.GeneratePacket(Frame(true, 1), new byte[1], 0, 1);
            Assert.Equal(expected, BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(2)));
        }
    }

    [Fact]
    public void Empty_payload_still_produces_one_packet()
    {
        var packetizer = new CastRtpPacketizer(96, Ssrc);

        Assert.Equal(1, packetizer.ComputePacketCount(0));
        var packet = packetizer.GeneratePacket(Frame(true, 0), Array.Empty<byte>(), 0, 1);
        Assert.Equal(CastRtpPacketizer.BaseHeaderSize, packet.Length);
    }

    [Fact]
    public void Frame_id_wraps_to_its_low_byte_on_the_wire()
    {
        var packetizer = new CastRtpPacketizer(96, Ssrc);
        var packet = packetizer.GeneratePacket(Frame(true, 1, frameId: 0x1234), new byte[1], 0, 1);

        Assert.Equal(0x34, packet[13]);
    }
}

public class RtcpSenderReportTests
{
    [Fact]
    public void Sender_report_header_is_well_formed()
    {
        var report = RtcpSenderReport.Build(
            0xDEADBEEF, DateTimeOffset.UnixEpoch, 12345, 10, 2000);

        Assert.Equal(RtcpSenderReport.PacketSize, report.Length);
        Assert.Equal(0x80, report[0]);                              // V=2, no padding, 0 blocks
        Assert.Equal(200, report[1]);                               // sender report

        // Length counts 32-bit words after the common header.
        Assert.Equal((report.Length / 4) - 1, BinaryPrimitives.ReadUInt16BigEndian(report.AsSpan(2)));
        Assert.Equal(0xDEADBEEF, BinaryPrimitives.ReadUInt32BigEndian(report.AsSpan(4)));
        Assert.Equal(12345u, BinaryPrimitives.ReadUInt32BigEndian(report.AsSpan(16)));
        Assert.Equal(10u, BinaryPrimitives.ReadUInt32BigEndian(report.AsSpan(20)));
        Assert.Equal(2000u, BinaryPrimitives.ReadUInt32BigEndian(report.AsSpan(24)));
    }

    [Fact]
    public void Ntp_epoch_offset_is_correct()
    {
        // 1970-01-01 is 2,208,988,800 seconds after the NTP epoch of 1900-01-01.
        ulong ntp = RtcpSenderReport.ToNtpTimestamp(DateTimeOffset.UnixEpoch);

        Assert.Equal(2_208_988_800UL, ntp >> 32);
        Assert.Equal(0UL, ntp & 0xFFFF_FFFF);
    }

    [Fact]
    public void Ntp_fraction_encodes_sub_second_time()
    {
        ulong ntp = RtcpSenderReport.ToNtpTimestamp(DateTimeOffset.UnixEpoch.AddMilliseconds(500));

        Assert.Equal(2_208_988_800UL, ntp >> 32);
        Assert.InRange(ntp & 0xFFFF_FFFF, 0x7FFF_0000UL, 0x8001_0000UL);   // ~half of 2^32
    }

    [Theory]
    [InlineData(200, true)]
    [InlineData(201, true)]
    [InlineData(206, true)]
    [InlineData(96, false)]    // an RTP payload type, not RTCP
    [InlineData(127, false)]
    public void Rtcp_detection_separates_the_two_protocols_on_one_port(byte second, bool expected)
    {
        var packet = new byte[] { 0x80, second, 0x00, 0x06 };
        Assert.Equal(expected, RtcpSenderReport.IsRtcp(packet));
    }

    [Fact]
    public void Short_packets_are_not_mistaken_for_rtcp()
    {
        Assert.False(RtcpSenderReport.IsRtcp(new byte[] { 0x80, 200 }));
        Assert.False(RtcpSenderReport.IsRtcp(Array.Empty<byte>()));
    }
}
