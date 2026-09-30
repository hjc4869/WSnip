using WSnip.Core.Platform;

namespace WSnip.Linux;

/// <summary>
/// Global shortcuts are left to the desktop, whose shortcut settings can give one to the New snip
/// action of WSnip's desktop entry.
/// </summary>
internal sealed class LinuxHotkeyService : IGlobalHotkeyService
{
    public bool IsSupported => false;

    public event EventHandler<HotkeyGesture>? Pressed
    {
        add { }
        remove { }
    }

    public string? Register(HotkeyGesture gesture) => "Shortcuts are assigned in the system settings.";

    public void UnregisterAll()
    {
    }

    public void Dispose()
    {
    }
}
