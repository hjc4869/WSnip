using System.Diagnostics;
using LightStudio.Logging;
using Microsoft.Win32.SafeHandles;
using Tmds.DBus.Protocol;
using WSnip.Core.Capture;
using WSnip.Core.Imaging;
using WSnip.Core.Platform;
using WSnip.Linux.Interop;
using WSnip.Linux.Portal;

namespace WSnip.Linux.Capture;

/// <summary>
/// Captures displays and windows through the desktop's ScreenCast portal, reading one frame from
/// each shared PipeWire stream. The first capture asks which displays to share; the portal's
/// restore token lets later captures skip the question until the user withdraws the permission.
/// </summary>
/// <remarks>Compositors share 8-bit SDR frames, which become scRGB with SDR white at one.</remarks>
public sealed class PortalScreenCaptureService : IScreenCaptureService, IDisposable
{
    private const string MissingPipeWire =
        "WSnip captures the screen through PipeWire, which this system does not have. Install PipeWire and the screen sharing " +
        "portal of your desktop, such as xdg-desktop-portal-kde or xdg-desktop-portal-gnome, then sign in again.";

    private const string MissingPortal =
        "WSnip captures the screen through the desktop's screen sharing portal, which this session does not offer. Install " +
        "xdg-desktop-portal with the portal of your desktop, such as xdg-desktop-portal-kde or xdg-desktop-portal-gnome, and " +
        "PipeWire, then sign in again.";

    private static readonly TimeSpan FrameTimeout = TimeSpan.FromSeconds(5);

    private readonly SessionBus bus;
    private readonly string restoreTokenPath;
    private readonly SemaphoreSlim gate = new(1, 1);

    internal PortalScreenCaptureService(SessionBus bus, string dataDirectory)
    {
        this.bus = bus;
        restoreTokenPath = Path.Combine(dataDirectory, "screencast-restore-token");
    }

    public bool IsSupported => PipeWire.IsAvailable;

    public async Task<ScreenSnapshot> CaptureAsync(CaptureOptions options, CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var watch = Stopwatch.StartNew();
            await using ScreenCastSession session = await StartAsync(new ScreenCastRequest
            {
                Sources = ScreenCastSources.Monitor,
                Multiple = true,
                IncludeCursor = options.IncludeCursor,
                Persist = true,
                RestoreToken = ReadRestoreToken(),
            }, cancellationToken).ConfigureAwait(false);
            SaveRestoreToken(session.RestoreToken);

            DateTimeOffset capturedAt = DateTimeOffset.Now;
            IReadOnlyList<PipeWireFrame> frames = await GrabAsync(session, alpha: false, cancellationToken).ConfigureAwait(false);
            SharedDisplay[] displays = session.Streams.Select(stream => new SharedDisplay($"node {stream.NodeId}", null, stream.Position, stream.Size)).ToArray();
            MonitorCapture[] monitors = await Task.Run(() => DisplayLayout.CreateMonitors(displays, frames), cancellationToken).ConfigureAwait(false);
            AppLog.Information("Capture", $"Captured {monitors.Length} display(s) in {watch.ElapsedMilliseconds} ms: " +
                string.Join("; ", monitors.Select((m, i) => $"{m.DeviceName} {m.Bounds} logical {m.LogicalBounds} {frames[i].Info.Format}")));
            return new ScreenSnapshot { Monitors = monitors, CapturedAt = capturedAt };
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>Asks the user to choose a window in the system's sharing dialog and captures it.</summary>
    public async Task<WindowCapture> CaptureWindowAsync(CapturedWindow window, CaptureOptions options, CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var watch = Stopwatch.StartNew();
            await using ScreenCastSession session = await StartAsync(new ScreenCastRequest
            {
                Sources = ScreenCastSources.Window,
                IncludeCursor = options.IncludeCursor,
            }, cancellationToken).ConfigureAwait(false);

            DateTimeOffset capturedAt = DateTimeOffset.Now;
            PipeWireFrame frame = (await GrabAsync(session, alpha: true, cancellationToken).ConfigureAwait(false))[0];
            HdrImage image = await Task.Run(() => FrameConverter.ToLinear(frame, keepAlpha: true), cancellationToken).ConfigureAwait(false);
            AppLog.Information("Capture", $"Captured a window {image.Width}x{image.Height} ({frame.Info.Format}) in {watch.ElapsedMilliseconds} ms.");
            return new WindowCapture
            {
                Window = window with { Bounds = new PixelRect(0, 0, image.Width, image.Height) },
                Color = DisplayColorInfo.Sdr,
                Image = image,
                CapturedAt = capturedAt,
            };
        }
        finally
        {
            gate.Release();
        }
    }

    public void Dispose() => gate.Dispose();

    private async Task<ScreenCastSession> StartAsync(ScreenCastRequest request, CancellationToken cancellationToken)
    {
        if (!PipeWire.IsAvailable)
            throw new PlatformNotSupportedException(MissingPipeWire);

        DBusConnection connection;
        ScreenCastCapabilities capabilities;
        try
        {
            connection = await bus.ConnectToPortalAsync().ConfigureAwait(false);
            capabilities = await ScreenCastSession.QueryAsync(connection).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is DBusConnectionException or InvalidOperationException ||
                                          exception is DBusErrorReplyException reply && DesktopPortal.IsMissing(reply))
        {
            throw new PlatformNotSupportedException(MissingPortal, exception);
        }

        return await ScreenCastSession.StartAsync(connection, capabilities, request, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<IReadOnlyList<PipeWireFrame>> GrabAsync(ScreenCastSession session, bool alpha, CancellationToken cancellationToken)
    {
        using SafeFileHandle remote = await session.OpenPipeWireRemoteAsync().ConfigureAwait(false);
        uint[] nodes = session.Streams.Select(stream => stream.NodeId).ToArray();
        return await PipeWireFrameReader.ReadAsync(remote, nodes, alpha, FrameTimeout, cancellationToken).ConfigureAwait(false);
    }

    private string? ReadRestoreToken()
    {
        try
        {
            return File.Exists(restoreTokenPath) ? File.ReadAllText(restoreTokenPath).Trim() : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            AppLog.Warning("Capture", "The screen sharing permission could not be read; the system will ask again.", exception);
            return null;
        }
    }

    /// <summary>Keeps the token of the latest session; each token restores one session only.</summary>
    private void SaveRestoreToken(string? token)
    {
        try
        {
            if (string.IsNullOrEmpty(token))
            {
                File.Delete(restoreTokenPath);
                return;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(restoreTokenPath)!);
            File.WriteAllText(restoreTokenPath, token);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            AppLog.Warning("Capture", "The screen sharing permission could not be saved; the system will ask again next time.", exception);
        }
    }
}
