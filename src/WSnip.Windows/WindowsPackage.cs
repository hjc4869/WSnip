using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Windows.ApplicationModel;
using Windows.ApplicationModel.Activation;
using Windows.Storage;
using WSnip.Windows.Interop;

namespace WSnip.Windows;

/// <summary>Whether WSnip runs from its MSIX package, and how the package started it.</summary>
[SupportedOSPlatform("windows10.0.19041.0")]
public static unsafe class WindowsPackage
{
    private const int NoPackage = 15700;

    /// <summary>True when the process has package identity (the MSIX install), false for the WiX or a development build.</summary>
    public static bool IsPackaged { get; } = DetectPackage();

    /// <summary>
    /// Where settings and logs live. The package keeps them in its own application data, as Light
    /// Player does, so the MSIX and WiX installs never share or leave behind each other's files;
    /// unpackaged builds use %APPDATA%\WSnip.
    /// </summary>
    public static string DataDirectory => IsPackaged
        ? Path.Combine(ApplicationData.Current.LocalFolder.Path, "WSnip")
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WSnip");

    /// <summary>Whether the package's startup task launched this process at sign-in.</summary>
    public static bool IsStartupActivation()
    {
        if (!IsPackaged)
            return false;
        try
        {
            return AppInstance.GetActivatedEventArgs()?.Kind == ActivationKind.StartupTask;
        }
        catch (Exception exception) when (exception is COMException or InvalidCastException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool DetectPackage()
    {
        uint length = 0;
        return Native.GetCurrentPackageFullName(&length, null) != NoPackage;
    }
}
