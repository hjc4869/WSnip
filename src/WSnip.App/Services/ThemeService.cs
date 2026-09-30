using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Styling;
using Avalonia.Threading;
using WSnip.Core.Platform;
using WSnip.Core.Settings;

namespace WSnip.App.Services;

/// <summary>
/// Applies the app palette: neutral surfaces, an accent derived from the desktop accent color, and
/// translucent chrome over the Mica Alt backdrop on Windows 11.
/// </summary>
public sealed class ThemeService
{
    private static readonly Color FallbackAccent = Color.FromRgb(0x00, 0x78, 0xD4);
    private readonly IWindowIntegrationService windows;
    private readonly List<Window> attached = [];
    private ResourceDictionary? installed;
    private ThemePreference preference = ThemePreference.System;
    private bool micaEnabled = true;

    public ThemeService(IWindowIntegrationService windows)
    {
        this.windows = windows;
        if (Application.Current?.PlatformSettings is { } platform)
            platform.ColorValuesChanged += (_, _) => Dispatcher.UIThread.Post(Apply);
    }

    public bool IsMicaSupported => windows.IsMicaSupported;

    public bool IsMicaActive => micaEnabled && windows.IsMicaSupported;

    public void Configure(ThemePreference theme, bool mica)
    {
        preference = theme;
        micaEnabled = mica;
        Apply();
    }

    /// <summary>Keeps a window's backdrop and caption in step with the theme.</summary>
    public void Attach(Window window)
    {
        attached.Add(window);
        window.Opened += (_, _) => ApplyWindow(window);
        window.Closed += (_, _) => attached.Remove(window);
        window.ActualThemeVariantChanged += (_, _) => ApplyWindow(window);
        ApplyWindow(window);
    }

    private void Apply()
    {
        if (Application.Current is not { } app)
            return;

        app.RequestedThemeVariant = preference switch
        {
            ThemePreference.Light => ThemeVariant.Light,
            ThemePreference.Dark => ThemeVariant.Dark,
            _ => ThemeVariant.Default,
        };

        Color accent = app.PlatformSettings?.GetColorValues().AccentColor1 ?? FallbackAccent;
        var palette = new ResourceDictionary();
        palette["AppIsMicaActive"] = IsMicaActive;
        ResourceDictionary light = Variant(accent, dark: false);
        palette.ThemeDictionaries[ThemeVariant.Default] = light;
        palette.ThemeDictionaries[ThemeVariant.Light] = Variant(accent, dark: false);
        palette.ThemeDictionaries[ThemeVariant.Dark] = Variant(accent, dark: true);
        if (installed is not null)
            app.Resources.MergedDictionaries.Remove(installed);
        app.Resources.MergedDictionaries.Add(palette);
        installed = palette;

        foreach (Window window in attached.ToArray())
            ApplyWindow(window);
    }

    // Windows paint AppShellBackgroundBrush, which turns transparent under Mica so the material shows.
    private void ApplyWindow(Window window)
    {
        bool dark = window.ActualThemeVariant == ThemeVariant.Dark;
        if (IsMicaActive)
        {
            window.TransparencyBackgroundFallback = Brushes.Transparent;
            window.TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
        }
        else
        {
            window.TransparencyLevelHint = [WindowTransparencyLevel.None];
            window.ClearValue(TopLevel.TransparencyBackgroundFallbackProperty);
        }

        if (window.TryGetPlatformHandle() is { } handle)
        {
            windows.SetMica(handle.Handle, IsMicaActive, dark);
            windows.SetDarkFrame(handle.Handle, dark);
        }
    }

    private ResourceDictionary Variant(Color systemAccent, bool dark)
    {
        HslColor hsl = systemAccent.ToHsl();
        Color accent = new HslColor(1, hsl.H, hsl.S, Math.Clamp(hsl.L, dark ? 0.62 : 0.24, dark ? 0.80 : 0.42)).ToRgb();
        Color selection = dark
            ? new HslColor(1, hsl.H, Math.Min(hsl.S, 0.44), 0.24).ToRgb()
            : new HslColor(1, hsl.H, Math.Min(hsl.S, 0.50), 0.88).ToRgb();
        Color tint = dark ? new HslColor(1, hsl.H, hsl.S, 0.16).ToRgb() : accent;
        double amount = dark ? 0.11 : 0.033;

        Color page = Mix(dark ? 0x161616 : 0xF6F6F6, tint, amount);
        Color panel = Mix(dark ? 0x1E1E1E : 0xFFFFFF, tint, amount);
        Color nav = Mix(dark ? 0x202020 : 0xEFEFEF, tint, amount);
        Color canvas = Mix(dark ? 0x111111 : 0xE9E9E9, tint, amount);

        var d = new ResourceDictionary
        {
            ["AppAccentColor"] = accent,
            ["AppAccentBrush"] = new SolidColorBrush(accent),
            ["AppOnAccentBrush"] = new SolidColorBrush(dark ? Colors.Black : Colors.White),
            ["AppSelectionBrush"] = new SolidColorBrush(selection),
            ["AppPageBackgroundBrush"] = new SolidColorBrush(page),
            ["AppPanelBackgroundBrush"] = new SolidColorBrush(panel),
            ["AppNavBackgroundBrush"] = new SolidColorBrush(nav),
            ["AppFlyoutBrush"] = new SolidColorBrush(dark ? Color.FromRgb(0x2B, 0x2B, 0x2B) : Color.FromRgb(0xF9, 0xF9, 0xF9)),
            ["SystemAccentColor"] = accent,
        };

        (Color dark1, Color dark2, Color dark3, Color light1, Color light2, Color light3) = AccentShades(accent);
        d["SystemAccentColorDark1"] = dark1;
        d["SystemAccentColorDark2"] = dark2;
        d["SystemAccentColorDark3"] = dark3;
        d["SystemAccentColorLight1"] = light1;
        d["SystemAccentColorLight2"] = light2;
        d["SystemAccentColorLight3"] = light3;

        if (!IsMicaActive)
        {
            d["AppShellBackgroundBrush"] = new SolidColorBrush(page);
            d["AppToolbarSurfaceBrush"] = new SolidColorBrush(nav);
            d["AppContentLayerBrush"] = new SolidColorBrush(panel);
            d["AppCanvasBackgroundBrush"] = new SolidColorBrush(canvas);
            d["AppChromeDividerBrush"] = new SolidColorBrush(dark ? Color.FromRgb(0x32, 0x32, 0x32) : Color.FromRgb(0xE2, 0xE2, 0xE2));
            d["AppSubtleHoverBrush"] = new SolidColorBrush(dark ? Color.FromRgb(0x2A, 0x2A, 0x2A) : Color.FromRgb(0xE6, 0xE6, 0xE6));
            d["AppSubtlePressedBrush"] = new SolidColorBrush(dark ? Color.FromRgb(0x33, 0x33, 0x33) : Color.FromRgb(0xDA, 0xDA, 0xDA));
            return d;
        }

        // Over the backdrop the chrome shares the caption's material and content rises off it in
        // translucent steps, like WinUI's layer-on-Mica brushes; hovers wash what lies beneath.
        d["AppShellBackgroundBrush"] = new SolidColorBrush(Colors.Transparent);
        d["AppToolbarSurfaceBrush"] = new SolidColorBrush(Colors.Transparent);
        d["AppContentLayerBrush"] = new SolidColorBrush(LayerOnBackdrop(page, dark, second: true));
        d["AppCanvasBackgroundBrush"] = new SolidColorBrush(LayerOnBackdrop(canvas, dark, second: false));
        d["AppChromeDividerBrush"] = new SolidColorBrush(Colors.Transparent);
        d["AppSubtleHoverBrush"] = new SolidColorBrush(dark ? Color.FromArgb(0x18, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x14, 0, 0, 0));
        d["AppSubtlePressedBrush"] = new SolidColorBrush(dark ? Color.FromArgb(0x28, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x24, 0, 0, 0));
        return d;
    }

    private static Color LayerOnBackdrop(Color page, bool dark, bool second)
    {
        if (!dark)
            return Color.FromArgb(second ? (byte)0xF0 : (byte)0xB0, page.R, page.G, page.B);
        HslColor hsl = page.ToHsl();
        Color lifted = new HslColor(1, hsl.H, hsl.S, Math.Min(1, hsl.L + 0.12)).ToRgb();
        return Color.FromArgb(second ? (byte)0xC0 : (byte)0x70, lifted.R, lifted.G, lifted.B);
    }

    private static Color Mix(int rgb, Color tint, double amount)
    {
        Color a = Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
        return Color.FromRgb(
            (byte)Math.Round(a.R + (tint.R - a.R) * amount),
            (byte)Math.Round(a.G + (tint.G - a.G) * amount),
            (byte)Math.Round(a.B + (tint.B - a.B) * amount));
    }

    private static (Color, Color, Color, Color, Color, Color) AccentShades(Color accent)
    {
        HslColor hsl = accent.ToHsl();
        Color Shade(double step) => new HslColor(hsl.A, hsl.H, hsl.S, Math.Clamp(hsl.L + step, 0, 1)).ToRgb();
        return (Shade(-28.5 / 255), Shade(-49 / 255.0), Shade(-74.5 / 255), Shade(39 / 255.0), Shade(70 / 255.0), Shade(103 / 255.0));
    }
}
