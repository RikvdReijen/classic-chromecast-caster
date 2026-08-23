using System.Runtime.InteropServices;

namespace RikRealization.ClassicCast.Media;

/// <summary>
/// Desktop capture via GDI <c>BitBlt</c>.
///
/// This is the dependable path, not the fast one: it copies through system memory and
/// costs real CPU at 1080p. It exists because it works on every Windows machine, in every
/// session type, with no GPU assumptions — so the pipeline can always be brought up and
/// measured even where Desktop Duplication refuses.
/// </summary>
public sealed class GdiFrameSource : IFrameSource
{
    private const int SrcCopy = 0x00CC0020;

    /// <summary>Includes layered windows, which a plain BitBlt silently omits.</summary>
    private const int CaptureBlt = 0x40000000;

    private readonly IntPtr _screenDc;
    private readonly IntPtr _memoryDc;
    private readonly IntPtr _bitmap;
    private readonly IntPtr _previousBitmap;
    private readonly IntPtr _bits;
    private readonly byte[] _pixels;
    private readonly DisplayInfo _display;

    private bool _disposed;

    public GdiFrameSource(DisplayInfo display)
    {
        _display = display;
        Width = display.Width;
        Height = display.Height;

        _screenDc = GetDC(IntPtr.Zero);
        if (_screenDc == IntPtr.Zero)
            throw new InvalidOperationException("Could not obtain a screen device context.");

        _memoryDc = CreateCompatibleDC(_screenDc);

        // A DIB section rather than a compatible bitmap: BitBlt writes straight into
        // memory we can read, so capturing costs one blit instead of a blit followed by
        // GetDIBits converting and copying the whole frame a second time. At 1080p that
        // second pass was the single most expensive step in the pipeline.
        var info = new BitmapInfo
        {
            Header = new BitmapInfoHeader
            {
                Size = (uint)Marshal.SizeOf<BitmapInfoHeader>(),
                Width = Width,
                // Negative height requests top-down rows, matching what encoders expect.
                Height = -Height,
                Planes = 1,
                BitCount = 32,
                Compression = 0,
            },
        };

        _bitmap = CreateDIBSection(_screenDc, ref info, 0, out _bits, IntPtr.Zero, 0);
        if (_bitmap == IntPtr.Zero || _bits == IntPtr.Zero)
            throw new InvalidOperationException("Could not create a DIB section for capture.");

        _previousBitmap = SelectObject(_memoryDc, _bitmap);
        _pixels = new byte[Width * Height * 4];
    }

    public int Width { get; }
    public int Height { get; }
    public string Description => $"GDI BitBlt on {_display.Name}";

    public CapturedFrame? TryCapture(TimeSpan timeout)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!BitBlt(_memoryDc, 0, 0, Width, Height,
                    _screenDc, _display.X, _display.Y, SrcCopy | CaptureBlt))
            return null;

        // GDI batches drawing; without this the DIB may not hold the blit yet.
        GdiFlush();

        Marshal.Copy(_bits, _pixels, 0, _pixels.Length);

        return new CapturedFrame
        {
            Pixels = _pixels,
            Width = Width,
            Height = Height,
            Stride = Width * 4,
            CapturedAt = DateTimeOffset.UtcNow,
        };
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_memoryDc != IntPtr.Zero)
        {
            SelectObject(_memoryDc, _previousBitmap);
            DeleteDC(_memoryDc);
        }
        if (_bitmap != IntPtr.Zero) DeleteObject(_bitmap);
        if (_screenDc != IntPtr.Zero) ReleaseDC(IntPtr.Zero, _screenDc);
    }

    // ---- interop -------------------------------------------------------------------

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfoHeader
    {
        public uint Size;
        public int Width;
        public int Height;
        public ushort Planes;
        public ushort BitCount;
        public uint Compression;
        public uint SizeImage;
        public int XPelsPerMeter;
        public int YPelsPerMeter;
        public uint ClrUsed;
        public uint ClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfo
    {
        public BitmapInfoHeader Header;
        // A 32bpp BI_RGB DIB uses no palette, but the colour table field must be present.
        public uint ColorMask0, ColorMask1, ColorMask2;
    }

    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")] private static extern int GdiFlush();

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateDIBSection(IntPtr hdc, ref BitmapInfo info, uint usage,
                                                  out IntPtr bits, IntPtr section, uint offset);

    [DllImport("gdi32.dll")]
    private static extern bool BitBlt(IntPtr dest, int x, int y, int w, int h,
                                      IntPtr src, int srcX, int srcY, int rop);
}
