using System.Diagnostics;
using System.Runtime.Versioning;
using LightStudio.Logging;
using Microsoft.Win32;
using Windows.ApplicationModel;
using WSnip.Core.Platform;
using WSnip.Windows.Interop;

namespace WSnip.Windows;

[SupportedOSPlatform("windows10.0.19041.0")]
public sealed class WindowsShellService : IShellService
{
    /// <summary>The TaskId of the startup task in the MSIX manifest.</summary>
    public const string StartupTaskId = "WSnipStartup";

    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValue = "WSnip";
    private static readonly Guid ScreenshotsFolder = new("b7bede81-df94-4682-a7d8-57a52620b86f");

    public unsafe string DefaultScreenshotFolder
    {
        get
        {
            Guid folder = ScreenshotsFolder;
            char* path = null;
            try
            {
                // KF_FLAG_CREATE, so a fresh profile gets the folder the shell would create.
                if (Native.SHGetKnownFolderPath(&folder, 0x8000, 0, &path) >= 0 && path != null)
                    return new string(path);
            }
            finally
            {
                Native.CoTaskMemFree(path);
            }

            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Screenshots");
        }
    }

    public void RevealInFolder(string path)
    {
        // Explorer expects the quotes around the path only; Windows paths cannot contain quotes.
        string arguments = File.Exists(path)
            ? $"/select,\"{path}\""
            : $"\"{Path.GetDirectoryName(path) ?? path}\"";
        Process.Start(new ProcessStartInfo("explorer.exe", arguments) { UseShellExecute = false })?.Dispose();
    }

    public void OpenFolder(string path)
    {
        Directory.CreateDirectory(path);
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true })?.Dispose();
    }

    public async Task<bool> IsLaunchAtStartupEnabledAsync()
    {
        if (WindowsPackage.IsPackaged)
        {
            StartupTask task = await StartupTask.GetAsync(StartupTaskId);
            return task.State is StartupTaskState.Enabled or StartupTaskState.EnabledByPolicy;
        }

        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(RunValue) is string;
    }

    public async Task SetLaunchAtStartupAsync(bool enabled, string executablePath, string arguments)
    {
        // A package's registry writes stay private to it, so the package declares a startup task
        // instead; WindowsPackage.IsStartupActivation tells that launch apart.
        if (WindowsPackage.IsPackaged)
        {
            StartupTask task = await StartupTask.GetAsync(StartupTaskId);
            if (!enabled)
            {
                task.Disable();
                return;
            }

            StartupTaskState state = await task.RequestEnableAsync();
            if (state is not (StartupTaskState.Enabled or StartupTaskState.EnabledByPolicy))
                AppLog.Warning("Shell", $"Windows did not enable the startup task ({state}); it can be turned on in Settings > Apps > Startup.");
            return;
        }

        using RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled)
            key.SetValue(RunValue, $"\"{executablePath}\" {arguments}".TrimEnd());
        else
            key.DeleteValue(RunValue, throwOnMissingValue: false);
    }
}
