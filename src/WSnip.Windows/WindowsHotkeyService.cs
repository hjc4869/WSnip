using System.Collections.Concurrent;
using System.Runtime.Versioning;
using WSnip.Core.Platform;
using WSnip.Core.Strings;
using WSnip.Windows.Interop;

namespace WSnip.Windows;

/// <summary>
/// Registers system-wide hotkeys on a dedicated thread whose message queue receives WM_HOTKEY,
/// so the shortcut keeps working while the UI thread is busy.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed unsafe class WindowsHotkeyService : IGlobalHotkeyService
{
    private const uint Alt = 0x1, Control = 0x2, Shift = 0x4, Win = 0x8, NoRepeat = 0x4000;

    private readonly Thread thread;
    private readonly BlockingCollection<Action> work = [];
    private readonly Dictionary<int, HotkeyGesture> registered = [];
    private readonly TaskCompletionSource<uint> started = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int nextId = 1;
    private bool disposed;

    public WindowsHotkeyService()
    {
        thread = new Thread(Run) { IsBackground = true, Name = "WSnip hotkeys" };
        thread.Start();
    }

    public event EventHandler<HotkeyGesture>? Pressed;

    public bool IsSupported => true;

    public string? Register(HotkeyGesture gesture)
    {
        uint? key = VirtualKey(gesture.Key);
        if (key is null)
            return string.Format(AppStrings.UnsupportedKey, gesture.Key);

        uint modifiers = NoRepeat | (gesture.Alt ? Alt : 0) | (gesture.Control ? Control : 0) |
            (gesture.Shift ? Shift : 0) | (gesture.Windows ? Win : 0);
        return Invoke(() =>
        {
            int id = nextId++;
            if (!Native.RegisterHotKey(0, id, modifiers, key.Value))
            {
                int error = System.Runtime.InteropServices.Marshal.GetLastPInvokeError();

                // ERROR_HOTKEY_ALREADY_REGISTERED; for Print Screen held by another app, Windows sets no error.
                return error is 1409 or 0
                    ? string.Format(AppStrings.HotkeyUsed, gesture)
                    : string.Format(AppStrings.HotkeyRegistrationFailed, gesture, error);
            }

            registered[id] = gesture;
            return null;
        });
    }

    public void UnregisterAll() => Invoke(() =>
    {
        foreach (int id in registered.Keys)
            Native.UnregisterHotKey(0, id);
        registered.Clear();
        return (string?)null;
    });

    public void Dispose()
    {
        if (disposed)
            return;
        UnregisterAll();
        disposed = true;
        work.CompleteAdding();
        Native.PostThreadMessage(started.Task.Result, Native.WmQuit, 0, 0);
        thread.Join(TimeSpan.FromSeconds(2));
    }

    private string? Invoke(Func<string?> action)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        var result = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        work.Add(() =>
        {
            try
            {
                result.SetResult(action());
            }
            catch (Exception exception)
            {
                result.SetException(exception);
            }
        });
        Native.PostThreadMessage(started.Task.Result, Native.WmApp, 0, 0);
        return result.Task.GetAwaiter().GetResult();
    }

    private void Run()
    {
        Msg message;
        Native.PeekMessage(&message, 0, 0, 0, 0);
        started.SetResult(Native.GetCurrentThreadId());
        while (Native.GetMessage(&message, 0, 0, 0) > 0)
        {
            if (message.Message == Native.WmHotkey && registered.TryGetValue((int)message.WParam, out HotkeyGesture? gesture))
                Pressed?.Invoke(this, gesture);
            while (work.TryTake(out Action? action))
                action();
        }
    }

    private static uint? VirtualKey(string key)
    {
        if (key.Length == 1 && char.IsAsciiLetterOrDigit(key[0]))
            return char.ToUpperInvariant(key[0]);
        if (key.Length is 2 or 3 && (key[0] is 'F' or 'f') && int.TryParse(key.AsSpan(1), out int function) && function is >= 1 and <= 24)
            return (uint)(0x70 + function - 1);
        return key.ToLowerInvariant() switch
        {
            "printscreen" or "prtsc" or "print" => 0x2C,
            "space" => 0x20,
            "insert" => 0x2D,
            "delete" => 0x2E,
            "home" => 0x24,
            "end" => 0x23,
            "pageup" => 0x21,
            "pagedown" => 0x22,
            "pause" => 0x13,
            "scrolllock" => 0x91,
            _ => null,
        };
    }
}
