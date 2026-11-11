using Dialed.Client.Data;
using Dialed.Domain.Barcodes;
using Dialed.Domain.Model;
using Dialed.Domain.Text;

namespace Dialed.Client.Catalog;

/// <summary>
/// Makes a bean of my own from a catalog entry (#105): a copy, never a link. The roaster is the one I already have (the label matcher
/// decides) or a new vendor made from its name; the barcode that led here, if any, goes on the bean.
/// </summary>
public static class CatalogCopier
{
    public static async Task<Bean> SaveBeanAsync(CatalogBean entry, string? barcode, IRepository<Bean> beans, IRepository<Vendor> vendors, Visibility visibility, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);

        string? roasterId = null;
        if (!string.IsNullOrWhiteSpace(entry.RoasterName))
        {
            var known = LabelMatcher.Find(entry.RoasterName, null, null, await vendors.ListAsync(cancellationToken), []).BestRoaster?.Item;
            roasterId = (known ?? await vendors.SaveAsync(new Vendor { Name = entry.RoasterName.Trim(), Kinds = VendorKinds.Roaster }, cancellationToken)).Id;
        }

        return await beans.SaveAsync(new Bean
        {
            Name = entry.Name.Trim(),
            RoasterId = roasterId,
            Origin = new BeanOrigin(Country: entry.Country, Region: entry.Region),
            Process = entry.Process,
            Varieties = entry.Varieties,
            RoastLevel = entry.RoastLevel,
            TastingNotes = entry.TastingNotes,
            Barcodes = BarcodeMatcher.NormalizeAll(barcode is null ? [] : [barcode]),
            Visibility = visibility,
        }, cancellationToken);
    }
}
