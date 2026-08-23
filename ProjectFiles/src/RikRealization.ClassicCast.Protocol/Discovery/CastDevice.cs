using System.Net;

namespace RikRealization.ClassicCast.Protocol.Discovery;

/// <summary>A Cast receiver found on the local network.</summary>
public sealed record CastDevice
{
    /// <summary>Stable device UUID from the <c>id</c> TXT key. Survives IP changes.</summary>
    public required string Id { get; init; }

    /// <summary>User-visible name from the <c>fn</c> TXT key, e.g. "Living Room TV".</summary>
    public required string FriendlyName { get; init; }

    /// <summary>
    /// Model string from the <c>md</c> TXT key. Note that Gen 1, 2 and 3 pucks all report
    /// plain "Chromecast" — they cannot be told apart from mDNS alone. Querying
    /// <c>http://{ip}:8008/setup/eureka_info</c> is the accurate way to get the hardware
    /// revision, and we will need it to pick sane defaults per generation.
    /// </summary>
    public required string Model { get; init; }

    public required IPAddress Address { get; init; }

    /// <summary>CASTV2 control port. Always 8009 in practice, but take it from SRV.</summary>
    public required int Port { get; init; }

    /// <summary>The mDNS instance name, useful for de-duplicating across interfaces.</summary>
    public required string InstanceName { get; init; }

    public IReadOnlyDictionary<string, string> Txt { get; init; } =
        new Dictionary<string, string>();

    /// <summary>Capability bitmask from the <c>ca</c> TXT key, 0 when absent.</summary>
    public int Capabilities =>
        Txt.TryGetValue("ca", out var ca) && int.TryParse(ca, out var v) ? v : 0;

    /// <summary>True when the device reports video output rather than being audio-only.</summary>
    public bool HasVideoOutput => (Capabilities & 0x04) != 0;

    public override string ToString() => $"{FriendlyName} [{Model}] {Address}:{Port}";
}
