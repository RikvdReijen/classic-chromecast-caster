using SharpGen.Runtime;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace RikRealization.ClassicCast.Media;

/// <summary>
/// Desktop capture via DXGI Desktop Duplication.
///
/// The frame stays in GPU memory until we ask for it, so this replaces GDI's full
/// screen-to-system-memory blit — measured at 31 ms per 1080p frame — with a GPU-side copy
/// and a single mapped read. It is the difference between 27 fps and a real 60.
///
/// It is also more fragile than GDI: the duplication is lost on a resolution change, a
/// UAC prompt, or a full-screen exclusive application, and has to be rebuilt.
/// </summary>
public sealed class DesktopDuplicationFrameSource : IFrameSource
{
    private const int WaitTimeout = unchecked((int)0x887A0027);   // DXGI_ERROR_WAIT_TIMEOUT
    private const int AccessLost = unchecked((int)0x887A0026);    // DXGI_ERROR_ACCESS_LOST

    private readonly DisplayInfo _display;
    private readonly ID3D11Device _device;
    private readonly ID3D11DeviceContext _context;
    private readonly ID3D11Texture2D _staging;
    private readonly byte[] _pixels;

    private IDXGIOutputDuplication _duplication;
    private bool _hasFrame;
    private bool _disposed;

    public DesktopDuplicationFrameSource(DisplayInfo display)
    {
        _display = display;
        Width = display.Width;
        Height = display.Height;

        var result = D3D11.D3D11CreateDevice(
            null,
            DriverType.Hardware,
            DeviceCreationFlags.BgraSupport,
            new[] { FeatureLevel.Level_11_1, FeatureLevel.Level_11_0, FeatureLevel.Level_10_1 },
            out _device!,
            out _context!);

        if (result.Failure)
            throw new InvalidOperationException($"Could not create a Direct3D 11 device: {result}");

        _duplication = CreateDuplication(_device, display);

        // A staging texture is the only kind the CPU can map. Every captured frame is
        // copied into this one on the GPU, then read once.
        _staging = _device.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)Width,
            Height = (uint)Height,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Staging,
            BindFlags = BindFlags.None,
            CPUAccessFlags = CpuAccessFlags.Read,
            MiscFlags = ResourceOptionFlags.None,
        });

        _pixels = new byte[Width * Height * 4];
    }

    public int Width { get; }
    public int Height { get; }
    public string Description => $"DXGI Desktop Duplication on {_display.Name}";

    public CapturedFrame? TryCapture(TimeSpan timeout)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_hasFrame)
        {
            _duplication.ReleaseFrame();
            _hasFrame = false;
        }

        var result = _duplication.AcquireNextFrame(
            (uint)timeout.TotalMilliseconds, out var frameInfo, out IDXGIResource? resource);

        if (result.Failure)
        {
            resource?.Dispose();

            // Nothing on screen changed. The last frame we captured is still what the
            // display shows, so hand it back rather than starving the encoder.
            if (result.Code == WaitTimeout) return LastFrame();

            if (result.Code == AccessLost)
            {
                Rebuild();
                return LastFrame();
            }

            return null;
        }

        _hasFrame = true;

        try
        {
            // AccumulatedFrames of zero means only the mouse cursor moved; the desktop
            // image is unchanged and re-reading it would be wasted bandwidth.
            if (frameInfo.LastPresentTime == 0) return LastFrame();

            using var texture = resource!.QueryInterface<ID3D11Texture2D>();
            _context.CopyResource(_staging, texture);

            var mapped = _context.Map(_staging, 0, Vortice.Direct3D11.MapMode.Read, Vortice.Direct3D11.MapFlags.None);
            try
            {
                CopyRows(mapped.DataPointer, (int)mapped.RowPitch);
            }
            finally
            {
                _context.Unmap(_staging, 0);
            }

            return new CapturedFrame
            {
                Pixels = _pixels,
                Width = Width,
                Height = Height,
                Stride = Width * 4,
                CapturedAt = DateTimeOffset.UtcNow,
            };
        }
        finally
        {
            resource?.Dispose();
        }
    }

    /// <summary>
    /// Copies the mapped surface into the managed buffer. The GPU's row pitch is usually
    /// wider than the visible row, so the padding has to be skipped.
    /// </summary>
    private void CopyRows(IntPtr source, int rowPitch)
    {
        int rowBytes = Width * 4;

        if (rowPitch == rowBytes)
        {
            System.Runtime.InteropServices.Marshal.Copy(source, _pixels, 0, _pixels.Length);
            return;
        }

        for (int y = 0; y < Height; y++)
        {
            System.Runtime.InteropServices.Marshal.Copy(
                source + y * rowPitch, _pixels, y * rowBytes, rowBytes);
        }
    }

    private CapturedFrame LastFrame() => new()
    {
        Pixels = _pixels,
        Width = Width,
        Height = Height,
        Stride = Width * 4,
        CapturedAt = DateTimeOffset.UtcNow,
        IsNewContent = false,
    };

    /// <summary>
    /// Rebuilds the duplication after the desktop pulled it out from under us — a
    /// resolution change, a UAC prompt, or a game taking exclusive full screen.
    /// </summary>
    private void Rebuild()
    {
        try { _duplication.Dispose(); } catch { }

        try
        {
            _duplication = CreateDuplication(_device, _display);
        }
        catch (Exception)
        {
            // The desktop is not available to us right now. The next call will try again.
        }
    }

    private static IDXGIOutputDuplication CreateDuplication(ID3D11Device device, DisplayInfo display)
    {
        using var dxgiDevice = device.QueryInterface<IDXGIDevice>();
        using var adapter = dxgiDevice.GetAdapter();

        for (uint i = 0; ; i++)
        {
            var enumerated = adapter.EnumOutputs(i, out IDXGIOutput? output);
            if (enumerated.Failure || output is null) break;

            using (output)
            {
                // Match on the device name so --display picks the same screen GDI would.
                bool matches = string.Equals(
                    output.Description.DeviceName.TrimEnd('\0'), display.Name,
                    StringComparison.OrdinalIgnoreCase);

                if (!matches) continue;

                using var output1 = output.QueryInterface<IDXGIOutput1>();
                return output1.DuplicateOutput(device);
            }
        }

        throw new InvalidOperationException(
            $"No DXGI output matched \"{display.Name}\" for duplication.");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_hasFrame)
        {
            try { _duplication.ReleaseFrame(); } catch { }
        }

        _duplication.Dispose();
        _staging.Dispose();
        _context.Dispose();
        _device.Dispose();
    }
}
