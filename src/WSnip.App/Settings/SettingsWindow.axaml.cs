using System.Reflection;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using WSnip.App.Capture;
using WSnip.App.Services;
using WSnip.Core.Capture;
using WSnip.Core.Encoding;
using WSnip.Core.Imaging;
using WSnip.Core.Platform;
using WSnip.Core.Settings;

namespace WSnip.App.Settings;

/// <summary>Preferences; every change applies and is saved immediately.</summary>
public partial class SettingsWindow : Window
{
    private static readonly (SdrToneMapping Mode, string Label, string Description)[] ToneMappings =
    [
        (SdrToneMapping.Adaptive, "Adaptive (recommended)", "Highlights roll off smoothly around HDR content while the rest of the screen keeps its exact colors."),
        (SdrToneMapping.Global, "Tone map everything", "The same highlight roll-off applies to the whole snip."),
        (SdrToneMapping.Clip, "Clip highlights", "Highlights are cut at SDR white, keeping their hue."),
    ];

    private readonly AppController controller;
    private bool loading;

    public SettingsWindow()
        : this(App.Controller ?? throw new InvalidOperationException("The app has not started."))
    {
    }

    public SettingsWindow(AppController controller)
    {
        this.controller = controller;
        InitializeComponent();

        foreach (SnipModeOption option in SnipModeOption.All)
            ModeBox.Items.Add(new ComboBoxItem { Content = option.Label, Tag = option.Mode });
        foreach (DelayOption option in DelayOption.All)
            DelayBox.Items.Add(new ComboBoxItem { Content = option.Label, Tag = option.Seconds });
        foreach (SnipFormat format in SnipExporter.AllFormats.Where(SnipExporter.IsAvailable))
        {
            SdrFormatBox.Items.Add(new ComboBoxItem { Content = SnipExporter.DisplayName(format), Tag = format });
            HdrFormatBox.Items.Add(new ComboBoxItem { Content = SnipExporter.DisplayName(format), Tag = format });
        }

        foreach ((SdrToneMapping mode, string label, _) in ToneMappings)
            ToneMappingBox.Items.Add(new ComboBoxItem { Content = label, Tag = mode });
        foreach (ThemePreference theme in Enum.GetValues<ThemePreference>())
            ThemeBox.Items.Add(new ComboBoxItem { Content = theme == ThemePreference.System ? "Use system setting" : theme.ToString(), Tag = theme });
        MicaRow.IsVisible = controller.Theme.IsMicaSupported;

        Load();

        HotkeyBox.AddHandler(KeyDownEvent, OnHotkeyKeyDown, RoutingStrategies.Tunnel);
        PrintScreenButton.Click += (_, _) => Update(s => s.Hotkey = "PrintScreen");
        ClearHotkeyButton.Click += (_, _) => Update(s => s.Hotkey = string.Empty);
        ModeBox.SelectionChanged += (_, _) => Update(s => s.Mode = Selected<SnipMode>(ModeBox));
        DelayBox.SelectionChanged += (_, _) => Update(s => s.DelaySeconds = Selected<int>(DelayBox));
        CursorSwitch.IsCheckedChanged += (_, _) => Update(s => s.IncludeCursor = CursorSwitch.IsChecked == true);
        CopySwitch.IsCheckedChanged += (_, _) => Update(s => s.CopyToClipboard = CopySwitch.IsChecked == true);
        EditorSwitch.IsCheckedChanged += (_, _) => Update(s => s.OpenEditorAfterCapture = EditorSwitch.IsChecked == true);
        PixelInfoSwitch.IsCheckedChanged += (_, _) => Update(s => s.ShowPixelInfo = PixelInfoSwitch.IsChecked == true);
        AutoSaveSwitch.IsCheckedChanged += (_, _) => Update(s => s.AutoSave = AutoSaveSwitch.IsChecked == true);
        FolderButton.Click += async (_, _) => await PickFolderAsync();
        SdrFormatBox.SelectionChanged += (_, _) => Update(s => s.SdrFormat = Selected<SnipFormat>(SdrFormatBox));
        HdrFormatBox.SelectionChanged += (_, _) => Update(s => s.HdrFormat = Selected<SnipFormat>(HdrFormatBox));
        QualitySlider.PropertyChanged += (_, e) =>
        {
            if (e.Property == RangeBase.ValueProperty)
            {
                QualityText.Text = ((int)QualitySlider.Value).ToString(System.Globalization.CultureInfo.CurrentCulture);
                Update(s => s.Quality = (int)QualitySlider.Value);
            }
        };
        ToneMappingBox.SelectionChanged += (_, _) => Update(s => s.ToneMapping = Selected<SdrToneMapping>(ToneMappingBox));
        ThemeBox.SelectionChanged += (_, _) => Update(s => s.Theme = Selected<ThemePreference>(ThemeBox));
        MicaSwitch.IsCheckedChanged += (_, _) => Update(s => s.UseMica = MicaSwitch.IsChecked == true);
        StartupSwitch.IsCheckedChanged += (_, _) => Update(s => s.LaunchAtStartup = StartupSwitch.IsChecked == true);
    }

    private void Load()
    {
        loading = true;
        AppSettings settings = controller.Settings;
        HotkeyBox.Text = controller.Hotkey?.ToString() ?? string.Empty;
        HotkeyInfo.Text = controller.HotkeyError ?? "Click the box and press a key combination.";
        Select(ModeBox, settings.Mode);
        Select(DelayBox, settings.DelaySeconds);
        CursorSwitch.IsChecked = settings.IncludeCursor;
        CopySwitch.IsChecked = settings.CopyToClipboard;
        EditorSwitch.IsChecked = settings.OpenEditorAfterCapture;
        PixelInfoSwitch.IsChecked = settings.ShowPixelInfo;
        AutoSaveSwitch.IsChecked = settings.AutoSave;
        FolderText.Text = controller.SaveFolder;
        Select(SdrFormatBox, settings.SdrFormat);
        Select(HdrFormatBox, settings.HdrFormat);
        QualitySlider.Value = settings.Quality;
        QualityText.Text = settings.Quality.ToString(System.Globalization.CultureInfo.CurrentCulture);
        Select(ToneMappingBox, settings.ToneMapping);
        ToneMappingInfo.Text = ToneMappings.First(t => t.Mode == settings.ToneMapping).Description;
        Select(ThemeBox, settings.Theme);
        MicaSwitch.IsChecked = settings.UseMica;
        StartupSwitch.IsChecked = settings.LaunchAtStartup;

        string version = typeof(SettingsWindow).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "";
        string missing = string.Join(", ", SnipExporter.AllFormats.Where(f => !SnipExporter.IsAvailable(f)).Select(SnipExporter.DisplayName));
        AboutText.Text = $"WSnip {version.Split('+')[0]}. HDR snips keep their highlights and wide colors; the SDR version is what " +
            "apps without HDR support show." + (missing.Length > 0 ? $" Unavailable formats: {missing}." : string.Empty);
        loading = false;
    }

    private void Update(Action<AppSettings> change)
    {
        if (loading)
            return;
        controller.UpdateSettings(controller.Settings.With(change));
        Load();
    }

    private void OnHotkeyKeyDown(object? sender, KeyEventArgs e)
    {
        e.Handled = true;
        if (e.Key is Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin)
            return;
        if (e.Key is Key.Escape or Key.Tab)
        {
            e.Handled = e.Key == Key.Escape;
            return;
        }

        string? name = KeyName(e.Key);
        if (name is null)
            return;
        var gesture = new HotkeyGesture(name,
            Control: e.KeyModifiers.HasFlag(KeyModifiers.Control),
            Shift: e.KeyModifiers.HasFlag(KeyModifiers.Shift),
            Alt: e.KeyModifiers.HasFlag(KeyModifiers.Alt),
            Windows: e.KeyModifiers.HasFlag(KeyModifiers.Meta));
        Update(s => s.Hotkey = gesture.ToString());
    }

    private static string? KeyName(Key key) => key switch
    {
        >= Key.A and <= Key.Z => key.ToString(),
        >= Key.D0 and <= Key.D9 => ((char)('0' + (key - Key.D0))).ToString(),
        >= Key.F1 and <= Key.F24 => key.ToString(),
        Key.Snapshot => "PrintScreen",
        Key.Space => "Space",
        Key.Insert => "Insert",
        Key.Delete => "Delete",
        Key.Home => "Home",
        Key.End => "End",
        Key.PageUp => "PageUp",
        Key.PageDown => "PageDown",
        Key.Pause => "Pause",
        Key.Scroll => "ScrollLock",
        _ => null,
    };

    private async Task PickFolderAsync()
    {
        IStorageFolder? start = await StorageProvider.TryGetFolderFromPathAsync(controller.SaveFolder);
        IReadOnlyList<IStorageFolder> folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Folder for saved snips",
            SuggestedStartLocation = start,
        });
        if (folders.Count > 0 && folders[0].TryGetLocalPath() is { } path)
            Update(s => s.SaveFolder = path);
    }

    private static T Selected<T>(ComboBox box) => box.SelectedItem is ComboBoxItem { Tag: T value } ? value : default!;

    private static void Select<T>(ComboBox box, T value)
    {
        foreach (ComboBoxItem item in box.Items.OfType<ComboBoxItem>())
        {
            if (item.Tag is T tag && EqualityComparer<T>.Default.Equals(tag, value))
            {
                box.SelectedItem = item;
                return;
            }
        }
    }
}
