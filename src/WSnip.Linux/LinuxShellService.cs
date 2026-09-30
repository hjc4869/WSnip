using System.Diagnostics;
using LightStudio.Logging;
using Tmds.DBus.Protocol;
using WSnip.Core.Platform;

namespace WSnip.Linux;

/// <summary>Integration with the freedesktop file manager.</summary>
internal sealed class LinuxShellService : IShellService
{
    private const string FileManagerService = "org.freedesktop.FileManager1";
    private const string FileManagerPath = "/org/freedesktop/FileManager1";
    private readonly SessionBus bus;

    public LinuxShellService(SessionBus bus, string? dataDirectory = null)
    {
        this.bus = bus;
    }

    public string DefaultScreenshotFolder
    {
        get
        {
            // .NET finds the pictures folder through the XDG user directories.
            string pictures = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
            if (string.IsNullOrEmpty(pictures))
                pictures = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Pictures");
            return Path.Combine(pictures, "Screenshots");
        }
    }

    public void RevealInFolder(string path) => _ = RevealAsync(path);

    public void OpenFolder(string path)
    {
        Directory.CreateDirectory(path);
        var start = new ProcessStartInfo("xdg-open") { UseShellExecute = false };
        start.ArgumentList.Add(path);
        try
        {
            Process.Start(start)?.Dispose();
        }
        catch (System.ComponentModel.Win32Exception exception)
        {
            AppLog.Warning("Shell", $"The folder {path} could not be opened; xdg-open is missing.", exception);
        }
    }

    public Task<bool> IsLaunchAtStartupEnabledAsync() => Task.FromResult(false);

    public Task SetLaunchAtStartupAsync(bool enabled, string executablePath, string arguments) => Task.CompletedTask;

    private async Task RevealAsync(string path)
    {
        try
        {
            DBusConnection connection = await bus.ConnectAsync().ConfigureAwait(false);
            MessageWriter writer = connection.GetMessageWriter();
            writer.WriteMethodCallHeader(destination: FileManagerService, path: FileManagerPath, @interface: FileManagerService,
                signature: "ass", member: "ShowItems");
            writer.WriteArray(new[] { new Uri(path).AbsoluteUri });
            writer.WriteString(string.Empty);
            await connection.CallMethodAsync(writer.CreateMessage()).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is DBusExceptionBase or InvalidOperationException)
        {
            // Without a file manager that selects files, its folder opens instead.
            AppLog.Information("Shell", $"Showing {path} in its folder failed ({exception.Message}); opening the folder.");
            OpenFolder(Path.GetDirectoryName(path) ?? path);
        }
    }
}
