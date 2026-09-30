using System.Diagnostics;
using LightStudio.Logging;
using WSnip.Core.Capture;
using WSnip.Core.Platform;
using WSnip.Linux.Interop;
using WSnip.Linux.KWin;

namespace WSnip.Linux.Capture;

/// <summary>
/// Captures displays through KWin's screencast protocol where KWin offers it to WSnip, and through
/// the ScreenCast portal otherwise. KWin streams every display without asking and leaves WSnip's
/// own windows out; single windows are picked in the portal's sharing dialog either way.
/// </summary>
/// <remarks>KWin streams 8-bit SDR frames, as it does through the portal.</remarks>
public sealed class KWinScreenCaptureService : IScreenCaptureService, IDisposable
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    private readonly PortalScreenCaptureService portal;
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
                await PipeWireFrameReader.ReadAsync(null, streams.Select(stream => stream.Node).ToArray(), Timeout, cancellationToken).ConfigureAwait(false);
            SharedDisplay[] displays = streams.Select(stream => stream.Display).ToArray();
            MonitorCapture[] monitors = await Task.Run(() => DisplayLayout.CreateMonitors(displays, frames), cancellationToken).ConfigureAwait(false);
            AppLog.Information("Capture", $"Captured {monitors.Length} display(s) through KWin in {watch.ElapsedMilliseconds} ms: " +
                string.Join("; ", monitors.Select((m, i) => $"{m.DeviceName} {m.Bounds} logical {m.LogicalBounds} {frames[i].Info.Format}")));
            return new ScreenSnapshot { Monitors = monitors, CapturedAt = capturedAt };
        }
    }

    public Task<WindowCapture> CaptureWindowAsync(CapturedWindow window, CaptureOptions options, CancellationToken cancellationToken = default) =>
        portal.CaptureWindowAsync(window, options, cancellationToken);

    public void Dispose() => portal.Dispose();

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
