using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using LightStudio.Logging;
using WSnip.Core.Capture;
using WSnip.Core.Platform;
using WSnip.Windows.Interop;

using WSnip.Core.Strings;

namespace WSnip.Windows.Capture;

/// <summary>
/// Picks a window without freezing the screen: low-level mouse and keyboard hooks on a dedicated
/// thread watch for the click, which is kept from the window under it, while every standard system
/// pointer shows as a crosshair.
/// </summary>
/// <remarks>
/// The hooks do no more than record input and post to their own thread, so they stay far below the
/// system's hook timeout. The pointer scheme is reloaded from the user's settings when picking ends,
/// and again if the process exits while picking.
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed unsafe class WindowsWindowPicker : IWindowPickerService
{
    private const int MouseHook = 14;
    private const int KeyboardHook = 13;
    private const uint LeftButtonDown = 0x0201, LeftButtonUp = 0x0202, RightButtonDown = 0x0204, RightButtonUp = 0x0205;
    private const uint KeyDown = 0x0100, KeyUp = 0x0101, SystemKeyDown = 0x0104, SystemKeyUp = 0x0105;
    private const uint Escape = 0x1B;
    private const uint Picked = Native.WmApp + 1;
    private const uint Cancelled = Native.WmApp + 2;
    private const uint ReloadCursors = 0x0057;
    private const int CrossCursor = 32515;

    // Arrow, I-beam, wait, cross, up arrow, the four sizing arrows, move, unavailable, hand and app starting.
    private static readonly uint[] SystemCursors = [32512, 32513, 32514, 32515, 32516, 32642, 32643, 32644, 32645, 32646, 32648, 32649, 32650];

    private static Session? current;
    private static int cursorsReplaced;

    static WindowsWindowPicker()
    {
        AppDomain.CurrentDomain.ProcessExit += (_, _) => RestoreCursors();
        AppDomain.CurrentDomain.UnhandledException += (_, _) => RestoreCursors();
    }

    public bool IsSupported => true;

    public Task<CapturedWindow?> PickAsync(CancellationToken cancellationToken = default)
    {
        var session = new Session();
        if (Interlocked.CompareExchange(ref current, session, null) is not null)
            throw new InvalidOperationException("A window is already being picked.");

        var thread = new Thread(() => Run(session)) { IsBackground = true, Name = "WSnip window picker" };
        thread.Start();
        if (cancellationToken.CanBeCanceled)
        {
            CancellationTokenRegistration registration = cancellationToken.Register(() => session.Post(Cancelled));
            session.Result.Task.ContinueWith(_ => registration.Dispose(), TaskScheduler.Default);
        }

        return session.Result.Task;
    }

    private static void Run(Session session)
    {
        nint mouse = 0, keyboard = 0;
        CapturedWindow? picked = null;
        try
        {
            Msg message;
            Native.PeekMessage(&message, 0, 0, 0, 0);
            session.Start(Native.GetCurrentThreadId());
            nint module = Native.GetModuleHandle(null);
            mouse = Native.SetWindowsHookEx(MouseHook, &OnMouse, module, 0);
            keyboard = Native.SetWindowsHookEx(KeyboardHook, &OnKeyboard, module, 0);
            if (mouse == 0 || keyboard == 0)
                throw new InvalidOperationException(string.Format(AppStrings.PointerWatchFailed, Marshal.GetLastPInvokeError()));
            ReplaceCursors();

            while (Native.GetMessage(&message, 0, 0, 0) > 0)
            {
                if (message.Message == Cancelled)
                    break;
                if (message.Message != Picked)
                    continue;

                int x = (int)message.WParam, y = (int)message.LParam;
                picked = WindowEnumerator.WindowAt(x, y);
                if (picked is not null)
                    break;
                AppLog.Information("Picker", $"No window to capture at {x},{y}; still picking.");
            }

            session.Result.TrySetResult(picked);
        }
        catch (Exception exception)
        {
            session.Result.TrySetException(exception);
        }
        finally
        {
            if (mouse != 0)
                Native.UnhookWindowsHookEx(mouse);
            if (keyboard != 0)
                Native.UnhookWindowsHookEx(keyboard);
            RestoreCursors();
            Interlocked.CompareExchange(ref current, null, session);
            session.Result.TrySetResult(null);
        }
    }

    [UnmanagedCallersOnly]
    private static nint OnMouse(int code, nuint message, nint data)
    {
        if (code >= 0 && current is { } session)
        {
            var info = (MouseHookInfo*)data;
            switch ((uint)message)
            {
                case LeftButtonDown:
                    session.PressedAt = (info->Point.X, info->Point.Y);
                    return 1;
                case LeftButtonUp:
                    if (session.PressedAt is { } pressed)
                        session.Post(Picked, (nuint)(uint)pressed.X, pressed.Y);
                    session.PressedAt = null;
                    return 1;
                case RightButtonDown:
                    return 1;
                case RightButtonUp:
                    session.Post(Cancelled);
                    return 1;
            }
        }

        return Native.CallNextHookEx(0, code, message, data);
    }

    [UnmanagedCallersOnly]
    private static nint OnKeyboard(int code, nuint message, nint data)
    {
        if (code >= 0 && current is { } session && ((KeyboardHookInfo*)data)->VirtualKey == Escape)
        {
            if ((uint)message is KeyDown or SystemKeyDown)
                session.Post(Cancelled);
            if ((uint)message is KeyDown or SystemKeyDown or KeyUp or SystemKeyUp)
                return 1;
        }

        return Native.CallNextHookEx(0, code, message, data);
    }

    private static void ReplaceCursors()
    {
        nint cross = Native.LoadCursor(0, CrossCursor);
        if (cross == 0)
            return;
        Interlocked.Exchange(ref cursorsReplaced, 1);
        foreach (uint id in SystemCursors)
        {
            // The system takes ownership of, and later destroys, the cursor it is given.
            nint copy = Native.CopyIcon(cross);
            if (copy != 0)
                Native.SetSystemCursor(copy, id);
        }
    }

    private static void RestoreCursors()
    {
        if (Interlocked.Exchange(ref cursorsReplaced, 0) == 1)
            Native.SystemParametersInfo(ReloadCursors, 0, null, 0);
    }

    private sealed class Session
    {
        private readonly object gate = new();
        private uint threadId;
        private bool cancelled;

        public TaskCompletionSource<CapturedWindow?> Result { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Where the left button went down, in physical desktop pixels; touched on the picker thread only.</summary>
        public (int X, int Y)? PressedAt { get; set; }

        public void Start(uint id)
        {
            lock (gate)
            {
                threadId = id;
                if (cancelled)
                    Native.PostThreadMessage(id, Cancelled, 0, 0);
            }
        }

        public void Post(uint message, nuint wParam = 0, nint lParam = 0)
        {
            lock (gate)
            {
                if (threadId != 0)
                    Native.PostThreadMessage(threadId, message, wParam, lParam);
                else if (message == Cancelled)
                    cancelled = true;
            }
        }
    }
}
