namespace Dialed.Domain.Equipment;

/// <summary>Turns what a person types for a grinder's scale into a <see cref="GrindScale"/>, or says what is wrong.</summary>
public static class GrindScaleInput
{
    public const string ErrorRange = "Grind_ErrorRange";
    public const string ErrorStep = "Grind_ErrorStep";
    public const string ErrorMissing = "Grind_ErrorMissing";

    /// <param name="kind">Clicks count whole clicks from <paramref name="min"/>; Number is a dial, stepless when <paramref name="step"/> is 0 or empty.</param>
    /// <param name="errorKey">A localization key when the input is not usable.</param>
    public static bool TryCreate(GrindSettingKind kind, decimal? min, decimal? max, decimal? step, out GrindScale? scale, out string? errorKey)
    {
        scale = null;
        errorKey = null;
        var low = min ?? 0m;

        if (max is null)
        {
            errorKey = ErrorMissing;
            return false;
        }

        if (max <= low || low < 0m)
        {
            errorKey = ErrorRange;
            return false;
        }

        if (kind == GrindSettingKind.Clicks)
        {
            scale = GrindScale.Clicks(max.Value, low);
            return true;
        }

        var size = step ?? 0m;
        if (size < 0m || size > max - low)
        {
            errorKey = ErrorStep;
            return false;
        }

        scale = GrindScale.Number(low, max.Value, size);
        return true;
    }

    public static string Describe(GrindScale scale) =>
        scale.Kind == GrindSettingKind.Clicks
            ? $"{GrindScale.Format(scale.Min)}–{GrindScale.Format(scale.Max)} clicks"
            : scale.IsStepped
                ? $"{GrindScale.Format(scale.Min)}–{GrindScale.Format(scale.Max)}, step {GrindScale.Format(scale.Step)}"
                : $"{GrindScale.Format(scale.Min)}–{GrindScale.Format(scale.Max)}, stepless";
}
