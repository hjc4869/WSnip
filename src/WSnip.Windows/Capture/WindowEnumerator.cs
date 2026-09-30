using System.Runtime.InteropServices;
using WSnip.Core.Capture;
using WSnip.Core.Imaging;
using WSnip.Windows.Interop;

namespace WSnip.Windows.Capture;

/// <summary>Lists visible top-level windows in Z order, with their DWM frame bounds.</summary>
internal static unsafe class WindowEnumerator
{
    private const long TransparentStyle = 0x20;
    private const long LayeredStyle = 0x80000;

    private static readonly HashSet<string> IgnoredClasses = new(StringComparer.Ordinal)
    {
        "Progman", "WorkerW", "Windows.UI.Core.CoreWindow",
    };

    public static List<CapturedWindow> Enumerate(PixelRect virtualBounds)
    {
        uint self = (uint)Environment.ProcessId;
        nint shell = Native.GetShellWindow();
        var processNames = new Dictionary<uint, string?>();
        var windows = new List<CapturedWindow>();
        char* buffer = stackalloc char[256];
        foreach (nint handle in TopLevelWindows())
        {
            if (!IsCandidate(handle, self, shell, buffer, out uint processId, out PixelRect frame))
                continue;
            PixelRect bounds = frame.Intersect(virtualBounds);
            if (bounds.Width < 8 || bounds.Height < 8)
                continue;
            if (!processNames.TryGetValue(processId, out string? processName))
                processNames[processId] = processName = ProcessName(processId);
            windows.Add(new CapturedWindow(handle, Title(handle, buffer), processName, bounds));
        }

        return windows;
    }

    /// <summary>The topmost listed window under a point, with its whole frame, leaving out this process's windows.</summary>
    public static CapturedWindow? WindowAt(int x, int y)
    {
        uint self = (uint)Environment.ProcessId;
        nint shell = Native.GetShellWindow();
        char* buffer = stackalloc char[256];
        foreach (nint handle in TopLevelWindows())
        {
            if (IsCandidate(handle, self, shell, buffer, out uint processId, out PixelRect frame) &&
                frame.Width >= 8 && frame.Height >= 8 && frame.Contains(x, y))
            {
                return new CapturedWindow(handle, Title(handle, buffer), ProcessName(processId), frame);
            }
        }

        return null;
    }

    private static List<nint> TopLevelWindows()
    {
        var handles = new List<nint>();
        GCHandle list = GCHandle.Alloc(handles);
        try
        {
            Native.EnumWindows(&CollectWindow, GCHandle.ToIntPtr(list));
        }
        finally
        {
            list.Free();
        }

        return handles;
    }

    private static bool IsCandidate(nint handle, uint self, nint shell, char* buffer, out uint processId, out PixelRect frame)
    {
        processId = 0;
        frame = default;
        if (handle == shell || !Native.IsWindowVisible(handle) || Native.IsIconic(handle))
            return false;

        uint owner;
        Native.GetWindowThreadProcessId(handle, &owner);
        processId = owner;
        if (owner == self)
            return false;

        int cloaked = 0;
        if (Native.DwmGetWindowAttribute(handle, Native.DwmCloaked, &cloaked, sizeof(int)) >= 0 && cloaked != 0)
            return false;

        long style = Native.GetWindowLongPtr(handle, Native.ExtendedStyle);
        if ((style & TransparentStyle) != 0 && (style & LayeredStyle) != 0)
            return false;

        int classLength = Native.GetClassName(handle, buffer, 256);
        if (classLength > 0 && IgnoredClasses.Contains(new string(buffer, 0, classLength)))
            return false;

        Rect bounds;
        if (Native.DwmGetWindowAttribute(handle, Native.DwmExtendedFrameBounds, &bounds, (uint)sizeof(Rect)) < 0)
            return false;
        frame = PixelRect.FromEdges(bounds.Left, bounds.Top, bounds.Right, bounds.Bottom);
        return true;
    }

    private static string Title(nint handle, char* buffer)
    {
        int length = Native.GetWindowText(handle, buffer, 256);
        return length > 0 ? new string(buffer, 0, length) : string.Empty;
    }

    [UnmanagedCallersOnly]
    private static int CollectWindow(nint window, nint data)
    {
        ((List<nint>)GCHandle.FromIntPtr(data).Target!).Add(window);
        return 1;
    }

    private static string? ProcessName(uint processId)
    {
        nint process = Native.OpenProcess(Native.ProcessQueryLimitedInformation, false, processId);
        if (process == 0)
            return null;
        try
        {
            char* path = stackalloc char[1024];
            uint size = 1024;
            return Native.QueryFullProcessImageName(process, 0, path, &size)
                ? Path.GetFileNameWithoutExtension(new string(path, 0, (int)size))
                : null;
        }
        finally
        {
            Native.CloseHandle(process);
        }
    }
}
