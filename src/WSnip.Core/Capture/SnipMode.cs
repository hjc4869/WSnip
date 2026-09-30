namespace WSnip.Core.Capture;

/// <summary>How the capture overlay turns pointer input into a region.</summary>
public enum SnipMode
{
    Rectangle,
    Window,
    Fullscreen,
    Freeform,

    /// <summary>
    /// A whole window read from the compositor, including parts hidden behind other windows and
    /// its transparency. The screen is not frozen; the user clicks the window to capture.
    /// </summary>
    WholeWindow,
}
