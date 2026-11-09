using System.Globalization;
using System.Resources;
using System.Text.RegularExpressions;
using Dialed.UI.Resources;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Dialed.UI.Tests;

public sealed partial class LocalizationTests
{
    private static readonly ResourceManager Manager = new("Dialed.UI.Resources.Strings", typeof(Strings).Assembly);

    private static Dictionary<string, string> Load(CultureInfo culture)
    {
        var set = Manager.GetResourceSet(culture, createIfNotExists: true, tryParents: false);
        set.ShouldNotBeNull($"no resources for {culture.Name}");
        var result = new Dictionary<string, string>();
        foreach (System.Collections.DictionaryEntry entry in set)
        {
            result[(string)entry.Key] = (string)entry.Value!;
        }

        return result;
    }

    [Fact]
    public void Every_English_text_has_a_Dutch_translation()
    {
        var english = Load(CultureInfo.InvariantCulture);
        var dutch = Load(new CultureInfo("nl"));

        dutch.Keys.Except(english.Keys).ShouldBeEmpty("Dutch has keys that English lacks");
        english.Keys.Except(dutch.Keys).ShouldBeEmpty("keys without a Dutch translation");
        dutch.Where(p => string.IsNullOrWhiteSpace(p.Value)).ShouldBeEmpty();
    }

    [Fact]
    public void Translations_keep_the_same_placeholders()
    {
        var english = Load(CultureInfo.InvariantCulture);
        var dutch = Load(new CultureInfo("nl"));

        foreach (var (key, text) in english)
        {
            Placeholders(dutch[key]).ShouldBe(Placeholders(text), $"placeholders differ in {key}");
        }
    }

    [Fact]
    public void The_localizer_answers_in_Dutch_for_a_Dutch_culture_and_English_otherwise()
    {
        var localizer = new ResourceManagerStringLocalizerFactory(Options.Create(new LocalizationOptions()), NullLoggerFactory.Instance)
            .Create(typeof(Strings));
        var before = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = new CultureInfo("nl-NL");
            localizer["Save"].Value.ShouldBe("Opslaan");
            CultureInfo.CurrentUICulture = new CultureInfo("de-DE");
            localizer["Save"].Value.ShouldBe("Save"); // no German: falls back to English
        }
        finally
        {
            CultureInfo.CurrentUICulture = before;
        }
    }

    private static string[] Placeholders(string text) =>
        [.. PlaceholderPattern().Matches(text).Select(m => m.Value).Order()];

    [GeneratedRegex(@"\{\d+\}")]
    private static partial Regex PlaceholderPattern();
}
