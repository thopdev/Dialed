using Dialed.Domain.Model;

namespace Dialed.Domain.DialIn;

/// <summary>Where a bag stands in being dialled in for the method brewed most recently.</summary>
public enum CoachStage
{
    /// <summary>Nothing brewed from this bag yet.</summary>
    NotStarted,

    /// <summary>Brewed, but the cup is not there yet: a low rating or a taste that is off. More tries follow.</summary>
    Working,

    /// <summary>The latest brew is rated <see cref="DialInCoach.GoodRating"/> or more and tastes balanced.</summary>
    DialedIn,
}

/// <summary>The coach's view of one bag and method: how many tries so far, the best rating, and the brew to build on.</summary>
public sealed record CoachProgress(CoachStage Stage, string? MethodCode, int Attempts, int? BestRating, Brew? Latest);

/// <summary>
/// Dial-in coach: after a first brew of a new bean it keeps count of the tries for the same method until one is rated well and tastes
/// balanced, so the screen can say "try 3, best 3 stars" and offer the next step of <see cref="DialInAdvisor"/>. Pure bookkeeping on logged brews.
/// </summary>
public static class DialInCoach
{
    /// <summary>The rating at which the coach calls a bag dialled in (together with a balanced taste).</summary>
    public const int GoodRating = 4;

    public static CoachProgress Build(string bagId, IEnumerable<Brew> brews)
    {
        ArgumentNullException.ThrowIfNull(brews);
        var all = brews.Where(b => !b.Deleted && b.BagId == bagId).OrderBy(b => b.BrewedAt).ToList();
        if (all.Count == 0)
        {
            return new CoachProgress(CoachStage.NotStarted, null, 0, null, null);
        }

        var latest = all[^1];
        var series = all.Where(b => b.MethodCode == latest.MethodCode).ToList();
        var best = series.Where(b => b.Rating is not null).Select(b => b.Rating).Max();
        var done = latest.Rating is >= GoodRating && latest.Taste.IsBalanced;
        return new CoachProgress(done ? CoachStage.DialedIn : CoachStage.Working, latest.MethodCode, series.Count, best, latest);
    }
}
