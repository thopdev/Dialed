using Dialed.Domain.Model;

namespace Dialed.Domain.Text;

public sealed record MatchCandidate<T>(T Item, double Score)
{
    /// <summary>Close enough that the app can suggest linking to it instead of creating a new one.</summary>
    public bool IsLikelySame => Score >= LabelMatcher.LikelySame;
}

public sealed record LabelMatch(IReadOnlyList<MatchCandidate<Vendor>> Roasters, IReadOnlyList<MatchCandidate<Bean>> Beans)
{
    public MatchCandidate<Vendor>? BestRoaster => Roasters.FirstOrDefault(c => c.IsLikelySame);

    public MatchCandidate<Bean>? BestBean => Beans.FirstOrDefault(c => c.IsLikelySame);
}

/// <summary>
/// Recognises roasters and beans that are already in the household when a label was scanned (#82), so the app can offer
/// "link to existing" next to "create new". Scores run from 0 to 1.
/// </summary>
public static class LabelMatcher
{
    public const double LikelySame = 0.85;

    public const double MinCandidate = 0.5;

    public const int MaxCandidates = 5;

    private static readonly string[] RoasterNoise =
        ["coffee", "coffees", "roaster", "roasters", "roastery", "roasting", "roasted", "koffie", "koffiebranderij", "branderij", "brander", "co", "company", "cafe", "the", "de", "bv"];

    public static LabelMatch Find(string? roaster, string? beanName, string? country, IEnumerable<Vendor> vendors, IEnumerable<Bean> beans)
    {
        var roasters = Rank(vendors.Where(v => !v.Deleted && v.Kinds.HasFlag(VendorKinds.Roaster)), v => RoasterScore(roaster, v.Name));
        var roasterScores = roasters.ToDictionary(c => c.Item.Id, c => c.Score);
        var hasRoaster = !string.IsNullOrWhiteSpace(RoasterKey(roaster));

        var scored = beans
            .Where(b => !b.Deleted)
            .Select(bean =>
            {
                var score = Similarity(beanName, bean.Name);
                if (score > 0 && hasRoaster && bean.RoasterId is { } id)
                {
                    // the same name from a different roaster is probably another coffee
                    score *= roasterScores.GetValueOrDefault(id) >= MinCandidate ? 1d : 0.6d;
                }

                if (score > 0 && Known(country, bean.Origin.Country) && FuzzyMatch.Normalize(country) != FuzzyMatch.Normalize(bean.Origin.Country))
                {
                    score *= 0.8d;
                }

                return new MatchCandidate<Bean>(bean, Math.Round(score, 3));
            });

        return new LabelMatch(roasters, Top(scored));
    }

    /// <summary>Symmetric similarity of two names: the text on a label and the name in the list may each be the longer one.</summary>
    public static double Similarity(string? a, string? b) => Math.Max(FuzzyMatch.Score(a, b), FuzzyMatch.Score(b, a));

    public static double RoasterScore(string? scanned, string? known)
    {
        var a = RoasterKey(scanned);
        var b = RoasterKey(known);
        return a.Length == 0 || b.Length == 0 ? 0d : Math.Round(Similarity(a, b), 3);
    }

    /// <summary>The roaster name without words like "Coffee Roasters", which labels and lists often differ in.</summary>
    public static string RoasterKey(string? name)
    {
        var words = FuzzyMatch.Normalize(name).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var meaningful = words.Where(w => !RoasterNoise.Contains(w)).ToArray();
        return string.Join(' ', meaningful.Length > 0 ? meaningful : words);
    }

    private static bool Known(string? a, string? b) => !string.IsNullOrWhiteSpace(a) && !string.IsNullOrWhiteSpace(b);

    private static List<MatchCandidate<T>> Rank<T>(IEnumerable<T> items, Func<T, double> score) =>
        Top(items.Select(item => new MatchCandidate<T>(item, score(item))));

    private static List<MatchCandidate<T>> Top<T>(IEnumerable<MatchCandidate<T>> candidates) =>
        [.. candidates.Where(c => c.Score >= MinCandidate).OrderByDescending(c => c.Score).Take(MaxCandidates)];
}
