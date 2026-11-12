using System.Text.Json;
using System.Text.Json.Serialization;
using Dialed.Domain.Model;

namespace Dialed.Domain.BrewMethods;

/// <summary>A generic recipe shipped with the app for a brew method. Read-only; copy it to make it your own.</summary>
public sealed record RecipeTemplate(
    string MethodCode,
    string Name,
    decimal? DoseG = null,
    decimal? WaterG = null,
    decimal? YieldG = null,
    decimal? TempC = null,
    int? TimeS = null,
    int? PreinfusionS = null,
    string? Filter = null,
    string? Notes = null,
    IReadOnlyList<RecipeStep>? Steps = null)
{
    /// <summary>A personal recipe for the household, copied from this template. Ids and audit fields are filled in when it is saved.</summary>
    public Recipe ToRecipe(string? grinderId = null, string? brewerId = null) => new()
    {
        MethodCode = MethodCode,
        Name = Name,
        DoseG = DoseG,
        WaterG = WaterG,
        YieldG = YieldG,
        TempC = TempC,
        TimeS = TimeS,
        PreinfusionS = PreinfusionS,
        Filter = Filter,
        Notes = Notes,
        Steps = Steps ?? [],
        GrinderId = grinderId,
        BrewerId = brewerId,
        IsDefaultTemplate = false,
    };
}

/// <summary>
/// One default recipe per seeded brew method, as versioned JSON embedded in this assembly
/// (<c>BrewMethods/default-recipes.json</c>), so it is available offline and the same on every platform.
/// </summary>
public sealed class DefaultRecipeCatalog
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    private readonly Dictionary<string, RecipeTemplate> byMethod;

    private DefaultRecipeCatalog(int version, IReadOnlyList<RecipeTemplate> recipes)
    {
        Version = version;
        Recipes = recipes;
        byMethod = recipes.GroupBy(r => r.MethodCode, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
    }

    public int Version { get; }

    public IReadOnlyList<RecipeTemplate> Recipes { get; }

    public static DefaultRecipeCatalog Default { get; } = LoadEmbedded("default-recipes.json", onePerMethod: true);

    /// <summary>Well-known pour routines (4:6, Hoffmann, Rao and others): several per method, with the pattern of each pour. Read-only; copy one to make it your own.</summary>
    public static DefaultRecipeCatalog Routines { get; } = LoadEmbedded("pour-routines.json", onePerMethod: false);

    public RecipeTemplate? ForMethod(string methodCode) => byMethod.GetValueOrDefault(methodCode);

    /// <summary>Reads templates from JSON. Rejects unknown fields, unknown methods, duplicates, and values the method does not use.</summary>
    public static DefaultRecipeCatalog Parse(Stream json, BrewMethodCatalog methods, bool onePerMethod = true)
    {
        ArgumentNullException.ThrowIfNull(methods);
        var file = JsonSerializer.Deserialize<CatalogFile>(json, Options)
            ?? throw new JsonException("Default recipe catalog is empty.");
        if (file.Recipes is null or { Count: 0 })
        {
            throw new JsonException("Default recipe catalog has no recipes.");
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var recipe in file.Recipes)
        {
            var method = methods.Find(recipe.MethodCode)
                ?? throw new JsonException($"Default recipe '{recipe.Name}' uses unknown method '{recipe.MethodCode}'.");
            if (!seen.Add(recipe.MethodCode) && onePerMethod)
            {
                throw new JsonException($"More than one default recipe for '{recipe.MethodCode}'.");
            }

            Require(method, BrewParameter.Dose, recipe.DoseG is not null, recipe.Name);
            Require(method, BrewParameter.Water, recipe.WaterG is not null, recipe.Name);
            Require(method, BrewParameter.Yield, recipe.YieldG is not null, recipe.Name);
            Require(method, BrewParameter.Temperature, recipe.TempC is not null, recipe.Name);
            Require(method, BrewParameter.Time, recipe.TimeS is not null, recipe.Name);
            Require(method, BrewParameter.Preinfusion, recipe.PreinfusionS is not null, recipe.Name);
            Require(method, BrewParameter.Filter, recipe.Filter is not null, recipe.Name);
            Require(method, BrewParameter.Steps, recipe.Steps is { Count: > 0 }, recipe.Name);
        }

        return new DefaultRecipeCatalog(file.Version, file.Recipes);
    }

    // A template may leave out a parameter its method uses (for example the filter), but never fill in one it does not.
    private static void Require(BrewMethod method, BrewParameter parameter, bool present, string name)
    {
        if (present && !method.Uses(parameter))
        {
            throw new JsonException($"Default recipe '{name}' sets {parameter}, which {method.Name} does not use.");
        }
    }

    private static DefaultRecipeCatalog LoadEmbedded(string file, bool onePerMethod)
    {
        using var stream = typeof(DefaultRecipeCatalog).Assembly.GetManifestResourceStream($"Dialed.Domain.{file}")
            ?? throw new InvalidOperationException($"{file} is not embedded in Dialed.Domain.");
        return Parse(stream, BrewMethodCatalog.Default, onePerMethod);
    }

    private sealed record CatalogFile(int Version, List<RecipeTemplate>? Recipes);
}
