using System.Text.Json;
using System.Text.Json.Serialization;

namespace Dialed.Domain.BrewMethods;

/// <summary>
/// The seeded, read-only list of brew methods. The data is versioned JSON embedded in this assembly
/// (<c>BrewMethods/brew-methods.json</c>), so it is available offline and identical on every platform.
/// </summary>
public sealed class BrewMethodCatalog
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    private readonly Dictionary<string, BrewMethod> byCode;

    private BrewMethodCatalog(int version, IReadOnlyList<BrewMethod> methods)
    {
        Version = version;
        Methods = methods;
        byCode = methods.ToDictionary(method => method.Code, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Version of the data file; raised when methods or parameters change.</summary>
    public int Version { get; }

    public IReadOnlyList<BrewMethod> Methods { get; }

    /// <summary>The catalog bundled with the app.</summary>
    public static BrewMethodCatalog Default { get; } = LoadEmbedded();

    /// <summary>All methods with the favourites first; both groups keep the catalog order.</summary>
    public IReadOnlyList<BrewMethod> Ordered(IEnumerable<string> favourites)
    {
        ArgumentNullException.ThrowIfNull(favourites);
        var favourite = favourites.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return [.. Methods.Where(m => favourite.Contains(m.Code)), .. Methods.Where(m => !favourite.Contains(m.Code))];
    }

    public BrewMethod? Find(string code) => byCode.GetValueOrDefault(code);

    public BrewMethod Get(string code) =>
        Find(code) ?? throw new KeyNotFoundException($"Unknown brew method '{code}'.");

    /// <summary>Reads a catalog from JSON in the format of <c>brew-methods.json</c>. Rejects unknown fields, duplicate codes and empty parameter lists.</summary>
    public static BrewMethodCatalog Parse(Stream json)
    {
        var file = JsonSerializer.Deserialize<CatalogFile>(json, Options)
            ?? throw new JsonException("Brew method catalog is empty.");

        if (file.Methods is null or { Count: 0 })
        {
            throw new JsonException("Brew method catalog has no methods.");
        }

        var methods = new List<BrewMethod>(file.Methods.Count);
        foreach (var entry in file.Methods)
        {
            if (string.IsNullOrWhiteSpace(entry.Code) || string.IsNullOrWhiteSpace(entry.Name))
            {
                throw new JsonException("Every brew method needs a code and a name.");
            }

            if (entry.Parameters is null or { Count: 0 })
            {
                throw new JsonException($"Brew method '{entry.Code}' has no parameters.");
            }

            if (methods.Any(existing => string.Equals(existing.Code, entry.Code, StringComparison.OrdinalIgnoreCase)))
            {
                throw new JsonException($"Duplicate brew method '{entry.Code}'.");
            }

            methods.Add(new BrewMethod(entry.Code, entry.Name, entry.Category, [.. entry.Parameters.Distinct()]));
        }

        return new BrewMethodCatalog(file.Version, methods);
    }

    private static BrewMethodCatalog LoadEmbedded()
    {
        using var stream = typeof(BrewMethodCatalog).Assembly.GetManifestResourceStream("Dialed.Domain.brew-methods.json")
            ?? throw new InvalidOperationException("brew-methods.json is not embedded in Dialed.Domain.");
        return Parse(stream);
    }

    private sealed record CatalogFile(int Version, List<MethodEntry>? Methods);

    private sealed record MethodEntry(string? Code, string? Name, BrewMethodCategory Category, List<BrewParameter>? Parameters);
}
