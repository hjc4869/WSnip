using System.Runtime.Versioning;
using WSnip.Core.Platform;
using WSnip.Windows.Capture;

namespace WSnip.Windows;

[SupportedOSPlatform("windows10.0.19041.0")]
public static class WindowsPlatform
{
    public const string AppId = "LightStudio.WSnip";

    public static PlatformServices Create() => new()
    {
        DataDirectory = WindowsPackage.DataDirectory,
        Capture = new WindowsScreenCaptureService(),
        WindowPicker = new WindowsWindowPicker(),
        Hotkeys = new WindowsHotkeyService(),
        Windows = new WindowsIntegrationService(),
        Shell = new WindowsShellService(),
        Clipboard = new WindowsClipboardService(),
        SingleInstance = new WindowsSingleInstanceService(AppId),
        DefaultHotkey = new HotkeyGesture("H", Shift: true, Windows: true),
    };
}
