using System.Text.Json;
using RikRealization.ClassicCast.Protocol.Streaming;
using Xunit;

namespace RikRealization.ClassicCast.Protocol.Tests;

/// <summary>
/// The OFFER is hand-built JSON against a protocol we do not own. These tests pin the
/// field names to Open Screen's reference sample
/// (<c>cast/streaming/impl/offer_messages_unittest.cc</c>, <c>kValidOffer</c>) so a
/// well-meaning rename cannot silently break negotiation.
/// </summary>
public class OfferTests
{
    private static JsonElement Render(StreamOffer offer)
    {
        string json = offer.ToJson("7");
        return JsonDocument.Parse(json).RootElement.Clone();
    }

    [Fact]
    public void Envelope_uses_seqNum_not_requestId()
    {
        var root = Render(StreamOffer.CreateMirroringOffer(new CastResolution(1920, 1080),
            TimeSpan.FromMilliseconds(120)));

        Assert.Equal("OFFER", root.GetProperty("type").GetString());
        Assert.Equal(7, root.GetProperty("seqNum").GetInt32());
        Assert.False(root.TryGetProperty("requestId", out _));
    }

    [Fact]
    public void SeqNum_is_a_number_not_a_string()
    {
        // The placeholder substitution has to leave an unquoted integer behind.
        var root = Render(StreamOffer.CreateMirroringOffer(new CastResolution(1280, 720),
            TimeSpan.FromMilliseconds(200)));

        Assert.Equal(JsonValueKind.Number, root.GetProperty("seqNum").ValueKind);
    }

    [Fact]
    public void Offer_body_carries_castMode_and_supportedStreams()
    {
        var root = Render(StreamOffer.CreateMirroringOffer(new CastResolution(1920, 1080),
            TimeSpan.FromMilliseconds(120)));

        var offer = root.GetProperty("offer");
        Assert.Equal("mirroring", offer.GetProperty("castMode").GetString());
        Assert.Equal(JsonValueKind.Array, offer.GetProperty("supportedStreams").ValueKind);
    }

    [Fact]
    public void Video_stream_matches_the_reference_field_names()
    {
        var root = Render(StreamOffer.CreateMirroringOffer(new CastResolution(1920, 1080),
            TimeSpan.FromMilliseconds(150)));

        var video = root.GetProperty("offer").GetProperty("supportedStreams")
            .EnumerateArray()
            .First(s => s.GetProperty("type").GetString() == "video_source");

        Assert.Equal("h264", video.GetProperty("codecName").GetString());
        Assert.Equal("cast", video.GetProperty("rtpProfile").GetString());
        Assert.Equal("1/90000", video.GetProperty("timeBase").GetString());
        Assert.Equal("60000/1000", video.GetProperty("maxFrameRate").GetString());
        Assert.Equal(150, video.GetProperty("targetDelay").GetInt32());

        foreach (var required in new[]
                 { "index", "ssrc", "rtpPayloadType", "maxBitRate", "aesKey", "aesIvMask", "resolutions" })
            Assert.True(video.TryGetProperty(required, out _), $"missing {required}");
    }

    [Fact]
    public void Audio_stream_uses_the_48k_timebase_and_channel_count()
    {
        var root = Render(StreamOffer.CreateMirroringOffer(new CastResolution(1920, 1080),
            TimeSpan.FromMilliseconds(120)));

        var audio = root.GetProperty("offer").GetProperty("supportedStreams")
            .EnumerateArray()
            .First(s => s.GetProperty("type").GetString() == "audio_source");

        Assert.Equal("opus", audio.GetProperty("codecName").GetString());
        Assert.Equal("1/48000", audio.GetProperty("timeBase").GetString());
        Assert.Equal(2, audio.GetProperty("channels").GetInt32());
        Assert.True(audio.GetProperty("bitRate").GetInt32() > 0);
    }

    [Fact]
    public void Aes_material_is_sixteen_bytes_lowercase_hex()
    {
        var root = Render(StreamOffer.CreateMirroringOffer(new CastResolution(1920, 1080),
            TimeSpan.FromMilliseconds(120)));

        foreach (var stream in root.GetProperty("offer").GetProperty("supportedStreams").EnumerateArray())
        {
            foreach (var field in new[] { "aesKey", "aesIvMask" })
            {
                string value = stream.GetProperty(field).GetString()!;
                Assert.Equal(32, value.Length);
                Assert.Equal(value.ToLowerInvariant(), value);
                Assert.All(value, c => Assert.True(Uri.IsHexDigit(c), $"{field} is not hex: {value}"));
            }
        }
    }

    [Fact]
    public void Every_stream_gets_distinct_keys_and_ssrcs()
    {
        var offer = StreamOffer.CreateMirroringOffer(new CastResolution(1920, 1080),
            TimeSpan.FromMilliseconds(120));

        Assert.Equal(offer.Streams.Count, offer.Streams.Select(s => s.Ssrc).Distinct().Count());
        Assert.Equal(offer.Streams.Count, offer.Streams.Select(s => s.Index).Distinct().Count());
        Assert.Equal(offer.Streams.Count,
            offer.Streams.Select(s => Convert.ToHexString(s.AesKey)).Distinct().Count());
    }

    [Theory]
    [InlineData(true, 96, 127)]   // Chrome-compatible AndroidTV values
    [InlineData(false, 101, 96)]  // spec values from rtp_defines.h
    public void Payload_types_follow_the_selected_scheme(
        bool androidHack, int expectedVideo, int expectedAudio)
    {
        var offer = StreamOffer.CreateMirroringOffer(new CastResolution(1920, 1080),
            TimeSpan.FromMilliseconds(120), useAndroidRtpHack: androidHack);

        var h264 = offer.Streams.OfType<CastVideoStream>().First(s => s.Codec == CastVideoCodec.H264);
        var opus = offer.Streams.OfType<CastAudioStream>().First();

        Assert.Equal(expectedVideo, h264.RtpPayloadType);
        Assert.Equal(expectedAudio, opus.RtpPayloadType);
    }

    [Fact]
    public void No_audio_offer_omits_the_audio_source()
    {
        var offer = StreamOffer.CreateMirroringOffer(new CastResolution(1920, 1080),
            TimeSpan.FromMilliseconds(120), includeAudio: false);

        Assert.Empty(offer.Streams.OfType<CastAudioStream>());
        Assert.NotEmpty(offer.Streams.OfType<CastVideoStream>());
    }
}
