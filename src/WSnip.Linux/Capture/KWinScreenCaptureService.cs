using System.Collections.Concurrent;
using System.Diagnostics;
using LightStudio.Logging;
using WSnip.Core.Capture;
using WSnip.Core.Imaging;
using WSnip.Core.Platform;
using WSnip.Linux.Interop;
using WSnip.Linux.KWin;

namespace WSnip.Linux.Capture;

/// <summary>
/// Captures through KWin's screencast protocol where KWin offers it to WSnip, and through the
/// ScreenCast portal otherwise. KWin streams every display without asking and leaves WSnip's own
/// windows out, and streams a window that <see cref="KWinWindowPicker"/> picked whole.
/// </summary>
/// <remarks>KWin streams 8-bit SDR frames, as it does through the portal.</remarks>
public sealed class KWinScreenCaptureService : IScreenCaptureService, IDisposable
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    private readonly PortalScreenCaptureService portal;
    private readonly ConcurrentDictionary<nint, string> pickedWindows = new();
    private long lastPickedWindow;
    private bool? available;

    internal KWinScreenCaptureService(PortalScreenCaptureService portal) => this.portal = portal;

    public bool IsSupported => portal.IsSupported;

    /// <summary>Whether displays are captured through KWin, which leaves this app's windows out of them.</summary>
    public bool ExcludesOwnWindows
    {
        get
        {
            if (available is null)
            {
                using KWinScreencast? kwin = Connect();
                Remember(kwin is not null);
            }

            return available!.Value;
        }
    }

    public async Task<ScreenSnapshot> CaptureAsync(CaptureOptions options, CancellationToken cancellationToken = default)
    {
        var watch = Stopwatch.StartNew();
        KWinScreencast? kwin = await Task.Run(Connect, cancellationToken).ConfigureAwait(false);
        Remember(kwin is not null);
        if (kwin is null)
            return await portal.CaptureAsync(options, cancellationToken).ConfigureAwait(false);

        using (kwin)
        {
            List<(SharedDisplay Display, uint Node)> streams =
                await Task.Run(() => kwin.StreamDisplays(options.IncludeCursor, Timeout), cancellationToken).ConfigureAwait(false);
            DateTimeOffset capturedAt = DateTimeOffset.Now;
            IReadOnlyList<PipeWireFrame> frames =
                await PipeWireFrameReader.ReadAsync(null, streams.Select(stream => stream.Node).ToArray(), alpha: false, Timeout, cancellationToken).ConfigureAwait(false);
            SharedDisplay[] displays = streams.Select(stream => stream.Display).ToArray();
            MonitorCapture[] monitors = await Task.Run(() => DisplayLayout.CreateMonitors(displays, frames), cancellationToken).ConfigureAwait(false);
            AppLog.Information("Capture", $"Captured {monitors.Length} display(s) through KWin in {watch.ElapsedMilliseconds} ms: " +
                string.Join("; ", monitors.Select((m, i) => $"{m.DeviceName} {m.Bounds} logical {m.LogicalBounds} {frames[i].Info.Format}")));
            return new ScreenSnapshot { Monitors = monitors, CapturedAt = capturedAt };
        }
    }

    public async Task<WindowCapture> CaptureWindowAsync(CapturedWindow window, CaptureOptions options, CancellationToken cancellationToken = default)
    {
        if (!pickedWindows.TryRemove(window.Handle, out string? uuid))
            return await portal.CaptureWindowAsync(window, options, cancellationToken).ConfigureAwait(false);

        var watch = Stopwatch.StartNew();
        using KWinScreencast kwin = await Task.Run(Connect, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"KWin no longer offers {KWinScreencast.Interface} to WSnip.");
        uint node = await Task.Run(() => kwin.StreamWindow(uuid, options.IncludeCursor, Timeout), cancellationToken).ConfigureAwait(false);
        DateTimeOffset capturedAt = DateTimeOffset.Now;
        PipeWireFrame frame = (await PipeWireFrameReader.ReadAsync(null, [node], alpha: true, Timeout, cancellationToken).ConfigureAwait(false))[0];
        HdrImage image = await Task.Run(() => FrameConverter.ToLinear(frame, keepAlpha: true), cancellationToken).ConfigureAwait(false);
        AppLog.Information("Capture", $"Captured the window '{window.Title}' through KWin: {image.Width}x{image.Height} ({frame.Info.Format}) in {watch.ElapsedMilliseconds} ms.");
        return new WindowCapture
        {
            Window = window with { Bounds = new PixelRect(0, 0, image.Width, image.Height) },
            Color = DisplayColorInfo.Sdr,
            Image = image,
            CapturedAt = capturedAt,
        };
    }

    public void Dispose() => portal.Dispose();

    /// <summary>Checks afresh whether KWin streams to this app.</summary>
    internal async Task<bool> IsKWinAvailableAsync()
    {
        using KWinScreencast? kwin = await Task.Run(Connect).ConfigureAwait(false);
        Remember(kwin is not null);
        return kwin is not null;
    }

    /// <summary>Gives a window KWin picked a handle, which <see cref="CaptureWindowAsync"/> captures through KWin.</summary>
    /// <param name="uuid">KWin's id of the window.</param>
    internal nint AddPickedWindow(string uuid)
    {
        var handle = (nint)Interlocked.Increment(ref lastPickedWindow);
        pickedWindows[handle] = uuid;
        return handle;
    }

    private static KWinScreencast? Connect() => PipeWire.IsAvailable ? KWinScreencast.TryConnect() : null;

    private void Remember(bool kwin)
    {
        if (available != kwin)
        {
            AppLog.Information("Capture", kwin
                ? "KWin streams the displays."
                : $"Displays are captured through the ScreenCast portal, as the compositor does not offer {KWinScreencast.Interface} to WSnip.");
        }

        available = kwin;
    }
}
