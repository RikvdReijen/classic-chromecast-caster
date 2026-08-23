using RikRealization.ClassicCast.Media;
using Xunit;

namespace RikRealization.ClassicCast.Protocol.Tests;

/// <summary>
/// The classifier decides which sources get offered as signage and which quietly get a
/// 400 ms jitter buffer instead of 60 ms. Being wrong in either direction is user-visible:
/// too eager and half the source list is mislabelled, too shy and the feature does nothing.
/// </summary>
public class SignageDetectorTests
{
    private static WindowInfo Window(
        string title = "Some Window",
        string process = "someapp",
        string className = "Chrome_WidgetWin_1",
        bool coversDisplay = false,
        bool isForeground = false) =>
        new(new IntPtr(1), title, process, className,
            X: 0, Y: 0, Width: 1920, Height: 1080,
            IsForeground: isForeground, CoversADisplay: coversDisplay);

    [Fact]
    public void An_idle_maximised_window_is_not_signage()
    {
        // The bug this pins: File Explorer, a music player and a chat window all sat
        // maximised, unfocused and static, and all three were labelled signage. General
        // signals describe an idle window exactly as well as a dashboard.
        var verdict = SignageDetector.Classify(
            Window(title: "Software - File Explorer", process: "explorer", coversDisplay: true),
            changeRate: 0.0);

        Assert.False(verdict.IsSignage);
    }

    [Fact]
    public void Static_and_fullscreen_together_are_still_not_enough()
    {
        var verdict = SignageDetector.Classify(Window(coversDisplay: true), changeRate: 0.001);

        Assert.False(verdict.IsSignage);
        Assert.True(verdict.Score < SignageDetector.Threshold);
    }

    [Fact]
    public void A_powerpoint_slideshow_is_signage_on_its_own()
    {
        // The slideshow window class exists only while presenting, so it needs no help.
        var verdict = SignageDetector.Classify(
            Window(title: "PowerPoint Slide Show", className: "screenClass"));

        Assert.True(verdict.IsSignage);
        Assert.Contains("PowerPoint slideshow", verdict.Explanation);
    }

    [Fact]
    public void A_named_dashboard_that_sits_still_is_signage()
    {
        var verdict = SignageDetector.Classify(
            Window(title: "Grafana - Production overview", process: "brave"),
            changeRate: 0.002);

        Assert.True(verdict.IsSignage);
        Assert.Contains("Grafana", verdict.Explanation);
    }

    [Fact]
    public void A_named_dashboard_alone_is_not_quite_enough()
    {
        // A dashboard being resized or scrolled is being worked in, not displayed.
        var verdict = SignageDetector.Classify(
            Window(title: "Grafana - Production overview", isForeground: true),
            changeRate: 0.2);

        Assert.False(verdict.IsSignage);
    }

    [Fact]
    public void Constant_motion_rules_a_window_out_even_when_named_like_a_dashboard()
    {
        // A video playing in a tab titled "dashboard" is not a board on a wall.
        var verdict = SignageDetector.Classify(
            Window(title: "dashboard demo video", coversDisplay: true),
            changeRate: 0.8);

        Assert.False(verdict.IsSignage);
        Assert.Contains("changing constantly", verdict.Explanation);
    }

    [Fact]
    public void A_long_untouched_named_board_is_signage()
    {
        var verdict = SignageDetector.Classify(
            Window(title: "Home Assistant", coversDisplay: true),
            idleFocusTime: TimeSpan.FromMinutes(20),
            changeRate: 0.01);

        Assert.True(verdict.IsSignage);
    }

    [Fact]
    public void The_verdict_explains_itself()
    {
        // The interface shows this, so a classification can be argued with rather than
        // taken on faith.
        var verdict = SignageDetector.Classify(
            Window(title: "Power BI - Sales", coversDisplay: true),
            changeRate: 0.001);

        Assert.NotEmpty(verdict.Reasons);
        Assert.Contains("Power BI", verdict.Explanation);
        Assert.Contains("almost static", verdict.Explanation);
    }

    [Fact]
    public void An_unmeasured_change_rate_does_not_break_classification()
    {
        // Measurement fails for protected or minimised windows; the rest must still work.
        var verdict = SignageDetector.Classify(
            Window(title: "Kibana", coversDisplay: true), changeRate: null);

        Assert.DoesNotContain("static", verdict.Explanation);
        Assert.True(verdict.Score > 0);
    }
}
