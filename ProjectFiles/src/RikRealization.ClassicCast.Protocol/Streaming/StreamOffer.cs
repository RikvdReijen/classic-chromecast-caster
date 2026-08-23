using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace RikRealization.ClassicCast.Protocol.Streaming;

public enum CastVideoCodec { H264, Vp8, Vp9, Av1 }
public enum CastAudioCodec { Opus, Aac }

/// <summary>Pixel dimensions offered for a video stream.</summary>
public readonly record struct CastResolution(int Width, int Height)
{
    public override string ToString() => $"{Width}x{Height}";
}

/// <summary>
/// Fields shared by every offered stream. Names and semantics follow Chromium's Open
/// Screen Library (<c>cast/streaming/public/offer_messages.cc</c>); the encryption
/// material here is what the RTP payloads will be sealed with.
/// </summary>
public abstract record CastStream
{
    public required int Index { get; init; }
    public required uint Ssrc { get; init; }
    public required int RtpPayloadType { get; init; }
    public required int RtpTimebase { get; init; }

    /// <summary>
    /// How long the receiver buffers before presenting. Open Screen's default is 400 ms,
    /// and it is the single largest term in the end-to-end latency budget, so we offer
    /// considerably less and adapt upward only if the link cannot hold it.
    /// </summary>
    public TimeSpan TargetDelay { get; init; } = TimeSpan.FromMilliseconds(120);

    /// <summary>AES-128 key for the RTP payloads. Sixteen bytes.</summary>
    public required byte[] AesKey { get; init; }

    /// <summary>Per-frame IV mask, XORed with the frame counter. Sixteen bytes.</summary>
    public required byte[] AesIvMask { get; init; }

    public abstract string SourceType { get; }

    protected JsonObject ToJsonBase() => new()
    {
        ["index"] = Index,
        ["type"] = SourceType,
        ["rtpProfile"] = "cast",
        ["rtpPayloadType"] = RtpPayloadType,
        ["ssrc"] = Ssrc,
        ["targetDelay"] = (int)TargetDelay.TotalMilliseconds,
        ["timeBase"] = $"1/{RtpTimebase}",
        // Open Screen hex-encodes these lowercase; match it rather than risk a picky parser.
        ["aesKey"] = Convert.ToHexString(AesKey).ToLowerInvariant(),
        ["aesIvMask"] = Convert.ToHexString(AesIvMask).ToLowerInvariant(),
    };

    public abstract JsonObject ToJson();
}

public sealed record CastVideoStream : CastStream
{
    public override string SourceType => "video_source";

    public CastVideoCodec Codec { get; init; } = CastVideoCodec.H264;
    public int MaxBitRate { get; init; } = 5_000_000;

    /// <summary>Rational, as the protocol requires: "60000/1000" rather than "60".</summary>
    public string MaxFrameRate { get; init; } = "60000/1000";

    public string Profile { get; init; } = "main";
    public string Level { get; init; } = "4";

    public IReadOnlyList<CastResolution> Resolutions { get; init; } =
        new[] { new CastResolution(1920, 1080), new CastResolution(1280, 720) };

    public override JsonObject ToJson()
    {
        var json = ToJsonBase();
        json["codecName"] = Codec.WireName();
        json["maxFrameRate"] = MaxFrameRate;
        json["maxBitRate"] = MaxBitRate;
        json["profile"] = Profile;
        json["level"] = Level;

        var resolutions = new JsonArray();
        foreach (var r in Resolutions)
            resolutions.Add(new JsonObject { ["width"] = r.Width, ["height"] = r.Height });
        json["resolutions"] = resolutions;

        return json;
    }
}

public sealed record CastAudioStream : CastStream
{
    public override string SourceType => "audio_source";

    public CastAudioCodec Codec { get; init; } = CastAudioCodec.Opus;
    public int BitRate { get; init; } = 128_000;
    public int Channels { get; init; } = 2;

    public override JsonObject ToJson()
    {
        var json = ToJsonBase();
        json["codecName"] = Codec.WireName();
        json["bitRate"] = BitRate;
        json["channels"] = Channels;
        return json;
    }
}

/// <summary>A complete <c>OFFER</c> payload.</summary>
public sealed record StreamOffer
{
    /// <summary>"mirroring" or "remoting". We only ever mirror.</summary>
    public string CastMode { get; init; } = "mirroring";

    public IReadOnlyList<CastStream> Streams { get; init; } = Array.Empty<CastStream>();

    public string ToJson(string seqPlaceholder)
    {
        var streams = new JsonArray();
        foreach (var stream in Streams) streams.Add(stream.ToJson());

        var message = new JsonObject
        {
            ["type"] = "OFFER",
            ["seqNum"] = seqPlaceholder,
            ["offer"] = new JsonObject
            {
                ["castMode"] = CastMode,
                ["supportedStreams"] = streams,
            },
        };

        // seqNum is an integer on the wire. It is carried as a placeholder string here so
        // CastSession can stamp the real number, so unquote it on the way out.
        return message.ToJsonString(new JsonSerializerOptions { WriteIndented = false })
            .Replace($"\"{seqPlaceholder}\"", seqPlaceholder);
    }

    /// <summary>
    /// Builds the offer this project actually sends: hardware-friendly H.264 video with a
    /// software VP8 alternative, plus Opus audio. The receiver picks from the list, so
    /// offering both codecs costs nothing and buys the fallback for free.
    /// </summary>
    public static StreamOffer CreateMirroringOffer(
        CastResolution resolution,
        TimeSpan targetDelay,
        bool includeAudio = true,
        bool useAndroidRtpHack = true,
        bool includeVideo = true)
    {
        var resolutions = new[] { resolution, new CastResolution(1280, 720) }
            .Distinct()
            .ToArray();

        var streams = new List<CastStream>();

        if (includeVideo) streams.AddRange(new CastStream[]
        {
            new CastVideoStream
            {
                Index = 0,
                Ssrc = CastSsrc.Next(),
                Codec = CastVideoCodec.H264,
                RtpPayloadType = RtpPayloadTypes.For(CastVideoCodec.H264, useAndroidRtpHack),
                RtpTimebase = 90_000,
                TargetDelay = targetDelay,
                AesKey = RandomNumberGenerator.GetBytes(16),
                AesIvMask = RandomNumberGenerator.GetBytes(16),
                Resolutions = resolutions,
            },
            new CastVideoStream
            {
                Index = 1,
                Ssrc = CastSsrc.Next(),
                Codec = CastVideoCodec.Vp8,
                RtpPayloadType = RtpPayloadTypes.For(CastVideoCodec.Vp8, useAndroidRtpHack),
                RtpTimebase = 90_000,
                TargetDelay = targetDelay,
                AesKey = RandomNumberGenerator.GetBytes(16),
                AesIvMask = RandomNumberGenerator.GetBytes(16),
                Resolutions = resolutions,
            },
        });

        if (includeAudio)
        {
            streams.Add(new CastAudioStream
            {
                // Index 2 keeps the numbering stable whether or not video is present, so a
                // capture log reads the same either way.
                Index = 2,
                Ssrc = CastSsrc.Next(),
                Codec = CastAudioCodec.Opus,
                RtpPayloadType = RtpPayloadTypes.For(CastAudioCodec.Opus, useAndroidRtpHack),
                RtpTimebase = 48_000,
                TargetDelay = targetDelay,
                AesKey = RandomNumberGenerator.GetBytes(16),
                AesIvMask = RandomNumberGenerator.GetBytes(16),
            });
        }

        if (streams.Count == 0)
            throw new ArgumentException("An offer must carry at least one stream.");

        return new StreamOffer { Streams = streams };
    }
}

public static class CastCodecExtensions
{
    public static string WireName(this CastVideoCodec codec) => codec switch
    {
        CastVideoCodec.H264 => "h264",
        CastVideoCodec.Vp8 => "vp8",
        CastVideoCodec.Vp9 => "vp9",
        CastVideoCodec.Av1 => "av1",
        _ => throw new ArgumentOutOfRangeException(nameof(codec)),
    };

    public static string WireName(this CastAudioCodec codec) => codec switch
    {
        CastAudioCodec.Opus => "opus",
        CastAudioCodec.Aac => "aac",
        _ => throw new ArgumentOutOfRangeException(nameof(codec)),
    };
}

/// <summary>
/// RTP payload type numbers, from Open Screen's <c>rtp_defines.h</c>.
/// </summary>
public static class RtpPayloadTypes
{
    public const int AudioOpus = 96;
    public const int AudioAac = 97;
    public const int VideoVp8 = 100;
    public const int VideoH264 = 101;
    public const int VideoVp9 = 103;
    public const int VideoAv1 = 104;

    // Out-of-spec values that some receivers require regardless of the codec in use.
    // Chrome's sender always sends these, so matching it is the safest default when
    // talking to the built-in mirroring receiver.
    public const int AudioAndroidHack = 127;
    public const int VideoAndroidHack = 96;

    public static int For(CastVideoCodec codec, bool androidHack) => androidHack
        ? VideoAndroidHack
        : codec switch
        {
            CastVideoCodec.H264 => VideoH264,
            CastVideoCodec.Vp8 => VideoVp8,
            CastVideoCodec.Vp9 => VideoVp9,
            CastVideoCodec.Av1 => VideoAv1,
            _ => throw new ArgumentOutOfRangeException(nameof(codec)),
        };

    public static int For(CastAudioCodec codec, bool androidHack) => androidHack
        ? AudioAndroidHack
        : codec switch
        {
            CastAudioCodec.Opus => AudioOpus,
            CastAudioCodec.Aac => AudioAac,
            _ => throw new ArgumentOutOfRangeException(nameof(codec)),
        };
}

/// <summary>Allocates synchronisation source identifiers for offered streams.</summary>
public static class CastSsrc
{
    private static uint _next = 1;

    /// <summary>
    /// SSRC 0 and uint.MaxValue are avoided: the former reads as "unset" and the latter
    /// appears in Open Screen's own test fixtures as a sentinel.
    /// </summary>
    public static uint Next()
    {
        uint value = (uint)Random.Shared.Next(1, int.MaxValue) + Interlocked.Increment(ref _next);
        return value is 0 or uint.MaxValue ? 1 : value;
    }
}
