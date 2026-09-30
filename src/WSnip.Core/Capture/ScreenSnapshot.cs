using WSnip.Core.Imaging;

namespace WSnip.Core.Capture;

/// <summary>How a display composes and presents color when it was captured.</summary>
/// <param name="HdrActive">The display is in HDR mode, so one scRGB unit is 80 nits.</param>
/// <param name="AdvancedColor">The compositor blends in FP16 (HDR, or SDR with automatic color management).</param>
/// <param name="SdrWhiteNits">SDR content brightness in HDR mode; the nominal 80 nits otherwise.</param>
/// <param name="MaxLuminanceNits">Reported peak luminance of the panel, when known.</param>
/// <param name="MinLuminanceNits">Reported black level of the panel, when known.</param>
public sealed record DisplayColorInfo(
    bool HdrActive,
    bool AdvancedColor,
    double SdrWhiteNits,
    double? MaxLuminanceNits,
    double? MinLuminanceNits)
{
    public static DisplayColorInfo Sdr { get; } = new(false, false, ColorMath.ScRgbUnitNits, null, null);

    /// <summary>scRGB units that correspond to diffuse (SDR) white in the captured pixels.</summary>
    public double WhiteScale => HdrActive && SdrWhiteNits > 0 ? SdrWhiteNits / ColorMath.ScRgbUnitNits : 1;

    /// <summary>Peak luminance relative to SDR white, or 1 when the display is not in HDR mode.</summary>
    public double HeadroomRatio => HdrActive && MaxLuminanceNits is > 0 and var peak && SdrWhiteNits > 0
        ? Math.Max(1, peak / SdrWhiteNits)
        : 1;
}

/// <summary>One display captured in its native pixels.</summary>
public sealed class MonitorCapture
{
    public required string DeviceName { get; init; }

    public string? FriendlyName { get; init; }

    /// <summary>Position in physical pixels on the virtual desktop.</summary>
    public required PixelRect Bounds { get; init; }

    /// <summary>
    /// The display in the coordinates the windowing system gives screens, where those are not the
    /// physical pixels of <see cref="Bounds"/>: Wayland lays displays out in logical pixels.
    /// </summary>
    public PixelRect? LogicalBounds { get; init; }

    /// <summary>Display scaling relative to 96 DPI.</summary>
    public double Scaling { get; init; } = 1;

    public bool IsPrimary { get; init; }

    public required DisplayColorInfo Color { get; init; }

    /// <summary>Composition pixels as captured: scRGB, where <see cref="DisplayColorInfo.WhiteScale"/> is SDR white.</summary>
    public required HdrImage Image { get; init; }
}

/// <summary>A top-level window visible when the screen was captured.</summary>
/// <param name="Handle">Native handle, meaningful only to the platform that produced it.</param>
/// <param name="Bounds">Visible frame in physical virtual-desktop pixels.</param>
public sealed record CapturedWindow(nint Handle, string Title, string? ProcessName, PixelRect Bounds);

/// <summary>Everything captured at the moment a snip starts.</summary>
public sealed class ScreenSnapshot
{
    public required IReadOnlyList<MonitorCapture> Monitors { get; init; }

    /// <summary>Visible windows from the topmost to the bottommost.</summary>
    public IReadOnlyList<CapturedWindow> Windows { get; init; } = [];

    public DateTimeOffset CapturedAt { get; init; } = DateTimeOffset.Now;

    public PixelRect VirtualBounds => Monitors.Aggregate(default(PixelRect), (bounds, monitor) => bounds.Union(monitor.Bounds));

    public MonitorCapture? MonitorAt(int x, int y) =>
        Monitors.FirstOrDefault(monitor => monitor.Bounds.Contains(x, y));

    public CapturedWindow? WindowAt(int x, int y) =>
        Windows.FirstOrDefault(window => window.Bounds.Contains(x, y));
}

/// <summary>A single window as the compositor holds it, independent of what covers it on screen.</summary>
public sealed class WindowCapture
{
    public required CapturedWindow Window { get; init; }

    /// <summary>The display the window is on, whose SDR white the pixels are relative to.</summary>
    public required DisplayColorInfo Color { get; init; }

    /// <summary>Composition pixels: scRGB with straight alpha, where <see cref="DisplayColorInfo.WhiteScale"/> is SDR white.</summary>
    public required HdrImage Image { get; init; }

    public DateTimeOffset CapturedAt { get; init; } = DateTimeOffset.Now;
}
