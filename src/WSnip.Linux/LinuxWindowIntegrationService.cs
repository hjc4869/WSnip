using WSnip.Core.Platform;
using WSnip.Linux.Capture;

namespace WSnip.Linux;

/// <summary>Window integration where the compositor draws window frames and decides what a capture shows.</summary>
/// <param name="canPlaceWindows">Whether the windowing backend places windows itself, as X11 does and Wayland does not.</param>
/// <param name="capture">The display capture, which leaves this app's windows out when it goes through KWin.</param>
internal sealed class LinuxWindowIntegrationService(bool canPlaceWindows, KWinScreenCaptureService capture) : IWindowIntegrationService
{
    public bool IsMicaSupported => false;

    public bool CanExcludeFromCapture => capture.ExcludesOwnWindows;

    public bool CanPlaceWindows => canPlaceWindows;

    public void SetMica(nint windowHandle, bool enabled, bool darkTheme)
    {
    }

    // The windowing backend passes the app's theme to the window frame.
    public void SetDarkFrame(nint windowHandle, bool darkTheme)
    {
    }

    // KWin leaves every window of this process out of the displays it streams.
    public void SetExcludedFromCapture(nint windowHandle, bool excluded)
    {
    }

    public bool TryGetCursorPosition(out int x, out int y)
    {
        x = y = 0;
        return false;
    }
}
