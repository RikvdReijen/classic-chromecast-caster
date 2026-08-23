using System.Text.Json;

namespace RikRealization.ClassicCast.Protocol.Streaming;

/// <summary>What the receiver says it can cope with. Directly shapes the encoder config.</summary>
public sealed record ReceiverConstraints
{
    public long? MaxPixelsPerSecond { get; init; }
    public int? MaxBitRate { get; init; }
    public int? MinBitRate { get; init; }

    /// <summary>The receiver's ceiling on buffering. Our target delay must fit inside it.</summary>
    public TimeSpan? MaxDelay { get; init; }

    public CastResolution? MaxDimensions { get; init; }
    public string? MaxFrameRate { get; init; }
}

/// <summary>The receiver's own display, when it reports one.</summary>
public sealed record ReceiverDisplay
{
    public CastResolution? Dimensions { get; init; }
    public string? FrameRate { get; init; }
    public string? AspectRatio { get; init; }

    /// <summary>"sender" or "receiver" — who is expected to scale to fit.</summary>
    public string? Scaling { get; init; }
}

/// <summary>A parsed <c>ANSWER</c>.</summary>
public sealed record StreamAnswer
{
    /// <summary>The UDP port the receiver is listening on. RTP goes here.</summary>
    public required int UdpPort { get; init; }

    /// <summary>Which of our offered stream indexes the receiver accepted.</summary>
    public required IReadOnlyList<int> SendIndexes { get; init; }

    /// <summary>The receiver's SSRCs, positionally matched to <see cref="SendIndexes"/>.</summary>
    public required IReadOnlyList<uint> Ssrcs { get; init; }

    public ReceiverConstraints? Constraints { get; init; }
    public ReceiverDisplay? Display { get; init; }

    /// <summary>
    /// Parses an ANSWER envelope, throwing with the receiver's own wording when it
    /// reports a failure. A <c>result</c> of anything but "ok" means no session.
    /// </summary>
    public static StreamAnswer Parse(JsonElement root)
    {
        string? result = root.TryGetProperty("result", out var r) ? r.GetString() : null;

        if (!string.Equals(result, "ok", StringComparison.OrdinalIgnoreCase))
        {
            string description = "no description given";
            int? code = null;

            if (root.TryGetProperty("error", out var error) &&
                error.ValueKind == JsonValueKind.Object)
            {
                if (error.TryGetProperty("description", out var d) && d.GetString() is { } text)
                    description = text;
                if (error.TryGetProperty("code", out var c) && c.TryGetInt32(out int value))
                    code = value;
            }

            throw new CastNegotiationException(
                $"The receiver rejected the offer ({result ?? "no result"}" +
                $"{(code is null ? "" : $", code {code}")}): {description}");
        }

        if (!root.TryGetProperty("answer", out var answer) ||
            answer.ValueKind != JsonValueKind.Object)
            throw new CastNegotiationException("The ANSWER reported ok but carried no answer body.");

        if (!answer.TryGetProperty("udpPort", out var portElement) ||
            !portElement.TryGetInt32(out int port))
            throw new CastNegotiationException("The ANSWER carried no udpPort.");

        return new StreamAnswer
        {
            UdpPort = port,
            SendIndexes = ReadInts(answer, "sendIndexes"),
            Ssrcs = ReadUInts(answer, "ssrcs"),
            Constraints = ParseConstraints(answer),
            Display = ParseDisplay(answer),
        };
    }

    private static ReceiverConstraints? ParseConstraints(JsonElement answer)
    {
        if (!answer.TryGetProperty("constraints", out var constraints) ||
            constraints.ValueKind != JsonValueKind.Object) return null;

        if (!constraints.TryGetProperty("video", out var video) ||
            video.ValueKind != JsonValueKind.Object) return new ReceiverConstraints();

        CastResolution? maxDimensions = null;
        string? maxFrameRate = null;
        if (video.TryGetProperty("maxDimensions", out var dims) &&
            dims.ValueKind == JsonValueKind.Object)
        {
            maxDimensions = ReadResolution(dims);
            if (dims.TryGetProperty("frameRate", out var fr))
                maxFrameRate = fr.ValueKind == JsonValueKind.String ? fr.GetString() : fr.ToString();
        }

        return new ReceiverConstraints
        {
            MaxPixelsPerSecond = ReadNumber(video, "maxPixelsPerSecond") is { } pps ? (long)pps : null,
            MaxBitRate = ReadNumber(video, "maxBitRate") is { } max ? (int)max : null,
            MinBitRate = ReadNumber(video, "minBitRate") is { } min ? (int)min : null,
            MaxDelay = ReadNumber(video, "maxDelay") is { } delay
                ? TimeSpan.FromMilliseconds(delay) : null,
            MaxDimensions = maxDimensions,
            MaxFrameRate = maxFrameRate,
        };
    }

    private static ReceiverDisplay? ParseDisplay(JsonElement answer)
    {
        if (!answer.TryGetProperty("display", out var display) ||
            display.ValueKind != JsonValueKind.Object) return null;

        CastResolution? dimensions = null;
        string? frameRate = null;
        if (display.TryGetProperty("dimensions", out var dims) &&
            dims.ValueKind == JsonValueKind.Object)
        {
            dimensions = ReadResolution(dims);
            if (dims.TryGetProperty("frameRate", out var fr))
                frameRate = fr.ValueKind == JsonValueKind.String ? fr.GetString() : fr.ToString();
        }

        return new ReceiverDisplay
        {
            Dimensions = dimensions,
            FrameRate = frameRate,
            AspectRatio = display.TryGetProperty("aspectRatio", out var ar) ? ar.GetString() : null,
            Scaling = display.TryGetProperty("scaling", out var sc) ? sc.GetString() : null,
        };
    }

    private static CastResolution? ReadResolution(JsonElement element) =>
        element.TryGetProperty("width", out var w) && w.TryGetInt32(out int width) &&
        element.TryGetProperty("height", out var h) && h.TryGetInt32(out int height)
            ? new CastResolution(width, height)
            : null;

    private static double? ReadNumber(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Number
            ? value.GetDouble()
            : null;

    private static IReadOnlyList<int> ReadInts(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var array) ||
            array.ValueKind != JsonValueKind.Array) return Array.Empty<int>();

        var list = new List<int>();
        foreach (var item in array.EnumerateArray())
            if (item.TryGetInt32(out int value)) list.Add(value);
        return list;
    }

    private static IReadOnlyList<uint> ReadUInts(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var array) ||
            array.ValueKind != JsonValueKind.Array) return Array.Empty<uint>();

        var list = new List<uint>();
        foreach (var item in array.EnumerateArray())
        {
            // SSRCs are 32-bit unsigned and routinely exceed int.MaxValue.
            if (item.TryGetUInt32(out uint value)) list.Add(value);
            else if (item.TryGetInt64(out long wide)) list.Add(unchecked((uint)wide));
        }
        return list;
    }
}

public sealed class CastNegotiationException : Exception
{
    public CastNegotiationException(string message) : base(message) { }
}
