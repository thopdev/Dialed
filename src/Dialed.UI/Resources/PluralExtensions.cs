using Microsoft.Extensions.Localization;

namespace Dialed.UI.Resources;

public static class PluralExtensions
{
    /// <summary>
    /// "1 bean" or "2 beans": the entry <c>Count_{noun}_One</c> when the number is one, <c>Count_{noun}_Other</c> otherwise, so every language
    /// gets its own singular and plural instead of a number glued to a plural noun.
    /// </summary>
    public static string Count(this IStringLocalizer<Strings> localizer, string noun, int count)
    {
        ArgumentNullException.ThrowIfNull(localizer);
        return string.Format(localizer[$"Count_{noun}_{(count == 1 ? "One" : "Other")}"].Value, count);
    }
}
