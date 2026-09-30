using System.Globalization;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using LightStudio.Logging;
using WSnip.App.Capture;
using WSnip.App.Rendering;
using WSnip.App.Services;
using WSnip.Core.Capture;
using WSnip.Core.Encoding;
using WSnip.Core.Imaging;

namespace WSnip.App.Editor;

/// <summary>
/// The main window: starts snips, shows the latest one in HDR where the display allows, and offers
/// pen, highlighter, eraser and crop markup with copy and save.
/// </summary>
public partial class EditorWindow : Window
{
    private static readonly (string Name, ScRgb Color)[] PenColors =
    [
        ("Black", ScRgb.FromSrgb(0x1A, 0x1A, 0x1A)), ("White", ScRgb.FromSrgb(0xFF, 0xFF, 0xFF)), ("Red", ScRgb.FromSrgb(0xE8, 0x11, 0x23)),
        ("Yellow", ScRgb.FromSrgb(0xFF, 0xB9, 0x00)), ("Green", ScRgb.FromSrgb(0x10, 0x89, 0x3E)),
        ("Blue", ScRgb.FromSrgb(0x00, 0x78, 0xD7)), ("Purple", ScRgb.FromSrgb(0x88, 0x64, 0xB8)),
    ];

    private static readonly (string Name, ScRgb Color)[] HighlighterColors =
    [
        ("Yellow", ScRgb.FromSrgb(0xFF, 0xF1, 0x00)), ("Green", ScRgb.FromSrgb(0x9B, 0xE5, 0x64)), ("Cyan", ScRgb.FromSrgb(0x5C, 0xE1, 0xE6)),
        ("Pink", ScRgb.FromSrgb(0xFF, 0x8A, 0xD8)), ("Orange", ScRgb.FromSrgb(0xFF, 0xB3, 0x47)),
    ];

    private readonly AppController controller;
    private readonly ToggleButton[] tools;
    private readonly ColorEditor colorEditor = new();
    private readonly Flyout colorFlyout;
    private EditorDocument? document;
    private string? savedPath;
    private ScRgb penColor = PenColors[2].Color;
    private ScRgb highlighterColor = HighlighterColors[0].Color;
    private CustomColor? editingColor;
    private double penSize = 4;
    private double highlighterSize = 18;
    private bool updating;

    public EditorWindow()
        : this(App.Controller ?? throw new InvalidOperationException("The app has not started."))
    {
    }

    public EditorWindow(AppController controller)
    {
        this.controller = controller;
        InitializeComponent();
        tools = [PenTool, HighlighterTool, EraserTool, CropTool];

        // Items are data shown through templates, so the selected item follows theme changes
        // (a control as item content is shown as a snapshot of it).
        ModeBox.ItemsSource = SnipModeOption.All;
        DelayBox.ItemsSource = DelayOption.All;
        colorFlyout = new Flyout { Content = colorEditor, Placement = PlacementMode.Bottom };
        colorFlyout.Closed += (_, _) => editingColor = null;
        colorEditor.Applied += (_, color) => ApplyCustomColor(color);
        colorEditor.Cancelled += (_, _) => colorFlyout.Hide();

        NewButton.Click += async (_, _) => await controller.StartSnipAsync(SelectedMode(), SelectedDelay());
        ModeBox.SelectionChanged += (_, _) => SaveSnipOptions();
        DelayBox.SelectionChanged += (_, _) => SaveSnipOptions();
        PenTool.IsCheckedChanged += (_, _) => OnToolToggled(PenTool, EditorTool.Pen);
        HighlighterTool.IsCheckedChanged += (_, _) => OnToolToggled(HighlighterTool, EditorTool.Highlighter);
        EraserTool.IsCheckedChanged += (_, _) => OnToolToggled(EraserTool, EditorTool.Eraser);
        CropTool.IsCheckedChanged += (_, _) => OnToolToggled(CropTool, EditorTool.Crop);
        UndoButton.Click += (_, _) => document?.Undo();
        RedoButton.Click += (_, _) => document?.Redo();
        HdrToggle.IsCheckedChanged += (_, _) =>
        {
            if (!updating)
                SetShowHdr(HdrToggle.IsChecked == true);
        };
        ZoomInButton.Click += (_, _) => Canvas.ZoomBy(1.25);
        ZoomOutButton.Click += (_, _) => Canvas.ZoomBy(0.8);
        ZoomButton.Click += (_, _) => ToggleFit();
        CopyButton.Click += async (_, _) => await CopyAsync();
        SaveButton.Click += async (_, _) => await SaveAsAsync();
        OpenFolderItem.Click += (_, _) => controller.Platform.Shell.OpenFolder(controller.SaveFolder);
        SettingsItem.Click += (_, _) => controller.ShowSettings();
        ExitItem.Click += (_, _) => controller.Exit();
        ApplyCropButton.Click += (_, _) => ApplyCrop();
        CancelCropButton.Click += (_, _) => SelectTool(EditorTool.Select);
        SavedLink.Click += (_, _) =>
        {
            if (savedPath is not null)
                controller.Platform.Shell.RevealInFolder(savedPath);
        };
        SizeSlider.PropertyChanged += (_, e) =>
        {
            if (e.Property == RangeBase.ValueProperty && !updating)
                SetStrokeSize(SizeSlider.Value);
        };
        AddColorButton.Click += (_, _) => OpenColorEditor(AddColorButton, null);
        PickColorTool.IsCheckedChanged += (_, _) =>
        {
            Canvas.IsPickingColor = PickColorTool.IsChecked == true;
            UpdatePixelInfo(Canvas.Hover);
        };

        Canvas.ViewChanged += (_, _) => UpdateZoomLabel();
        Canvas.SurfaceChanged += (_, _) => UpdateStatus();
        Canvas.CropChanged += (_, _) => UpdateCropBar();
        Canvas.HoverChanged += (_, hover) => UpdatePixelInfo(hover);
        Canvas.PixelPicked += (_, pixel) => PickColor(pixel);
        AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);

        OnSettingsChanged();
        UpdateDocumentState();
    }

    /// <summary>Shows a new snip, replacing the current one.</summary>
    public void Load(EditorDocument next, string? saved)
    {
        ReleaseDocument();
        document = next;
        savedPath = saved;
        document.Changed += OnDocumentChanged;
        SelectTool(EditorTool.Select);
        Canvas.Document = document;
        UpdateDocumentState();
    }

    /// <summary>Lets go of the current snip, so the window opens empty next time.</summary>
    public void Clear()
    {
        colorFlyout.Hide();
        SelectTool(EditorTool.Select);
        Canvas.Document = null;
        ReleaseDocument();
        savedPath = null;
        UpdateDocumentState();
    }

    /// <summary>Picks up preferences that affect the editor.</summary>
    public void OnSettingsChanged()
    {
        updating = true;
        ModeBox.SelectedItem = SnipModeOption.For(controller.Settings.Mode);
        DelayBox.SelectedItem = DelayOption.For(controller.Settings.DelaySeconds);
        updating = false;
        Canvas.SdrMapping = controller.Settings.ToneMapping;
        Canvas.ShowHdr = controller.Settings.ShowHdrInEditor;
        updating = true;
        HdrToggle.IsChecked = controller.Settings.ShowHdrInEditor;
        updating = false;
        if (ToolOptions.IsVisible)
            BuildToolOptions(Canvas.Tool == EditorTool.Highlighter);
        UpdatePixelInfo(Canvas.Hover);
        UpdateEmptyHint();
        UpdateStatus();
    }

    private void ReleaseDocument()
    {
        if (document is null)
            return;
        document.Changed -= OnDocumentChanged;
        document.Dispose();
        document = null;
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        bool control = e.KeyModifiers.HasFlag(KeyModifiers.Control);
        bool shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        switch (e.Key)
        {
            case Key.N when control:
                _ = controller.StartSnipAsync(SelectedMode(), SelectedDelay());
                break;
            case Key.Z when control && !shift:
                document?.Undo();
                break;
            case Key.Y when control:
            case Key.Z when control && shift:
                document?.Redo();
                break;
            case Key.C when control:
                _ = CopyAsync();
                break;
            case Key.S when control:
                _ = SaveAsAsync();
                break;
            case Key.D0 or Key.NumPad0 when control:
                Canvas.ZoomToFit();
                break;
            case Key.D1 or Key.NumPad1 when control:
                Canvas.ZoomToActualSize();
                break;
            case Key.OemPlus or Key.Add when control:
                Canvas.ZoomBy(1.25);
                break;
            case Key.OemMinus or Key.Subtract when control:
                Canvas.ZoomBy(0.8);
                break;
            case Key.Enter when Canvas.Tool == EditorTool.Crop:
                ApplyCrop();
                break;
            case Key.Escape when Canvas.IsPickingColor:
                PickColorTool.IsChecked = false;
                break;
            case Key.Escape when Canvas.Tool != EditorTool.Select:
                SelectTool(EditorTool.Select);
                break;
            default:
                return;
        }

        e.Handled = true;
    }

    private void OnDocumentChanged(object? sender, EventArgs e) => UpdateDocumentState();

    private void OnToolToggled(ToggleButton button, EditorTool tool)
    {
        if (!updating)
            SelectTool(button.IsChecked == true ? tool : EditorTool.Select);
    }

    private void SelectTool(EditorTool tool)
    {
        bool wasUpdating = updating;
        updating = true;
        Canvas.Tool = tool;
        PenTool.IsChecked = tool == EditorTool.Pen;
        HighlighterTool.IsChecked = tool == EditorTool.Highlighter;
        EraserTool.IsChecked = tool == EditorTool.Eraser;
        CropTool.IsChecked = tool == EditorTool.Crop;
        PickColorTool.IsChecked = false;
        updating = wasUpdating;
        ToolOptions.IsVisible = tool is EditorTool.Pen or EditorTool.Highlighter;
        if (ToolOptions.IsVisible)
            BuildToolOptions(tool == EditorTool.Highlighter);
        UpdateCropBar();
        UpdatePixelInfo(Canvas.Hover);
    }

    private void BuildToolOptions(bool highlighter)
    {
        bool wasUpdating = updating;
        updating = true;
        Swatches.Children.Clear();
        ScRgb selected = highlighter ? highlighterColor : penColor;
        foreach ((string name, ScRgb color) in highlighter ? HighlighterColors : PenColors)
            Swatches.Children.Add(CreateSwatch(color, name, color == selected, highlighter, custom: null));

        CustomColor[] custom = highlighter ? controller.Settings.CustomHighlighterColors : controller.Settings.CustomPenColors;
        if (custom.Length > 0)
            Swatches.Children.Add(new Border { Classes = { "divider" } });
        foreach (CustomColor color in custom)
        {
            ScRgb value = StrokeColor(color, highlighter);
            Swatches.Children.Add(CreateSwatch(value, ColorEditor.Describe(value, WhiteNits), value == selected, highlighter, color));
        }

        SizeSlider.Value = highlighter ? highlighterSize : penSize;
        Canvas.StrokeColor = selected;
        Canvas.StrokeSize = SizeSlider.Value;
        updating = wasUpdating;
    }

    private RadioButton CreateSwatch(ScRgb color, string tip, bool selected, bool highlighter, CustomColor? custom)
    {
        var swatch = new RadioButton
        {
            Classes = { "swatch" },
            GroupName = "swatches",
            IsChecked = selected,
            Content = new ColorSwatch { Color = color },
        };
        ToolTip.SetTip(swatch, tip);
        AutomationProperties.SetName(swatch, tip);
        swatch.IsCheckedChanged += (_, _) =>
        {
            if (swatch.IsChecked == true)
                SetStrokeColor(color, highlighter);
        };

        if (custom is not null)
        {
            var edit = new MenuItem { Header = "Edit color" };
            edit.Click += (_, _) => OpenColorEditor(swatch, custom);
            var remove = new MenuItem { Header = "Remove color" };
            remove.Click += (_, _) => RemoveCustomColor(custom, highlighter);
            swatch.ContextMenu = new ContextMenu { Items = { edit, remove } };
        }

        return swatch;
    }

    private void SetStrokeColor(ScRgb color, bool highlighter)
    {
        if (highlighter)
            highlighterColor = color;
        else
            penColor = color;
        Canvas.StrokeColor = color;
    }

    /// <summary>Highlighters multiply, so they take custom colors clipped to SDR sRGB.</summary>
    private static ScRgb StrokeColor(CustomColor color, bool highlighter) =>
        highlighter ? color.ToScRgb().ClampToSdr() : color.ToScRgb();

    private double WhiteNits => document?.SourceSdrWhiteNits ?? ColorMath.ReferenceWhiteNits;

    private void OpenColorEditor(Control anchor, CustomColor? existing)
    {
        bool highlighter = Canvas.Tool == EditorTool.Highlighter;
        colorEditor.ReferenceWhiteNits = WhiteNits;
        colorEditor.Load(existing ?? CustomColor.FromScRgb(highlighter ? highlighterColor : penColor), extended: !highlighter, existing: existing is not null);
        colorFlyout.ShowAt(anchor);

        // Showing closes the flyout if it was open elsewhere, which forgets the color being edited.
        editingColor = existing;
    }

    private void ApplyCustomColor(CustomColor color)
    {
        CustomColor? replaced = editingColor;
        colorFlyout.Hide();
        AddCustomColor(color, Canvas.Tool == EditorTool.Highlighter, replaced);
    }

    /// <summary>Saves a custom color for the current tool and selects it.</summary>
    private void AddCustomColor(CustomColor color, bool highlighter, CustomColor? replacing = null)
    {
        CustomColor[] current = highlighter ? controller.Settings.CustomHighlighterColors : controller.Settings.CustomPenColors;
        CustomColor[] updated = replacing is not null && Array.IndexOf(current, replacing) is >= 0 and var index
            ? [.. current[..index], color, .. current[(index + 1)..]]
            : current.Contains(color) ? current : [.. current, color];
        SetStrokeColor(StrokeColor(color, highlighter), highlighter);

        // Saving the settings rebuilds the swatches through OnSettingsChanged.
        controller.UpdateSettings(controller.Settings.With(s =>
        {
            if (highlighter)
                s.CustomHighlighterColors = updated;
            else
                s.CustomPenColors = updated;
        }));
    }

    private void RemoveCustomColor(CustomColor color, bool highlighter)
    {
        CustomColor[] current = highlighter ? controller.Settings.CustomHighlighterColors : controller.Settings.CustomPenColors;
        if (StrokeColor(color, highlighter) == (highlighter ? highlighterColor : penColor))
            SetStrokeColor(highlighter ? HighlighterColors[0].Color : PenColors[2].Color, highlighter);
        controller.UpdateSettings(controller.Settings.With(s =>
        {
            if (highlighter)
                s.CustomHighlighterColors = current.Where(c => c != color).ToArray();
            else
                s.CustomPenColors = current.Where(c => c != color).ToArray();
        }));
    }

    /// <summary>Keeps the sampled pixel, HDR and wide colors included, as a custom color of the current tool.</summary>
    private void PickColor(PixelHover pixel)
    {
        PickColorTool.IsChecked = false;
        if (document is null || Canvas.Tool is not (EditorTool.Pen or EditorTool.Highlighter))
            return;
        bool highlighter = Canvas.Tool == EditorTool.Highlighter;
        ScRgb color = document.PixelAt(pixel.X, pixel.Y).Color;
        AddCustomColor(CustomColor.FromScRgb(highlighter ? color.ClampToSdr() : color), highlighter);
    }

    private void UpdatePixelInfo(PixelHover? hover)
    {
        bool show = hover is not null && document is not null && controller.Settings.ShowPixelInfo &&
            (Canvas.IsPickingColor || Canvas.Tool is EditorTool.Select or EditorTool.Pen or EditorTool.Highlighter);
        PixelInfo.IsVisible = show;
        if (!show || hover is not { } pixel || document is null)
            return;

        (ScRgb color, float alpha) = document.PixelAt(pixel.X, pixel.Y);
        CultureInfo culture = CultureInfo.CurrentCulture;
        PixelSwatch.Color = color;
        PixelPosition.Text = string.Create(culture, $"X {pixel.X}  Y {pixel.Y}");
        bool plainSrgb = color.Gamut == ColorPrimaries.Bt709 && !color.IsHdr;
        if (plainSrgb)
        {
            (byte r, byte g, byte b) = color.ToSrgbBytes();
            PixelColor.Text = string.Create(culture, $"sRGB #{r:X2}{g:X2}{b:X2}  ({r}, {g}, {b})");
        }
        else
        {
            CustomColor described = CustomColor.FromScRgb(color);
            string gamut = described.Primaries switch
            {
                ColorPrimaries.Bt709 => "sRGB",
                ColorPrimaries.DisplayP3 => "Display P3",
                _ => "BT.2020",
            };
            PixelColor.Text = string.Create(culture, $"{gamut} {described.Red:0.000}, {described.Green:0.000}, {described.Blue:0.000}") +
                (described.Brightness > 1 ? string.Create(culture, $" \u00D7 {described.Brightness:0.00}  HDR") : string.Empty);
        }

        PixelLinear.IsVisible = !plainSrgb;
        PixelLinear.Text = string.Create(culture, $"Linear scRGB {color.R:0.000}, {color.G:0.000}, {color.B:0.000}");
        float luminance = color.Luminance;
        PixelLuminance.Text = string.Create(culture, $"Luminance {luminance:0.000}\u00D7 SDR white, {luminance * WhiteNits:0.#} nits") +
            (alpha < 0.999f ? string.Create(culture, $"  \u00B7  alpha {alpha:0.00}") : string.Empty);

        // Beside the pointer, flipped to the other side near the edges of the view.
        PixelInfo.Measure(Size.Infinity);
        Size size = PixelInfo.DesiredSize;
        Size area = PixelInfoLayer.Bounds.Size;
        double x = pixel.Position.X + 20, y = pixel.Position.Y + 20;
        if (x + size.Width > area.Width)
            x = pixel.Position.X - 12 - size.Width;
        if (y + size.Height > area.Height)
            y = pixel.Position.Y - 12 - size.Height;
        Avalonia.Controls.Canvas.SetLeft(PixelInfo, Math.Max(0, x));
        Avalonia.Controls.Canvas.SetTop(PixelInfo, Math.Max(0, y));
    }

    private void SetStrokeSize(double size)
    {
        if (Canvas.Tool == EditorTool.Highlighter)
            highlighterSize = size;
        else
            penSize = size;
        Canvas.StrokeSize = size;
    }

    private void SetShowHdr(bool show)
    {
        Canvas.ShowHdr = show;
        controller.UpdateSettings(controller.Settings.With(s => s.ShowHdrInEditor = show));
    }

    private void SaveSnipOptions()
    {
        if (updating || ModeBox.SelectedItem is not SnipModeOption mode || DelayBox.SelectedItem is not DelayOption delay)
            return;
        if (mode.Mode != controller.Settings.Mode || delay.Seconds != controller.Settings.DelaySeconds)
            controller.UpdateSettings(controller.Settings.With(s =>
            {
                s.Mode = mode.Mode;
                s.DelaySeconds = delay.Seconds;
            }));
    }

    private SnipMode SelectedMode() => ModeBox.SelectedItem is SnipModeOption option ? option.Mode : controller.Settings.Mode;

    private int SelectedDelay() => DelayBox.SelectedItem is DelayOption option ? option.Seconds : controller.Settings.DelaySeconds;

    private void ToggleFit()
    {
        if (Canvas.IsFit)
            Canvas.ZoomToActualSize();
        else
            Canvas.ZoomToFit();
    }

    private void ApplyCrop()
    {
        if (document is not null && Canvas.CropRegion is { } region)
            document.Crop(region);
        SelectTool(EditorTool.Select);
    }

    private async Task CopyAsync()
    {
        if (document is null)
            return;
        try
        {
            Snip snip = await Task.Run(document.Flatten);
            await controller.CopyAsync(snip);
            ShowStatusMessage("Copied to the clipboard.");
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            AppLog.Error("Editor", "Copying failed.", exception);
            MessageDialog.Show(this, "WSnip", $"The snip could not be copied.\n\n{exception.Message}");
        }
    }

    private async Task SaveAsAsync()
    {
        if (document is null)
            return;

        Snip snip = await Task.Run(document.Flatten);
        SnipFormat preferred = controller.Settings.FormatFor(snip);
        var formats = SnipExporter.AllFormats.Where(SnipExporter.IsAvailable).OrderBy(f => f == preferred ? 0 : 1).ToList();
        var choices = formats.Select(f => new FilePickerFileType(SnipExporter.DisplayName(f))
        {
            Patterns = [$"*{SnipExporter.Extension(f)}"],
            MimeTypes = [SnipExporter.MimeType(f)],
        }).ToList();

        IStorageFolder? start = await StorageProvider.TryGetFolderFromPathAsync(
            savedPath is not null ? Path.GetDirectoryName(savedPath)! : controller.SaveFolder);
        SaveFilePickerResult result = await StorageProvider.SaveFilePickerWithResultAsync(new FilePickerSaveOptions
        {
            Title = "Save snip",
            SuggestedFileName = Path.GetFileNameWithoutExtension(SnipExporter.DefaultFileName(document.CapturedAt, preferred)),
            DefaultExtension = SnipExporter.Extension(preferred).TrimStart('.'),
            FileTypeChoices = choices,
            SuggestedStartLocation = start,
            ShowOverwritePrompt = true,
        });
        if (result.File?.TryGetLocalPath() is not { } path)
            return;

        int chosen = result.SelectedFileType is { } type ? choices.IndexOf(type) : -1;
        SnipFormat format = chosen >= 0 ? formats[chosen] : SnipExporter.FromExtension(path) ?? preferred;
        if (SnipExporter.FromExtension(path) is { } byExtension && byExtension != format &&
            !(byExtension == SnipFormat.Png && format == SnipFormat.PngHdr))
        {
            format = byExtension;
        }

        try
        {
            ShowStatusMessage("Saving\u2026");
            await controller.SaveAsync(snip, path, format);
            savedPath = path;
            document.MarkClean();
            UpdateDocumentState();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or NotSupportedException or InvalidOperationException)
        {
            AppLog.Error("Editor", $"Saving {path} failed.", exception);
            MessageDialog.Show(this, "WSnip", $"The snip could not be saved.\n\n{exception.Message}");
            UpdateStatus();
        }
    }

    private void UpdateDocumentState()
    {
        bool has = document is not null;
        EmptyState.IsVisible = !has;
        Canvas.IsVisible = has;
        foreach (ToggleButton tool in tools)
            tool.IsEnabled = has;
        UndoButton.IsEnabled = document?.CanUndo == true;
        RedoButton.IsEnabled = document?.CanRedo == true;
        CopyButton.IsEnabled = has;
        SaveButton.IsEnabled = has;
        ZoomInButton.IsEnabled = has;
        ZoomOutButton.IsEnabled = has;
        ZoomButton.IsEnabled = has;
        SavedLink.IsVisible = savedPath is not null;
        SavedText.Text = savedPath is null ? null : Path.GetFileName(savedPath);
        Title = document is null ? "WSnip" : $"WSnip \u2014 {SnipExporter.DefaultFileName(document.CapturedAt, SnipFormat.Png)[..^4]}";
        UpdateZoomLabel();
        UpdateStatus();
    }

    private void UpdateZoomLabel() => ZoomLabel.Text = document is null ? "100%" : $"{Canvas.Zoom * 100:0}%";

    private void UpdateCropBar()
    {
        CropBar.IsVisible = Canvas.Tool == EditorTool.Crop;
        CropSize.Text = Canvas.CropRegion is { } region ? $"{region.Width} \u00D7 {region.Height}" : null;
    }

    private void UpdateEmptyHint()
    {
        if (!controller.Platform.Hotkeys.IsSupported)
        {
            EmptyHint.Text = "Select New to capture the screen, or give WSnip's New snip action a shortcut in the system settings.";
            return;
        }

        string hotkey = controller.Hotkey?.ToString() ?? "a hotkey (none set)";
        EmptyHint.Text = controller.HotkeyError is { } error
            ? $"Select New to capture the screen. {error} Choose another shortcut in Settings."
            : $"Press {hotkey} anywhere, or select New, to capture the screen. HDR and wide-color content is kept.";
    }

    private void UpdateStatus()
    {
        HdrToggle.IsEnabled = Canvas.Surface.ExtendedRange;
        HdrIcon.Data = (Avalonia.Media.Geometry?)this.FindResource(HdrToggle.IsChecked == true && Canvas.Surface.ExtendedRange ? "IconHdrOn" : "IconHdrOff");
        ToolTip.SetTip(HdrToggle, Canvas.Surface.ExtendedRange
            ? (HdrToggle.IsChecked == true ? "Showing HDR; select to preview the SDR version" : "Showing the SDR version; select to show HDR")
            : "This display shows SDR only");
        if (document is null)
        {
            StatusText.Text = Canvas.Surface.ExtendedRange ? DisplayDescription() : null;
            return;
        }

        HdrStatistics stats = document.Current.Statistics;
        var parts = new List<string> { $"{document.Width} \u00D7 {document.Height} px" };
        if (stats.HasHdr)
        {
            double nits = stats.PeakComponent * document.SourceSdrWhiteNits;
            parts.Add(string.Create(CultureInfo.CurrentCulture, $"HDR, peak {stats.PeakComponent:0.0}\u00D7 SDR white ({nits:0} nits)"));
        }
        else
        {
            parts.Add("SDR");
        }

        if (stats.OutsideDisplayP3Pixels >= 16)
            parts.Add("wide color beyond Display P3");
        else if (stats.HasWideGamut)
            parts.Add("wide color (Display P3)");
        if (document.Current.Strokes.Count > 0)
            parts.Add($"{document.Current.Strokes.Count} stroke{(document.Current.Strokes.Count == 1 ? "" : "s")}");
        parts.Add(DisplayDescription());
        StatusText.Text = string.Join("  \u00B7  ", parts);
    }

    private string DisplayDescription() => Canvas.Surface switch
    {
        { ExtendedRange: true, Headroom: > 1.01 } surface => string.Create(CultureInfo.CurrentCulture,
            $"HDR display, {surface.Headroom:0.0}\u00D7 headroom"),
        { ExtendedRange: true } => "Wide color display",
        _ => "SDR display",
    };

    private void ShowStatusMessage(string message) => StatusText.Text = message;
}
