using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using WSnip.App.Settings;
using WSnip.Core.Imaging;
using WSnip.Core.Strings;

namespace WSnip.App.Editor;

/// <summary>What the tone mapping window was closed with: the tuned settings, and whether they become the default.</summary>
public sealed record ToneMappingResult(ToneMapSettings Settings, bool SetAsDefault);

/// <summary>
/// Tunes the tone mapping of one snip: where it applies, the curve and its parameters. Every
/// change previews in the editor at once; Save keeps it for the snip, and optionally as the default.
/// </summary>
public partial class ToneMappingWindow : Window
{
    private readonly EditorDocument? document;
    private readonly Choice<SdrToneMapping>[] scopes;
    private readonly Choice<ToneMapCurve>[] curves;
    private ToneMapSettings settings = ToneMapSettings.Default;
    private bool loading;

    public ToneMappingWindow()
    {
        InitializeComponent();
        scopes = SettingsWindow.ToneMappings.Select(t => new Choice<SdrToneMapping>(t.Mode, t.Label)).ToArray();
        curves = ToneMapCurves.All.Select(c => new Choice<ToneMapCurve>(c, ToneMapCurves.Name(c))).ToArray();
        ScopeBox.ItemsSource = scopes;
        CurveBox.ItemsSource = curves;
    }

    /// <param name="canShowHdr">Whether the editor can show the HDR capture to compare with.</param>
    public ToneMappingWindow(EditorDocument document, ToneMapSettings initial, bool canShowHdr)
        : this()
    {
        this.document = document;
        settings = initial;
        if (!canShowHdr)
        {
            ShowToneMappedSwitch.IsEnabled = false;
            ShowToneMappedCaption.Text = AppStrings.SdrOnly;
        }

        ShowToneMappedSwitch.IsCheckedChanged += (_, _) => ShowToneMappedChanged?.Invoke(this, ShowToneMappedSwitch.IsChecked != false);
        ScopeBox.SelectionChanged += (_, _) =>
        {
            if (!loading && ScopeBox.SelectedItem is Choice<SdrToneMapping> scope)
                Apply(settings with { Scope = scope.Value }, rebuildParameters: false);
        };
        CurveBox.SelectionChanged += (_, _) =>
        {
            if (!loading && CurveBox.SelectedItem is Choice<ToneMapCurve> curve)
                Apply(settings with { Curve = curve.Value }, rebuildParameters: true);
        };
        ResetButton.Click += (_, _) => Apply(settings.ResetCurve(), rebuildParameters: true);
        SaveButton.Click += (_, _) => Close(new ToneMappingResult(settings, DefaultSwitch.IsChecked == true));
        CancelButton.Click += (_, _) => Close(null);

        Load(rebuildParameters: true);
        document.Preview(settings);
    }

    /// <summary>Raised with whether the editor should show the tone mapped SDR version rather than the HDR capture.</summary>
    public event EventHandler<bool>? ShowToneMappedChanged;

    private void Apply(ToneMapSettings next, bool rebuildParameters)
    {
        settings = next;
        document?.Preview(settings);
        Load(rebuildParameters);
    }

    private void Load(bool rebuildParameters)
    {
        loading = true;
        ScopeBox.SelectedItem = Array.Find(scopes, s => s.Value == settings.Scope);
        ScopeInfo.Text = SettingsWindow.ToneMappings.First(t => t.Mode == settings.Scope).Description;
        CurveBox.SelectedItem = Array.Find(curves, c => c.Value == settings.Curve);
        CurveInfo.Text = ToneMapCurves.Description(settings.Curve);
        CurveRow.IsEnabled = settings.Scope != SdrToneMapping.Clip;
        ResetButton.IsEnabled = !settings.IsCurveAtDefaults;
        if (rebuildParameters)
        {
            ParameterRows.Children.Clear();
            foreach (ToneMapParameter parameter in ToneMapCurves.Parameters(settings.Curve))
                ParameterRows.Children.Add(CreateParameterRow(parameter));
        }

        loading = false;
    }

    private Grid CreateParameterRow(ToneMapParameter parameter)
    {
        float initial = settings[parameter];
        var label = new TextBlock { Text = parameter.Label, VerticalAlignment = VerticalAlignment.Center };
        var value = new TextBlock
        {
            Text = parameter.Format(initial),
            Classes = { "caption" },
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        var slider = new Slider
        {
            Minimum = parameter.Minimum,
            Maximum = parameter.Maximum,
            SmallChange = parameter.Step,
            LargeChange = parameter.Step * 5,
            TickFrequency = parameter.Step,
            IsSnapToTickEnabled = true,
            Value = initial,
        };
        AutomationProperties.SetName(slider, parameter.Label);
        if (parameter.Tip is { } tip)
        {
            ToolTip.SetTip(label, tip);
            ToolTip.SetTip(slider, tip);
        }

        slider.PropertyChanged += (_, e) =>
        {
            if (e.Property != RangeBase.ValueProperty || loading)
                return;
            float snapped = (float)(Math.Round((slider.Value - parameter.Minimum) / parameter.Step) * parameter.Step + parameter.Minimum);
            value.Text = parameter.Format(snapped);
            Apply(settings.With(parameter, snapped), rebuildParameters: false);
        };

        var row = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            RowDefinitions = new RowDefinitions("Auto,Auto"),
        };
        Grid.SetColumn(value, 1);
        Grid.SetRow(slider, 1);
        Grid.SetColumnSpan(slider, 2);
        row.Children.Add(label);
        row.Children.Add(value);
        row.Children.Add(slider);
        return row;
    }

    /// <summary>A combo box item shown by its label, so the selection follows theme changes.</summary>
    private sealed record Choice<T>(T Value, string Label)
    {
        public override string ToString() => Label;
    }
}
