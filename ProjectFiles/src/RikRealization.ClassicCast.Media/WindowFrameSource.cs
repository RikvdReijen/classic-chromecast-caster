using System.Runtime.InteropServices;

namespace RikRealization.ClassicCast.Media;

/// <summary>
/// Captures a single application window.
///
/// Two mechanisms, because neither works everywhere. <c>PrintWindow</c> asks the window to
/// draw itself, so it captures even when covered — but a GPU-composited window (browsers,
/// some Electron apps) can answer with a blank frame. Blitting the screen region always
/// produces pixels but picks up anything overlapping. The first frame decides which to use.
///
/// The encoder is fixed to one resolution for the life of a session, so the captured size
/// is pinned at construction. A window resized mid-cast is letterboxed into that frame
/// rather than restarting the whole pipeline.
/// </summary>
public sealed class WindowFrameSource : IFrameSource
{
    private const int SrcCopy = 0x00CC0020;
    private const int CaptureBlt = 0x40000000;
    private const uint RenderFullContent = 2;

    private readonly IntPtr _window;
    private readonly string _title;
    private readonly IntPtr _screenDc;
    private readonly IntPtr _memoryDc;
    private readonly IntPtr _bitmap;
    private readonly IntPtr _previousBitmap;
    private readonly IntPtr _bits;
    private readonly byte[] _pixels;

    private bool? _printWindowWorks;
    private bool _disposed;

    public WindowFrameSource(WindowInfo window)
    {
        _window = window.Handle;
        _title = window.Title;

        // Even dimensions, because 4:2:0 chroma cannot represent anything else.
        Width = Math.Max(2, window.Width - (window.Width % 2));
        Height = Math.Max(2, window.Height - (window.Height % 2));

        _screenDc = GetDC(IntPtr.Zero);
        _memoryDc = CreateCompatibleDC(_screenDc);

        var info = new BitmapInfo
        {
            Header = new BitmapInfoHeader
            {
                Size = (uint)Marshal.SizeOf<BitmapInfoHeader>(),
                Width = Width,
                Height = -Height,          // top-down
                Planes = 1,
                BitCount = 32,
                Compression = 0,
            },
        };

        _bitmap = CreateDIBSection(_screenDc, ref info, 0, out _bits, IntPtr.Zero, 0);
        if (_bitmap == IntPtr.Zero || _bits == IntPtr.Zero)
            throw new InvalidOperationException("Could not create a capture surface for the window.");

        _previousBitmap = SelectObject(_memoryDc, _bitmap);
        _pixels = new byte[Width * Height * 4];
    }

    public int Width { get; }
    public int Height { get; }

    public string Description =>
        $"{(_printWindowWorks == false ? "Screen region" : "PrintWindow")} capture of “{_title}”";

    public CapturedFrame? TryCapture(TimeSpan timeout)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        // The window can close mid-session; that ends the source rather than erroring.
        if (!IsWindow(_window) || IsIconic(_window)) return null;

        bool captured = _printWindowWorks switch
        {
            true => PrintWindow(_window, _memoryDc, RenderFullContent),
            false => BlitScreenRegion(),
            null => ChooseMechanism(),
        };

        if (!captured) return null;

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

    /// <summary>
    /// Tries PrintWindow once and checks whether it actually drew anything. A window that
    /// renders through the GPU often returns success and a blank surface.
    /// </summary>
    private bool ChooseMechanism()
    {
        if (PrintWindow(_window, _memoryDc, RenderFullContent))
        {
            GdiFlush();
            if (!SurfaceIsBlank())
            {
                _printWindowWorks = true;
                return true;
            }
        }

        _printWindowWorks = false;
        return BlitScreenRegion();
    }

    private bool BlitScreenRegion()
    {
        if (!WindowEnumerator.GetWindowRect(_window, out var rect)) return false;

        return BitBlt(_memoryDc, 0, 0, Width, Height,
                      _screenDc, rect.Left, rect.Top, SrcCopy | CaptureBlt);
    }

    /// <summary>Samples a grid of pixels rather than the whole surface, which is enough.</summary>
    private bool SurfaceIsBlank()
    {
        Marshal.Copy(_bits, _pixels, 0, _pixels.Length);

        for (int i = 0; i < _pixels.Length; i += 4 * 997)
        {
            if (_pixels[i] != 0 || _pixels[i + 1] != 0 || _pixels[i + 2] != 0) return false;
        }

        return true;
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
        public uint ColorMask0, ColorMask1, ColorMask2;
    }

    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool PrintWindow(IntPtr hWnd, IntPtr hdc, uint flags);
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
