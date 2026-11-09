using System.Globalization;
using Dialed.Domain.Inventory;
using Dialed.Domain.Model;

namespace Dialed.Domain.Import;

/// <summary>What a CSV column can be imported as.</summary>
public enum ImportField
{
    Name,
    Roaster,
    Country,
    Region,
    Process,
    Varieties,
    RoastLevel,
    Rating,
    Notes,
    WeightG,
    Price,
    PurchasedOn,
    RoastDate,
}

/// <summary>One CSV row turned into a bean, an optional roaster name and an optional bag (when a weight was given).</summary>
public sealed record PlannedBean(int Line, Bean Bean, string? RoasterName, Bag? Bag);

public sealed record ImportPlan(IReadOnlyList<PlannedBean> Beans, int AlreadyThere, IReadOnlyList<string> Warnings);

/// <summary>Reads beans (and the bags they came in) from a CSV export of a spreadsheet or another coffee app. Pure: no storage.</summary>
public static class BeanImport
{
    private static readonly Dictionary<ImportField, string[]> Synonyms = new()
    {
        [ImportField.Name] = ["name", "bean", "beans", "coffee", "naam", "boon", "koffie", "product", "title"],
        [ImportField.Roaster] = ["roaster", "roastery", "brand", "brander", "branderij", "merk", "producer name"],
        [ImportField.Country] = ["country", "origin", "land", "herkomst"],
        [ImportField.Region] = ["region", "regio"],
        [ImportField.Process] = ["process", "processing", "method", "proces", "verwerking"],
        [ImportField.Varieties] = ["variety", "varieties", "varietal", "cultivar", "variëteit", "variëteiten", "soort"],
        [ImportField.RoastLevel] = ["roast", "roast level", "roastlevel", "roast degree", "branding", "brandingsgraad"],
        [ImportField.Rating] = ["rating", "score", "stars", "beoordeling", "cijfer"],
        [ImportField.Notes] = ["notes", "note", "tasting notes", "comments", "comment", "notities", "notitie", "opmerkingen", "smaak"],
        [ImportField.WeightG] = ["weight", "size", "bag size", "amount", "gewicht", "grams", "gram", "g"],
        [ImportField.Price] = ["price", "cost", "paid", "prijs"],
        [ImportField.PurchasedOn] = ["purchased", "purchase date", "bought", "bought on", "date", "datum", "gekocht", "aankoopdatum"],
        [ImportField.RoastDate] = ["roast date", "roasted", "roasted on", "brandingsdatum", "gebrand op"],
    };

    private static readonly string[] DateFormats = ["yyyy-MM-dd", "dd-MM-yyyy", "d-M-yyyy", "dd/MM/yyyy", "d/M/yyyy", "dd.MM.yyyy", "d.M.yyyy", "yyyy/MM/dd"];

    /// <summary>Guesses which column holds what from the header names (English and Dutch). Each column is used for at most one field.</summary>
    public static IReadOnlyDictionary<ImportField, int> DetectMapping(IReadOnlyList<string> headers)
    {
        var mapping = new Dictionary<ImportField, int>();
        var used = new HashSet<int>();
        var normalized = headers.Select(h => h.Trim().ToLowerInvariant().Replace('_', ' ').Replace('-', ' ')).ToList();

        // exact matches first, so "roast date" is not taken for "roast"
        foreach (var field in Enum.GetValues<ImportField>())
        {
            var index = normalized.FindIndex(h => Synonyms[field].Contains(h));
            if (index >= 0 && used.Add(index))
            {
                mapping[field] = index;
            }
        }

        return mapping;
    }

    public static ImportPlan Plan(
        IReadOnlyList<IReadOnlyList<string>> rows,
        IReadOnlyDictionary<ImportField, int> mapping,
        IEnumerable<(string Name, string? Roaster)> existing)
    {
        var known = existing.Select(e => Key(e.Name, e.Roaster)).ToHashSet();
        var planned = new List<PlannedBean>();
        var warnings = new List<string>();
        var already = 0;

        for (var i = 0; i < rows.Count; i++)
        {
            var line = i + 2; // the header is line 1
            string? Get(ImportField field) =>
                mapping.TryGetValue(field, out var column) && column < rows[i].Count && rows[i][column].Length > 0 ? rows[i][column] : null;

            var name = Get(ImportField.Name);
            if (name is null)
            {
                warnings.Add($"Line {line}: no name, skipped.");
                continue;
            }

            var roaster = Get(ImportField.Roaster);
            if (!known.Add(Key(name, roaster)))
            {
                already++;
                continue;
            }

            var bean = new Bean
            {
                Name = name,
                Origin = new BeanOrigin(Country: Get(ImportField.Country), Region: Get(ImportField.Region)),
                Process = Get(ImportField.Process),
                Varieties = SplitList(Get(ImportField.Varieties)),
                RoastLevel = ParseRoast(Get(ImportField.RoastLevel)),
                Notes = Get(ImportField.Notes),
                Rating = ParseRating(Get(ImportField.Rating), line, warnings),
            };

            planned.Add(new PlannedBean(line, bean, roaster, PlanBag(Get(ImportField.WeightG), Get(ImportField.Price), Get(ImportField.PurchasedOn), Get(ImportField.RoastDate), line, warnings)));
        }

        return new ImportPlan(planned, already, warnings);
    }

    private static string Key(string name, string? roaster) => $"{name.Trim().ToLowerInvariant()}|{(roaster ?? "").Trim().ToLowerInvariant()}";

    private static Bag? PlanBag(string? weight, string? price, string? purchased, string? roasted, int line, List<string> warnings)
    {
        var grams = ParseWeightG(weight);
        if (grams is null)
        {
            if (weight is not null)
            {
                warnings.Add($"Line {line}: weight \"{weight}\" not understood, no bag added.");
            }

            return null;
        }

        var (cents, currency) = ParsePrice(price);
        if (price is not null && cents is null)
        {
            warnings.Add($"Line {line}: price \"{price}\" not understood.");
        }

        return new Bag
        {
            BeanId = "",
            WeightG = grams.Value,
            PriceCents = cents,
            Currency = cents is null ? null : currency ?? "EUR",
            PurchasedOn = ParseDate(purchased),
            RoastDate = ParseDate(roasted),
            Status = BagStatus.Sealed,
        };
    }

    public static IReadOnlyList<string> SplitList(string? text) =>
        string.IsNullOrWhiteSpace(text) ? [] : [.. text.Split([',', ';', '/', '|'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];

    public static RoastLevel? ParseRoast(string? text)
    {
        var t = text?.Trim().ToLowerInvariant().Replace('-', ' ');
        return t switch
        {
            null or "" => null,
            "light" or "licht" or "blond" => RoastLevel.Light,
            "medium light" or "medium licht" or "light medium" => RoastLevel.MediumLight,
            "medium" or "middel" => RoastLevel.Medium,
            "medium dark" or "medium donker" => RoastLevel.MediumDark,
            "dark" or "donker" => RoastLevel.Dark,
            _ => null,
        };
    }

    private static int? ParseRating(string? text, int line, List<string> warnings)
    {
        if (text is null)
        {
            return null;
        }

        if (TryNumber(text.Replace("★", "").Replace("/5", "").Trim(), out var value) && value is >= 1 and <= 5)
        {
            return (int)Math.Round(value, MidpointRounding.AwayFromZero);
        }

        warnings.Add($"Line {line}: rating \"{text}\" is not between 1 and 5, ignored.");
        return null;
    }

    /// <summary>Grams from "250", "250 g", "0.25 kg", "8.8 oz" or "12 oz" (comma or dot as decimal mark).</summary>
    public static decimal? ParseWeightG(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var t = text.Trim().ToLowerInvariant();
        var factor = 1m;
        foreach (var (suffix, f) in new[] { ("kg", 1000m), ("lbs", 453.592m), ("lb", 453.592m), ("oz", 28.3495m), ("gr", 1m), ("g", 1m) })
        {
            if (t.EndsWith(suffix, StringComparison.Ordinal))
            {
                t = t[..^suffix.Length].Trim();
                factor = f;
                break;
            }
        }

        return TryNumber(t, out var value) && value > 0 ? Math.Round(value * factor, 0) : null;
    }

    /// <summary>Cents and currency from "12,50", "€ 12.50", "$9" or "12.50 EUR".</summary>
    public static (int? Cents, string? Currency) ParsePrice(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return (null, null);
        }

        var t = text.Trim();
        string? currency = null;
        foreach (var (symbol, code) in new[] { ("€", "EUR"), ("$", "USD"), ("£", "GBP"), ("EUR", "EUR"), ("USD", "USD"), ("GBP", "GBP") })
        {
            if (t.Contains(symbol, StringComparison.OrdinalIgnoreCase))
            {
                currency ??= code;
                t = t.Replace(symbol, "", StringComparison.OrdinalIgnoreCase);
            }
        }

        return TryNumber(t.Trim(), out var value) && value > 0 ? ((int)Math.Round(value * 100m, MidpointRounding.AwayFromZero), currency) : (null, currency);
    }

    public static DateOnly? ParseDate(string? text) =>
        !string.IsNullOrWhiteSpace(text) && DateOnly.TryParseExact(text.Trim(), DateFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ? date : null;

    private static bool TryNumber(string text, out decimal value)
    {
        var t = text.Trim();
        if (t.Contains(',') && !t.Contains('.'))
        {
            t = t.Replace(',', '.');
        }
        else
        {
            t = t.Replace(",", "");
        }

        return decimal.TryParse(t, NumberStyles.Number, CultureInfo.InvariantCulture, out value);
    }
}
