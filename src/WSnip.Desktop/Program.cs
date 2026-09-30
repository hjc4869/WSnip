using Avalonia;
using FFmpeg.AutoGen.Bindings.DynamicallyLoaded;
using LightStudio.FfmpegShim;
using LightStudio.Logging;
using WSnip.Core.Platform;
using WSnip.Windows;

namespace WSnip.Desktop;

internal static class Program
{
    // Avalonia is not initialized before AppMain, so nothing here may touch UI types.
    [STAThread]
    public static int Main(string[] args)
    {
        PlatformServices platform = WindowsPlatform.Create();
        AppLog.Configure(writeToConsole: false, Path.Combine(platform.DataDirectory, "logs", "wsnip.log"));

        // The MSIX startup task cannot pass arguments; a sign-in launch goes to the tray like the Run key's.
        if (WindowsPackage.IsStartupActivation())
            args = [.. args, "--background"];

        if (!platform.SingleInstance.TryClaim())
        {
            // A later launch hands its arguments to the running instance, which shows the editor
            // or starts a snip; only if that instance cannot be reached does this one carry on.
            if (platform.SingleInstance.ForwardAsync(args).GetAwaiter().GetResult())
            {
                AppLog.Shutdown();
                return 0;
            }
        }

        DynamicallyLoadedBindings.Initialize();
        FfmpegLog.Attach();

        App.App.Platform = platform;
        App.App.Arguments = args;
        try
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
            return 0;
        }
        catch (Exception exception)
        {
            AppLog.Critical("Startup", "WSnip terminated unexpectedly.", exception);
            throw;
        }
        finally
        {
            AppLog.Shutdown();
        }
    }

    /// <summary>
    /// Extended-linear color renders into a 16-bit float scRGB surface, the format the compositor
    /// blends in, so snips show their HDR highlights and wide colors as captured.
    /// </summary>
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App.App>()
        .UsePlatformDetect()
        .With(new Win32PlatformOptions { ColorMode = Win32ColorMode.ExtendedLinear })
        .With(new SkiaOptions { MaxGpuResourceSizeBytes = 512 * 1024 * 1024 })
        .WithInterFont()
        .LogToTrace();
}
