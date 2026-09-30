using System.Runtime.Versioning;
using WSnip.Core.Platform;
using WSnip.Windows.Interop;

namespace WSnip.Windows;

/// <summary>
/// DWM window integration: the Mica Alt backdrop, the caption theme and the display affinity
/// that keeps the app's own windows out of its captures.
/// </summary>
/// <remarks>
/// Avalonia's own Mica level only approximates the material inside the client area; a transparent
/// window with the compositor's backdrop gives one material across the caption and the content.
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed unsafe class WindowsIntegrationService : IWindowIntegrationService
{
    private const int NoBackdrop = 1;
    private const int TabbedBackdrop = 4;
    private const uint AffinityNone = 0;
    private const uint AffinityExcludeFromCapture = 0x11;

    public bool IsMicaSupported => OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000);

    public void SetMica(nint windowHandle, bool enabled, bool darkTheme)
    {
        if (windowHandle == 0 || !IsMicaSupported)
            return;
        SetDarkFrame(windowHandle, darkTheme);
        int backdrop = enabled ? TabbedBackdrop : NoBackdrop;
        Native.DwmSetWindowAttribute(windowHandle, Native.DwmSystemBackdropType, &backdrop, sizeof(int));
    }

    public void SetDarkFrame(nint windowHandle, bool darkTheme)
    {
        if (windowHandle == 0)
            return;
        int dark = darkTheme ? 1 : 0;
        Native.DwmSetWindowAttribute(windowHandle, Native.DwmUseImmersiveDarkMode, &dark, sizeof(int));
    }

    public void SetExcludedFromCapture(nint windowHandle, bool excluded)
    {
        if (windowHandle != 0)
            Native.SetWindowDisplayAffinity(windowHandle, excluded ? AffinityExcludeFromCapture : AffinityNone);
    }

    public bool TryGetCursorPosition(out int x, out int y)
    {
        Point point;
        bool ok = Native.GetCursorPos(&point);
        x = point.X;
        y = point.Y;
        return ok;
    }
}
