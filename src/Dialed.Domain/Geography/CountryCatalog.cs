namespace Dialed.Domain.Geography;

/// <summary>A country the origin map can place: its English name, ISO 3166 code, a point near its middle (or its coffee growing area) and other names people type for it.</summary>
public sealed record CountryPlace(string Name, string Iso2, double Latitude, double Longitude, IReadOnlyList<string> Aliases)
{
    /// <summary>The flag as an emoji (two regional indicator letters). Some systems (Windows) show the letters instead, so show the name next to it.</summary>
    public string Flag => string.Concat(Iso2.ToUpperInvariant().Select(c => char.ConvertFromUtf32(0x1F1E6 + (c - 'A'))));
}

/// <summary>
/// Finds the country behind the free text in a bean's origin ("Ethiopia", "ethiopië", "Costa Rica ", "Hawaii"). Matching ignores case, accents and
/// extra spaces. Text that is not a known country gives nothing: a bean without a recognised country is simply not shown on a map.
/// </summary>
public static class CountryCatalog
{
    public static IReadOnlyList<CountryPlace> All { get; } =
    [
        new("Ethiopia", "ET", 9.0, 39.5, ["Ethiopië"]),
        new("Kenya", "KE", 0.2, 37.9, []),
        new("Rwanda", "RW", -1.95, 29.9, []),
        new("Burundi", "BI", -3.4, 29.9, []),
        new("Tanzania", "TZ", -6.4, 34.9, ["Tanzania", "United Republic of Tanzania"]),
        new("Uganda", "UG", 1.4, 32.3, ["Oeganda"]),
        new("Democratic Republic of the Congo", "CD", -2.9, 23.7, ["Democratische Republiek Congo", "DR Congo", "DRC", "Congo-Kinshasa", "Congo"]),
        new("Malawi", "MW", -13.3, 34.3, []),
        new("Zambia", "ZM", -13.1, 27.8, ["Zambia"]),
        new("Zimbabwe", "ZW", -19.0, 29.2, []),
        new("Yemen", "YE", 15.5, 47.6, ["Jemen"]),
        new("Cameroon", "CM", 5.7, 12.7, ["Kameroen"]),
        new("Ivory Coast", "CI", 7.5, -5.5, ["Ivoorkust", "Côte d'Ivoire", "Cote d'Ivoire"]),
        new("Angola", "AO", -12.3, 17.5, []),
        new("Madagascar", "MG", -19.4, 46.7, []),
        new("Colombia", "CO", 4.6, -74.1, ["Colombië"]),
        new("Brazil", "BR", -10.8, -52.9, ["Brazilië", "Brasil"]),
        new("Peru", "PE", -9.2, -75.0, []),
        new("Ecuador", "EC", -1.4, -78.4, []),
        new("Bolivia", "BO", -16.7, -64.7, []),
        new("Venezuela", "VE", 7.1, -66.2, []),
        new("Costa Rica", "CR", 9.9, -84.2, []),
        new("Panama", "PA", 8.5, -80.1, []),
        new("Nicaragua", "NI", 12.9, -85.0, []),
        new("Honduras", "HN", 14.8, -86.6, []),
        new("El Salvador", "SV", 13.7, -88.9, []),
        new("Guatemala", "GT", 15.7, -90.4, []),
        new("Mexico", "MX", 23.6, -102.5, ["Mexico", "México"]),
        new("Jamaica", "JM", 18.2, -77.3, ["Jamaïca"]),
        new("Haiti", "HT", 19.0, -72.7, ["Haïti"]),
        new("Dominican Republic", "DO", 18.9, -70.5, ["Dominicaanse Republiek"]),
        new("Cuba", "CU", 21.6, -79.0, []),
        new("Puerto Rico", "PR", 18.2, -66.5, []),
        new("United States", "US", 39.8, -98.6, ["Verenigde Staten", "USA", "US", "Hawaii"]),
        new("Indonesia", "ID", -2.5, 118.0, ["Indonesië", "Sumatra", "Java", "Sulawesi"]),
        new("India", "IN", 21.0, 78.9, ["India"]),
        new("Vietnam", "VN", 14.1, 108.3, ["Viet Nam"]),
        new("Thailand", "TH", 15.1, 101.0, []),
        new("Laos", "LA", 18.2, 103.9, []),
        new("Myanmar", "MM", 21.0, 96.5, ["Birma", "Burma"]),
        new("China", "CN", 35.0, 103.8, []),
        new("Philippines", "PH", 12.9, 122.8, ["Filipijnen"]),
        new("Papua New Guinea", "PG", -6.3, 143.9, ["Papoea-Nieuw-Guinea", "PNG"]),
        new("Timor-Leste", "TL", -8.8, 125.9, ["Oost-Timor", "East Timor"]),
        new("Nepal", "NP", 28.4, 84.1, []),
        new("Sri Lanka", "LK", 7.9, 80.7, []),
        new("Taiwan", "TW", 23.7, 121.0, []),
        new("Australia", "AU", -25.7, 134.5, ["Australië"]),
        new("New Zealand", "NZ", -41.5, 172.5, ["Nieuw-Zeeland"]),
        new("Netherlands", "NL", 52.2, 5.3, ["Nederland"]),
        new("Germany", "DE", 51.2, 10.4, ["Duitsland"]),
        new("Italy", "IT", 42.8, 12.6, ["Italië"]),
        new("France", "FR", 46.6, 2.5, ["Frankrijk"]),
        new("Spain", "ES", 40.2, -3.6, ["Spanje"]),
        new("United Kingdom", "GB", 54.0, -2.5, ["Verenigd Koninkrijk", "UK", "England"]),
        new("Japan", "JP", 36.2, 138.3, []),
        new("South Korea", "KR", 36.4, 127.8, ["Zuid-Korea"]),
        new("Saudi Arabia", "SA", 24.0, 45.1, ["Saoedi-Arabië"]),
        new("Ghana", "GH", 7.9, -1.0, []),
        new("Nigeria", "NG", 9.1, 8.7, []),
        new("Sierra Leone", "SL", 8.5, -11.8, []),
        new("Togo", "TG", 8.6, 0.8, []),
        new("Liberia", "LR", 6.4, -9.4, []),
        new("Guinea", "GN", 10.9, -10.9, ["Guinee"]),
        new("Central African Republic", "CF", 6.6, 20.9, ["Centraal-Afrikaanse Republiek"]),
        new("Gabon", "GA", -0.8, 11.6, []),
        new("Fiji", "FJ", -17.7, 178.1, []),
        new("Vanuatu", "VU", -16.0, 167.0, []),
        new("Malaysia", "MY", 4.2, 102.0, ["Maleisië"]),
        new("Cambodia", "KH", 12.6, 104.9, ["Cambodja"]),
        new("Paraguay", "PY", -23.4, -58.4, []),
        new("Belize", "BZ", 17.2, -88.5, []),
        new("Trinidad and Tobago", "TT", 10.7, -61.2, []),
        new("Congo (Republic)", "CG", -0.7, 15.2, ["Republic of the Congo", "Congo-Brazzaville"]),
    ];

    /// <summary>Countries on the map where coffee is not grown for sale, only drunk: the rest is the passport's list of coffee countries.</summary>
    private static readonly HashSet<string> NotGrowing = ["NL", "DE", "IT", "FR", "ES", "GB", "JP", "KR", "SA", "NZ"];

    /// <summary>The coffee growing countries the origin passport lists, sorted by name.</summary>
    public static IReadOnlyList<CountryPlace> Producers { get; } = [.. All.Where(p => !NotGrowing.Contains(p.Iso2)).OrderBy(p => p.Name, StringComparer.Ordinal)];

    private static readonly Dictionary<string, CountryPlace> ByName = Build();

    public static CountryPlace? Find(string? text) =>
        !string.IsNullOrWhiteSpace(text) && ByName.TryGetValue(Normalize(text), out var place) ? place : null;

    private static Dictionary<string, CountryPlace> Build()
    {
        var map = new Dictionary<string, CountryPlace>();
        foreach (var place in All)
        {
            foreach (var name in place.Aliases.Prepend(place.Name).Append(place.Iso2))
            {
                map.TryAdd(Normalize(name), place);
            }
        }

        return map;
    }

    internal static string Normalize(string text)
    {
        var decomposed = text.Trim().Normalize(System.Text.NormalizationForm.FormD);
        var letters = decomposed.Where(c => System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c) != System.Globalization.UnicodeCategory.NonSpacingMark);
        return string.Join(' ', new string([.. letters]).ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }
}
