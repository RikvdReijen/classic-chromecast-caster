using System.Text.Json;
using RikRealization.ClassicCast.Protocol.Streaming;
using Xunit;

namespace RikRealization.ClassicCast.Protocol.Tests;

public class AnswerTests
{
    private static JsonElement Parse(string json) =>
        JsonDocument.Parse(json).RootElement.Clone();

    /// <summary>
    /// Shape observed from a real Gen 2/3 Chromecast during the milestone 2 spike: it
    /// accepted the audio stream and the H.264 video stream, and listed them audio-first
    /// rather than in the order they were offered.
    /// </summary>
    private const string RealAnswer = """
        {
          "type": "ANSWER",
          "seqNum": 1,
          "result": "ok",
          "answer": {
            "udpPort": 10008,
            "sendIndexes": [2, 0],
            "ssrcs": [1383781994, 466151719]
          }
        }
        """;

    [Fact]
    public void Parses_udp_port_and_selected_streams()
    {
        var answer = StreamAnswer.Parse(Parse(RealAnswer));

        Assert.Equal(10008, answer.UdpPort);
        Assert.Equal(new[] { 2, 0 }, answer.SendIndexes);
        Assert.Equal(new uint[] { 1383781994, 466151719 }, answer.Ssrcs);
    }

    [Fact]
    public void Ssrcs_above_int_max_survive_parsing()
    {
        // SSRCs are 32-bit unsigned; a naive int parse silently drops the top half.
        var answer = StreamAnswer.Parse(Parse("""
            {"result":"ok","answer":{"udpPort":1234,"sendIndexes":[0],"ssrcs":[4294967295]}}
            """));

        Assert.Equal(uint.MaxValue, Assert.Single(answer.Ssrcs));
    }

    [Fact]
    public void Rejection_surfaces_the_receiver_wording()
    {
        var ex = Assert.Throws<CastNegotiationException>(() => StreamAnswer.Parse(Parse("""
            {"type":"ANSWER","seqNum":1,"result":"error",
             "error":{"code":42,"description":"unsupported codec"}}
            """)));

        Assert.Contains("unsupported codec", ex.Message);
        Assert.Contains("42", ex.Message);
    }

    [Fact]
    public void Missing_udp_port_is_an_error_not_a_zero_port()
    {
        Assert.Throws<CastNegotiationException>(() => StreamAnswer.Parse(Parse("""
            {"result":"ok","answer":{"sendIndexes":[0],"ssrcs":[1]}}
            """)));
    }

    [Fact]
    public void Ok_result_with_no_answer_body_is_an_error()
    {
        Assert.Throws<CastNegotiationException>(() =>
            StreamAnswer.Parse(Parse("""{"result":"ok"}""")));
    }

    [Fact]
    public void Constraints_and_display_are_parsed_when_present()
    {
        var answer = StreamAnswer.Parse(Parse("""
            {
              "result": "ok",
              "answer": {
                "udpPort": 51706,
                "sendIndexes": [0],
                "ssrcs": [19088744],
                "constraints": {
                  "video": {
                    "maxPixelsPerSecond": 62208000,
                    "minBitRate": 300000,
                    "maxBitRate": 10000000,
                    "maxDelay": 4000,
                    "maxDimensions": {"width": 1920, "height": 1080, "frameRate": "60"}
                  }
                },
                "display": {
                  "dimensions": {"width": 1920, "height": 1080, "frameRate": "60"},
                  "aspectRatio": "16:9",
                  "scaling": "sender"
                }
              }
            }
            """));

        Assert.Equal(new CastResolution(1920, 1080), answer.Constraints!.MaxDimensions);
        Assert.Equal(10_000_000, answer.Constraints.MaxBitRate);
        Assert.Equal(TimeSpan.FromSeconds(4), answer.Constraints.MaxDelay);
        Assert.Equal(62_208_000, answer.Constraints.MaxPixelsPerSecond);

        Assert.Equal(new CastResolution(1920, 1080), answer.Display!.Dimensions);
        Assert.Equal("16:9", answer.Display.AspectRatio);
        Assert.Equal("sender", answer.Display.Scaling);
    }

    [Fact]
    public void Absent_constraints_and_display_are_null_not_defaults()
    {
        // The real device omitted both. Fabricating empty objects would invite code that
        // treats "unknown" as "unconstrained".
        var answer = StreamAnswer.Parse(Parse(RealAnswer));

        Assert.Null(answer.Constraints);
        Assert.Null(answer.Display);
    }
}
