namespace Dialed.Domain.Model;

/// <summary>Where a guided brew is at a given moment.</summary>
/// <param name="ElapsedS">Seconds since the timer started.</param>
/// <param name="CurrentIndex">Index of the step that is running, or -1 before the first step starts.</param>
/// <param name="Current">The running step, or <c>null</c> before the first one.</param>
/// <param name="Next">The step after it, or <c>null</c> after the last one.</param>
/// <param name="SecondsToNext">Countdown to <paramref name="Next"/>, or <c>null</c> when there is none.</param>
/// <param name="TargetWaterG">Total water to have poured by the end of the running step (carried over from earlier steps when it has none).</param>
/// <param name="IsFinished">The recipe's time is up.</param>
public sealed record TimerState(
    int ElapsedS,
    int CurrentIndex,
    RecipeStep? Current,
    RecipeStep? Next,
    int? SecondsToNext,
    decimal? TargetWaterG,
    bool IsFinished);

/// <summary>Turns a recipe's steps into what the timer shows at any second. Pure, so it is tested without a clock.</summary>
public sealed class BrewTimeline
{
    private readonly IReadOnlyList<RecipeStep> steps;

    public BrewTimeline(IEnumerable<RecipeStep> steps, int? totalS = null)
    {
        ArgumentNullException.ThrowIfNull(steps);
        this.steps = [.. steps.OrderBy(step => step.AtS)];

        var lastEnd = this.steps.Count == 0 ? (int?)null : this.steps.Max(step => step.AtS + (step.DurationS ?? 0));
        TotalS = totalS is null ? lastEnd : lastEnd is null ? totalS : Math.Max(totalS.Value, lastEnd.Value);
    }

    public IReadOnlyList<RecipeStep> Steps => steps;

    /// <summary>The recipe's time, or the end of its last step when that is later; <c>null</c> when neither is known.</summary>
    public int? TotalS { get; }

    public static BrewTimeline For(Recipe recipe)
    {
        ArgumentNullException.ThrowIfNull(recipe);
        return new BrewTimeline(recipe.Steps, recipe.TimeS);
    }

    public TimerState At(int elapsedS)
    {
        var elapsed = Math.Max(0, elapsedS);
        var index = -1;
        for (var i = 0; i < steps.Count; i++)
        {
            if (steps[i].AtS <= elapsed)
            {
                index = i;
            }
        }

        var current = index >= 0 ? steps[index] : null;
        var next = index + 1 < steps.Count ? steps[index + 1] : null;

        decimal? target = null;
        for (var i = index; i >= 0 && target is null; i--)
        {
            target = steps[i].WaterG;
        }

        return new TimerState(
            elapsed,
            index,
            current,
            next,
            next is null ? null : next.AtS - elapsed,
            target,
            TotalS is { } total && elapsed >= total);
    }

    /// <summary>True when a new step has started between the two moments, so the timer can give its cue.</summary>
    public bool StepStartedBetween(int previousS, int nowS) => At(nowS).CurrentIndex != At(previousS).CurrentIndex;
}

public enum ShotStatus
{
    TooShort,
    InBand,
    TooLong,
}

/// <summary>The time window a good espresso shot should land in.</summary>
public sealed record ShotBand(int LowerS, int UpperS)
{
    /// <summary>25 to 30 seconds, the usual 1:2 shot.</summary>
    public static ShotBand Default { get; } = new(25, 30);

    /// <summary>Around the recipe's time (three seconds under to two over), or the default band without one.</summary>
    public static ShotBand For(Recipe? recipe) =>
        recipe?.TimeS is { } time and > 0 ? new ShotBand(Math.Max(1, time - 3), time + 2) : Default;

    public ShotStatus StatusAt(int elapsedS) =>
        elapsedS < LowerS ? ShotStatus.TooShort : elapsedS <= UpperS ? ShotStatus.InBand : ShotStatus.TooLong;
}
