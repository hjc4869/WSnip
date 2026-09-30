using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using WSnip.Core.Platform;

namespace WSnip.App.Settings;

/// <summary>
/// A settings row for a global shortcut: the user clicks the box and presses a key combination,
/// chooses Print Screen or clears it. The owner decides whether to take the combination.
/// </summary>
public partial class HotkeyRow : UserControl
{
    private HotkeyGesture? gesture;
    private string? caption;
    private string? warning;

    public HotkeyRow()
    {
        InitializeComponent();
        GestureBox.AddHandler(KeyDownEvent, OnGestureKeyDown, RoutingStrategies.Tunnel);
        GestureBox.GotFocus += (_, _) => RecordingChanged?.Invoke(this, EventArgs.Empty);
        GestureBox.LostFocus += (_, _) => RecordingChanged?.Invoke(this, EventArgs.Empty);
        PrintScreenButton.Click += (_, _) => GestureChosen?.Invoke(this, new HotkeyGesture("PrintScreen"));
        ClearButton.Click += (_, _) => GestureChosen?.Invoke(this, null);
    }

    /// <summary>Raised with the combination the user chose, or null when they cleared the shortcut.</summary>
    public event EventHandler<HotkeyGesture?>? GestureChosen;

    /// <summary>Raised when the box gains or loses the keyboard focus, and with it the key combinations pressed.</summary>
    public event EventHandler? RecordingChanged;

    /// <summary>Whether the key combinations the user presses go to the box.</summary>
    public bool IsRecording => GestureBox.IsFocused;

    public string? Title
    {
        get => TitleText.Text;
        set => TitleText.Text = value;
    }

    /// <summary>What the shortcut does, shown while there is no warning.</summary>
    public string? Caption
    {
        get => caption;
        set
        {
            caption = value;
            ShowInfo();
        }
    }

    /// <summary>Why the shortcut is not in effect or was not changed, shown instead of the caption.</summary>
    public string? Warning
    {
        get => warning;
        set
        {
            warning = value;
            ShowInfo();
        }
    }

    /// <summary>The shortcut shown in the box.</summary>
    public HotkeyGesture? Gesture
    {
        get => gesture;
        set
        {
            gesture = value;
            GestureBox.Text = value?.ToString() ?? string.Empty;
        }
    }

    /// <summary>Whether the shortcut can be changed here, rather than only described.</summary>
    public bool IsEditorVisible
    {
        get => Editor.IsVisible;
        set => Editor.IsVisible = value;
    }

    private void ShowInfo()
    {
        InfoText.Text = warning ?? caption;
        InfoText.Classes.Set("warning", warning is not null);
    }

    private void OnGestureKeyDown(object? sender, KeyEventArgs e)
    {
        e.Handled = true;
        if (e.Key is Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin)
            return;
        if (e.Key is Key.Escape or Key.Tab)
        {
            e.Handled = e.Key == Key.Escape;
            return;
        }

        string? name = KeyName(e.Key);
        if (name is null)
            return;
        GestureChosen?.Invoke(this, new HotkeyGesture(name,
            Control: e.KeyModifiers.HasFlag(KeyModifiers.Control),
            Shift: e.KeyModifiers.HasFlag(KeyModifiers.Shift),
            Alt: e.KeyModifiers.HasFlag(KeyModifiers.Alt),
            Windows: e.KeyModifiers.HasFlag(KeyModifiers.Meta)));
    }

    private static string? KeyName(Key key) => key switch
    {
        >= Key.A and <= Key.Z => key.ToString(),
        >= Key.D0 and <= Key.D9 => ((char)('0' + (key - Key.D0))).ToString(),
        >= Key.F1 and <= Key.F24 => key.ToString(),
        Key.Snapshot => "PrintScreen",
        Key.Space => "Space",
        Key.Insert => "Insert",
        Key.Delete => "Delete",
        Key.Home => "Home",
        Key.End => "End",
        Key.PageUp => "PageUp",
        Key.PageDown => "PageDown",
        Key.Pause => "Pause",
        Key.Scroll => "ScrollLock",
        _ => null,
    };
}
