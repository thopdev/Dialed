using System.Globalization;

namespace Dialed.Domain.Equipment;

public enum GrindSettingKind
{
    /// <summary>A number on the grinder's dial or display (Eureka 0-11, Niche 0-50, Fellow Ode 1-11).</summary>
    Number,

    /// <summary>Clicks counted from fully closed (Comandante, Timemore).</summary>
    Clicks,
}

public enum GrindValidation
{
    Valid,
    BelowMinimum,
    AboveMaximum,
    NotOnStep,
}

/// <summary>
/// How a grinder's setting is entered: its kind, range and step. A step of 0 means stepless (any decimal in range);
/// click grinders always step by whole clicks.
/// </summary>
public sealed record GrindScale
{
    private GrindScale(GrindSettingKind kind, decimal min, decimal max, decimal step)
    {
        if (max <= min)
        {
            throw new ArgumentException("The highest setting must be above the lowest.", nameof(max));
        }

        if (step < 0m || (step > 0m && step > max - min))
        {
            throw new ArgumentOutOfRangeException(nameof(step), "The step must be 0 (stepless) or fit within the range.");
        }

        Kind = kind;
        Min = min;
        Max = max;
        Step = step;
    }

    public GrindSettingKind Kind { get; }

    public decimal Min { get; }

    public decimal Max { get; }

    /// <summary>Distance between two settings; 0 for a stepless grinder.</summary>
    public decimal Step { get; }

    public bool IsStepped => Step > 0m;

    /// <summary>A numbered dial. <paramref name="step"/> 0 = stepless.</summary>
    public static GrindScale Number(decimal min, decimal max, decimal step = 0m) => new(GrindSettingKind.Number, min, max, step);

    /// <summary>Whole clicks from closed, for example 0 to 40 on a hand grinder.</summary>
    public static GrindScale Clicks(decimal max, decimal min = 0m) => new(GrindSettingKind.Clicks, min, max, 1m);

    public GrindValidation Validate(decimal value)
    {
        if (value < Min)
        {
            return GrindValidation.BelowMinimum;
        }

        if (value > Max)
        {
            return GrindValidation.AboveMaximum;
        }

        return IsStepped && (value - Min) % Step != 0m ? GrindValidation.NotOnStep : GrindValidation.Valid;
    }

    /// <summary>The next coarser setting, or the same one at the top of the range.</summary>
    public decimal Increase(decimal value) => IsStepped ? Math.Min(Max, Snap(value) + Step) : value;

    /// <summary>The next finer setting, or the same one at the bottom of the range.</summary>
    public decimal Decrease(decimal value) => IsStepped ? Math.Max(Min, Snap(value) - Step) : value;

    /// <summary>Nearest valid setting inside the range.</summary>
    public decimal Snap(decimal value)
    {
        var clamped = Math.Clamp(value, Min, Max);
        return IsStepped ? Min + (Math.Round((clamped - Min) / Step, MidpointRounding.AwayFromZero) * Step) : clamped;
    }

    /// <summary>Reads what the user typed; accepts a comma or a point as decimal separator.</summary>
    public static bool TryParse(string? text, out decimal value)
    {
        value = 0m;
        return !string.IsNullOrWhiteSpace(text)
            && decimal.TryParse(text.Trim().Replace(',', '.'), NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out value);
    }

    /// <summary>The setting as written down: "18" for clicks, "5.5" for a dial (no trailing zeros).</summary>
    public static string Format(decimal value) => value.ToString("0.##", CultureInfo.InvariantCulture);
}
