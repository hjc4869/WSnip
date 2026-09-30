using WSnip.Core.Capture;
using WSnip.Core.Platform;

namespace WSnip.Linux.Capture;

/// <summary>
/// Leaves the choice of window to the system's sharing dialog, which the portal shows when the
/// capture starts, so there is nothing to point at beforehand.
/// </summary>
public sealed class PortalWindowPicker(PortalScreenCaptureService capture) : IWindowPickerService
{
    public bool IsSupported => capture.IsSupported;

    public Task<CapturedWindow?> PickAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<CapturedWindow?>(new CapturedWindow(0, "Shared window", null, default));
}
