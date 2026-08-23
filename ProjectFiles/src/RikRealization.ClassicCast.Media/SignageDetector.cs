using System.Runtime.InteropServices;

namespace RikRealization.ClassicCast.Media;

/// <summary>How signage-like a window looks, and why.</summary>
public sealed record SignageVerdict(bool IsSignage, int Score, IReadOnlyList<string> Reasons)
{
    public string Explanation => Reasons.Count == 0 ? "" : string.Join(", ", Reasons);
}

/// <summary>
/// Recognises windows meant to be looked at rather than worked in: slide decks,
/// dashboards, status boards, kiosk pages.
///
/// The point is not cleverness for its own sake. A dashboard does not care about a few
/// hundred milliseconds of delay, and buying smoothness with buffer is a straight win
/// there, while the same trade would ruin an interactive desktop. Classifying the source
/// is what lets the two be treated differently without asking the user to know the
/// difference.
///
/// Weighting these took a correction. The general signals - a window that fills a screen,
/// nobody typing into it, pixels that barely move - describe an idle window just as well as
/// a dashboard, and on a real desktop that is most of them. So no combination of general
/// signals reaches the threshold by itself; something has to positively identify the window
/// as presentational, whether that is a slideshow window class or a recognisable title.
/// </summary>
public static class SignageDetector
{
    /// <summary>Total score at which a window is offered as signage.</summary>
    public const int Threshold = 50;

    /// <summary>
    /// Window classes that mean "presenting" unambiguously. PowerPoint's slideshow window
    /// is the clearest example: it exists only during a presentation.
    /// </summary>
    private static readonly (string ClassName, string Reason)[] PresentationClasses =
    {
        ("screenClass", "PowerPoint slideshow"),
        ("PPTFrameClass", "PowerPoint"),
        ("SjPresentationClass", "Impress presentation"),
    };

    private static readonly (string Needle, string Reason)[] TitleHints =
    {
        ("grafana", "Grafana"),
        ("power bi", "Power BI"),
        ("tableau", "Tableau"),
        ("datadog", "Datadog"),
        ("kibana", "Kibana"),
        ("home assistant", "Home Assistant"),
        ("google slides", "Google Slides"),
        ("canva", "Canva"),
        ("prezi", "Prezi"),
        ("dashboard", "titled a dashboard"),
        ("kiosk", "kiosk"),
    };

    /// <summary>
    /// Classifies a window. <paramref name="changeRate"/> is the fraction of sampled pixels
    /// that changed between two looks, or null when it was not measured.
    /// </summary>
    public static SignageVerdict Classify(
        WindowInfo window, TimeSpan? idleFocusTime = null, double? changeRate = null)
    {
        int score = 0;
        var reasons = new List<string>();

        foreach (var (className, reason) in PresentationClasses)
        {
            if (!window.ClassName.Equals(className, StringComparison.OrdinalIgnoreCase)) continue;
            score += 60;
            reasons.Add(reason);
            break;
        }

        // Weak on its own. Measured against a real desktop, plenty of ordinary windows
        // sit maximised and unfocused - a file manager, a music player, a chat window -
        // and calling those signage flags half the list, which defeats the purpose.
        if (window.CoversADisplay && !window.IsForeground)
        {
            score += 15;
            reasons.Add("fills a display, not in focus");
        }
        else if (window.CoversADisplay)
        {
            score += 10;
            reasons.Add("fills a display");
        }

        // The most general signal there is: a board repaints a few tiles a minute, while a
        // video or a game never stops. It needs no knowledge of the application at all.
        if (changeRate is { } rate)
        {
            // Also weak alone: every window nobody is using is static. It earns its keep
            // in combination, and mainly by ruling things out.
            if (rate < 0.005)
            {
                score += 20;
                reasons.Add("almost static");
            }
            else if (rate < 0.05)
            {
                score += 15;
                reasons.Add("changes slowly");
            }
            else if (rate > 0.4)
            {
                score -= 40;
                reasons.Add("changing constantly");
            }
        }

        if (idleFocusTime is { } idle && idle > TimeSpan.FromSeconds(60))
        {
            score += 20;
            reasons.Add($"untouched for {idle.TotalMinutes:F0} min");
        }

        // Brittle and localised, but when it does match it is close to conclusive: a
        // window actually titled Grafana is a dashboard. Weighted accordingly, and enough
        // on its own to carry a static window over the line.
        string haystack = $"{window.Title} {window.ProcessName}".ToLowerInvariant();
        foreach (var (needle, reason) in TitleHints)
        {
            if (!haystack.Contains(needle, StringComparison.Ordinal)) continue;
            score += 30;
            reasons.Add(reason);
            break;
        }

        return new SignageVerdict(score >= Threshold, score, reasons);
    }

    /// <summary>
    /// Measures how much a window's pixels change over <paramref name="interval"/>, as a
    /// fraction between 0 and 1.
    ///
    /// Deliberately cheap: a small grid of samples rather than whole frames, because this
    /// runs for every candidate window each time the source list is opened, and the answer
    /// only needs to separate "a dashboard" from "a video".
    /// </summary>
    public static async Task<double?> MeasureChangeRateAsync(
        WindowInfo window, TimeSpan interval, CancellationToken ct = default)
    {
        try
        {
            var before = SampleWindow(window.Handle);
            if (before is null) return null;

            await Task.Delay(interval, ct);

            var after = SampleWindow(window.Handle);
            if (after is null || after.Length != before.Length) return null;

            int changed = 0;
            for (int i = 0; i < before.Length; i++)
            {
                // A tolerance, so gradient dithering and cursor blink do not read as motion.
                if (Math.Abs(before[i] - after[i]) > 8) changed++;
            }

            return (double)changed / before.Length;
        }
        catch (OperationCanceledException) { return null; }
        catch { return null; }
    }

    /// <summary>Grabs a small greyscale grid of the window as it appears on screen.</summary>
    private static byte[]? SampleWindow(IntPtr handle)
    {
        const int grid = 32;

        if (!WindowEnumerator.GetWindowRect(handle, out var rect)) return null;
        int width = rect.Right - rect.Left;
        int height = rect.Bottom - rect.Top;
        if (width < 8 || height < 8) return null;

        IntPtr screenDc = GetDC(IntPtr.Zero);
        IntPtr memoryDc = CreateCompatibleDC(screenDc);
        IntPtr bitmap = CreateCompatibleBitmap(screenDc, grid, grid);
        IntPtr previous = SelectObject(memoryDc, bitmap);

        try
        {
            SetStretchBltMode(memoryDc, 4);   // HALFTONE, so the shrink averages
            if (!StretchBlt(memoryDc, 0, 0, grid, grid,
                            screenDc, rect.Left, rect.Top, width, height, 0x00CC0020))
                return null;

            var header = new BitmapInfoHeader
            {
                Size = (uint)Marshal.SizeOf<BitmapInfoHeader>(),
                Width = grid,
                Height = -grid,
                Planes = 1,
                BitCount = 32,
                Compression = 0,
            };

            var pixels = new byte[grid * grid * 4];
            if (GetDIBits(memoryDc, bitmap, 0, grid, pixels, ref header, 0) == 0) return null;

            var samples = new byte[grid * grid];
            for (int i = 0; i < samples.Length; i++)
            {
                int p = i * 4;
                samples[i] = (byte)((pixels[p] + pixels[p + 1] + pixels[p + 2]) / 3);
            }

            return samples;
        }
        finally
        {
            SelectObject(memoryDc, previous);
            DeleteObject(bitmap);
            DeleteDC(memoryDc);
            ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfoHeader
    {
        public uint Size;
        public int Width, Height;
        public ushort Planes, BitCount;
        public uint Compression, SizeImage;
        public int XPelsPerMeter, YPelsPerMeter;
        public uint ClrUsed, ClrImportant;
    }

    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int w, int h);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")] private static extern int SetStretchBltMode(IntPtr hdc, int mode);
    [DllImport("gdi32.dll")]
    private static extern bool StretchBlt(IntPtr dest, int dx, int dy, int dw, int dh,
                                          IntPtr src, int sx, int sy, int sw, int sh, int rop);
    [DllImport("gdi32.dll")]
    private static extern int GetDIBits(IntPtr hdc, IntPtr bitmap, uint start, uint lines,
                                        byte[] bits, ref BitmapInfoHeader info, uint usage);
}
