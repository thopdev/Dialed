using System.Security.Cryptography;
using System.Text;
using Dialed.Domain.Model;
using Dialed.Domain.Text;

namespace Dialed.Domain.Catalog;

/// <summary>One bean someone chose to share publicly (#105). The contributor id is only used to count different people; it never leaves the build.</summary>
public sealed record CatalogBeanInput(
    string ContributorId,
    string Name,
    string? RoasterName,
    string? Country,
    string? Region,
    string? Process,
    IReadOnlyList<string> Varieties,
    RoastLevel? RoastLevel,
    IReadOnlyList<string> TastingNotes);

/// <summary>One roaster that a public bean (or a public vendor) names.</summary>
public sealed record CatalogRoasterInput(string ContributorId, string Name, string? Country, string? City, string? Website);

/// <summary>
/// Rules the owner of the service sets by hand (#105). They survive rebuilds because they are kept as keys, not as entry ids:
/// an entry is hidden when any of its keys is hidden, and two entries are merged when each holds one key of a pair.
/// </summary>
public sealed record CatalogRules(IReadOnlySet<string> HiddenKeys, IReadOnlyList<(string A, string B)> Merges)
{
    public static CatalogRules Empty { get; } = new(new HashSet<string>(), []);
}

/// <summary>
/// A catalog roaster. <see cref="People"/> counts the different people behind it; <see cref="Keys"/> are the normalised names it was
/// built from, for the owner's moderation tools only. Nothing here points back to a contributor.
/// </summary>
public sealed record CatalogRoaster(string Id, string Name, string? Country, string? City, string? Website, int People, IReadOnlyList<string> Keys);

public sealed record CatalogBean(
    string Id,
    string Name,
    string? RoasterId,
    string? RoasterName,
    string? Country,
    string? Region,
    string? Process,
    IReadOnlyList<string> Varieties,
    RoastLevel? RoastLevel,
    IReadOnlyList<string> TastingNotes,
    int People,
    IReadOnlyList<string> Keys);

public sealed record BeanCatalog(IReadOnlyList<CatalogRoaster> Roasters, IReadOnlyList<CatalogBean> Beans)
{
    public static BeanCatalog Empty { get; } = new([], []);
}

/// <summary>
/// Builds the shared catalog from what people chose to share (#105). Pure: no server, no storage. Roasters are grouped by
/// <see cref="LabelMatcher.RoasterKey"/> and near matches; beans by roaster, name and country. An entry only appears when at least
/// <see cref="CatalogBuilder.MinimumPeople"/> different people made the same one public, so one person's typo or junk never becomes
/// catalog data and no entry can be traced to one person's item. Field values are the most common among contributors; free text
/// (varieties, tasting notes) only keeps terms at least that many people used.
/// </summary>
public static class CatalogBuilder
{
    /// <summary>The lowest threshold that still protects contributors. A smaller setting is raised to this; do not lower it without revisiting the privacy page.</summary>
    public const int MinimumPeople = 2;

    public const int MaxTerms = 8;

    public static BeanCatalog Build(
        IEnumerable<CatalogBeanInput> beans,
        IEnumerable<CatalogRoasterInput> roasters,
        int minContributors = MinimumPeople,
        CatalogRules? rules = null)
    {
        ArgumentNullException.ThrowIfNull(beans);
        ArgumentNullException.ThrowIfNull(roasters);

        var threshold = Math.Max(MinimumPeople, minContributors);
        rules ??= CatalogRules.Empty;
        var beanList = beans.Where(b => !string.IsNullOrWhiteSpace(b.Name) && !string.IsNullOrWhiteSpace(b.ContributorId)).ToList();

        // A roaster named by a public bean counts like a public roaster: the person shared the coffee and who roasted it.
        var roasterInputs = roasters
            .Where(r => !string.IsNullOrWhiteSpace(r.Name) && !string.IsNullOrWhiteSpace(r.ContributorId))
            .Concat(beanList.Where(b => !string.IsNullOrWhiteSpace(b.RoasterName)).Select(b => new CatalogRoasterInput(b.ContributorId, b.RoasterName!, null, null, null)))
            .ToList();

        var roasterClusters = Cluster(
            roasterInputs.Count,
            i => RoasterKey(roasterInputs[i]),
            (a, b) => LabelMatcher.RoasterScore(roasterInputs[a].Name, roasterInputs[b].Name) >= LabelMatcher.LikelySame,
            rules.Merges);

        // Which roaster cluster each distinct roaster key belongs to, so beans can point to it.
        var clusterOfKey = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var c = 0; c < roasterClusters.Count; c++)
        {
            foreach (var i in roasterClusters[c])
            {
                clusterOfKey.TryAdd(RoasterKey(roasterInputs[i]), c);
            }
        }

        var roasterEntries = roasterClusters
            .Select(members => BuildRoaster([.. members.Select(i => roasterInputs[i])], threshold))
            .OfType<CatalogRoaster>()
            .ToList();

        // A bean's own roaster key is the key of its roaster cluster (the smallest key), so spelling variants land together.
        string ClusterKey(string? roasterName)
        {
            var key = LabelMatcher.RoasterKey(roasterName);
            return key.Length > 0 && clusterOfKey.TryGetValue(key, out var c) ? RoasterClusterKey(roasterClusters[c], roasterInputs) : "";
        }

        var beanKeys = beanList.Select(b => ClusterKey(b.RoasterName)).ToList();
        var beanClusters = Cluster(
            beanList.Count,
            i => BeanKey(beanKeys[i], beanList[i]),
            (a, b) => beanKeys[a] == beanKeys[b]
                && SameCountry(beanList[a].Country, beanList[b].Country)
                && LabelMatcher.Similarity(beanList[a].Name, beanList[b].Name) >= LabelMatcher.LikelySame,
            rules.Merges);
        beanClusters = AttachUnknownCountries(beanClusters, beanList, beanKeys);

        var beanEntries = beanClusters
            .Select(members => BuildBean([.. members.Select(i => beanList[i])], [.. members.Select(i => BeanKey(beanKeys[i], beanList[i]))], threshold))
            .OfType<CatalogBean>()
            .ToList();

        // Link each bean to its roaster entry (when that roaster itself made the cut and was not hidden).
        var visibleRoasters = roasterEntries
            .Where(r => !r.Keys.Any(rules.HiddenKeys.Contains))
            .ToList();
        var roasterIdByKey = visibleRoasters.SelectMany(r => r.Keys.Select(k => (k, r.Id))).GroupBy(p => p.k).ToDictionary(g => g.Key, g => g.First().Id, StringComparer.Ordinal);

        var beansOut = beanEntries
            .Where(b => !b.Keys.Any(rules.HiddenKeys.Contains))
            .Select(b => b with { RoasterId = b.RoasterName is null ? null : roasterIdByKey.GetValueOrDefault(LabelMatcher.RoasterKey(b.RoasterName)) })
            .OrderByDescending(b => b.People).ThenBy(b => b.Name, StringComparer.CurrentCultureIgnoreCase).ThenBy(b => b.Id, StringComparer.Ordinal)
            .ToList();

        return new BeanCatalog(
            [.. visibleRoasters.OrderByDescending(r => r.People).ThenBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase).ThenBy(r => r.Id, StringComparer.Ordinal)],
            beansOut);
    }

    private static string RoasterKey(CatalogRoasterInput input) => LabelMatcher.RoasterKey(input.Name);

    private static string RoasterClusterKey(IReadOnlyList<int> members, IReadOnlyList<CatalogRoasterInput> inputs) =>
        members.Select(i => RoasterKey(inputs[i])).Where(k => k.Length > 0).Order(StringComparer.Ordinal).FirstOrDefault() ?? "";

    private static string BeanKey(string roasterKey, CatalogBeanInput bean) =>
        $"{roasterKey}|{FuzzyMatch.Normalize(bean.Name)}|{FuzzyMatch.Normalize(bean.Country)}";

    /// <summary>Two beans are the same coffee only from the same country; both without a country count as the same.</summary>
    private static bool SameCountry(string? a, string? b) => FuzzyMatch.Normalize(a) == FuzzyMatch.Normalize(b);

    /// <summary>
    /// A bean whose country nobody filled in joins a coffee with a country when that is the only one it could be (same roaster, similar
    /// name); when two coffees fit (the same name from Ethiopia and Brazil) it stays apart, so a missing country never glues two coffees together.
    /// </summary>
    private static List<List<int>> AttachUnknownCountries(List<List<int>> clusters, IReadOnlyList<CatalogBeanInput> beans, IReadOnlyList<string> roasterKeys)
    {
        bool Unknown(List<int> cluster) => cluster.All(i => string.IsNullOrWhiteSpace(beans[i].Country));
        var known = clusters.Where(c => !Unknown(c)).ToList();
        var result = new List<List<int>>(known);
        foreach (var unknown in clusters.Where(Unknown))
        {
            var fits = known.Where(k => roasterKeys[k[0]] == roasterKeys[unknown[0]]
                && LabelMatcher.Similarity(beans[k[0]].Name, beans[unknown[0]].Name) >= LabelMatcher.LikelySame).ToList();
            if (fits.Count == 1)
            {
                fits[0].AddRange(unknown);
            }
            else
            {
                result.Add(unknown);
            }
        }

        return result;
    }

    /// <summary>
    /// Groups items: the same key always belongs together, and so do items the <paramref name="near"/> test accepts; hand-made merge rules
    /// join the groups that hold the two keys of a pair.
    /// </summary>
    private static List<List<int>> Cluster(int count, Func<int, string> key, Func<int, int, bool> near, IReadOnlyList<(string A, string B)> merges)
    {
        var parent = Enumerable.Range(0, count).ToArray();

        int Find(int x)
        {
            while (parent[x] != x)
            {
                parent[x] = parent[parent[x]];
                x = parent[x];
            }

            return x;
        }

        void Union(int a, int b)
        {
            var (ra, rb) = (Find(a), Find(b));
            if (ra != rb)
            {
                parent[Math.Max(ra, rb)] = Math.Min(ra, rb); // the earliest item leads, so the grouping is stable
            }
        }

        var keys = Enumerable.Range(0, count).Select(key).ToArray();
        var firstOfKey = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < count; i++)
        {
            if (keys[i].Length > 0 && !firstOfKey.TryAdd(keys[i], i))
            {
                Union(firstOfKey[keys[i]], i);
            }
        }

        // Near matches are only compared between distinct keys (one representative each), which keeps this cheap.
        var representatives = firstOfKey.Values.Order().ToList();
        for (var x = 0; x < representatives.Count; x++)
        {
            for (var y = x + 1; y < representatives.Count; y++)
            {
                if (Find(representatives[x]) != Find(representatives[y]) && near(representatives[x], representatives[y]))
                {
                    Union(representatives[x], representatives[y]);
                }
            }
        }

        foreach (var (a, b) in merges)
        {
            if (firstOfKey.TryGetValue(a, out var ia) && firstOfKey.TryGetValue(b, out var ib))
            {
                Union(ia, ib);
            }
        }

        // Items without a key (nothing to group on) stay on their own.
        return [.. Enumerable.Range(0, count).GroupBy(Find).OrderBy(g => g.Key).Select(g => g.ToList())];
    }

    private static CatalogRoaster? BuildRoaster(IReadOnlyList<CatalogRoasterInput> members, int threshold)
    {
        var people = members.Select(m => m.ContributorId).Distinct(StringComparer.Ordinal).Count();
        var keys = members.Select(RoasterKey).Where(k => k.Length > 0).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        var entry = new CatalogRoaster(
            "r-" + Hash(keys.FirstOrDefault() ?? ""),
            Vote(members.Select(m => (m.ContributorId, (string?)m.Name))) ?? "",
            Vote(members.Select(m => (m.ContributorId, m.Country))),
            Vote(members.Select(m => (m.ContributorId, m.City))),
            Vote(members.Select(m => (m.ContributorId, m.Website))),
            people,
            keys);
        return people >= threshold ? entry : null;
    }

    private static CatalogBean? BuildBean(IReadOnlyList<CatalogBeanInput> members, IReadOnlyList<string> memberKeys, int threshold)
    {
        var people = members.Select(m => m.ContributorId).Distinct(StringComparer.Ordinal).Count();
        if (people < threshold)
        {
            return null;
        }

        var keys = memberKeys.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        var roastLevels = members.Where(m => m.RoastLevel is not null).GroupBy(m => m.ContributorId).Select(g => g.First().RoastLevel!.Value)
            .GroupBy(l => l).OrderByDescending(g => g.Count()).ThenBy(g => g.Key).Select(g => (RoastLevel?)g.Key).FirstOrDefault();
        return new CatalogBean(
            "b-" + Hash(keys[0]),
            Vote(members.Select(m => (m.ContributorId, (string?)m.Name))) ?? "",
            null,
            Vote(members.Select(m => (m.ContributorId, m.RoasterName))),
            Vote(members.Select(m => (m.ContributorId, m.Country))),
            Vote(members.Select(m => (m.ContributorId, m.Region))),
            Vote(members.Select(m => (m.ContributorId, m.Process))),
            Terms(members.Select(m => (m.ContributorId, m.Varieties)), threshold),
            roastLevels,
            Terms(members.Select(m => (m.ContributorId, m.TastingNotes)), threshold),
            people,
            keys);
    }

    /// <summary>
    /// The most common value, counting each person once. A tie goes to the alphabetically first. The spelling shown is the one most
    /// people used for that value.
    /// </summary>
    internal static string? Vote(IEnumerable<(string Person, string? Value)> values)
    {
        var perPerson = values
            .Where(v => !string.IsNullOrWhiteSpace(v.Value))
            .GroupBy(v => v.Person, StringComparer.Ordinal)
            .Select(g => g.First().Value!.Trim())
            .ToList();
        var winner = perPerson
            .GroupBy(FuzzyMatch.Normalize)
            .Where(g => g.Key.Length > 0)
            .OrderByDescending(g => g.Count())
            .ThenBy(g => g.Key, StringComparer.Ordinal)
            .FirstOrDefault();
        return winner?.GroupBy(v => v).OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal).First().Key;
    }

    /// <summary>Terms (varieties, tasting notes) that at least <paramref name="threshold"/> different people used, most common first.</summary>
    internal static IReadOnlyList<string> Terms(IEnumerable<(string Person, IReadOnlyList<string> Terms)> lists, int threshold)
    {
        var uses = lists
            .SelectMany(l => l.Terms.Where(t => !string.IsNullOrWhiteSpace(t)).Select(t => (l.Person, Term: t.Trim())).DistinctBy(x => FuzzyMatch.Normalize(x.Term)))
            .GroupBy(x => FuzzyMatch.Normalize(x.Term))
            .Where(g => g.Key.Length > 0)
            .Select(g => (People: g.Select(x => x.Person).Distinct(StringComparer.Ordinal).Count(), Shown: g.GroupBy(x => x.Term).OrderByDescending(s => s.Count()).ThenBy(s => s.Key, StringComparer.Ordinal).First().Key))
            .Where(t => t.People >= threshold)
            .OrderByDescending(t => t.People).ThenBy(t => t.Shown, StringComparer.CurrentCultureIgnoreCase)
            .Take(MaxTerms)
            .Select(t => t.Shown);
        return [.. uses];
    }

    private static string Hash(string key) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..12].ToLowerInvariant();
}
