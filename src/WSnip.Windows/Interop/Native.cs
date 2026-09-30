using System.Runtime.InteropServices;

namespace WSnip.Windows.Interop;

[StructLayout(LayoutKind.Sequential)]
internal struct Rect
{
    public int Left;
    public int Top;
    public int Right;
    public int Bottom;
}

[StructLayout(LayoutKind.Sequential)]
internal struct Point
{
    public int X;
    public int Y;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct MonitorInfoEx
{
    public int Size;
    public Rect Monitor;
    public Rect Work;
    public uint Flags;
    public fixed char Device[32];
}

[StructLayout(LayoutKind.Sequential)]
internal struct Msg
{
    public nint Window;
    public uint Message;
    public nuint WParam;
    public nint LParam;
    public uint Time;
    public Point Point;
    public uint Private;
}

[StructLayout(LayoutKind.Sequential)]
internal struct Luid
{
    public uint Low;
    public int High;
}

[StructLayout(LayoutKind.Sequential)]
internal struct DisplayConfigPathInfo
{
    public Luid SourceAdapter;
    public uint SourceId;
    public uint SourceModeIndex;
    public uint SourceStatus;
    public Luid TargetAdapter;
    public uint TargetId;
    public uint TargetModeIndex;
    public int OutputTechnology;
    public int Rotation;
    public int Scaling;
    public uint RefreshNumerator;
    public uint RefreshDenominator;
    public int ScanLineOrdering;
    public int TargetAvailable;
    public uint TargetStatus;
    public uint Flags;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct DisplayConfigModeInfo
{
    public fixed byte Data[64];
}

[StructLayout(LayoutKind.Sequential)]
internal struct DisplayConfigHeader
{
    public int Type;
    public int Size;
    public Luid Adapter;
    public uint Id;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct DisplayConfigSourceName
{
    public DisplayConfigHeader Header;
    public fixed char GdiDeviceName[32];
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct DisplayConfigTargetName
{
    public DisplayConfigHeader Header;
    public uint Flags;
    public int OutputTechnology;
    public ushort ManufactureId;
    public ushort ProductCodeId;
    public uint ConnectorInstance;
    public fixed char FriendlyName[64];
    public fixed char DevicePath[128];
}

[StructLayout(LayoutKind.Sequential)]
internal struct DisplayConfigSdrWhiteLevel
{
    public DisplayConfigHeader Header;
    public uint SdrWhiteLevel;
}

[StructLayout(LayoutKind.Sequential)]
internal struct DisplayConfigAdvancedColorInfo
{
    public DisplayConfigHeader Header;
    public uint Value;
    public int ColorEncoding;
    public uint BitsPerColorChannel;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct DxgiOutputDesc1
{
    public fixed char DeviceName[32];
    public Rect DesktopCoordinates;
    public int AttachedToDesktop;
    public int Rotation;
    public nint Monitor;
    public uint BitsPerColor;
    public int ColorSpace;
    public fixed float Primaries[8];
    public float MinLuminance;
    public float MaxLuminance;
    public float MaxFullFrameLuminance;
}

[StructLayout(LayoutKind.Sequential)]
internal struct Texture2DDesc
{
    public uint Width;
    public uint Height;
    public uint MipLevels;
    public uint ArraySize;
    public uint Format;
    public uint SampleCount;
    public uint SampleQuality;
    public uint Usage;
    public uint BindFlags;
    public uint CpuAccessFlags;
    public uint MiscFlags;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct MappedSubresource
{
    public void* Data;
    public uint RowPitch;
    public uint DepthPitch;
}

[StructLayout(LayoutKind.Sequential)]
internal struct BitmapInfoHeader
{
    public uint Size;
    public int Width;
    public int Height;
    public ushort Planes;
    public ushort BitCount;
    public uint Compression;
    public uint SizeImage;
    public int XPelsPerMeter;
    public int YPelsPerMeter;
    public uint ClrUsed;
    public uint ClrImportant;
}

[StructLayout(LayoutKind.Sequential)]
internal struct MouseHookInfo
{
    public Point Point;
    public uint MouseData;
    public uint Flags;
    public uint Time;
    public nuint ExtraInfo;
}

[StructLayout(LayoutKind.Sequential)]
internal struct KeyboardHookInfo
{
    public uint VirtualKey;
    public uint ScanCode;
    public uint Flags;
    public uint Time;
    public nuint ExtraInfo;
}

internal static unsafe partial class Native
{
    public const uint MonitorInfoPrimary = 1;
    public const int ExtendedStyle = -20;
    public const long ToolWindowStyle = 0x80;
    public const long NoActivateStyle = 0x08000000;
    public const uint DwmCloaked = 14;
    public const uint DwmExtendedFrameBounds = 9;
    public const uint DwmUseImmersiveDarkMode = 20;
    public const uint DwmSystemBackdropType = 38;
    public const uint WmHotkey = 0x0312;
    public const uint WmQuit = 0x0012;
    public const uint WmApp = 0x8000;
    public const uint ProcessQueryLimitedInformation = 0x1000;
    public const uint MonitorDefaultToNearest = 2;

    [LibraryImport("user32.dll", EntryPoint = "SetWindowsHookExW", SetLastError = true)]
    public static partial nint SetWindowsHookEx(int hookType, delegate* unmanaged<int, nuint, nint, nint> procedure, nint module, uint threadId);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool UnhookWindowsHookEx(nint hook);

    [LibraryImport("user32.dll")]
    public static partial nint CallNextHookEx(nint hook, int code, nuint wParam, nint lParam);

    [LibraryImport("kernel32.dll", EntryPoint = "GetModuleHandleW")]
    public static partial nint GetModuleHandle(char* name);

    [LibraryImport("user32.dll", EntryPoint = "LoadCursorW")]
    public static partial nint LoadCursor(nint instance, nint name);

    [LibraryImport("user32.dll")]
    public static partial nint CopyIcon(nint icon);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetSystemCursor(nint cursor, uint id);

    [LibraryImport("user32.dll", EntryPoint = "SystemParametersInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SystemParametersInfo(uint action, uint parameter, void* data, uint update);

    [LibraryImport("user32.dll")]
    public static partial nint MonitorFromWindow(nint window, uint flags);

    [LibraryImport("kernel32.dll")]
    public static partial int GetCurrentPackageFullName(uint* length, char* name);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool EnumDisplayMonitors(nint hdc, nint clip, delegate* unmanaged<nint, nint, Rect*, nint, int> callback, nint data);

    [LibraryImport("user32.dll", EntryPoint = "GetMonitorInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetMonitorInfo(nint monitor, MonitorInfoEx* info);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool EnumWindows(delegate* unmanaged<nint, nint, int> callback, nint data);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool IsWindowVisible(nint window);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool IsIconic(nint window);

    [LibraryImport("user32.dll", EntryPoint = "GetWindowTextW")]
    public static partial int GetWindowText(nint window, char* text, int maxCount);

    [LibraryImport("user32.dll", EntryPoint = "GetClassNameW")]
    public static partial int GetClassName(nint window, char* text, int maxCount);

    [LibraryImport("user32.dll")]
    public static partial uint GetWindowThreadProcessId(nint window, uint* processId);

    [LibraryImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    public static partial nint GetWindowLongPtr(nint window, int index);

    [LibraryImport("user32.dll")]
    public static partial nint GetShellWindow();

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool RegisterHotKey(nint window, int id, uint modifiers, uint virtualKey);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool UnregisterHotKey(nint window, int id);

    [LibraryImport("user32.dll", EntryPoint = "GetMessageW")]
    public static partial int GetMessage(Msg* message, nint window, uint filterMin, uint filterMax);

    [LibraryImport("user32.dll", EntryPoint = "PeekMessageW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool PeekMessage(Msg* message, nint window, uint filterMin, uint filterMax, uint remove);

    [LibraryImport("user32.dll", EntryPoint = "PostThreadMessageW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool PostThreadMessage(uint threadId, uint message, nuint wParam, nint lParam);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetCursorPos(Point* point);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetWindowDisplayAffinity(nint window, uint affinity);

    [LibraryImport("user32.dll")]
    public static partial int GetDisplayConfigBufferSizes(uint flags, uint* pathCount, uint* modeCount);

    [LibraryImport("user32.dll")]
    public static partial int QueryDisplayConfig(uint flags, uint* pathCount, DisplayConfigPathInfo* paths, uint* modeCount,
        DisplayConfigModeInfo* modes, nint topology);

    [LibraryImport("user32.dll")]
    public static partial int DisplayConfigGetDeviceInfo(void* packet);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool OpenClipboard(nint owner);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool CloseClipboard();

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool EmptyClipboard();

    [LibraryImport("user32.dll", SetLastError = true)]
    public static partial nint SetClipboardData(uint format, nint memory);

    [LibraryImport("user32.dll", EntryPoint = "RegisterClipboardFormatW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    public static partial uint RegisterClipboardFormat(string name);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    public static partial nint GlobalAlloc(uint flags, nuint bytes);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    public static partial void* GlobalLock(nint memory);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GlobalUnlock(nint memory);

    [LibraryImport("kernel32.dll")]
    public static partial nint GlobalFree(nint memory);

    [LibraryImport("kernel32.dll")]
    public static partial uint GetCurrentThreadId();

    [LibraryImport("kernel32.dll", SetLastError = true)]
    public static partial nint OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint processId);

    [LibraryImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool QueryFullProcessImageName(nint process, uint flags, char* name, uint* size);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool CloseHandle(nint handle);

    [LibraryImport("dwmapi.dll")]
    public static partial int DwmGetWindowAttribute(nint window, uint attribute, void* value, uint size);

    [LibraryImport("dwmapi.dll")]
    public static partial int DwmSetWindowAttribute(nint window, uint attribute, void* value, uint size);

    [LibraryImport("shcore.dll")]
    public static partial int GetDpiForMonitor(nint monitor, int type, uint* dpiX, uint* dpiY);

    [LibraryImport("shell32.dll")]
    public static partial int SHGetKnownFolderPath(Guid* folder, uint flags, nint token, char** path);

    [LibraryImport("ole32.dll")]
    public static partial void CoTaskMemFree(void* memory);

    [LibraryImport("d3d11.dll")]
    public static partial int D3D11CreateDevice(nint adapter, int driverType, nint software, uint flags, int* featureLevels,
        uint featureLevelCount, uint sdkVersion, nint* device, int* featureLevel, nint* context);

    [LibraryImport("d3d11.dll")]
    public static partial int CreateDirect3D11DeviceFromDXGIDevice(nint dxgiDevice, nint* graphicsDevice);

    [LibraryImport("dxgi.dll")]
    public static partial int CreateDXGIFactory1(Guid* iid, nint* factory);

    [LibraryImport("combase.dll")]
    public static partial int RoGetActivationFactory(nint classId, Guid* iid, nint* factory);

    [LibraryImport("combase.dll")]
    public static partial int WindowsCreateString(char* source, uint length, nint* result);

    [LibraryImport("combase.dll")]
    public static partial int WindowsDeleteString(nint value);

    public static void ThrowIfFailed(int result, string operation)
    {
        if (result < 0)
            throw new COMException($"{operation} failed (0x{result:X8}).", result);
    }

    /// <summary>Gets a WinRT activation factory through an arbitrary interface, such as an interop one.</summary>
    public static nint GetActivationFactory(string className, Guid iid)
    {
        nint name;
        fixed (char* text = className)
            ThrowIfFailed(WindowsCreateString(text, (uint)className.Length, &name), "WindowsCreateString");
        try
        {
            nint factory;
            ThrowIfFailed(RoGetActivationFactory(name, &iid, &factory), $"Activating {className}");
            return factory;
        }
        finally
        {
            WindowsDeleteString(name);
        }
    }
}

/// <summary>Calls into COM objects through their vtables.</summary>
internal static unsafe class Vtbl
{
    public static nint QueryInterface(nint instance, Guid iid)
    {
        nint result;
        int hr = ((delegate* unmanaged[Stdcall]<nint, Guid*, nint*, int>)Slot(instance, 0))(instance, &iid, &result);
        Native.ThrowIfFailed(hr, "QueryInterface");
        return result;
    }

    public static nint TryQueryInterface(nint instance, Guid iid)
    {
        nint result;
        int hr = ((delegate* unmanaged[Stdcall]<nint, Guid*, nint*, int>)Slot(instance, 0))(instance, &iid, &result);
        return hr < 0 ? 0 : result;
    }

    public static void Release(nint instance)
    {
        if (instance != 0)
            ((delegate* unmanaged[Stdcall]<nint, uint>)Slot(instance, 2))(instance);
    }

    public static void* Slot(nint instance, int index) => (*(void***)instance)[index];
}
