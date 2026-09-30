using System.Diagnostics;
using System.Runtime.Versioning;
using LightStudio.Logging;
using Windows.Graphics;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using WSnip.Core.Capture;
using WSnip.Core.Imaging;
using WSnip.Core.Platform;
using WSnip.Core.Strings;
using WSnip.Windows.Interop;

namespace WSnip.Windows.Capture;

/// <summary>
/// Captures every display through Windows.Graphics.Capture in 16-bit float scRGB, which is the
/// format the compositor itself blends in when HDR or automatic color management is on, so HDR
/// highlights and wide-gamut colors survive exactly as they were shown.
/// </summary>
[SupportedOSPlatform("windows10.0.19041.0")]
public sealed class WindowsScreenCaptureService : IScreenCaptureService, IDisposable
{
    private static readonly Guid CaptureItemInterop = new("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356");
    private static readonly Guid CaptureItem = new("79C3F95B-31F7-4EC2-A464-632EF5D30760");
    private static readonly TimeSpan FrameTimeout = TimeSpan.FromSeconds(3);

    private readonly SemaphoreSlim gate = new(1, 1);
    private Direct3DDevice? device;
    private bool? borderless;

    public bool IsSupported => GraphicsCaptureSession.IsSupported();

    public async Task<ScreenSnapshot> CaptureAsync(CaptureOptions options, CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var watch = Stopwatch.StartNew();
            List<DisplayInfo> displays = DisplayEnumerator.Enumerate();
            if (displays.Count == 0)
                throw new InvalidOperationException(AppStrings.NoDisplays);

            PixelRect virtualBounds = displays.Aggregate(default(PixelRect), (bounds, display) => bounds.Union(display.Bounds));
            List<CapturedWindow> windows = WindowEnumerator.Enumerate(virtualBounds);
            DateTimeOffset capturedAt = DateTimeOffset.Now;
            bool allowBorderless = await EnsureBorderlessAsync().ConfigureAwait(false);

            MonitorCapture[] monitors;
            try
            {
                device ??= Direct3DDevice.Create();
                monitors = await Task.WhenAll(displays.Select(display =>
                    Task.Run(() => CaptureMonitor(device, display, options, allowBorderless, cancellationToken), cancellationToken)))
                    .ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // A removed or reset device fails every later call, so the next capture starts afresh.
                device?.Dispose();
                device = null;
                throw;
            }

            AppLog.Information("Capture", $"Captured {monitors.Length} display(s) and {windows.Count} window(s) in {watch.ElapsedMilliseconds} ms: " +
                string.Join("; ", monitors.Select(m => $"{m.DeviceName} {m.Bounds} hdr={m.Color.HdrActive} white={m.Color.SdrWhiteNits:0} peak={m.Color.MaxLuminanceNits:0}")));
            return new ScreenSnapshot { Monitors = monitors, Windows = windows, CapturedAt = capturedAt };
        }
        finally
        {
            gate.Release();
        }
    }

    public void Dispose()
    {
        device?.Dispose();
        device = null;
        gate.Dispose();
    }

    public async Task<WindowCapture> CaptureWindowAsync(CapturedWindow window, CaptureOptions options, CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var watch = Stopwatch.StartNew();
            nint monitor = Native.MonitorFromWindow(window.Handle, Native.MonitorDefaultToNearest);
            DisplayColorInfo color = DisplayEnumerator.Enumerate().FirstOrDefault(d => d.Handle == monitor)?.Color ?? DisplayColorInfo.Sdr;
            DateTimeOffset capturedAt = DateTimeOffset.Now;
            bool allowBorderless = await EnsureBorderlessAsync().ConfigureAwait(false);

            HdrImage image;
            try
            {
                device ??= Direct3DDevice.Create();
                Direct3DDevice current = device;
                image = await Task.Run(() => CaptureWindow(current, window, options, allowBorderless, cancellationToken), cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                device?.Dispose();
                device = null;
                throw;
            }

            AppLog.Information("Capture", $"Captured window '{window.Title}' ({window.ProcessName}) {image.Width}x{image.Height} in " +
                $"{watch.ElapsedMilliseconds} ms: hdr={color.HdrActive} white={color.SdrWhiteNits:0}");
            return new WindowCapture { Window = window, Color = color, Image = image, CapturedAt = capturedAt };
        }
        finally
        {
            gate.Release();
        }
    }

    private static HdrImage CaptureWindow(Direct3DDevice device, CapturedWindow window, CaptureOptions options, bool allowBorderless,
        CancellationToken cancellationToken)
    {
        GraphicsCaptureItem item = CreateItem(window.Handle, CreateForWindow);
        SizeInt32 size = item.Size;
        if (size.Width <= 0 || size.Height <= 0)
            throw new InvalidOperationException(string.Format(AppStrings.NoWindowContent, window.Title));

        using Direct3D11CaptureFramePool pool = Direct3D11CaptureFramePool.CreateFreeThreaded(
            device.WinRTDevice, DirectXPixelFormat.R16G16B16A16Float, 1, size);
        using GraphicsCaptureSession session = pool.CreateCaptureSession(item);
        session.IsCursorCaptureEnabled = options.IncludeCursor;
        if (allowBorderless && OperatingSystem.IsWindowsVersionAtLeast(10, 0, 20348))
            session.IsBorderRequired = false;

        session.StartCapture();
        var watch = Stopwatch.StartNew();
        bool resized = false;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using Direct3D11CaptureFrame? frame = pool.TryGetNextFrame();
            if (frame is not null)
            {
                // A window that grew since the pool was made only fits a pool of its new size.
                SizeInt32 content = frame.ContentSize;
                if (!resized && (content.Width > size.Width || content.Height > size.Height))
                {
                    resized = true;
                    size = content;
                    pool.Recreate(device.WinRTDevice, DirectXPixelFormat.R16G16B16A16Float, 1, size);
                    continue;
                }

                return device.Read(frame.Surface, Math.Min(content.Width, size.Width), Math.Min(content.Height, size.Height), keepAlpha: true);
            }

            if (watch.Elapsed > FrameTimeout)
                throw new TimeoutException(string.Format(AppStrings.WindowNoFrame, window.Title));
            Thread.Sleep(2);
        }
    }

    private static MonitorCapture CaptureMonitor(Direct3DDevice device, DisplayInfo display, CaptureOptions options,
        bool allowBorderless, CancellationToken cancellationToken)
    {
        GraphicsCaptureItem item = CreateItem(display.Handle, CreateForMonitor);
        using Direct3D11CaptureFramePool pool = Direct3D11CaptureFramePool.CreateFreeThreaded(
            device.WinRTDevice, DirectXPixelFormat.R16G16B16A16Float, 1, item.Size);
        using GraphicsCaptureSession session = pool.CreateCaptureSession(item);
        session.IsCursorCaptureEnabled = options.IncludeCursor;
        if (allowBorderless && OperatingSystem.IsWindowsVersionAtLeast(10, 0, 20348))
            session.IsBorderRequired = false;

        session.StartCapture();
        var watch = Stopwatch.StartNew();
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using Direct3D11CaptureFrame? frame = pool.TryGetNextFrame();
            if (frame is not null)
            {
                HdrImage image = device.Read(frame.Surface, display.Bounds.Width, display.Bounds.Height);
                return new MonitorCapture
                {
                    DeviceName = display.DeviceName,
                    FriendlyName = display.FriendlyName,
                    Bounds = display.Bounds with { Width = image.Width, Height = image.Height },
                    Scaling = display.Scaling,
                    IsPrimary = display.IsPrimary,
                    Color = display.Color,
                    Image = image,
                };
            }

            if (watch.Elapsed > FrameTimeout)
                throw new TimeoutException(string.Format(AppStrings.DisplayNoFrame, display.DeviceName));
            Thread.Sleep(2);
        }
    }

    // IGraphicsCaptureItemInterop methods after IUnknown.
    private const int CreateForWindow = 3;
    private const int CreateForMonitor = 4;

    private static unsafe GraphicsCaptureItem CreateItem(nint handle, int method)
    {
        nint factory = Native.GetActivationFactory("Windows.Graphics.Capture.GraphicsCaptureItem", CaptureItemInterop);
        nint item = 0;
        try
        {
            Guid iid = CaptureItem;
            Native.ThrowIfFailed(((delegate* unmanaged[Stdcall]<nint, nint, Guid*, nint*, int>)Vtbl.Slot(factory, method))(factory, handle, &iid, &item),
                method == CreateForWindow ? "IGraphicsCaptureItemInterop.CreateForWindow" : "IGraphicsCaptureItemInterop.CreateForMonitor");
            return GraphicsCaptureItem.FromAbi(item);
        }
        finally
        {
            Vtbl.Release(item);
            Vtbl.Release(factory);
        }
    }

    /// <summary>Asks once whether the capture may skip the yellow border around the display.</summary>
    private async Task<bool> EnsureBorderlessAsync()
    {
        if (borderless is { } known)
            return known;
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 20348))
            return (borderless = false).Value;

        try
        {
            var status = await GraphicsCaptureAccess.RequestAccessAsync(GraphicsCaptureAccessKind.Borderless);
            borderless = status == global::Windows.Security.Authorization.AppCapabilityAccess.AppCapabilityAccessStatus.Allowed;
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or NotSupportedException or System.Runtime.InteropServices.COMException)
        {
            AppLog.Warning("Capture", "Borderless capture is unavailable.", exception);
            borderless = false;
        }

        return borderless.Value;
    }
}
