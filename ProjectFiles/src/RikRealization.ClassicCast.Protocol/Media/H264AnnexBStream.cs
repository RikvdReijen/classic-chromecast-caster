namespace RikRealization.ClassicCast.Protocol.Media;

/// <summary>One H.264 access unit — the NAL units making up a single coded picture.</summary>
public sealed record H264AccessUnit(ReadOnlyMemory<byte> Data, bool IsKeyFrame)
{
    public int Length => Data.Length;
}

/// <summary>Where an access unit begins within a buffer, and what it contains.</summary>
public readonly record struct AccessUnitBoundary(int Offset, bool IsKeyFrame, bool HasVideoSlice);

/// <summary>
/// Splits an H.264 Annex-B elementary stream into access units.
///
/// Pictures are commonly coded as several slices — x264 emits one per thread, and hardware
/// encoders do the same — so a picture boundary cannot be assumed at every VCL NAL. The
/// boundary is found by reading <c>first_mb_in_slice</c> from the slice header: a slice
/// that starts at macroblock zero starts a new picture.
/// </summary>
public static class H264AnnexBStream
{
    private const int NalSps = 7;
    private const int NalPps = 8;
    private const int NalSei = 6;
    private const int NalAccessUnitDelimiter = 9;
    private const int NalIdrSlice = 5;
    private const int NalNonIdrSlice = 1;

    public static IReadOnlyList<H264AccessUnit> Parse(ReadOnlyMemory<byte> stream)
    {
        var boundaries = FindBoundaries(stream.Span);
        var units = new List<H264AccessUnit>(boundaries.Count);

        for (int i = 0; i < boundaries.Count; i++)
        {
            if (!boundaries[i].HasVideoSlice) continue;

            int start = boundaries[i].Offset;
            int end = i + 1 < boundaries.Count ? boundaries[i + 1].Offset : stream.Length;
            units.Add(new H264AccessUnit(stream[start..end], boundaries[i].IsKeyFrame));
        }

        return units;
    }

    public static IReadOnlyList<H264AccessUnit> ParseFile(string path) =>
        Parse(File.ReadAllBytes(path));

    /// <summary>
    /// Locates access unit boundaries. Trailing groups that carry no slice yet are still
    /// reported, so an incremental caller can tell a complete picture from one still
    /// arriving.
    /// </summary>
    public static IReadOnlyList<AccessUnitBoundary> FindBoundaries(ReadOnlySpan<byte> data)
    {
        var boundaries = new List<AccessUnitBoundary>();

        int groupStart = -1;
        bool groupHasVcl = false;
        bool groupHasIdr = false;

        foreach (var nal in FindNalUnits(data))
        {
            int type = nal.NalType;
            bool isVcl = type is NalNonIdrSlice or NalIdrSlice;

            // A new picture begins at a slice that starts from macroblock zero, or at the
            // parameter sets and delimiters that introduce one, once we already have a
            // slice in hand. Slices continuing the current picture are simply absorbed.
            bool startsNewUnit = groupHasVcl &&
                (isVcl
                    ? nal.FirstMacroblock == 0
                    : type is NalSps or NalPps or NalSei or NalAccessUnitDelimiter);

            if (startsNewUnit || groupStart < 0)
            {
                if (groupStart >= 0)
                    boundaries.Add(new AccessUnitBoundary(groupStart, groupHasIdr, groupHasVcl));

                groupStart = nal.Start;
                groupHasVcl = false;
                groupHasIdr = false;
            }

            if (isVcl)
            {
                groupHasVcl = true;
                if (type == NalIdrSlice) groupHasIdr = true;
            }
        }

        if (groupStart >= 0)
            boundaries.Add(new AccessUnitBoundary(groupStart, groupHasIdr, groupHasVcl));

        return boundaries;
    }

    /// <summary>
    /// A located NAL unit. <c>FirstMacroblock</c> is only meaningful for VCL NALs, where
    /// it is the <c>first_mb_in_slice</c> field, and is -1 otherwise.
    /// </summary>
    private readonly record struct NalUnit(int Start, int NalType, int FirstMacroblock);

    /// <summary>
    /// Reads <c>first_mb_in_slice</c>, the leading Exp-Golomb field of a slice header.
    ///
    /// Emulation-prevention bytes are not stripped: they can only appear after two zero
    /// bytes, and this field sits in the first few bits of the payload, so it cannot be
    /// affected. Anything deeper in the slice header would need proper unescaping.
    /// </summary>
    private static int ReadFirstMacroblock(ReadOnlySpan<byte> payload)
    {
        int totalBits = payload.Length * 8;
        int bitPosition = 0;

        // Count the leading zeroes that prefix an Exp-Golomb code, then consume the 1.
        int leadingZeros = 0;
        while (bitPosition < totalBits)
        {
            int bit = (payload[bitPosition >> 3] >> (7 - (bitPosition & 7))) & 1;
            bitPosition++;
            if (bit == 1) break;
            if (++leadingZeros > 31) return -1;
        }

        if (bitPosition + leadingZeros > totalBits) return -1;   // truncated NAL

        int value = 0;
        for (int i = 0; i < leadingZeros; i++)
        {
            value = (value << 1) | ((payload[bitPosition >> 3] >> (7 - (bitPosition & 7))) & 1);
            bitPosition++;
        }

        return value + (1 << leadingZeros) - 1;
    }

    /// <summary>
    /// Locates NAL units by start code. Both the three-byte and four-byte forms occur in
    /// the same stream — x264 uses four bytes for parameter sets and three for slices.
    /// <c>Start</c> is the offset of the start code, so slices keep their Annex-B framing.
    /// </summary>
    private static List<NalUnit> FindNalUnits(ReadOnlySpan<byte> data)
    {
        var found = new List<NalUnit>();

        for (int i = 0; i + 3 < data.Length; i++)
        {
            if (data[i] != 0x00 || data[i + 1] != 0x00) continue;

            int payloadStart;
            if (data[i + 2] == 0x01) payloadStart = i + 3;
            else if (data[i + 2] == 0x00 && data[i + 3] == 0x01) payloadStart = i + 4;
            else continue;

            if (payloadStart >= data.Length) break;

            int nalType = data[payloadStart] & 0x1F;
            int firstMacroblock = nalType is NalNonIdrSlice or NalIdrSlice
                ? ReadFirstMacroblock(data[(payloadStart + 1)..])
                : -1;

            found.Add(new NalUnit(i, nalType, firstMacroblock));
            i = payloadStart;
        }

        return found;
    }
}
