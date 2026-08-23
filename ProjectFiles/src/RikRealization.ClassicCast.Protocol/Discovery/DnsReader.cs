using System.Text;

namespace RikRealization.ClassicCast.Protocol.Discovery;

/// <summary>
/// Minimal DNS wire-format reader, only as much as mDNS service discovery needs.
/// Handles RFC 1035 name compression, which mDNS responders lean on heavily.
/// </summary>
internal ref struct DnsReader
{
    private readonly ReadOnlySpan<byte> _buf;
    private int _pos;

    public DnsReader(ReadOnlySpan<byte> buf) { _buf = buf; _pos = 0; }

    public int Position { get => _pos; set => _pos = value; }
    public bool AtEnd => _pos >= _buf.Length;
    public int Remaining => _buf.Length - _pos;

    public byte ReadByte() => _buf[_pos++];

    public ushort ReadUInt16()
    {
        ushort v = (ushort)((_buf[_pos] << 8) | _buf[_pos + 1]);
        _pos += 2;
        return v;
    }

    public uint ReadUInt32()
    {
        uint v = (uint)((_buf[_pos] << 24) | (_buf[_pos + 1] << 16) | (_buf[_pos + 2] << 8) | _buf[_pos + 3]);
        _pos += 4;
        return v;
    }

    public byte[] ReadBytes(int count)
    {
        var v = _buf.Slice(_pos, count).ToArray();
        _pos += count;
        return v;
    }

    public ReadOnlySpan<byte> Slice(int start, int length) => _buf.Slice(start, length);

    /// <summary>
    /// Reads a domain name, following compression pointers. On return the position sits
    /// after the name as it appeared here, not after whatever the pointer chain landed on.
    /// </summary>
    public string ReadName()
    {
        var sb = new StringBuilder();
        int p = _pos;
        bool jumped = false;
        int jumps = 0;

        while (p < _buf.Length)
        {
            byte len = _buf[p];

            if (len == 0)
            {
                p++;
                break;
            }

            if ((len & 0xC0) == 0xC0)
            {
                if (p + 1 >= _buf.Length) break;
                int target = ((len & 0x3F) << 8) | _buf[p + 1];
                if (!jumped) { _pos = p + 2; jumped = true; }
                // Malformed packets can point in circles; bail rather than spin.
                if (++jumps > 32 || target >= _buf.Length) break;
                p = target;
                continue;
            }

            p++;
            if (p + len > _buf.Length) break;
            if (sb.Length > 0) sb.Append('.');
            sb.Append(Encoding.UTF8.GetString(_buf.Slice(p, len)));
            p += len;
        }

        if (!jumped) _pos = p;
        return sb.ToString();
    }

    public void SkipName() => ReadName();
}
