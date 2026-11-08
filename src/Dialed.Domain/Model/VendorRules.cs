namespace Dialed.Domain.Model;

/// <summary>How many beans and bags point at a vendor.</summary>
public sealed record VendorUsage(int Beans, int Bags)
{
    public bool IsUsed => Beans > 0 || Bags > 0;
}

public static class VendorRules
{
    /// <summary>
    /// Cleans up a typed website: adds https:// when missing, only accepts http(s) addresses with a dot in the host.
    /// Empty input means "no website" (<c>null</c>, valid); anything unusable returns <c>false</c>.
    /// </summary>
    public static bool TryNormalizeWebsite(string? input, out string? website)
    {
        website = null;
        var text = input?.Trim();
        if (string.IsNullOrEmpty(text))
        {
            return true;
        }

        if (!text.Contains("://", StringComparison.Ordinal))
        {
            text = "https://" + text;
        }

        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp)
            || !uri.Host.Contains('.', StringComparison.Ordinal)
            || uri.Host.Contains(' ', StringComparison.Ordinal))
        {
            return false;
        }

        website = uri.GetLeftPart(UriPartial.Path).TrimEnd('/') + uri.Query + uri.Fragment;
        return true;
    }

    /// <summary>Beans roasted and bags bought at the vendor (deleted records are not counted).</summary>
    public static VendorUsage UsageOf(string vendorId, IEnumerable<Bean> beans, IEnumerable<Bag> bags)
    {
        ArgumentNullException.ThrowIfNull(beans);
        ArgumentNullException.ThrowIfNull(bags);

        return new VendorUsage(
            beans.Count(bean => !bean.Deleted && bean.RoasterId == vendorId),
            bags.Count(bag => !bag.Deleted && bag.SellerId == vendorId));
    }

    public static bool MatchesKind(Vendor vendor, VendorKinds filter) =>
        filter == VendorKinds.None || (vendor.Kinds & filter) != 0;
}
