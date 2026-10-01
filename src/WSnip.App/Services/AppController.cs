using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input.Platform;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using LightStudio.Logging;
using WSnip.App.Capture;
using WSnip.App.Editor;
using WSnip.App.Settings;
using WSnip.Core.Capture;
using WSnip.Core.Encoding;
using WSnip.Core.Imaging;
using WSnip.Core.Platform;
using WSnip.Core.Settings;
using WSnip.Core.Strings;

namespace WSnip.App.Services;

/// <summary>
/// Owns the running app: the hotkey and tray entry points, the capture flow from frozen screen to
/// delivered snip, the editor and settings windows, and the saved preferences.
/// </summary>
public sealed class AppController : IDisposable
{
    /// <summary>How long hidden windows take to leave the screen, including the compositor's closing animation.</summary>
    private static readonly TimeSpan HiddenWindowsSettle = TimeSpan.FromMilliseconds(300);

    /// <summary>What a shortcut can snip in: the snipping mode setting (null), then each mode.</summary>
    private static readonly SnipMode?[] HotkeyModes = [null, .. Enum.GetValues<SnipMode>()];

    private readonly Application app;
    private readonly PlatformServices platform;
    private readonly SettingsStore store;
    private readonly IClassicDesktopStyleApplicationLifetime lifetime;

    /// <summary>The shortcuts of the settings, with why any of them is not in effect.</summary>
    private readonly List<(SnipMode? Mode, HotkeyGesture Gesture, string? Error)> hotkeys = [];
    private EditorWindow? editor;
    private SettingsWindow? settingsWindow;
    private TrayIcon? tray;
    private IDisposable? windowClosedSubscription;
    private Bitmap? clipboardBitmap;
    private bool capturing;
    private bool exiting;
    private bool hotkeysPaused;

    public AppController(Application app, PlatformServices platform, SettingsStore store, IClassicDesktopStyleApplicationLifetime lifetime)
    {
        this.app = app;
        this.platform = platform;
        this.store = store;
        this.lifetime = lifetime;
        Settings = store.Load();
        Theme = new ThemeService(platform.Windows);
    }

    public AppSettings Settings { get; private set; }

    public ThemeService Theme { get; }

    public PlatformServices Platform => platform;

    /// <summary>Why the capture hotkey could not be registered, if it could not.</summary>
    public string? HotkeyError => HotkeyErrorFor(null);

    public HotkeyGesture? Hotkey => HotkeyFor(null);

    /// <summary>The shortcut that snips in a mode, or in the snipping mode setting when <paramref name="mode"/> is null.</summary>
    public HotkeyGesture? HotkeyFor(SnipMode? mode) => mode is { } snipMode
        ? HotkeyGesture.Parse(Settings.ModeHotkeys.GetValueOrDefault(snipMode))
        : Settings.Hotkey is null ? platform.DefaultHotkey : HotkeyGesture.Parse(Settings.Hotkey);

    /// <summary>Why the shortcut of <see cref="HotkeyFor"/> is not in effect, if it is not.</summary>
    public string? HotkeyErrorFor(SnipMode? mode) => hotkeys.Find(h => h.Mode == mode).Error;

    public string SaveFolder => string.IsNullOrWhiteSpace(Settings.SaveFolder) ? platform.Shell.DefaultScreenshotFolder : Settings.SaveFolder;

    public void Start(IReadOnlyList<string> arguments)
    {
        Theme.Configure(Settings.Theme, Settings.UseMica);
        platform.Hotkeys.Pressed += (_, gesture) => Dispatcher.UIThread.Post(() => OnHotkeyPressed(gesture));
        RegisterHotkeys();
        platform.SingleInstance.ArgumentsReceived += (_, forwarded) => Dispatcher.UIThread.Post(() => HandleArguments(forwarded, firstLaunch: false));
        platform.SingleInstance.StartListening();
        if (OperatingSystem.IsWindows())
            CreateTray();
        else
            windowClosedSubscription = Window.WindowClosedEvent.AddClassHandler<Window>((_, _) => Dispatcher.UIThread.Post(ExitIfIdle));
        _ = SyncLaunchAtStartupAsync();
        HandleArguments(arguments, firstLaunch: true);
    }

    /// <summary>
    /// Handles a launch: <c>--snip [rectangle|window|fullscreen|freeform|wholewindow]</c> starts a snip,
    /// <c>--background</c> starts in the notification area on Windows, anything else shows the editor.
    /// </summary>
    public void HandleArguments(IReadOnlyList<string> arguments, bool firstLaunch)
    {
        int snip = arguments.ToList().FindIndex(a => a.Equals("--snip", StringComparison.OrdinalIgnoreCase));
        if (snip >= 0)
        {
            SnipMode? mode = snip + 1 < arguments.Count && Enum.TryParse(arguments[snip + 1], ignoreCase: true, out SnipMode parsed)
                ? parsed
                : null;
            _ = StartSnipAsync(mode);
        }
        else if (!(OperatingSystem.IsWindows() && firstLaunch && arguments.Contains("--background", StringComparer.OrdinalIgnoreCase)))
        {
            ShowEditor();
        }
    }

    /// <summary>Freezes the screen, lets the user pick a region and delivers the snip.</summary>
    /// <remarks>Whole-window snips leave the screen live and wait for a click on a window instead.</remarks>
    public async Task StartSnipAsync(SnipMode? mode = null, int? delaySeconds = null)
    {
        if (capturing)
            return;
        capturing = true;
        var excluded = new List<nint>();
        List<(Window Window, Window? Owner)>? hidden = null;
        try
        {
            SnipMode initialMode = mode ?? Settings.Mode;
            int delay = delaySeconds ?? Settings.DelaySeconds;
            if (initialMode == SnipMode.WholeWindow)
            {
                await SnipWholeWindowAsync(delay);
                return;
            }

            // The app's own windows stay open but are left out of the capture, which shows what is
            // behind them instead. Where the system cannot leave them out, they step aside meanwhile.
            if (platform.Windows.CanExcludeFromCapture)
            {
                foreach (Window window in lifetime.Windows)
                {
                    if (window.IsVisible && window.TryGetPlatformHandle() is { } handle)
                    {
                        platform.Windows.SetExcludedFromCapture(handle.Handle, true);
                        excluded.Add(handle.Handle);
                    }
                }
            }
            else
            {
                hidden = HideWindows();
            }

            if (delay > 0)
                await Task.Delay(TimeSpan.FromSeconds(delay));
            else if (excluded.Count > 0)
                await Task.Delay(60);
            else if (hidden is { Count: > 0 })
                await Task.Delay(HiddenWindowsSettle);

            ScreenSnapshot snapshot = await platform.Capture.CaptureAsync(new CaptureOptions { IncludeCursor = Settings.IncludeCursor });
            platform.Windows.TryGetCursorPosition(out int x, out int y);
            var session = new OverlaySession(snapshot, initialMode, placeWindows: platform.Windows.CanPlaceWindows);
            SnipSelection? selection = await session.ShowAsync(new PixelPoint(x, y));

            // A mode picked on the overlay's own bar becomes the default for the next snip.
            if (session.Mode != initialMode)
                UpdateSettings(Settings.With(s => s.Mode = session.Mode));
            if (selection is null)
            {
                // Choosing a whole window on the bar leaves the frozen screen for the live picker.
                if (session.Mode == SnipMode.WholeWindow)
                    await SnipWholeWindowAsync(delaySeconds: 0);
                return;
            }

            Snip snip = await Task.Run(() => SnipComposer.Compose(snapshot, selection.Region, selection.Outline));
            AppLog.Information("Snip", $"{selection.Mode} snip {snip.Width}x{snip.Height}: {snip.Statistics}");
            await DeliverAsync(snip);
        }
        catch (OperationCanceledException exception)
        {
            // The user declined the system's screen sharing prompt.
            AppLog.Information("Snip", $"The snip was cancelled: {exception.Message}");
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            AppLog.Error("Snip", "Taking a snip failed.", exception);
            MessageDialog.Show(editor is { IsVisible: true } ? editor : null, "WSnip", string.Format(AppStrings.CaptureFailed, exception.Message));
        }
        finally
        {
            foreach (nint handle in excluded)
                platform.Windows.SetExcludedFromCapture(handle, false);
            if (hidden is not null)
                RestoreWindows(hidden);
            capturing = false;
            ExitIfIdle();
        }
    }

    public void ShowEditor(EditorDocument? document = null, string? savedPath = null)
    {
        EditorWindow window = EnsureEditor();
        if (document is not null)
            window.Load(document, savedPath);
        if (!window.IsVisible)
            window.Show();
        if (window.WindowState == WindowState.Minimized)
            window.WindowState = WindowState.Normal;
        window.Activate();
    }

    public void ShowSettings()
    {
        if (settingsWindow is { } open)
        {
            open.Activate();
            return;
        }

        settingsWindow = new SettingsWindow(this);
        Theme.Attach(settingsWindow);
        settingsWindow.Closed += (_, _) => settingsWindow = null;
        if (editor is { IsVisible: true } owner)
            settingsWindow.Show(owner);
        else
            settingsWindow.Show();
    }

    public void UpdateSettings(AppSettings updated)
    {
        bool themeChanged = updated.Theme != Settings.Theme || updated.UseMica != Settings.UseMica;
        bool hotkeysChanged = updated.Hotkey != Settings.Hotkey || !ReferenceEquals(updated.ModeHotkeys, Settings.ModeHotkeys);
        bool startupChanged = updated.LaunchAtStartup != Settings.LaunchAtStartup;
        Settings = updated;
        store.Save(updated);
        if (themeChanged)
            Theme.Configure(updated.Theme, updated.UseMica);
        if (hotkeysChanged)
            RegisterHotkeys();
        if (startupChanged)
            _ = SyncLaunchAtStartupAsync();
        editor?.OnSettingsChanged();
    }

    /// <summary>
    /// Gives a mode a shortcut, or the snipping mode setting when <paramref name="mode"/> is null,
    /// or takes it away when <paramref name="gesture"/> is null. A shortcut that another of WSnip's
    /// shortcuts, another app or Windows already uses is refused: nothing is saved or applied, and
    /// the reason is returned.
    /// </summary>
    public string? ChangeHotkey(SnipMode? mode, HotkeyGesture? gesture)
    {
        if (gesture is not null)
        {
            if (gesture == HotkeyFor(mode))
                return null;
            foreach (SnipMode? other in HotkeyModes)
            {
                if (other != mode && HotkeyFor(other) == gesture)
                    return string.Format(AppStrings.HotkeyConflict, gesture, HotkeyPurpose(other));
            }

            // Registering the shortcut before saving it finds out whether the system lets WSnip
            // have it; applying the settings then registers every shortcut afresh.
            if (platform.Hotkeys.Register(gesture) is { } refused)
                return refused;
        }

        UpdateSettings(Settings.With(s =>
        {
            if (mode is not { } snipMode)
            {
                s.Hotkey = gesture?.ToString() ?? string.Empty;
                return;
            }

            var modeHotkeys = new Dictionary<SnipMode, string>(s.ModeHotkeys);
            if (gesture is null)
                modeHotkeys.Remove(snipMode);
            else
                modeHotkeys[snipMode] = gesture.ToString();
            s.ModeHotkeys = modeHotkeys;
        }));
        return null;
    }

    /// <summary>
    /// Stops WSnip's shortcuts from starting snips, so that their key combinations reach its own
    /// windows, as when the user records a shortcut; or starts them again.
    /// </summary>
    public void PauseHotkeys(bool paused)
    {
        if (paused == hotkeysPaused)
            return;
        hotkeysPaused = paused;
        if (paused)
            platform.Hotkeys.UnregisterAll();
        else
            RegisterHotkeys();
    }

    /// <summary>Places the SDR rendition on the clipboard.</summary>
    public async Task CopyAsync(Snip snip)
    {
        ToneMapSettings toneMap = Settings.DefaultToneMap();
        ClipboardImage image = await Task.Run(() => ClipboardImage.FromRendition(SnipExporter.BuildSdr(snip, toneMap, flattenAlpha: false)));
        if (platform.Clipboard is { } system)
        {
            nint owner = EnsureEditor().TryGetPlatformHandle()?.Handle ?? 0;
            system.SetImage(image, owner);
            return;
        }

        // The windowing backend serves the image as PNG to apps that paste, so it stays alive while it is on the clipboard.
        IClipboard clipboard = EnsureEditor().Clipboard ?? throw new InvalidOperationException(AppStrings.ClipboardUnavailable);
        var bitmap = new Bitmap(new MemoryStream(image.Png));
        try
        {
            await clipboard.SetBitmapAsync(bitmap);
        }
        catch
        {
            bitmap.Dispose();
            throw;
        }

        clipboardBitmap?.Dispose();
        clipboardBitmap = bitmap;
    }

    /// <summary>Writes a snip in the format that suits its content into the screenshot folder.</summary>
    public async Task<string> AutoSaveAsync(Snip snip)
    {
        SnipFormat format = Settings.FormatFor(snip);
        if (!SnipExporter.IsAvailable(format))
            format = snip.Statistics.HasHdr ? SnipFormat.Jpeg : SnipFormat.Png;
        string folder = SaveFolder;
        Directory.CreateDirectory(folder);
        string path = UniquePath(Path.Combine(folder, SnipExporter.DefaultFileName(snip.CapturedAt, format)));
        await SnipExporter.ExportFileAsync(path, snip, format, Settings.ExportOptions());
        AppLog.Information("Snip", $"Saved {path}.");
        return path;
    }

    public async Task SaveAsync(Snip snip, string path, SnipFormat format)
    {
        await SnipExporter.ExportFileAsync(path, snip, format, Settings.ExportOptions());
        AppLog.Information("Snip", $"Saved {path}.");
    }

    public void Exit()
    {
        exiting = true;
        Dispose();
        lifetime.Shutdown();
    }

    public void Dispose()
    {
        windowClosedSubscription?.Dispose();
        windowClosedSubscription = null;
        tray?.Dispose();
        tray = null;
        clipboardBitmap?.Dispose();
        clipboardBitmap = null;
        platform.Hotkeys.Dispose();
        platform.SingleInstance.Dispose();
        (platform.Capture as IDisposable)?.Dispose();
    }

    private void ExitIfIdle()
    {
        if (!OperatingSystem.IsWindows() && !exiting && !capturing && !lifetime.Windows.Any(window => window.IsVisible))
            Exit();
    }

    /// <summary>
    /// Waits for a click on a window while the screen stays live, then reads that window whole
    /// from the compositor, with its transparency.
    /// </summary>
    /// <remarks>
    /// The app's own windows are hidden meanwhile, so they neither cover the windows to pick from
    /// nor stay active in front of them; they come back before the snip is delivered.
    /// </remarks>
    private async Task SnipWholeWindowAsync(int delaySeconds)
    {
        if (!platform.WindowPicker.IsSupported)
            throw new NotSupportedException(AppStrings.WindowPickingUnsupported);

        List<(Window Window, Window? Owner)> hidden = HideWindows();
        CapturedWindow? window;
        WindowCapture capture;
        try
        {
            if (delaySeconds > 0)
                await Task.Delay(TimeSpan.FromSeconds(delaySeconds));
            window = await platform.WindowPicker.PickAsync();
            if (window is null)
                return;
            capture = await platform.Capture.CaptureWindowAsync(window, new CaptureOptions { IncludeCursor = Settings.IncludeCursor });
        }
        finally
        {
            RestoreWindows(hidden);
        }

        Snip snip = await Task.Run(() => SnipComposer.FromWindow(capture));
        AppLog.Information("Snip", $"Whole window snip of '{window.Title}' {snip.Width}x{snip.Height}: {snip.Statistics}");
        await DeliverAsync(snip);
    }

    /// <summary>Hides the app's visible windows, returning them in opening order with their owners.</summary>
    private List<(Window Window, Window? Owner)> HideWindows()
    {
        List<(Window Window, Window? Owner)> hidden = lifetime.Windows
            .Where(w => w.IsVisible)
            .Select(w => (w, w.Owner as Window))
            .ToList();

        // Owned windows go first, so an owner is never hidden while its dialogs are still up.
        for (int i = hidden.Count - 1; i >= 0; i--)
            hidden[i].Window.Hide();
        return hidden;
    }

    /// <summary>Shows hidden windows again, owners first, unless they were closed or shown meanwhile.</summary>
    private void RestoreWindows(List<(Window Window, Window? Owner)> hidden)
    {
        if (exiting)
            return;
        foreach ((Window window, Window? owner) in hidden)
        {
            if (window.IsVisible || !lifetime.Windows.Contains(window))
                continue;
            if (owner is { IsVisible: true })
                window.Show(owner);
            else
                window.Show();
        }
    }

    private async Task DeliverAsync(Snip snip)
    {
        var document = new EditorDocument(snip);
        string? saved = null;
        string? problem = null;
        if (Settings.CopyToClipboard)
        {
            try
            {
                await CopyAsync(snip);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                AppLog.Error("Snip", "Copying the snip failed.", exception);
                problem = string.Format(AppStrings.CopyFailed, exception.Message);
            }
        }

        if (Settings.AutoSave)
        {
            try
            {
                saved = await AutoSaveAsync(snip);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                AppLog.Error("Snip", "Saving the snip failed.", exception);
                problem = string.Format(AppStrings.AutoSaveFailed, SaveFolder, exception.Message);
            }
        }

        // The editor keeps the snip when it could not be delivered.
        if (Settings.OpenEditorAfterCapture || editor is { IsVisible: true } || problem is not null)
            ShowEditor(document, saved);
        else
            EnsureEditor().Load(document, saved);
        if (problem is not null)
            MessageDialog.Show(editor, "WSnip", problem);
    }

    private EditorWindow EnsureEditor()
    {
        if (editor is not null)
            return editor;

        editor = new EditorWindow(this);
        Theme.Attach(editor);
        editor.Closing += (_, e) =>
        {
            // The app lives on in the tray; closing the window hides it and lets go of the snip,
            // which was already copied or saved as the settings ask.
            if (OperatingSystem.IsWindows() && !exiting)
            {
                e.Cancel = true;
                editor.Hide();
                editor.Clear();
                ReleaseMemory();
            }
        };
        editor.Closed += (_, _) => editor = null;
        return editor;
    }

    /// <summary>Returns the pixels of a closed snip to the system rather than holding them while in the tray.</summary>
    private static void ReleaseMemory() => Dispatcher.UIThread.Post(
        () => GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true),
        DispatcherPriority.Background);

    /// <summary>
    /// Registers the shortcuts of the settings, the snipping mode setting's first. One that repeats
    /// an earlier shortcut, as only an edited settings file can, is left out.
    /// </summary>
    private void RegisterHotkeys()
    {
        platform.Hotkeys.UnregisterAll();
        hotkeys.Clear();
        if (!platform.Hotkeys.IsSupported)
            return;
        foreach (SnipMode? mode in HotkeyModes)
        {
            if (HotkeyFor(mode) is not { } gesture)
                continue;
            int earlier = hotkeys.FindIndex(h => h.Gesture == gesture);
            string? error = earlier >= 0
                ? string.Format(AppStrings.HotkeyConflict, gesture, HotkeyPurpose(hotkeys[earlier].Mode))
                : platform.Hotkeys.Register(gesture);
            hotkeys.Add((mode, gesture, error));
            if (error is not null)
                AppLog.Warning("Hotkey", error);
        }

        if (hotkeys.Exists(h => h.Error is null))
            AppLog.Information("Hotkey", $"Registered {string.Join(", ", hotkeys.Where(h => h.Error is null).Select(h => $"{h.Gesture} for {HotkeyPurpose(h.Mode)}"))}.");

        // Registering found out which shortcuts the system refuses; while paused, none is in effect.
        if (hotkeysPaused)
            platform.Hotkeys.UnregisterAll();
    }

    private void OnHotkeyPressed(HotkeyGesture gesture)
    {
        foreach ((SnipMode? mode, HotkeyGesture registered, string? error) in hotkeys)
        {
            if (error is null && registered == gesture)
            {
                _ = StartSnipAsync(mode);
                return;
            }
        }
    }

    private static string HotkeyPurpose(SnipMode? mode) => mode switch
    {
        SnipMode.Rectangle => AppStrings.RectanglePurpose,
        SnipMode.Window => AppStrings.WindowPurpose,
        SnipMode.Fullscreen => AppStrings.FullscreenPurpose,
        SnipMode.Freeform => AppStrings.FreeformPurpose,
        SnipMode.WholeWindow => AppStrings.WholeWindowPurpose,
        _ => AppStrings.DefaultModePurpose,
    };

    private async Task SyncLaunchAtStartupAsync()
    {
        if (!OperatingSystem.IsWindows())
            return;
        try
        {
            bool wanted = Settings.LaunchAtStartup;
            if (Environment.ProcessPath is not { } executable || await platform.Shell.IsLaunchAtStartupEnabledAsync() == wanted)
                return;
            await platform.Shell.SetLaunchAtStartupAsync(wanted, executable, "--background");
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            AppLog.Warning("Startup", "Launch at startup could not be changed.", exception);
        }
    }

    private void CreateTray()
    {
        var menu = new NativeMenu();
        menu.Items.Add(MenuItem(AppStrings.NewSnip, () => _ = StartSnipAsync()));
        menu.Items.Add(MenuItem(AppStrings.OpenWSnip, () => ShowEditor()));
        menu.Items.Add(MenuItem(AppStrings.OpenFolder, () => platform.Shell.OpenFolder(SaveFolder)));
        menu.Items.Add(MenuItem(AppStrings.Settings, ShowSettings));
        menu.Items.Add(new NativeMenuItemSeparator());
        menu.Items.Add(MenuItem(AppStrings.Exit, Exit));

        using Stream icon = AssetLoader.Open(new Uri("avares://WSnip.App/Assets/wsnip.ico"));
        tray = new TrayIcon
        {
            Icon = new WindowIcon(icon),
            ToolTipText = "WSnip",
            Menu = menu,
            IsVisible = true,
        };
        tray.Clicked += (_, _) => ShowEditor();
        TrayIcon.SetIcons(app, [tray]);
    }

    private static NativeMenuItem MenuItem(string header, Action action)
    {
        var item = new NativeMenuItem(header);
        item.Click += (_, _) => action();
        return item;
    }

    private static string UniquePath(string path)
    {
        if (!File.Exists(path))
            return path;
        string directory = Path.GetDirectoryName(path)!;
        string name = Path.GetFileNameWithoutExtension(path);
        string extension = Path.GetExtension(path);
        for (int i = 2; ; i++)
        {
            string candidate = Path.Combine(directory, $"{name} ({i}){extension}");
            if (!File.Exists(candidate))
                return candidate;
        }
    }
}
