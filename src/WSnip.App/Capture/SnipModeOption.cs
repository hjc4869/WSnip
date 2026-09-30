using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using WSnip.Core.Capture;

namespace WSnip.App.Capture;

/// <summary>A snip mode as menus and pickers present it.</summary>
public sealed record SnipModeOption(SnipMode Mode, string Label, string IconKey, string Description)
{
    public static IReadOnlyList<SnipModeOption> All { get; } =
    [
        new(SnipMode.Rectangle, "Rectangle", "IconSnipRectangle", "Drag a rectangle on the frozen screen"),
        new(SnipMode.Window, "Window", "IconSnipWindow", "Pick a window on the frozen screen, as it is visible"),
        new(SnipMode.Fullscreen, "Full screen", "IconSnipFullscreen", "Pick a display on the frozen screen"),
        new(SnipMode.Freeform, "Freeform", "IconSnipFreeform", "Draw any shape on the frozen screen"),
        new(SnipMode.WholeWindow, "Whole window", "IconSnipWholeWindow",
            "Click a window while the screen stays live; it is captured whole, even where covered, with its transparency"),
    ];

    /// <summary>The glyph from the application's icon resources.</summary>
    public Geometry? Icon => Application.Current?.FindResource(IconKey) as Geometry;

    public static SnipModeOption For(SnipMode mode) => All.FirstOrDefault(o => o.Mode == mode) ?? All[0];
}

/// <summary>A delay before the snip starts, as pickers present it.</summary>
public sealed record DelayOption(int Seconds)
{
    public static IReadOnlyList<DelayOption> All { get; } = [new(0), new(3), new(5), new(10)];

    public string Label => Seconds == 0 ? "No delay" : $"{Seconds} seconds";

    public static DelayOption For(int seconds) => All.FirstOrDefault(o => o.Seconds == seconds) ?? All[0];
}
