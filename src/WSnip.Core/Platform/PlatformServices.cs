using WSnip.Core.Capture;
using WSnip.Core.Encoding;
using WSnip.Core.Imaging;

namespace WSnip.Core.Platform;

/// <summary>Captures every display at full precision.</summary>
public interface IScreenCaptureService
{
    /// <summary>Whether this system can capture displays.</summary>
    bool IsSupported { get; }

    /// <summary>
    /// Captures all displays at the same moment, keeping HDR and wide color content. Windows that
    /// belong to this process are left out of the window list.
    /// </summary>
    Task<ScreenSnapshot> CaptureAsync(CaptureOptions options, CancellationToken cancellationToken = default);

    /// <summary>
    /// Captures one window as the compositor holds it: the whole window even where other windows
    /// cover it, with its own transparency (such as rounded corners) instead of what lies behind.
    /// </summary>
    Task<WindowCapture> CaptureWindowAsync(CapturedWindow window, CaptureOptions options, CancellationToken cancellationToken = default);
}

/// <summary>Lets the user point at a window while the screen stays live.</summary>
public interface IWindowPickerService
{
    bool IsSupported { get; }

    /// <summary>
    /// Shows a capture pointer over the whole desktop and waits for a click. The click does not
    /// reach the window under it. Returns the top-level window clicked, leaving out this app's own
    /// windows, or null when the user presses Esc or the right mouse button.
    /// </summary>
    Task<CapturedWindow?> PickAsync(CancellationToken cancellationToken = default);
}

public sealed record CaptureOptions
{
    public bool IncludeCursor { get; init; }
}

/// <summary>A key combination of the global hotkey.</summary>
/// <param name="Key">A platform-independent key name such as "S", "F9" or "PrintScreen".</param>
public sealed record HotkeyGesture(string Key, bool Control = false, bool Shift = false, bool Alt = false, bool Windows = false)
{
    public override string ToString()
    {
        var parts = new List<string>(5);
        if (Windows)
            parts.Add("Win");
        if (Control)
            parts.Add("Ctrl");
        if (Alt)
            parts.Add("Alt");
        if (Shift)
            parts.Add("Shift");
        parts.Add(Key);
        return string.Join('+', parts);
    }

    public static HotkeyGesture? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;
        string[] parts = text.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
            return null;
        var gesture = new HotkeyGesture(parts[^1]);
        foreach (string modifier in parts[..^1])
        {
            gesture = modifier.ToLowerInvariant() switch
            {
                "win" or "windows" or "meta" => gesture with { Windows = true },
                "ctrl" or "control" => gesture with { Control = true },
                "alt" => gesture with { Alt = true },
                "shift" => gesture with { Shift = true },
                _ => throw new FormatException($"Unknown modifier '{modifier}'."),
            };
        }

        return gesture;
    }
}

/// <summary>System-wide keyboard shortcuts.</summary>
public interface IGlobalHotkeyService : IDisposable
{
    /// <summary>Whether the app can register shortcuts itself, rather than the user assigning them in the system settings.</summary>
    bool IsSupported { get; }

    event EventHandler<HotkeyGesture>? Pressed;

    /// <summary>Registers a gesture; returns an error message when the system refuses it.</summary>
    string? Register(HotkeyGesture gesture);

    void UnregisterAll();
}

/// <summary>Native window integration: translucent backdrops, frame colors and capture visibility.</summary>
public interface IWindowIntegrationService
{
    bool IsMicaSupported { get; }

    /// <summary>Whether <see cref="SetExcludedFromCapture"/> takes effect; otherwise the app hides its windows while it captures.</summary>
    bool CanExcludeFromCapture { get; }

    /// <summary>
    /// Whether windows can be placed at desktop coordinates. Where the compositor places them, as on
    /// Wayland, the capture overlay fills the display it opens on.
    /// </summary>
    bool CanPlaceWindows { get; }

    /// <summary>Applies or removes the Mica backdrop of a top-level window.</summary>
    void SetMica(nint windowHandle, bool enabled, bool darkTheme);

    /// <summary>Asks the window manager to draw the frame in dark or light colors.</summary>
    void SetDarkFrame(nint windowHandle, bool darkTheme);

    /// <summary>
    /// Hides a window from screen captures, which then show what lies behind it, so the app can
    /// stay open while it captures the screen.
    /// </summary>
    void SetExcludedFromCapture(nint windowHandle, bool excluded);

    /// <summary>The pointer position in physical virtual-desktop pixels.</summary>
    bool TryGetCursorPosition(out int x, out int y);
}

/// <summary>Interaction with the desktop shell.</summary>
public interface IShellService
{
    /// <summary>The folder the system uses for screenshots, such as Pictures\Screenshots.</summary>
    string DefaultScreenshotFolder { get; }

    void RevealInFolder(string path);

    void OpenFolder(string path);

    Task<bool> IsLaunchAtStartupEnabledAsync();

    /// <summary>Starts the app in the background when the user signs in.</summary>
    /// <param name="executablePath">The running executable, for platforms that register a command line.</param>
    Task SetLaunchAtStartupAsync(bool enabled, string executablePath, string arguments);
}

/// <summary>The system clipboard.</summary>
public interface IClipboardService
{
    /// <summary>
    /// Replaces the clipboard content with an image. The data is handed to the system right away,
    /// so it can still be pasted after the app exits.
    /// </summary>
    /// <param name="ownerWindow">A native window of this app, for platforms that tie clipboard content to a window.</param>
    void SetImage(ClipboardImage image, nint ownerWindow);
}

/// <summary>An SDR image in the forms other apps paste.</summary>
public sealed class ClipboardImage
{
    public required int Width { get; init; }

    public required int Height { get; init; }

    /// <summary>Straight-alpha RGBA in sRGB.</summary>
    public required byte[] Rgba { get; init; }

    public required bool HasAlpha { get; init; }

    /// <summary>The same image as PNG, which keeps transparency.</summary>
    public required byte[] Png { get; init; }

    /// <summary>Prepares an sRGB rendition, encoding the PNG form.</summary>
    public static ClipboardImage FromRendition(Rendition rendition)
    {
        if (rendition.Primaries != ColorPrimaries.Bt709)
            throw new ArgumentException("The clipboard takes sRGB images.", nameof(rendition));
        using var png = new MemoryStream();
        PngWriter.WriteSdr(png, rendition);
        return new ClipboardImage
        {
            Width = rendition.Width,
            Height = rendition.Height,
            Rgba = rendition.Sdr,
            HasAlpha = rendition.HasAlpha,
            Png = png.ToArray(),
        };
    }
}

/// <summary>Keeps one running instance and forwards the command lines of later launches to it.</summary>
public interface ISingleInstanceService : IDisposable
{
    /// <summary>Tries to become the primary instance.</summary>
    bool TryClaim();

    /// <summary>Raised on a background thread when another launch forwards its arguments.</summary>
    event EventHandler<IReadOnlyList<string>>? ArgumentsReceived;

    /// <summary>Starts accepting forwarded launches; only valid for the primary instance.</summary>
    void StartListening();

    /// <summary>Sends arguments to the primary instance.</summary>
    Task<bool> ForwardAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken = default);
}

/// <summary>Platform services the application depends on.</summary>
public sealed class PlatformServices
{
    /// <summary>The folder for settings and logs.</summary>
    public required string DataDirectory { get; init; }

    public required IScreenCaptureService Capture { get; init; }

    public required IWindowPickerService WindowPicker { get; init; }

    public required IGlobalHotkeyService Hotkeys { get; init; }

    public required IWindowIntegrationService Windows { get; init; }

    public required IShellService Shell { get; init; }

    /// <summary>The system clipboard, or null where the windowing backend's own clipboard serves.</summary>
    public IClipboardService? Clipboard { get; init; }

    public required ISingleInstanceService SingleInstance { get; init; }

    /// <summary>Gesture suggested for the capture hotkey on this platform.</summary>
    public required HotkeyGesture DefaultHotkey { get; init; }
}
