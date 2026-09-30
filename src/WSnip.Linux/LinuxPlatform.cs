using System.Runtime.Versioning;
using WSnip.Core.Platform;
using WSnip.Linux.Capture;

[assembly: SupportedOSPlatform("linux")]

namespace WSnip.Linux;

public static class LinuxPlatform
{
    /// <summary>The app id: the Flatpak id, the desktop entry name and the bus name of the running instance.</summary>
    public const string AppId = "im.hjc.WSnip";

    /// <summary>Whether WSnip runs in a Flatpak sandbox.</summary>
    public static bool IsSandboxed { get; } =
        !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("FLATPAK_ID")) || File.Exists("/.flatpak-info");

    /// <summary>Where settings and logs live: WSnip under the XDG config folder, which a Flatpak keeps in its own data.</summary>
    public static string DataDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WSnip");

    /// <param name="compositorPlacesWindows">Whether the windowing backend is Wayland, where apps cannot place their windows.</param>
    public static PlatformServices Create(bool compositorPlacesWindows)
    {
        var bus = new SessionBus(IsSandboxed ? null : AppId);
        var portal = new PortalScreenCaptureService(bus, DataDirectory);
        var capture = new KWinScreenCaptureService(portal);
        return new PlatformServices
        {
            DataDirectory = DataDirectory,
            Capture = capture,
            WindowPicker = new PortalWindowPicker(portal),
            Hotkeys = new LinuxHotkeyService(),
            Windows = new LinuxWindowIntegrationService(canPlaceWindows: !compositorPlacesWindows, capture),
            Shell = new LinuxShellService(bus, DataDirectory),
            SingleInstance = new DBusSingleInstanceService(bus, AppId),
            DefaultHotkey = new HotkeyGesture("PrintScreen"),
        };
    }
}
