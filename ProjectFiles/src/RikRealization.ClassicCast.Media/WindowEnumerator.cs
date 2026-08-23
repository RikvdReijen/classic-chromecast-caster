using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace RikRealization.ClassicCast.Media;

/// <summary>A top-level window that can be cast.</summary>
public sealed record WindowInfo(
    IntPtr Handle,
    string Title,
    string ProcessName,
    string ClassName,
    int X, int Y, int Width, int Height,
    bool IsForeground,
    bool CoversADisplay)
{
    public override string ToString() => $"{Title} — {ProcessName} ({Width}x{Height})";
}

/// <summary>Finds the windows worth offering as a capture source.</summary>
public static class WindowEnumerator
{
    private const int GwlExStyle = -20;
    private const long WsExToolWindow = 0x00000080;
    private const int DwmaCloaked = 14;

    public static IReadOnlyList<WindowInfo> Enumerate()
    {
        DisplayEnumerator.EnsureDpiAwareness();

        var displays = DisplayEnumerator.Enumerate();
        var foreground = GetForegroundWindow();
        var windows = new List<WindowInfo>();

        EnumWindows((handle, _) =>
        {
            if (!IsCapturable(handle, out var title, out var rect)) return true;

            string processName = "";
            try
            {
                GetWindowThreadProcessId(handle, out uint processId);
                processName = Process.GetProcessById((int)processId).ProcessName;
            }
            catch { /* the process may have gone, or be protected */ }

            int width = rect.Right - rect.Left;
            int height = rect.Bottom - rect.Top;

            bool coversDisplay = displays.Any(d =>
                width >= d.Width - 8 && height >= d.Height - 8 &&
                rect.Left <= d.X + 8 && rect.Top <= d.Y + 8);

            windows.Add(new WindowInfo(
                handle, title, processName, GetClass(handle),
                rect.Left, rect.Top, width, height,
                IsForeground: handle == foreground,
                CoversADisplay: coversDisplay));

            return true;
        }, IntPtr.Zero);

        return windows
            .OrderByDescending(w => w.IsForeground)
            .ThenByDescending(w => (long)w.Width * w.Height)
            .ToList();
    }

    public static WindowInfo? FromHandle(IntPtr handle) =>
        Enumerate().FirstOrDefault(w => w.Handle == handle);

    private static bool IsCapturable(IntPtr handle, out string title, out Rect rect)
    {
        title = "";
        rect = default;

        if (!IsWindowVisible(handle)) return false;
        if (IsIconic(handle)) return false;

        // Tool windows are palettes and tooltips, never something a user means to cast.
        if ((GetWindowLongPtr(handle, GwlExStyle).ToInt64() & WsExToolWindow) != 0) return false;

        // A cloaked window is one the shell is hiding: a UWP app suspended on another
        // virtual desktop, for instance. It is visible by the old rules and shows nothing.
        if (DwmGetWindowAttribute(handle, DwmaCloaked, out int cloaked, sizeof(int)) == 0 && cloaked != 0)
            return false;

        int length = GetWindowTextLength(handle);
        if (length == 0) return false;

        var buffer = new StringBuilder(length + 1);
        GetWindowText(handle, buffer, buffer.Capacity);
        title = buffer.ToString();
        if (title.Length == 0) return false;

        if (!GetWindowRect(handle, out rect)) return false;

        // Anything this small is a widget, not a window worth casting.
        return rect.Right - rect.Left >= 160 && rect.Bottom - rect.Top >= 120;
    }

    private static string GetClass(IntPtr handle)
    {
        var buffer = new StringBuilder(256);
        return GetClassName(handle, buffer, buffer.Capacity) > 0 ? buffer.ToString() : "";
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Rect { public int Left, Top, Right, Bottom; }

    private delegate bool EnumWindowsProc(IntPtr handle, IntPtr param);

    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr param);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr handle);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr handle);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern int GetWindowTextLength(IntPtr handle);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr handle, StringBuilder text, int count);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr handle, StringBuilder name, int count);
    [DllImport("user32.dll")] internal static extern bool GetWindowRect(IntPtr handle, out Rect rect);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr handle, out uint processId);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr handle, int index);
    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr handle, int attribute, out int value, int size);
}
