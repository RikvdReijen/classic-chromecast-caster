using System.Buffers.Binary;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;

namespace RikRealization.ClassicCast.Protocol.Channel;

/// <summary>
/// The CASTV2 transport: TLS to port 8009, with each <see cref="CastMessage"/> framed by a
/// four-byte big-endian length prefix.
/// </summary>
public sealed class CastChannel : IAsyncDisposable
{
    private readonly TcpClient _tcp;
    private readonly SslStream _ssl;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    private CastChannel(TcpClient tcp, SslStream ssl)
    {
        _tcp = tcp;
        _ssl = ssl;
    }

    /// <summary>Negotiated TLS version, worth logging when a Gen 1 puck refuses to connect.</summary>
    public SslProtocols Protocol => _ssl.SslProtocol;

    public static async Task<CastChannel> ConnectAsync(
        string host, int port = 8009, CancellationToken ct = default)
    {
        var tcp = new TcpClient { NoDelay = true };
        await tcp.ConnectAsync(host, port, ct);

        // Cast receivers present a self-signed certificate chained to Google's private
        // device CA, which no machine store trusts. Accepting it is required, not sloppy:
        // the Cast protocol itself carries no other way to reach the device. The channel
        // is still encrypted; it is only the identity check we cannot perform.
        var ssl = new SslStream(tcp.GetStream(), leaveInnerStreamOpen: false,
            userCertificateValidationCallback: static (_, _, _, _) => true);

        try
        {
            await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
            {
                TargetHost = host,
                // The validation callback is already supplied to the SslStream constructor
                // above; setting it here as well is rejected outright at runtime.
                // Gen 1 hardware is old enough that it may not offer TLS 1.3, and some
                // firmware revisions are TLS 1.2-only. Offer both and let it pick.
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
            }, ct);
        }
        catch
        {
            await ssl.DisposeAsync();
            tcp.Dispose();
            throw;
        }

        return new CastChannel(tcp, ssl);
    }

    public async Task SendAsync(CastMessage message, CancellationToken ct = default)
    {
        var payload = message.Serialize();
        var frame = new byte[4 + payload.Length];
        BinaryPrimitives.WriteUInt32BigEndian(frame, (uint)payload.Length);
        payload.CopyTo(frame, 4);

        await _writeLock.WaitAsync(ct);
        try
        {
            await _ssl.WriteAsync(frame, ct);
            await _ssl.FlushAsync(ct);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>Reads one framed message. Returns null when the device closes the channel.</summary>
    public async Task<CastMessage?> ReceiveAsync(CancellationToken ct = default)
    {
        var header = new byte[4];
        if (!await ReadExactAsync(header, ct)) return null;

        uint length = BinaryPrimitives.ReadUInt32BigEndian(header);

        // A receiver should never send us anything remotely this large; treat it as a
        // desynchronised stream rather than allocating on a corrupt length.
        if (length > 1024 * 1024)
            throw new InvalidDataException($"Implausible CASTV2 frame length {length}.");

        var body = new byte[length];
        if (!await ReadExactAsync(body, ct)) return null;

        return CastMessage.Deserialize(body);
    }

    private async Task<bool> ReadExactAsync(byte[] buffer, CancellationToken ct)
    {
        int read = 0;
        while (read < buffer.Length)
        {
            int n = await _ssl.ReadAsync(buffer.AsMemory(read), ct);
            if (n == 0) return false;
            read += n;
        }
        return true;
    }

    public async ValueTask DisposeAsync()
    {
        _writeLock.Dispose();
        await _ssl.DisposeAsync();
        _tcp.Dispose();
    }
}
