using System.Diagnostics;
using System.Runtime.InteropServices;
using LightStudio.Logging;
using NWayland.Interop;
using NWayland.Protocols.Plasma.ZkdeScreencastUnstableV1;
using NWayland.Protocols.Wayland;
using NWayland.Protocols.XdgOutputUnstableV1;
using WSnip.Linux.Capture;

namespace WSnip.Linux.KWin;

/// <summary>
/// A Wayland connection to KWin for its screencast protocol, which streams displays and windows to
/// PipeWire without asking and leaves the requesting process's windows out of display streams. KWin
/// offers the protocol only to apps whose desktop entry lists it in X-KDE-Wayland-Interfaces.
/// </summary>
internal sealed partial class KWinScreencast : IDisposable
{
    public const string Interface = "zkde_screencast_unstable_v1";
    private const short PollIn = 1;
    private const int Interrupted = 4;

    private readonly WlDisplay display;
    private readonly WlRegistry registry;
    private readonly List<(uint Name, string Interface, uint Version)> globals = [];

    private KWinScreencast(WlDisplay display)
    {
        this.display = display;
        registry = display.GetRegistry(new WlRegistry.Listener.Relay
        {
            OnGlobal = (_, name, @interface, version) => globals.Add((name, @interface, version)),
        });
    }

    /// <summary>Connects to the session's compositor, or returns null when it does not offer KWin's screencast to this app.</summary>
    public static KWinScreencast? TryConnect()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY")))
            return null;

        WlDisplay display;
        try
        {
            display = WlDisplay.Connect();
        }
        catch (NWaylandException exception)
        {
            AppLog.Debug("KWin", $"No Wayland connection: {exception.Message}");
            return null;
        }

        var connection = new KWinScreencast(display);
        try
        {
            connection.Check(display.Roundtrip());
            if (connection.Find(Interface) is not null)
                return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }

        connection.Dispose();
        return null;
    }

    /// <summary>Has KWin stream every display to PipeWire, giving the PipeWire node of each.</summary>
    /// <exception cref="InvalidOperationException">KWin could not stream a display.</exception>
    /// <exception cref="TimeoutException">KWin did not start the streams in time.</exception>
    public List<(SharedDisplay Display, uint Node)> StreamDisplays(bool includeCursor, TimeSpan timeout)
    {
        List<Output> outputs = BindOutputs();
        if (outputs.Count == 0)
            throw new InvalidOperationException("KWin has no display to share.");

        ZkdeScreencastUnstableV1 screencast = BindScreencast();
        PendingStream[] pending = outputs.Select(_ => new PendingStream()).ToArray();
        for (int i = 0; i < outputs.Count; i++)
            screencast.StreamOutput(outputs[i].Proxy!, Pointer(includeCursor), pending[i].Listener);
        DispatchUntil(() => pending.All(stream => stream.IsSettled), timeout, "KWin did not start sharing the displays in time.");

        var streams = new List<(SharedDisplay, uint)>(outputs.Count);
        for (int i = 0; i < outputs.Count; i++)
        {
            Output output = outputs[i];
            string device = output.Name ?? $"output {i}";
            if (pending[i].Node is not { } node)
                throw new InvalidOperationException($"KWin could not share the display {device}: {pending[i].Error}.");
            streams.Add((new SharedDisplay(device, output.Description, output.Position, output.Size), node));
        }

        return streams;
    }

    /// <summary>Has KWin stream one window whole, with its popups and transparency, giving the PipeWire node.</summary>
    /// <param name="uuid">KWin's id of the window.</param>
    /// <exception cref="InvalidOperationException">KWin could not stream the window, such as when it has closed.</exception>
    /// <exception cref="TimeoutException">KWin did not start the stream in time.</exception>
    public uint StreamWindow(string uuid, bool includeCursor, TimeSpan timeout)
    {
        var pending = new PendingStream();
        BindScreencast().StreamWindow(uuid, Pointer(includeCursor), pending.Listener);
        DispatchUntil(() => pending.IsSettled, timeout, "KWin did not start sharing the window in time.");
        return pending.Node ?? throw new InvalidOperationException($"KWin could not share the window: {pending.Error}.");
    }

    /// <summary>Disconnects, which ends the streams.</summary>
    public void Dispose() => display.Dispose();

    private static uint Pointer(bool includeCursor) =>
        (uint)(includeCursor ? ZkdeScreencastUnstableV1.PointerEnum.Embedded : ZkdeScreencastUnstableV1.PointerEnum.Hidden);

    private (uint Name, string Interface, uint Version)? Find(string @interface)
    {
        int index = globals.FindIndex(global => global.Interface == @interface);
        return index < 0 ? null : globals[index];
    }

    private ZkdeScreencastUnstableV1 BindScreencast()
    {
        (uint name, _, uint version) = Find(Interface)!.Value;
        return ZkdeScreencastUnstableV1.Bind(registry, name, Math.Min(version, 5));
    }

    /// <summary>Binds every display with its name and place in the compositor's logical layout.</summary>
    private List<Output> BindOutputs()
    {
        ZxdgOutputManagerV1? manager = Find("zxdg_output_manager_v1") is (uint managerName, _, uint managerVersion)
            ? ZxdgOutputManagerV1.Bind(registry, managerName, Math.Min(managerVersion, 3))
            : null;
        var outputs = new List<Output>();
        foreach ((uint name, string @interface, uint version) in globals)
        {
            if (@interface != "wl_output")
                continue;
            var output = new Output();
            output.Proxy = WlOutput.Bind(registry, name, Math.Min(version, 4), new WlOutput.Listener.Relay
            {
                OnName = (_, value) => output.Name = value,
                OnDescription = (_, value) => output.Description = value,
            });
            manager?.GetXdgOutput(output.Proxy, new ZxdgOutputV1.Listener.Relay
            {
                OnLogicalPosition = (_, x, y) => output.Position = (x, y),
                OnLogicalSize = (_, width, height) => output.Size = (width, height),
            });
            outputs.Add(output);
        }

        Check(display.Roundtrip());
        return outputs;
    }

    /// <summary>Dispatches events until a condition holds, waiting for them no longer than the timeout.</summary>
    private void DispatchUntil(Func<bool> done, TimeSpan timeout, string timeoutMessage)
    {
        var watch = Stopwatch.StartNew();
        int fd = display.GetFd();
        while (true)
        {
            Check(display.DispatchPending());
            if (done())
                return;
            TimeSpan remaining = timeout - watch.Elapsed;
            if (remaining <= TimeSpan.Zero)
                throw new TimeoutException(timeoutMessage);
            if (display.PrepareRead() != 0)
                continue;

            display.Flush();
            if (WaitReadable(fd, remaining))
                Check(display.ReadEvents());
            else
                display.CancelRead();
        }
    }

    private void Check(int result)
    {
        if (result < 0)
            throw new InvalidOperationException($"The connection to KWin failed: {display.GetProtocolError()?.Message ?? $"error {display.GetError()}"}.");
    }

    private static unsafe bool WaitReadable(int fd, TimeSpan timeout)
    {
        var request = new PollFd { Fd = fd, Events = PollIn };
        int milliseconds = (int)Math.Ceiling(Math.Min(timeout.TotalMilliseconds, int.MaxValue));
        while (true)
        {
            int result = poll(&request, 1, milliseconds);
            if (result >= 0)
                return result > 0;
            int error = Marshal.GetLastPInvokeError();
            if (error != Interrupted)
                throw new IOException($"Waiting for KWin failed (error {error}).");
        }
    }

    [LibraryImport("libc", SetLastError = true)]
    private static unsafe partial int poll(PollFd* fds, nuint count, int timeout);

    [StructLayout(LayoutKind.Sequential)]
    private struct PollFd
    {
        public int Fd;
        public short Events;
        public short ReturnedEvents;
    }

    private sealed class Output
    {
        public WlOutput? Proxy;
        public string? Name;
        public string? Description;
        public (int X, int Y)? Position;
        public (int Width, int Height)? Size;
    }

    /// <summary>A stream KWin is starting, until it has a PipeWire node or has failed.</summary>
    private sealed class PendingStream
    {
        public PendingStream() => Listener = new ZkdeScreencastStreamUnstableV1.Listener.Relay
        {
            OnCreated = (_, node) => Node = node,
            OnFailed = (_, error) => Error = error,
            OnClosed = _ => Error ??= "the stream ended",
        };

        public ZkdeScreencastStreamUnstableV1.Listener Listener { get; }

        public uint? Node { get; private set; }

        public string? Error { get; private set; }

        public bool IsSettled => Node is not null || Error is not null;
    }
}
