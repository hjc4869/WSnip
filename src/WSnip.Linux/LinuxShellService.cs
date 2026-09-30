using System.Diagnostics;
using System.Text;
using LightStudio.Logging;
using Tmds.DBus.Protocol;
using WSnip.Core.Platform;
using WSnip.Linux.Portal;

namespace WSnip.Linux;

/// <summary>
/// The freedesktop shell: the file manager, and starting at sign-in through an XDG autostart entry,
/// which the Background portal writes for a Flatpak.
/// </summary>
internal sealed class LinuxShellService(SessionBus bus, string dataDirectory) : IShellService
{
    private const string FileManagerService = "org.freedesktop.FileManager1";
    private const string FileManagerPath = "/org/freedesktop/FileManager1";
    private const string BackgroundInterface = "org.freedesktop.portal.Background";

    private string AutostartEntry => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "autostart",
        LinuxPlatform.AppId + ".desktop");

    /// <summary>Records what the Background portal was last asked, as a Flatpak cannot see the entry it writes.</summary>
    private string AutostartRequested => Path.Combine(dataDirectory, "autostart-requested");

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

    public Task<bool> IsLaunchAtStartupEnabledAsync() =>
        Task.FromResult(File.Exists(LinuxPlatform.IsSandboxed ? AutostartRequested : AutostartEntry));

    public async Task SetLaunchAtStartupAsync(bool enabled, string executablePath, string arguments)
    {
        if (LinuxPlatform.IsSandboxed)
        {
            await RequestBackgroundAsync(enabled, arguments).ConfigureAwait(false);
            return;
        }

        if (!enabled)
        {
            File.Delete(AutostartEntry);
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(AutostartEntry)!);
        await File.WriteAllTextAsync(AutostartEntry,
            $"""
            [Desktop Entry]
            Type=Application
            Name=WSnip
            Exec={ExecArgument(executablePath)} {arguments}
            Icon={LinuxPlatform.AppId}
            Terminal=false

            """).ConfigureAwait(false);
    }

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

    /// <summary>Asks the Background portal, which may ask the user, to start the app at sign-in.</summary>
    private async Task RequestBackgroundAsync(bool enabled, string arguments)
    {
        DBusConnection connection = await bus.ConnectAsync().ConfigureAwait(false);
        string token = DesktopPortal.NewToken();
        string[] commandLine = ["wsnip", .. arguments.Split(' ', StringSplitOptions.RemoveEmptyEntries)];
        (PortalResponse response, Dictionary<string, VariantValue> results) = await DesktopPortal.RequestAsync(connection, token, () =>
        {
            MessageWriter writer = connection.GetMessageWriter();
            writer.WriteMethodCallHeader(destination: DesktopPortal.Service, path: DesktopPortal.ObjectPath, @interface: BackgroundInterface,
                signature: "sa{sv}", member: "RequestBackground");
            writer.WriteString(string.Empty);
            writer.WriteDictionary(new Dictionary<string, VariantValue>
            {
                ["handle_token"] = VariantValue.String(token),
                ["reason"] = VariantValue.String("WSnip waits in the notification area, ready to take snips."),
                ["autostart"] = VariantValue.Bool(enabled),
                ["commandline"] = VariantValue.Array(commandLine),
            });
            return writer.CreateMessage();
        }, CancellationToken.None).ConfigureAwait(false);

        bool autostart = response == PortalResponse.Success &&
                         results.TryGetValue("autostart", out VariantValue value) && DesktopPortal.Unwrap(value).GetBool();
        if (autostart)
            await File.WriteAllTextAsync(AutostartRequested, string.Empty).ConfigureAwait(false);
        else
            File.Delete(AutostartRequested);
        if (enabled && !autostart)
            AppLog.Warning("Shell", $"The system did not allow starting at sign-in ({response}).");
    }

    /// <summary>
    /// Quotes an argument for the Exec key of a desktop entry, whose value is itself a string in
    /// which backslashes are doubled.
    /// </summary>
    private static string ExecArgument(string argument)
    {
        var quoted = new StringBuilder("\"");
        foreach (char c in argument)
        {
            if (c is '"' or '`' or '$' or '\\')
                quoted.Append('\\');
            quoted.Append(c);
        }

        return quoted.Append('"').ToString().Replace("%", "%%").Replace("\\", "\\\\");
    }
}
