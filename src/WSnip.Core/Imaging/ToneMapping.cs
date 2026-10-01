using System.Globalization;
using WSnip.Core.Strings;

namespace WSnip.Core.Imaging;

/// <summary>Curves that bring highlights above SDR white into the SDR rendition.</summary>
public enum ToneMapCurve
{
    /// <summary>The ITU-R BT.2390 EETF: a Hermite spline in the PQ domain above a knee.</summary>
    Bt2390,

    /// <summary>A Möbius shoulder above a linear knee, as in mpv.</summary>
    Mobius,

    /// <summary>Reinhard's x / (x + c), scaled to reach SDR white at the peak.</summary>
    Reinhard,

    /// <summary>John Hable's filmic curve from Uncharted 2.</summary>
    Hable,

    /// <summary>Krzysztof Narkowicz's fit of the ACES filmic curve.</summary>
    Aces,

    /// <summary>The Khronos PBR Neutral tone mapper.</summary>
    PbrNeutral,
}

public enum ToneMapUnit
{
    Number,
    Percent,
    Stops,
}

/// <summary>A tunable setting of a tone curve.</summary>
public sealed record ToneMapParameter(string Key, float Minimum, float Maximum, float Default, float Step, ToneMapUnit Unit = ToneMapUnit.Number)
{
    public string Label => Key switch
    {
        "kneeOffset" => AppStrings.ToneKneeOffset,
        "knee" => AppStrings.ToneKnee,
        "contrast" => AppStrings.ToneContrast,
        "exposure" => AppStrings.ToneExposure,
        "start" => AppStrings.ToneCompressionStart,
        "peak" => AppStrings.TonePeak,
        "desaturation" => AppStrings.ToneDesaturation,
        _ => Key,
    };

    public string? Tip => Key switch
    {
        "peak" => AppStrings.TonePeakTip,
        "desaturation" => AppStrings.ToneDesaturationTip,
        _ => null,
    };

    public string Format(float value) => Unit switch
    {
        ToneMapUnit.Percent => string.Format(CultureInfo.CurrentCulture, "{0:0}%", value * 100),
        ToneMapUnit.Stops => string.Format(CultureInfo.CurrentCulture, "{0:+0.0;-0.0;0.0} EV", value),
        _ => value.ToString("0.00", CultureInfo.CurrentCulture),
    };
}

/// <summary>The available tone curves and their settings.</summary>
public static class ToneMapCurves
{
    /// <summary>Scales the measured content peak; below 100% the brightest highlights clip.</summary>
    public static readonly ToneMapParameter Peak = new("peak", 0.25f, 2f, 1f, 0.05f, ToneMapUnit.Percent);

    /// <summary>How strongly compressed highlights fade toward white.</summary>
    public static readonly ToneMapParameter Desaturation = new("desaturation", 0f, 1f, 0f, 0.05f);

    /// <summary>
    /// BT.2390 knee start as (1 + offset) × target − offset in normalized PQ; 0.5 is the standard,
    /// larger values start the roll-off lower and soften it. Below 0.5 the spline would overshoot.
    /// </summary>
    public static readonly ToneMapParameter KneeOffset = new("kneeOffset", 0.5f, 2f, 0.5f, 0.05f);

    /// <summary>Möbius knee in linear light relative to SDR white.</summary>
    public static readonly ToneMapParameter MobiusKnee = new("knee", 0.3f, 0.95f, 0.6f, 0.01f);

    public static readonly ToneMapParameter ReinhardContrast = new("contrast", 0.1f, 0.9f, 0.5f, 0.01f);

    public static readonly ToneMapParameter HableExposure = new("exposure", -2f, 4f, 2f, 0.1f, ToneMapUnit.Stops);

    public static readonly ToneMapParameter AcesExposure = new("exposure", -2f, 2f, 0f, 0.1f, ToneMapUnit.Stops);

    public static readonly ToneMapParameter PbrStart = new("start", 0.5f, 0.95f, 0.76f, 0.01f);

    public static readonly ToneMapParameter PbrDesaturation = new("desaturation", 0f, 1f, 0.15f, 0.05f);

    private static readonly ToneMapParameter[] Bt2390Parameters = [KneeOffset, Peak, Desaturation];
    private static readonly ToneMapParameter[] MobiusParameters = [MobiusKnee, Peak, Desaturation];
    private static readonly ToneMapParameter[] ReinhardParameters = [ReinhardContrast, Peak, Desaturation];
    private static readonly ToneMapParameter[] HableParameters = [HableExposure, Peak, Desaturation];
    private static readonly ToneMapParameter[] AcesParameters = [AcesExposure, Peak, Desaturation];
    private static readonly ToneMapParameter[] PbrNeutralParameters = [PbrStart, PbrDesaturation];

    public static IReadOnlyList<ToneMapCurve> All { get; } = Enum.GetValues<ToneMapCurve>();

    public static IReadOnlyList<ToneMapParameter> Parameters(ToneMapCurve curve) => curve switch
    {
        ToneMapCurve.Mobius => MobiusParameters,
        ToneMapCurve.Reinhard => ReinhardParameters,
        ToneMapCurve.Hable => HableParameters,
        ToneMapCurve.Aces => AcesParameters,
        ToneMapCurve.PbrNeutral => PbrNeutralParameters,
        _ => Bt2390Parameters,
    };

    public static string Name(ToneMapCurve curve) => curve switch
    {
        ToneMapCurve.Mobius => AppStrings.CurveMobius,
        ToneMapCurve.Reinhard => AppStrings.CurveReinhard,
        ToneMapCurve.Hable => AppStrings.CurveHable,
        ToneMapCurve.Aces => AppStrings.CurveAces,
        ToneMapCurve.PbrNeutral => AppStrings.CurvePbrNeutral,
        _ => AppStrings.CurveBt2390,
    };

    public static string Description(ToneMapCurve curve) => curve switch
    {
        ToneMapCurve.Mobius => AppStrings.CurveMobiusDescription,
        ToneMapCurve.Reinhard => AppStrings.CurveReinhardDescription,
        ToneMapCurve.Hable => AppStrings.CurveHableDescription,
        ToneMapCurve.Aces => AppStrings.CurveAcesDescription,
        ToneMapCurve.PbrNeutral => AppStrings.CurvePbrNeutralDescription,
        _ => AppStrings.CurveBt2390Description,
    };
}

/// <summary>How the SDR rendition of a snip is tone mapped: where, with which curve, and its settings.</summary>
public sealed record ToneMapSettings
{
    private static readonly IReadOnlyDictionary<string, float> NoValues = new Dictionary<string, float>();

    public static ToneMapSettings Default { get; } = new();

    public SdrToneMapping Scope { get; init; } = SdrToneMapping.Adaptive;

    public ToneMapCurve Curve { get; init; } = ToneMapCurve.Bt2390;

    /// <summary>Parameter values that differ from their defaults, keyed as "Curve.parameter".</summary>
    public IReadOnlyDictionary<string, float> Values { get; init; } = NoValues;

    /// <summary>A parameter of the selected curve.</summary>
    public float this[ToneMapParameter parameter] => Get(Curve, parameter);

    public float Get(ToneMapCurve curve, ToneMapParameter parameter) =>
        Values.TryGetValue(Key(curve, parameter), out float value) && float.IsFinite(value)
            ? Math.Clamp(value, parameter.Minimum, parameter.Maximum)
            : parameter.Default;

    /// <summary>A copy with a parameter of the selected curve changed.</summary>
    public ToneMapSettings With(ToneMapParameter parameter, float value)
    {
        var values = new Dictionary<string, float>(Values);
        string key = Key(Curve, parameter);
        if (value == parameter.Default)
            values.Remove(key);
        else
            values[key] = Math.Clamp(value, parameter.Minimum, parameter.Maximum);
        return this with { Values = values };
    }

    /// <summary>A copy with the selected curve's parameters back at their defaults.</summary>
    public ToneMapSettings ResetCurve()
    {
        string prefix = Curve + ".";
        return this with { Values = Values.Where(v => !v.Key.StartsWith(prefix, StringComparison.Ordinal)).ToDictionary() };
    }

    public bool IsCurveAtDefaults => ToneMapCurves.Parameters(Curve).All(p => this[p] == p.Default);

    public bool Equals(ToneMapSettings? other) =>
        other is not null && Scope == other.Scope && Curve == other.Curve && Values.Count == other.Values.Count &&
        Values.All(v => other.Values.TryGetValue(v.Key, out float value) && value == v.Value);

    public override int GetHashCode() => HashCode.Combine(Scope, Curve, Values.Count);

    private static string Key(ToneMapCurve curve, ToneMapParameter parameter) => curve + "." + parameter.Key;
}
