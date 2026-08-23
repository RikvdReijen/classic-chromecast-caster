using System.Text.Json;

namespace RikRealization.ClassicCast.Protocol.Channel;

/// <summary>An app currently running on the receiver.</summary>
public sealed record ReceiverApplication
{
    public required string AppId { get; init; }
    public required string SessionId { get; init; }

    /// <summary>
    /// The destination id for messages aimed at this app. A virtual connection must be
    /// opened to it before it will accept anything.
    /// </summary>
    public required string TransportId { get; init; }

    public string DisplayName { get; init; } = "";
    public string StatusText { get; init; } = "";

    /// <summary>Namespaces the app advertises. Mirroring should list the webrtc namespace.</summary>
    public IReadOnlyList<string> Namespaces { get; init; } = Array.Empty<string>();

    public bool SupportsMirroring => Namespaces.Contains(CastSession.NsWebRtc);

    public override string ToString() => $"{DisplayName} ({AppId}) transport={TransportId}";
}

/// <summary>Parsed <c>RECEIVER_STATUS</c> payload.</summary>
public sealed record ReceiverStatus
{
    public IReadOnlyList<ReceiverApplication> Applications { get; init; } =
        Array.Empty<ReceiverApplication>();

    public double? VolumeLevel { get; init; }
    public bool? Muted { get; init; }

    public static ReceiverStatus Parse(JsonElement root)
    {
        if (!root.TryGetProperty("status", out var status))
            return new ReceiverStatus();

        var apps = new List<ReceiverApplication>();
        if (status.TryGetProperty("applications", out var applications) &&
            applications.ValueKind == JsonValueKind.Array)
        {
            foreach (var app in applications.EnumerateArray())
            {
                var namespaces = new List<string>();
                if (app.TryGetProperty("namespaces", out var ns) &&
                    ns.ValueKind == JsonValueKind.Array)
                {
                    foreach (var entry in ns.EnumerateArray())
                        if (entry.TryGetProperty("name", out var name) &&
                            name.GetString() is { } value)
                            namespaces.Add(value);
                }

                apps.Add(new ReceiverApplication
                {
                    AppId = Str(app, "appId"),
                    SessionId = Str(app, "sessionId"),
                    TransportId = Str(app, "transportId"),
                    DisplayName = Str(app, "displayName"),
                    StatusText = Str(app, "statusText"),
                    Namespaces = namespaces,
                });
            }
        }

        double? level = null;
        bool? muted = null;
        if (status.TryGetProperty("volume", out var volume) &&
            volume.ValueKind == JsonValueKind.Object)
        {
            if (volume.TryGetProperty("level", out var l) && l.ValueKind == JsonValueKind.Number)
                level = l.GetDouble();
            if (volume.TryGetProperty("muted", out var m) &&
                (m.ValueKind == JsonValueKind.True || m.ValueKind == JsonValueKind.False))
                muted = m.GetBoolean();
        }

        return new ReceiverStatus { Applications = apps, VolumeLevel = level, Muted = muted };
    }

    private static string Str(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) ? value.GetString() ?? "" : "";
}
