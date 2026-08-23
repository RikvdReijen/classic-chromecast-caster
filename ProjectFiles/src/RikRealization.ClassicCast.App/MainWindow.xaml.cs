using System.Collections.ObjectModel;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using RikRealization.ClassicCast.Media;
using RikRealization.ClassicCast.Protocol.Discovery;
using Windows.Graphics;

namespace RikRealization.ClassicCast.App;

/// <summary>Segoe MDL2 Assets code points, named so the intent is readable.</summary>
internal static class Glyphs
{
    public const string Monitor = "";
    public const string Window = "";
    public const string Volume = "";
    public const string Chevron = "";
    public const string Check = "";
}

/// <summary>A row in the "From" list.</summary>
public sealed class SourceItem
{
    public required string Glyph { get; init; }
    public required string Label { get; init; }
    public string Trailing { get; init; } = "";
    public Brush TrailingBrush { get; init; } = MainWindow.FaintBrush;

    /// <summary>Null for entries that are shown but cannot be cast.</summary>
    public CaptureTarget? Target { get; init; }

    /// <summary>Set when this looks like a board rather than something being worked in.</summary>
    public SignageVerdict? Signage { get; init; }

    public bool IsAvailable => Target is not null;
}

/// <summary>A row in the "To" list.</summary>
public sealed class DeviceItem
{
    public required string Name { get; init; }
    public required string Detail { get; init; }
    public required CastDevice Device { get; init; }

    public string StatusGlyph { get; set; } = Glyphs.Chevron;
    public Brush AccentColor { get; set; } = new SolidColorBrush(Colors.Gray);
}

public sealed partial class MainWindow : Window
{
    internal static readonly SolidColorBrush BrandBrush =
        new(Windows.UI.Color.FromArgb(0xFF, 0x43, 0xBC, 0xF7));
    internal static readonly SolidColorBrush IdleBrush =
        new(Windows.UI.Color.FromArgb(0xFF, 0x98, 0xA3, 0xAF));
    internal static readonly SolidColorBrush FaintBrush =
        new(Windows.UI.Color.FromArgb(0xFF, 0x6B, 0x76, 0x84));

    private readonly ObservableCollection<SourceItem> _sources = new();
    private readonly ObservableCollection<DeviceItem> _devices = new();
    private readonly DispatcherTimer _statsTimer = new();

    private MirroringSession? _session;
    private CancellationTokenSource? _discovery;

    public MainWindow()
    {
        InitializeComponent();

        Title = "Classic Chromecast caster";
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(TitleBar);

        // Roughly the proportions of a casting utility: tall and narrow, so it can sit
        // beside whatever is being cast rather than covering it. AppWindow works in
        // physical pixels, so the size is scaled to keep the same apparent dimensions on
        // a display running at anything other than 100%.
        double scale = GetDpiForWindow(
            WinRT.Interop.WindowNative.GetWindowHandle(this)) / 96.0;
        AppWindow.Resize(new SizeInt32((int)(390 * scale), (int)(680 * scale)));
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "app.ico"));

        SourceList.ItemsSource = _sources;
        DeviceList.ItemsSource = _devices;

        _statsTimer.Interval = TimeSpan.FromMilliseconds(500);
        _statsTimer.Tick += (_, _) => UpdateStats();

        _ = LoadSourcesAsync();
        StartDiscovery();
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    private async Task LoadSourcesAsync()
    {
        var items = new List<SourceItem>();

        foreach (var display in DisplayEnumerator.Enumerate())
        {
            items.Add(new SourceItem
            {
                Glyph = Glyphs.Monitor,
                Label = $"Display {display.Index + 1} ({display.Width}x{display.Height})",
                Trailing = display.IsPrimary ? "primary" : "",
                Target = CaptureTarget.FromDisplay(display),
            });
        }

        items.Add(new SourceItem
        {
            Glyph = Glyphs.Volume,
            Label = "Audio Only",
            Target = CaptureTarget.AudioOnly(),
        });

        // Extending the desktop means presenting Windows with a monitor that is not there,
        // which takes an indirect display driver: a signed kernel-mode component, not
        // something an application can do for itself. Listed so its absence is explained
        // rather than merely missing.
        items.Add(new SourceItem
        {
            Glyph = Glyphs.Monitor,
            Label = "Extend Desktop",
            Trailing = "needs a display driver",
        });

        _sources.Clear();
        foreach (var item in items) _sources.Add(item);
        if (_sources.Count > 0) SourceList.SelectedIndex = 0;

        await AddWindowSourcesAsync();
    }

    /// <summary>
    /// Adds capturable windows, boards first. Measuring how much each one changes takes a
    /// moment, so this runs after the list is already usable rather than holding it up.
    /// </summary>
    private async Task AddWindowSourcesAsync()
    {
        List<WindowInfo> windows;
        try { windows = WindowEnumerator.Enumerate().Take(12).ToList(); }
        catch { return; }

        // All measured at once: they share a single 400 ms wait rather than queueing.
        var measurements = windows.ToDictionary(
            w => w.Handle,
            w => SignageDetector.MeasureChangeRateAsync(w, TimeSpan.FromMilliseconds(400)));

        await Task.WhenAll(measurements.Values);

        var verdicts = windows.ToDictionary(
            w => w.Handle,
            w => SignageDetector.Classify(w, changeRate: measurements[w.Handle].Result));

        foreach (var window in windows.OrderByDescending(w => verdicts[w.Handle].Score))
        {
            var verdict = verdicts[window.Handle];

            _sources.Add(new SourceItem
            {
                Glyph = Glyphs.Window,
                Label = window.Title.Length > 40 ? window.Title[..39] + "…" : window.Title,
                Trailing = verdict.IsSignage ? "signage" : window.ProcessName,
                TrailingBrush = verdict.IsSignage ? BrandBrush : FaintBrush,
                Target = CaptureTarget.FromWindow(window),
                Signage = verdict,
            });
        }
    }

    private async void StartDiscovery()
    {
        _discovery?.Cancel();
        _discovery = new CancellationTokenSource();

        DiscoveryHintText.Text = "Searching for devices…";

        try
        {
            var found = await CastDiscovery.DiscoverAsync(TimeSpan.FromSeconds(5), _discovery.Token);

            string? selectedId = (DeviceList.SelectedItem as DeviceItem)?.Device.Id;
            _devices.Clear();

            foreach (var device in found)
            {
                _devices.Add(new DeviceItem
                {
                    Name = device.FriendlyName,
                    Detail = $"{device.Model} · {device.Address}",
                    Device = device,
                    StatusGlyph = Glyphs.Chevron,
                    AccentColor = IdleBrush,
                });
            }

            DiscoveryHintText.Text = _devices.Count switch
            {
                0 => "No devices found. They must be on this network, and a VPN can block discovery.",
                1 => "1 device found.",
                _ => $"{_devices.Count} devices found.",
            };

            if (selectedId is not null)
                DeviceList.SelectedItem = _devices.FirstOrDefault(d => d.Device.Id == selectedId);
            else if (_devices.Count > 0)
                DeviceList.SelectedIndex = 0;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            DiscoveryHintText.Text = $"Discovery failed: {ex.Message}";
        }
    }

    private void OnRefreshClicked(object sender, RoutedEventArgs e)
    {
        StartDiscovery();
        _ = LoadSourcesAsync();
    }

    private void OnSourceSelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateHeader();
    private void OnDeviceSelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateHeader();

    private void UpdateHeader()
    {
        var source = SourceList.SelectedItem as SourceItem;
        var device = DeviceList.SelectedItem as DeviceItem;

        ActiveSourceText.Text = source?.Label ?? "No source selected";

        // A signage source says why it was classified that way, so the guess stays
        // inspectable instead of being magic.
        ActiveTargetText.Text = source?.Signage is { IsSignage: true } verdict
            ? $"{device?.Name ?? "No device"} · {verdict.Explanation}"
            : device?.Name ?? "No device selected";

        CastButton.IsEnabled = _session is null && source?.IsAvailable == true && device is not null;
    }

    private async void OnCastClicked(object sender, RoutedEventArgs e)
    {
        if (_session is not null) return;

        if (SourceList.SelectedItem is not SourceItem { Target: { } target } source ||
            DeviceList.SelectedItem is not DeviceItem device)
            return;

        CastButton.IsEnabled = false;
        StatusText.Text = $"Connecting to {device.Name}…";

        // A board does not care about latency and does care about looking steady, so it
        // gets the smoothness-first profile without the user having to know that.
        var options = source.Signage is { IsSignage: true }
            ? MirroringOptions.Signage
            : new MirroringOptions();

        try
        {
            _session = await MirroringSession.StartAsync(device.Device, target, options);
            _session.Faulted += reason => DispatcherQueue.TryEnqueue(() => OnSessionFaulted(reason));

            device.StatusGlyph = Glyphs.Check;
            device.AccentColor = BrandBrush;
            RefreshDeviceRow(device);

            StopButton.IsEnabled = true;
            StatusText.Text = $"Casting {source.Label}";
            _statsTimer.Start();
        }
        catch (Exception ex)
        {
            _session = null;
            StatusText.Text = $"Could not start: {ex.Message}";
            CastButton.IsEnabled = true;
        }
    }

    private async void OnStopClicked(object sender, RoutedEventArgs e) => await StopAsync("Idle");

    private async void OnSessionFaulted(string reason) => await StopAsync($"Casting stopped: {reason}");

    private async Task StopAsync(string status)
    {
        _statsTimer.Stop();
        StopButton.IsEnabled = false;

        if (_session is not null)
        {
            var session = _session;
            _session = null;
            try { await session.DisposeAsync(); } catch { }
        }

        foreach (var device in _devices)
        {
            device.StatusGlyph = Glyphs.Chevron;
            device.AccentColor = IdleBrush;
            RefreshDeviceRow(device);
        }

        StatsText.Text = "";
        StatusText.Text = status;
        UpdateHeader();
    }

    /// <summary>
    /// The item classes are plain objects rather than observable ones, so a changed row is
    /// re-inserted to make the list pick it up. With a handful of devices that is cheaper
    /// than wiring change notification through everything.
    /// </summary>
    private void RefreshDeviceRow(DeviceItem device)
    {
        int index = _devices.IndexOf(device);
        if (index < 0) return;

        bool wasSelected = ReferenceEquals(DeviceList.SelectedItem, device);
        _devices[index] = device;
        if (wasSelected) DeviceList.SelectedIndex = index;
    }

    private void UpdateStats()
    {
        if (_session is null) return;

        var stats = _session.Snapshot();

        StatsText.Text = _session.Target.IsAudioOnly
            ? $"{stats.KilobitsPerSecond:F0} kbps · {stats.PlayoutDelay.TotalMilliseconds:F0} ms"
            : $"{stats.FramesPerSecond:F0} fps · {stats.KilobitsPerSecond / 1000:F1} Mbps · " +
              $"{stats.PlayoutDelay.TotalMilliseconds:F0} ms";

        StatusText.Text = stats.AudioActive
            ? $"Casting with audio · {stats.Encoder}"
            : $"Casting · {stats.Encoder}";
    }

    private void OnManualConnectClicked(object sender, RoutedEventArgs e)
    {
        string wanted = ManualAddressBox.Text.Trim();
        if (wanted.Length == 0) return;

        var match = _devices.FirstOrDefault(d =>
            d.Device.Address.ToString() == wanted ||
            d.Name.Contains(wanted, StringComparison.OrdinalIgnoreCase));

        if (match is not null)
        {
            DeviceList.SelectedItem = match;
            StatusText.Text = $"Selected {match.Name}";
        }
        else
        {
            StatusText.Text = $"No discovered device matches “{wanted}”";
        }
    }
}
