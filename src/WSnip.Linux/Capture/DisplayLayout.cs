using WSnip.Core.Capture;
using WSnip.Core.Imaging;
using WSnip.Linux.Interop;

namespace WSnip.Linux.Capture;

/// <summary>A display shared as a PipeWire stream.</summary>
/// <param name="Position">Top left in the compositor's logical coordinates, when known.</param>
/// <param name="Size">Size in the compositor's logical coordinates, when known.</param>
internal sealed record SharedDisplay(string DeviceName, string? FriendlyName, (int X, int Y)? Position, (int Width, int Height)? Size);

internal static class DisplayLayout
{
    /// <summary>
    /// Places each shared display by its position in the compositor's logical layout. Displays keep
    /// all their own pixels and are spread out at the largest display scale, so none overlaps the next.
    /// </summary>
    public static MonitorCapture[] CreateMonitors(IReadOnlyList<SharedDisplay> displays, IReadOnlyList<PipeWireFrame> frames)
    {
        var logical = new PixelRect[displays.Count];
        var scales = new double[displays.Count];
        for (int i = 0; i < displays.Count; i++)
        {
            SpaVideoInfo info = frames[i].Info;
            (int width, int height) = displays[i].Size is { Width: > 0, Height: > 0 } size ? size : (info.Width, info.Height);
            logical[i] = new PixelRect(displays[i].Position?.X ?? 0, displays[i].Position?.Y ?? 0, width, height);
            scales[i] = (double)info.Width / width;
        }

        // Displays without a position, such as a region, go beside the others rather than over them.
        int right = Enumerable.Range(0, displays.Count).Where(i => displays[i].Position is not null).Select(i => logical[i].Right).DefaultIfEmpty(0).Max();
        for (int i = 0; i < displays.Count; i++)
        {
            if (displays[i].Position is null && i > 0)
            {
                logical[i] = logical[i] with { X = right };
                right = logical[i].Right;
            }
        }

        double layout = scales.Max();
        var monitors = new MonitorCapture[displays.Count];
        for (int i = 0; i < displays.Count; i++)
        {
            SpaVideoInfo info = frames[i].Info;
            monitors[i] = new MonitorCapture
            {
                DeviceName = displays[i].DeviceName,
                FriendlyName = displays[i].FriendlyName,
                Bounds = new PixelRect((int)Math.Round(logical[i].X * layout), (int)Math.Round(logical[i].Y * layout), info.Width, info.Height),
                LogicalBounds = logical[i],
                Scaling = scales[i],
                Color = DisplayColorInfo.Sdr,
                Image = FrameConverter.ToLinear(frames[i], keepAlpha: false),
            };
        }

        return monitors;
    }
}
