using Dialed.Domain.Text;

namespace Dialed.Domain.Model;

public enum BeanSort
{
    Name,
    Rating,
    Newest,
}

/// <summary>What the bean list is narrowed to. A <c>null</c> member means "any".</summary>
public sealed record BeanFilter(
    string? Search = null,
    string? RoasterId = null,
    string? Process = null,
    RoastLevel? RoastLevel = null,
    bool? InStock = null);

public static class BeanQuery
{
    /// <summary>Common processing methods offered as a list; anything else is entered as "Other".</summary>
    public static IReadOnlyList<string> KnownProcesses { get; } =
        ["Washed", "Natural", "Honey", "Anaerobic", "Carbonic maceration"];

    public static bool IsKnownProcess(string? process) =>
        process is not null && KnownProcesses.Contains(process, StringComparer.OrdinalIgnoreCase);

    /// <summary>A bean is in stock while at least one of its bags is not finished.</summary>
    public static bool HasStock(string beanId, IEnumerable<Bag> bags) =>
        bags.Any(bag => bag.BeanId == beanId && bag.Status != Inventory.BagStatus.Finished);

    public static IReadOnlyList<Bean> Apply(IEnumerable<Bean> beans, IEnumerable<Bag> bags, BeanFilter filter, BeanSort sort)
    {
        ArgumentNullException.ThrowIfNull(beans);
        ArgumentNullException.ThrowIfNull(bags);
        ArgumentNullException.ThrowIfNull(filter);

        var bagList = bags as IReadOnlyCollection<Bag> ?? [.. bags];
        var query = beans.Where(bean => !bean.Deleted);

        if (filter.RoasterId is not null)
        {
            query = query.Where(bean => bean.RoasterId == filter.RoasterId);
        }

        if (filter.Process is not null)
        {
            query = query.Where(bean => string.Equals(bean.Process, filter.Process, StringComparison.OrdinalIgnoreCase));
        }

        if (filter.RoastLevel is { } level)
        {
            query = query.Where(bean => bean.RoastLevel == level);
        }

        if (filter.InStock is { } inStock)
        {
            query = query.Where(bean => HasStock(bean.Id, bagList) == inStock);
        }

        if (!string.IsNullOrWhiteSpace(filter.Search))
        {
            return FuzzyMatch.Rank(query, filter.Search, bean => bean.Name);
        }

        return sort switch
        {
            BeanSort.Rating => [.. query.OrderByDescending(bean => bean.Rating ?? 0).ThenBy(bean => FuzzyMatch.Normalize(bean.Name), StringComparer.Ordinal)],
            BeanSort.Newest => [.. query.OrderByDescending(bean => bean.CreatedAt)],
            _ => [.. query.OrderBy(bean => FuzzyMatch.Normalize(bean.Name), StringComparer.Ordinal)],
        };
    }

    /// <summary>Splits free text into trimmed, distinct (case-insensitive) values, e.g. for varieties or tasting notes.</summary>
    public static IReadOnlyList<string> AddUnique(IReadOnlyList<string> existing, string? input)
    {
        var value = input?.Trim();
        if (string.IsNullOrEmpty(value) || existing.Contains(value, StringComparer.OrdinalIgnoreCase))
        {
            return existing;
        }

        return [.. existing, value];
    }
}
