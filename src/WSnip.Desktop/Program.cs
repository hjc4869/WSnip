using Avalonia;
using FFmpeg.AutoGen.Bindings.DynamicallyLoaded;
using LightStudio.FfmpegShim;
using LightStudio.Logging;
using WSnip.Core.Platform;
#if WINDOWS
using WSnip.Windows;
#elif LINUX
using WSnip.Linux;
#endif

namespace WSnip.Desktop;

internal static class Program
{
    // Avalonia is not initialized before AppMain, so nothing here may touch UI types.
    [STAThread]
    public static int Main(string[] args)
    {
        PlatformServices platform = CreatePlatform();
        AppLog.Configure(writeToConsole: false, Path.Combine(platform.DataDirectory, "logs", "wsnip.log"));

#if WINDOWS
        // The MSIX startup task cannot pass arguments; a sign-in launch goes to the tray like the Run key's.
        if (WindowsPackage.IsStartupActivation())
            args = [.. args, "--background"];
#endif

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
    /// Extended-linear color renders into a 16-bit float surface that the compositor blends in, so
    /// snips show their HDR highlights and wide colors as captured. In a Wayland session Linux uses
    /// the Wayland backend, and X11 otherwise.
    /// </summary>
    public static AppBuilder BuildAvaloniaApp()
    {
        AppBuilder builder = AppBuilder.Configure<App.App>()
            .UsePlatformDetect()
            // A 4K FP16 texture with mipmaps plus window surfaces fits without upload churn,
            // while avoiding the old half-GiB cache of retired screenshot textures.
            .With(new SkiaOptions { MaxGpuResourceSizeBytes = 128 * 1024 * 1024 })
            .WithInterFont()
            .LogToTrace();
#if WINDOWS
        builder.With(new Win32PlatformOptions { ColorMode = Win32ColorMode.ExtendedLinear });
#elif LINUX
        if (IsWaylandSession)
        {
            builder.With(new WaylandPlatformOptions
            {
                ColorMode = WaylandColorMode.ExtendedLinear,
                HdrPresentationPreferences =
                [
                    WaylandHdrPresentationMode.LinearRelative, WaylandHdrPresentationMode.PqRelative,
                    WaylandHdrPresentationMode.LinearPerceptual, WaylandHdrPresentationMode.PqPerceptual,
                    WaylandHdrPresentationMode.Sdr,
                ],
            }).UseWayland();
        }
#endif
        return builder;
    }

#if LINUX
    private static bool IsWaylandSession { get; } =
        string.Equals(Environment.GetEnvironmentVariable("XDG_SESSION_TYPE"), "wayland", StringComparison.OrdinalIgnoreCase);
#endif

    private static PlatformServices CreatePlatform()
    {
#if WINDOWS
        return WindowsPlatform.Create();
#elif LINUX
        return OperatingSystem.IsLinux()
            ? LinuxPlatform.Create(compositorPlacesWindows: IsWaylandSession)
            : throw new PlatformNotSupportedException("This build of WSnip runs on Linux.");
#else
        throw new PlatformNotSupportedException("WSnip runs on Windows and Linux.");
#endif
    }
}
