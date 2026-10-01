using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using WSnip.Core.Capture;
using WSnip.Core.Strings;

namespace WSnip.App.Capture;

/// <summary>A snip mode as menus and pickers present it.</summary>
public sealed record SnipModeOption(SnipMode Mode, string Label, string IconKey, string Description)
{
    public static IReadOnlyList<SnipModeOption> All { get; } =
    [
        new(SnipMode.Rectangle, AppStrings.Rectangle, "IconSnipRectangle", AppStrings.RectangleDescription),
        new(SnipMode.Window, AppStrings.Window, "IconSnipWindow", AppStrings.WindowDescription),
        new(SnipMode.Fullscreen, AppStrings.Fullscreen, "IconSnipFullscreen", AppStrings.FullscreenDescription),
        new(SnipMode.Freeform, AppStrings.Freeform, "IconSnipFreeform", AppStrings.FreeformDescription),
        new(SnipMode.WholeWindow, AppStrings.WholeWindow, "IconSnipWholeWindow", AppStrings.WholeWindowDescription),
    ];

    /// <summary>The glyph from the application's icon resources.</summary>
    public Geometry? Icon => Application.Current?.FindResource(IconKey) as Geometry;

    public static SnipModeOption For(SnipMode mode) => All.FirstOrDefault(o => o.Mode == mode) ?? All[0];
}

/// <summary>A delay before the snip starts, as pickers present it.</summary>
public sealed record DelayOption(int Seconds)
{
    public static IReadOnlyList<DelayOption> All { get; } = [new(0), new(3), new(5), new(10)];

    public string Label => Seconds == 0 ? AppStrings.NoDelay : string.Format(AppStrings.Seconds, Seconds);

    public bool HasDelay => Seconds > 0;

    /// <summary>Compact seconds badge; the menu retains its localized label.</summary>
    public string BadgeText => HasDelay ? $"{Seconds}s" : string.Empty;

    public static DelayOption For(int seconds) => All.FirstOrDefault(o => o.Seconds == seconds) ?? All[0];
}
