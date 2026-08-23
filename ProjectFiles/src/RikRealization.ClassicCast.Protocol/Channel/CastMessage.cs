using System.Text;

namespace RikRealization.ClassicCast.Protocol.Channel;

/// <summary>
/// The CASTV2 envelope, hand-encoded against Chromium's <c>cast_channel.proto</c>:
/// <code>
///   1 protocol_version  varint (enum, CASTV2_1_0 = 0)
///   2 source_id         string
///   3 destination_id    string
///   4 namespace         string
///   5 payload_type      varint (enum, STRING = 0, BINARY = 1)
///   6 payload_utf8      string
///   7 payload_binary    bytes
/// </code>
/// Only these seven fields exist, so a full protobuf runtime would be a dependency we
/// pay for once and use for nothing else.
/// </summary>
public sealed class CastMessage
{
    public string SourceId { get; set; } = "sender-0";
    public string DestinationId { get; set; } = "receiver-0";
    public string Namespace { get; set; } = "";
    public string? PayloadUtf8 { get; set; }
    public byte[]? PayloadBinary { get; set; }

    public bool IsBinary => PayloadBinary is not null;

    public byte[] Serialize()
    {
        var buf = new List<byte>(128);

        WriteTag(buf, 1, WireType.Varint);
        WriteVarint(buf, 0);                                    // CASTV2_1_0

        WriteString(buf, 2, SourceId);
        WriteString(buf, 3, DestinationId);
        WriteString(buf, 4, Namespace);

        WriteTag(buf, 5, WireType.Varint);
        WriteVarint(buf, IsBinary ? 1UL : 0UL);

        if (PayloadUtf8 is not null)
            WriteString(buf, 6, PayloadUtf8);

        if (PayloadBinary is not null)
        {
            WriteTag(buf, 7, WireType.LengthDelimited);
            WriteVarint(buf, (ulong)PayloadBinary.Length);
            buf.AddRange(PayloadBinary);
        }

        return buf.ToArray();
    }

    public static CastMessage Deserialize(ReadOnlySpan<byte> data)
    {
        var msg = new CastMessage();
        int pos = 0;

        while (pos < data.Length)
        {
            ulong tag = ReadVarint(data, ref pos);
            int field = (int)(tag >> 3);
            var wire = (WireType)(tag & 0x7);

            switch (field)
            {
                case 1:
                case 5:
                    ReadVarint(data, ref pos);                  // version / payload type
                    break;

                case 2: msg.SourceId = ReadString(data, ref pos); break;
                case 3: msg.DestinationId = ReadString(data, ref pos); break;
                case 4: msg.Namespace = ReadString(data, ref pos); break;
                case 6: msg.PayloadUtf8 = ReadString(data, ref pos); break;

                case 7:
                {
                    int len = (int)ReadVarint(data, ref pos);
                    msg.PayloadBinary = data.Slice(pos, len).ToArray();
                    pos += len;
                    break;
                }

                default:
                    SkipUnknown(data, ref pos, wire);
                    break;
            }
        }

        return msg;
    }

    public override string ToString() =>
        $"{SourceId} -> {DestinationId} [{Namespace}] {PayloadUtf8 ?? $"<{PayloadBinary?.Length ?? 0} bytes>"}";

    // ---- protobuf primitives -------------------------------------------------------

    private enum WireType { Varint = 0, Fixed64 = 1, LengthDelimited = 2, Fixed32 = 5 }

    private static void WriteTag(List<byte> buf, int field, WireType wire) =>
        WriteVarint(buf, (ulong)((field << 3) | (int)wire));

    private static void WriteVarint(List<byte> buf, ulong value)
    {
        while (value >= 0x80)
        {
            buf.Add((byte)(value | 0x80));
            value >>= 7;
        }
        buf.Add((byte)value);
    }

    private static void WriteString(List<byte> buf, int field, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        WriteTag(buf, field, WireType.LengthDelimited);
        WriteVarint(buf, (ulong)bytes.Length);
        buf.AddRange(bytes);
    }

    private static ulong ReadVarint(ReadOnlySpan<byte> data, ref int pos)
    {
        ulong value = 0;
        int shift = 0;
        while (pos < data.Length)
        {
            byte b = data[pos++];
            value |= (ulong)(b & 0x7F) << shift;
            if ((b & 0x80) == 0) break;
            shift += 7;
            if (shift > 63) throw new InvalidDataException("Varint is longer than 64 bits.");
        }
        return value;
    }

    private static string ReadString(ReadOnlySpan<byte> data, ref int pos)
    {
        int len = (int)ReadVarint(data, ref pos);
        var s = Encoding.UTF8.GetString(data.Slice(pos, len));
        pos += len;
        return s;
    }

    private static void SkipUnknown(ReadOnlySpan<byte> data, ref int pos, WireType wire)
    {
        switch (wire)
        {
            case WireType.Varint: ReadVarint(data, ref pos); break;
            case WireType.Fixed64: pos += 8; break;
            case WireType.Fixed32: pos += 4; break;
            // Read the length into a local first: `pos += ReadVarint(..., ref pos)` would
            // capture the pre-call value of pos and skip from the wrong offset.
            case WireType.LengthDelimited:
            {
                int len = (int)ReadVarint(data, ref pos);
                pos += len;
                break;
            }
            default: throw new InvalidDataException($"Unsupported wire type {wire}.");
        }
    }
}
