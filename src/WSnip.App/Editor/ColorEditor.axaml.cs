using System.Globalization;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using WSnip.Core.Imaging;

namespace WSnip.App.Editor;

/// <summary>
/// Defines an annotation color: components on sRGB, Display P3 or BT.2020 primaries and, for pens,
/// a brightness above SDR white. The preview shows the color as it is on HDR and wide-color displays.
/// </summary>
public partial class ColorEditor : UserControl
{
    private static readonly (ColorPrimaries Primaries, string Label)[] Gamuts =
    [
        (ColorPrimaries.Bt709, "sRGB"), (ColorPrimaries.DisplayP3, "Display P3"), (ColorPrimaries.Bt2020, "BT.2020"),
    ];

    private bool updating;
    private bool allowExtended = true;

    public ColorEditor()
    {
        InitializeComponent();
        foreach ((_, string label) in Gamuts)
            GamutBox.Items.Add(label);

        foreach (Slider slider in new[] { RedSlider, GreenSlider, BlueSlider, BrightnessSlider })
        {
            slider.PropertyChanged += (_, e) =>
            {
                if (e.Property == RangeBase.ValueProperty && !updating)
                    Refresh(updateHex: true);
            };
        }

        GamutBox.SelectionChanged += (_, _) =>
        {
            if (!updating)
                Refresh(updateHex: false);
        };
        HexBox.TextChanged += (_, _) =>
        {
            if (!updating && TryParseHex(HexBox.Text, out byte r, out byte g, out byte b))
            {
                updating = true;
                RedSlider.Value = r;
                GreenSlider.Value = g;
                BlueSlider.Value = b;
                updating = false;
                Refresh(updateHex: false);
            }
        };
        ApplyButton.Click += (_, _) => Applied?.Invoke(this, Current);
        CancelButton.Click += (_, _) => Cancelled?.Invoke(this, EventArgs.Empty);
    }

    public event EventHandler<CustomColor>? Applied;

    public event EventHandler? Cancelled;

    /// <summary>SDR white in nits, to express the luminance of the color.</summary>
    public double ReferenceWhiteNits { get; set; } = ColorMath.ReferenceWhiteNits;

    /// <summary>The color as currently entered.</summary>
    public CustomColor Current => new(
        Gamuts[Math.Max(0, GamutBox.SelectedIndex)].Primaries,
        (float)(RedSlider.Value / 255),
        (float)(GreenSlider.Value / 255),
        (float)(BlueSlider.Value / 255),
        allowExtended ? (float)Math.Pow(2, BrightnessSlider.Value) : 1);

    /// <summary>Starts editing a color.</summary>
    /// <param name="extended">Offers wide-gamut primaries and HDR brightness; highlighters stay in SDR sRGB.</param>
    /// <param name="existing">Whether the color replaces an existing one rather than being added.</param>
    public void Load(CustomColor color, bool extended, bool existing)
    {
        allowExtended = extended;
        if (!extended)
            color = CustomColor.FromScRgb(color.ToScRgb().ClampToSdr());

        updating = true;
        GamutBox.SelectedIndex = Math.Max(0, Array.FindIndex(Gamuts, g => g.Primaries == color.Primaries));
        RedSlider.Value = Math.Clamp(color.Red, 0, 1) * 255;
        GreenSlider.Value = Math.Clamp(color.Green, 0, 1) * 255;
        BlueSlider.Value = Math.Clamp(color.Blue, 0, 1) * 255;
        BrightnessSlider.Value = Math.Log2(Math.Clamp(color.Brightness, 1, CustomColor.MaxBrightness));
        updating = false;

        GamutLabel.IsVisible = GamutBox.IsVisible = extended;
        BrightnessLabel.IsVisible = BrightnessSlider.IsVisible = BrightnessText.IsVisible = extended;
        TitleText.Text = existing ? "Edit color" : "New color";
        ApplyButton.Content = existing ? "Save" : "Add";
        Refresh(updateHex: true);
    }

    private void Refresh(bool updateHex)
    {
        CustomColor color = Current;
        ScRgb value = color.ToScRgb();
        Preview.Color = value;
        RedText.Text = Format(RedSlider.Value);
        GreenText.Text = Format(GreenSlider.Value);
        BlueText.Text = Format(BlueSlider.Value);
        BrightnessText.Text = string.Create(CultureInfo.CurrentCulture, $"{color.Brightness:0.0}\u00D7");
        if (updateHex)
        {
            bool wasUpdating = updating;
            updating = true;
            HexBox.Text = $"#{Round(RedSlider.Value):X2}{Round(GreenSlider.Value):X2}{Round(BlueSlider.Value):X2}";
            updating = wasUpdating;
        }

        SummaryText.Text = Describe(value, ReferenceWhiteNits);
    }

    /// <summary>A one-line account of a color's gamut and luminance.</summary>
    public static string Describe(ScRgb color, double whiteNits)
    {
        string gamut = color.Gamut switch
        {
            ColorPrimaries.Bt709 => "Within sRGB",
            ColorPrimaries.DisplayP3 => "Wide color (Display P3)",
            _ => "Wide color (BT.2020)",
        };
        float luminance = color.Luminance;
        string range = color.IsHdr ? "HDR" : "SDR";
        return string.Create(CultureInfo.CurrentCulture,
            $"{gamut}, {range}. Luminance {luminance:0.00}\u00D7 SDR white, {luminance * whiteNits:0} nits.");
    }

    private static string Format(double value) => Round(value).ToString(CultureInfo.CurrentCulture);

    private static int Round(double value) => (int)Math.Clamp(Math.Round(value), 0, 255);

    private static bool TryParseHex(string? text, out byte r, out byte g, out byte b)
    {
        r = g = b = 0;
        string hex = (text ?? string.Empty).Trim().TrimStart('#');
        if (hex.Length != 6 || !uint.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint rgb))
            return false;
        r = (byte)(rgb >> 16);
        g = (byte)(rgb >> 8);
        b = (byte)rgb;
        return true;
    }
}
