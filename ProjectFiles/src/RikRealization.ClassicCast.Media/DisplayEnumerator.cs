using System.Runtime.InteropServices;

namespace RikRealization.ClassicCast.Media;

/// <summary>Finds the displays available to capture.</summary>
public static class DisplayEnumerator
{
    /// <summary>
    /// Opts this process into per-monitor DPI awareness. Without it Windows reports and
    /// hands back virtualised coordinates on a scaled display, so a 3840x2160 screen at
    /// 150% would be captured as a blurry 2560x1440.
    /// </summary>
    public static void EnsureDpiAwareness()
    {
        try
        {
            // -4 is DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2.
            if (SetProcessDpiAwarenessContext(new IntPtr(-4))) return;
        }
        catch (EntryPointNotFoundException) { /* pre-1703 Windows */ }

        try { SetProcessDPIAware(); }
        catch (EntryPointNotFoundException) { }
    }

    public static IReadOnlyList<DisplayInfo> Enumerate()
    {
        EnsureDpiAwareness();

        var displays = new List<DisplayInfo>();

        bool Callback(IntPtr monitor, IntPtr hdc, ref Rect rect, IntPtr data)
        {
            var info = new MonitorInfoEx { Size = Marshal.SizeOf<MonitorInfoEx>() };
            if (!GetMonitorInfo(monitor, ref info)) return true;

            displays.Add(new DisplayInfo(
                Index: displays.Count,
                Name: string.IsNullOrWhiteSpace(info.DeviceName)
                    ? $"Display {displays.Count + 1}"
                    : info.DeviceName.TrimEnd('\0'),
                X: info.Monitor.Left,
                Y: info.Monitor.Top,
                Width: info.Monitor.Right - info.Monitor.Left,
                Height: info.Monitor.Bottom - info.Monitor.Top,
                IsPrimary: (info.Flags & 1) != 0));

            return true;
        }

        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, Callback, IntPtr.Zero);

        // Primary first: it is what a user means by "my screen" almost every time.
        return displays
            .OrderByDescending(d => d.IsPrimary)
            .Select((d, i) => d with { Index = i })
            .ToList();
    }

    // ---- interop -------------------------------------------------------------------

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MonitorInfoEx
    {
        public int Size;
        public Rect Monitor;
        public Rect Work;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string DeviceName;
    }

    private delegate bool MonitorEnumProc(IntPtr monitor, IntPtr hdc, ref Rect rect, IntPtr data);

    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc callback, IntPtr data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfoEx info);

    [DllImport("user32.dll")] private static extern bool SetProcessDpiAwarenessContext(IntPtr context);
    [DllImport("user32.dll")] private static extern bool SetProcessDPIAware();
}
