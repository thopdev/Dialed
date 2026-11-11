using System.Globalization;
using Dialed.UI.Resources;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Dialed.UI.Tests;

/// <summary>Counts read as "1 bean" and "2 beans", in both languages.</summary>
public sealed class PluralTests
{
    private static IStringLocalizer<Strings> Localizer() =>
        new StringLocalizer<Strings>(new ResourceManagerStringLocalizerFactory(Options.Create(new LocalizationOptions()), NullLoggerFactory.Instance));

    private static string In(string culture, string noun, int count)
    {
        var (before, beforeUi) = (CultureInfo.CurrentCulture, CultureInfo.CurrentUICulture);
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.CurrentUICulture = new CultureInfo(culture);
            return Localizer().Count(noun, count);
        }
        finally
        {
            (CultureInfo.CurrentCulture, CultureInfo.CurrentUICulture) = (before, beforeUi);
        }
    }

    [Theory]
    [InlineData("en", "Beans", 1, "1 bean")]
    [InlineData("en", "Beans", 0, "0 beans")]
    [InlineData("en", "Beans", 2, "2 beans")]
    [InlineData("en", "Recipes", 1, "1 recipe")]
    [InlineData("en", "Followers", 1, "1 follower")]
    [InlineData("en", "People", 1, "1 person")]
    [InlineData("en", "People", 7, "7 people")]
    [InlineData("nl", "Beans", 1, "1 boon")]
    [InlineData("nl", "Beans", 3, "3 bonen")]
    [InlineData("nl", "Recipes", 1, "1 recept")]
    [InlineData("nl", "Recipes", 4, "4 recepten")]
    [InlineData("nl", "Followers", 1, "1 volger")]
    [InlineData("nl", "People", 1, "1 persoon")]
    [InlineData("nl", "People", 2, "2 mensen")]
    public void A_count_uses_the_singular_for_one_and_the_plural_otherwise(string culture, string noun, int count, string expected) =>
        In(culture, noun, count).ShouldBe(expected);
}
