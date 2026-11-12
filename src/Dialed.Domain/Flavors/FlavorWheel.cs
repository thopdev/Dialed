using System.Globalization;
using System.Text;

namespace Dialed.Domain.Flavors;

/// <summary>The main branches of the coffee flavour wheel (SCA), without the ones that are not tastes you would write on a bag.</summary>
public enum FlavorCategory
{
    Fruity,
    Floral,
    Sweet,
    NuttyCocoa,
    Spices,
    Roasted,
    GreenVegetative,
    SourFermented,
}

/// <summary>How many tasting notes fall in a branch of the wheel.</summary>
public sealed record FlavorShare(FlavorCategory Category, int Count);

/// <summary>
/// Sorts free-text tasting notes ("blueberry", "dark chocolate", "bosbes") into the branches of the flavour wheel, in English and Dutch.
/// Matching ignores case and accents and looks at the whole note first, then at its words (plurals are understood), so "dark chocolate" and
/// "red berries" are found. A note with no known word is left out: the wheel only shows what it can place.
/// </summary>
public static class FlavorWheel
{
    private static readonly Dictionary<string, FlavorCategory> Words = Build();

    public static FlavorCategory? Classify(string? note)
    {
        var text = Normalize(note);
        if (text.Length == 0)
        {
            return null;
        }

        if (Lookup(text) is { } whole)
        {
            return whole;
        }

        foreach (var word in text.Split([' ', '-', '/', ','], StringSplitOptions.RemoveEmptyEntries))
        {
            if (Lookup(word) is { } found)
            {
                return found;
            }
        }

        return null;
    }

    /// <summary>The count per branch for the given notes, biggest first (then in wheel order); branches without a note are left out.</summary>
    public static IReadOnlyList<FlavorShare> Tally(IEnumerable<string> notes)
    {
        ArgumentNullException.ThrowIfNull(notes);
        return
        [
            .. notes.Select(Classify).OfType<FlavorCategory>()
                .GroupBy(c => c)
                .Select(g => new FlavorShare(g.Key, g.Count()))
                .OrderByDescending(s => s.Count).ThenBy(s => s.Category),
        ];
    }

    /// <summary>
    /// Known flavour words that start with what is typed (ignoring case and accents), shortest first, without the ones already chosen.
    /// Shorter words come first.
    /// </summary>
    public static IReadOnlyList<string> Suggest(string? typed, IEnumerable<string>? already = null, int max = 6)
    {
        var prefix = Normalize(typed);
        if (prefix.Length == 0 || max <= 0)
        {
            return [];
        }

        var taken = (already ?? []).Select(Normalize).ToHashSet();
        return
        [
            .. Words.Keys
                .Where(w => w.StartsWith(prefix, StringComparison.Ordinal) && w != prefix && !taken.Contains(w))
                .OrderBy(w => w.Length).ThenBy(w => w, StringComparer.Ordinal)
                .Take(max),
        ];
    }

    private static FlavorCategory? Lookup(string word)
    {
        if (Words.TryGetValue(word, out var category))
        {
            return category;
        }

        // Plurals: berries, peaches, nuts.
        string[] singulars = word.EndsWith("ies", StringComparison.Ordinal) ? [word[..^3] + "y"]
            : word.EndsWith("es", StringComparison.Ordinal) ? [word[..^2], word[..^1]]
            : word.EndsWith('s') ? [word[..^1]]
            : [];
        return singulars.Select(w => Words.TryGetValue(w, out var found) ? found : (FlavorCategory?)null).FirstOrDefault(c => c is not null);
    }

    private static string Normalize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return "";
        }

        var decomposed = text.Trim().Normalize(NormalizationForm.FormD);
        return new string([.. decomposed.Where(c => CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)]).ToLowerInvariant();
    }

    private static Dictionary<string, FlavorCategory> Build()
    {
        var map = new Dictionary<string, FlavorCategory>();
        void Add(FlavorCategory category, string words)
        {
            foreach (var word in words.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            {
                map.TryAdd(Normalize(word), category);
            }
        }

        Add(FlavorCategory.Fruity, "fruit, fruity, berry, blueberry, strawberry, raspberry, blackberry, blackcurrant, currant, redcurrant, cherry, apple, pear, peach, apricot, plum, grape, grapefruit, orange, lemon, lime, citrus, tangerine, mandarin, mango, pineapple, papaya, passion fruit, passionfruit, tropical, banana, melon, watermelon, fig, date, raisin, prune, pomegranate, cranberry, lychee, kiwi, stone fruit, jam, jammy, coconut, cassis, "
            + "bes, bosbes, aardbei, framboos, braam, kers, appel, peer, perzik, abrikoos, pruim, druif, pompelmoes, sinaasappel, citroen, limoen, mandarijn, ananas, banaan, meloen, vijg, rozijn, granaatappel, cranberry, kokos, fruitig");
        Add(FlavorCategory.Floral, "floral, flower, flowers, jasmine, rose, lavender, hibiscus, elderflower, chamomile, bergamot, tea, earl grey, honeysuckle, orange blossom, violet, "
            + "bloem, bloemen, bloemig, jasmijn, roos, lavendel, kamille, thee, vlier");
        Add(FlavorCategory.Sweet, "sweet, honey, caramel, toffee, maple, syrup, molasses, brown sugar, sugar, vanilla, butterscotch, marshmallow, candy, fudge, treacle, nougat, panela, "
            + "zoet, honing, karamel, stroop, ahornsiroop, melasse, suiker, bruine suiker, vanille, snoep");
        Add(FlavorCategory.NuttyCocoa, "nutty, nut, hazelnut, almond, peanut, walnut, pecan, pistachio, cashew, chocolate, cocoa, cacao, praline, marzipan, "
            + "noot, noten, hazelnoot, amandel, pinda, walnoot, pistache, cashewnoot, chocolade, cacao, marsepein");
        Add(FlavorCategory.Spices, "spice, spices, spicy, cinnamon, clove, nutmeg, cardamom, pepper, anise, ginger, cumin, liquorice, licorice, allspice, "
            + "kruiden, kruidig, kaneel, kruidnagel, nootmuskaat, kardemom, peper, anijs, gember, komijn, drop");
        Add(FlavorCategory.Roasted, "roasted, roast, toast, toasty, smoky, smoke, burnt, ash, tobacco, malt, malty, grain, cereal, bread, biscuit, cracker, charcoal, woody, wood, cedar, "
            + "gebrand, geroosterd, rook, rokerig, verbrand, as, tabak, mout, graan, brood, koekje, hout, houtig, ceder");
        Add(FlavorCategory.GreenVegetative, "green, herbal, herb, herbs, grass, grassy, vegetal, vegetable, hay, straw, olive, pea, cucumber, leafy, earthy, earth, "
            + "groen, kruidenachtig, gras, groente, hooi, stro, olijf, erwt, komkommer, aards");
        Add(FlavorCategory.SourFermented, "sour, tart, acidic, winey, wine, fermented, boozy, whiskey, whisky, rum, vinegar, lactic, yoghurt, yogurt, kombucha, funky, "
            + "zuur, wijn, wijnig, gefermenteerd, whisky, azijn, yoghurt");
        return map;
    }
}
