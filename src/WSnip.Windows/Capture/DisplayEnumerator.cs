using System.Runtime.InteropServices;
using WSnip.Core.Capture;
using WSnip.Core.Imaging;
using WSnip.Windows.Interop;

namespace WSnip.Windows.Capture;

internal sealed record DisplayInfo(
    nint Handle,
    string DeviceName,
    string? FriendlyName,
    PixelRect Bounds,
    bool IsPrimary,
    double Scaling,
    DisplayColorInfo Color);

/// <summary>Lists displays with their placement, scaling and advanced color state.</summary>
internal static unsafe class DisplayEnumerator
{
    private const uint OnlyActivePaths = 2;
    private const int GetSourceName = 1;
    private const int GetTargetName = 2;
    private const int GetAdvancedColorInfo = 9;
    private const int GetSdrWhiteLevel = 11;
    private const int DxgiColorSpaceHdr10 = 12;
    private const int DxgiNotFound = unchecked((int)0x887A0002);

    private static readonly Guid DxgiFactory1 = new("770aae78-f26f-4dba-a829-253c83d1b387");
    private static readonly Guid DxgiOutput6 = new("068346e8-aaec-4b84-add7-137f513f77a1");

    public static List<DisplayInfo> Enumerate()
    {
        var handles = new List<nint>();
        GCHandle list = GCHandle.Alloc(handles);
        try
        {
            Native.EnumDisplayMonitors(0, 0, &CollectMonitor, GCHandle.ToIntPtr(list));
        }
        finally
        {
            list.Free();
        }

        Dictionary<string, TargetState> targets = QueryTargets();
        Dictionary<nint, OutputState> outputs = QueryOutputs();
        var displays = new List<DisplayInfo>(handles.Count);
        foreach (nint handle in handles)
        {
            MonitorInfoEx info = default;
            info.Size = sizeof(MonitorInfoEx);
            if (!Native.GetMonitorInfo(handle, &info))
                continue;

            string device = new(info.Device);
            targets.TryGetValue(device, out TargetState target);
            outputs.TryGetValue(handle, out OutputState output);
            uint dpiX = 96, dpiY = 96;
            if (Native.GetDpiForMonitor(handle, 0, &dpiX, &dpiY) < 0)
                dpiX = 96;

            bool hdr = output.Hdr;
            var color = new DisplayColorInfo(
                hdr,
                hdr || target.AdvancedColor,
                hdr && target.SdrWhiteNits > 0 ? target.SdrWhiteNits : ColorMath.ScRgbUnitNits,
                output.MaxLuminance > 0 ? output.MaxLuminance : null,
                output.MinLuminance > 0 ? output.MinLuminance : null);
            displays.Add(new DisplayInfo(
                handle,
                device,
                string.IsNullOrWhiteSpace(target.FriendlyName) ? null : target.FriendlyName,
                PixelRect.FromEdges(info.Monitor.Left, info.Monitor.Top, info.Monitor.Right, info.Monitor.Bottom),
                (info.Flags & Native.MonitorInfoPrimary) != 0,
                dpiX / 96.0,
                color));
        }

        return displays;
    }

    [UnmanagedCallersOnly]
    private static int CollectMonitor(nint monitor, nint hdc, Rect* bounds, nint data)
    {
        ((List<nint>)GCHandle.FromIntPtr(data).Target!).Add(monitor);
        return 1;
    }

    private record struct TargetState(string? FriendlyName, double SdrWhiteNits, bool AdvancedColor);

    private record struct OutputState(bool Hdr, double MaxLuminance, double MinLuminance);

    private static Dictionary<string, TargetState> QueryTargets()
    {
        var result = new Dictionary<string, TargetState>(StringComparer.OrdinalIgnoreCase);
        uint pathCount, modeCount;
        if (Native.GetDisplayConfigBufferSizes(OnlyActivePaths, &pathCount, &modeCount) != 0)
            return result;

        var paths = new DisplayConfigPathInfo[pathCount];
        var modes = new DisplayConfigModeInfo[modeCount];
        fixed (DisplayConfigPathInfo* pathData = paths)
        fixed (DisplayConfigModeInfo* modeData = modes)
        {
            if (Native.QueryDisplayConfig(OnlyActivePaths, &pathCount, pathData, &modeCount, modeData, 0) != 0)
                return result;
        }

        for (int i = 0; i < pathCount; i++)
        {
            DisplayConfigPathInfo path = paths[i];
            var source = new DisplayConfigSourceName
            {
                Header = Header(GetSourceName, sizeof(DisplayConfigSourceName), path.SourceAdapter, path.SourceId),
            };
            if (Native.DisplayConfigGetDeviceInfo(&source) != 0)
                continue;

            var target = new DisplayConfigTargetName
            {
                Header = Header(GetTargetName, sizeof(DisplayConfigTargetName), path.TargetAdapter, path.TargetId),
            };
            string? friendly = Native.DisplayConfigGetDeviceInfo(&target) == 0 ? new string(target.FriendlyName) : null;

            var white = new DisplayConfigSdrWhiteLevel
            {
                Header = Header(GetSdrWhiteLevel, sizeof(DisplayConfigSdrWhiteLevel), path.TargetAdapter, path.TargetId),
            };
            double whiteNits = Native.DisplayConfigGetDeviceInfo(&white) == 0 && white.SdrWhiteLevel > 0
                ? white.SdrWhiteLevel / 1000.0 * ColorMath.ScRgbUnitNits
                : 0;

            var advanced = new DisplayConfigAdvancedColorInfo
            {
                Header = Header(GetAdvancedColorInfo, sizeof(DisplayConfigAdvancedColorInfo), path.TargetAdapter, path.TargetId),
            };
            bool advancedColor = Native.DisplayConfigGetDeviceInfo(&advanced) == 0 && (advanced.Value & 2) != 0;
            result[new string(source.GdiDeviceName)] = new TargetState(friendly, whiteNits, advancedColor);
        }

        return result;
    }

    private static DisplayConfigHeader Header(int type, int size, Luid adapter, uint id) =>
        new() { Type = type, Size = size, Adapter = adapter, Id = id };

    private static Dictionary<nint, OutputState> QueryOutputs()
    {
        var result = new Dictionary<nint, OutputState>();
        Guid iid = DxgiFactory1;
        nint factory;
        if (Native.CreateDXGIFactory1(&iid, &factory) < 0)
            return result;

        try
        {
            for (uint adapterIndex = 0; ; adapterIndex++)
            {
                nint adapter;
                int hr = ((delegate* unmanaged[Stdcall]<nint, uint, nint*, int>)Vtbl.Slot(factory, 12))(factory, adapterIndex, &adapter);
                if (hr == DxgiNotFound)
                    break;
                Native.ThrowIfFailed(hr, "IDXGIFactory1.EnumAdapters1");
                try
                {
                    for (uint outputIndex = 0; ; outputIndex++)
                    {
                        nint output;
                        hr = ((delegate* unmanaged[Stdcall]<nint, uint, nint*, int>)Vtbl.Slot(adapter, 7))(adapter, outputIndex, &output);
                        if (hr == DxgiNotFound)
                            break;
                        Native.ThrowIfFailed(hr, "IDXGIAdapter.EnumOutputs");
                        nint output6 = Vtbl.TryQueryInterface(output, DxgiOutput6);
                        Vtbl.Release(output);
                        if (output6 == 0)
                            continue;
                        try
                        {
                            DxgiOutputDesc1 desc;
                            if (((delegate* unmanaged[Stdcall]<nint, DxgiOutputDesc1*, int>)Vtbl.Slot(output6, 27))(output6, &desc) >= 0)
                                result[desc.Monitor] = new OutputState(desc.ColorSpace == DxgiColorSpaceHdr10, desc.MaxLuminance, desc.MinLuminance);
                        }
                        finally
                        {
                            Vtbl.Release(output6);
                        }
                    }
                }
                finally
                {
                    Vtbl.Release(adapter);
                }
            }
        }
        finally
        {
            Vtbl.Release(factory);
        }

        return result;
    }
}
