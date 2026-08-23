using RikRealization.ClassicCast.Protocol.Channel;
using Xunit;

namespace RikRealization.ClassicCast.Protocol.Tests;

/// <summary>
/// The CASTV2 envelope is hand-encoded protobuf. A wrong field number or wire type would
/// produce a message the device silently ignores, which is painful to diagnose live.
/// </summary>
public class CastMessageTests
{
    [Fact]
    public void String_payload_round_trips()
    {
        var original = new CastMessage
        {
            SourceId = "sender-0",
            DestinationId = "receiver-0",
            Namespace = CastSession.NsReceiver,
            PayloadUtf8 = """{"type":"GET_STATUS","requestId":1}""",
        };

        var decoded = CastMessage.Deserialize(original.Serialize());

        Assert.Equal(original.SourceId, decoded.SourceId);
        Assert.Equal(original.DestinationId, decoded.DestinationId);
        Assert.Equal(original.Namespace, decoded.Namespace);
        Assert.Equal(original.PayloadUtf8, decoded.PayloadUtf8);
        Assert.Null(decoded.PayloadBinary);
    }

    [Fact]
    public void Binary_payload_round_trips()
    {
        var payload = new byte[] { 0x00, 0x01, 0x7F, 0x80, 0xFF };
        var decoded = CastMessage.Deserialize(new CastMessage
        {
            Namespace = "urn:x-cast:com.google.cast.binary",
            PayloadBinary = payload,
        }.Serialize());

        Assert.Equal(payload, decoded.PayloadBinary);
        Assert.True(decoded.IsBinary);
    }

    [Fact]
    public void Field_numbers_match_cast_channel_proto()
    {
        var bytes = new CastMessage
        {
            SourceId = "s",
            DestinationId = "d",
            Namespace = "n",
            PayloadUtf8 = "p",
        }.Serialize();

        // 1 protocol_version varint, then length-delimited 2/3/4, varint 5, then 6.
        Assert.Equal(0x08, bytes[0]);          // field 1, wire type 0
        Assert.Contains<byte>(0x12, bytes);    // field 2, wire type 2
        Assert.Contains<byte>(0x1A, bytes);    // field 3, wire type 2
        Assert.Contains<byte>(0x22, bytes);    // field 4, wire type 2
        Assert.Contains<byte>(0x28, bytes);    // field 5, wire type 0
        Assert.Contains<byte>(0x32, bytes);    // field 6, wire type 2
    }

    [Fact]
    public void Multibyte_utf8_survives_the_length_prefix()
    {
        // Friendly names routinely contain non-ASCII; length is in bytes, not characters.
        const string name = """{"type":"CONNECT","name":"Salon—Télé 📺"}""";
        var decoded = CastMessage.Deserialize(
            new CastMessage { Namespace = "n", PayloadUtf8 = name }.Serialize());

        Assert.Equal(name, decoded.PayloadUtf8);
    }

    [Fact]
    public void Unknown_fields_are_skipped_rather_than_corrupting_the_parse()
    {
        var known = new CastMessage
        {
            SourceId = "sender-0",
            DestinationId = "receiver-0",
            Namespace = "n",
            PayloadUtf8 = "p",
        }.Serialize();

        // Append field 9, wire type 2 (length-delimited), three bytes of payload — a
        // field a future firmware might add. It must not derail the fields we do know.
        var extended = known.Concat(new byte[] { 0x4A, 0x03, 0xAA, 0xBB, 0xCC }).ToArray();
        var decoded = CastMessage.Deserialize(extended);

        Assert.Equal("sender-0", decoded.SourceId);
        Assert.Equal("p", decoded.PayloadUtf8);
    }

    [Fact]
    public void Large_payload_uses_a_multibyte_varint_length()
    {
        string payload = new('x', 5000);   // length needs two varint bytes
        var decoded = CastMessage.Deserialize(
            new CastMessage { Namespace = "n", PayloadUtf8 = payload }.Serialize());

        Assert.Equal(payload, decoded.PayloadUtf8);
    }
}
