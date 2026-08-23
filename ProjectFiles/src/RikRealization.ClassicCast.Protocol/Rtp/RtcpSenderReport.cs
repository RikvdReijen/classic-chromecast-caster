using System.Buffers.Binary;

namespace RikRealization.ClassicCast.Protocol.Rtp;

/// <summary>
/// Builds RTCP Sender Reports. The receiver needs these to tie our RTP timestamps to wall
/// clock; without them it has no timeline to schedule playout against, so frames can
/// arrive perfectly and still never appear.
///
/// Layout per RFC 3550 section 6.4.1, serialised the way Open Screen's
/// <c>sender_report_builder.cc</c> does it.
/// </summary>
public static class RtcpSenderReport
{
    public const byte PacketTypeSenderReport = 200;
    public const byte PacketTypeReceiverReport = 201;

    /// <summary>Everything after the four-byte common header, with no report blocks.</summary>
    private const int PayloadSize = 24;

    public const int PacketSize = 4 + PayloadSize;

    /// <summary>Seconds between the NTP epoch (1900-01-01) and the Unix epoch.</summary>
    private const ulong NtpToUnixEpochSeconds = 2_208_988_800UL;

    public static byte[] Build(
        uint senderSsrc,
        DateTimeOffset referenceTime,
        uint rtpTimestamp,
        uint sentPacketCount,
        uint sentOctetCount)
    {
        var packet = new byte[PacketSize];
        var buffer = packet.AsSpan();

        // Version 2, no padding, zero report blocks.
        buffer[0] = 0b1000_0000;
        buffer[1] = PacketTypeSenderReport;

        // Length is in 32-bit words, excluding the common header itself.
        BinaryPrimitives.WriteUInt16BigEndian(buffer[2..], PayloadSize / sizeof(uint));

        BinaryPrimitives.WriteUInt32BigEndian(buffer[4..], senderSsrc);
        BinaryPrimitives.WriteUInt64BigEndian(buffer[8..], ToNtpTimestamp(referenceTime));
        BinaryPrimitives.WriteUInt32BigEndian(buffer[16..], rtpTimestamp);
        BinaryPrimitives.WriteUInt32BigEndian(buffer[20..], sentPacketCount);
        BinaryPrimitives.WriteUInt32BigEndian(buffer[24..], sentOctetCount);

        return packet;
    }

    /// <summary>
    /// Converts to the NTP 64-bit fixed-point form: seconds since 1900 in the high word,
    /// binary fraction of a second in the low word.
    /// </summary>
    public static ulong ToNtpTimestamp(DateTimeOffset time)
    {
        double unixSeconds = time.ToUnixTimeMilliseconds() / 1000.0;
        double ntpSeconds = unixSeconds + NtpToUnixEpochSeconds;

        ulong whole = (ulong)ntpSeconds;
        ulong fraction = (ulong)((ntpSeconds - whole) * 4_294_967_296.0);

        return (whole << 32) | (fraction & 0xFFFF_FFFF);
    }

    /// <summary>
    /// Identifies an inbound RTCP packet type, so a single socket can carry RTP and RTCP
    /// together — which Cast does, unlike plain RTP's separate-ports convention.
    /// </summary>
    public static bool IsRtcp(ReadOnlySpan<byte> packet)
    {
        if (packet.Length < 4) return false;
        if ((packet[0] & 0b1100_0000) != 0b1000_0000) return false;

        // The RTCP packet-type range does not collide with the Cast RTP payload types.
        return packet[1] >= 192 && packet[1] <= 223;
    }

    public static string DescribePacketType(byte packetType) => packetType switch
    {
        200 => "sender report",
        201 => "receiver report",
        202 => "source description",
        203 => "goodbye",
        204 => "application-defined (Cast feedback)",
        206 => "payload-specific feedback",
        207 => "extended reports",
        _ => $"type {packetType}",
    };
}
