namespace RikRealization.ClassicCast.Media;

/// <summary>One captured desktop frame, as top-down BGRA.</summary>
public sealed class CapturedFrame
{
    public required byte[] Pixels { get; init; }
    public required int Width { get; init; }
    public required int Height { get; init; }

    /// <summary>Bytes per row, which may exceed <c>Width * 4</c> because of padding.</summary>
    public required int Stride { get; init; }

    public required DateTimeOffset CapturedAt { get; init; }

    /// <summary>
    /// False when the source had nothing new to give us. The desktop is idle far more
    /// often than not, and re-encoding an unchanged frame wastes bitrate for no benefit.
    /// </summary>
    public bool IsNewContent { get; init; } = true;
}

/// <summary>A source of desktop frames.</summary>
public interface IFrameSource : IDisposable
{
    int Width { get; }
    int Height { get; }

    /// <summary>Human-readable name of the capture method, for diagnostics.</summary>
    string Description { get; }

    /// <summary>
    /// Grabs the next frame, or returns null if none was available within
    /// <paramref name="timeout"/>.
    /// </summary>
    CapturedFrame? TryCapture(TimeSpan timeout);
}

/// <summary>A display that can be captured.</summary>
public sealed record DisplayInfo(int Index, string Name, int X, int Y, int Width, int Height, bool IsPrimary)
{
    public override string ToString() =>
        $"{Name} {Width}x{Height} at {X},{Y}{(IsPrimary ? " (primary)" : "")}";
}
