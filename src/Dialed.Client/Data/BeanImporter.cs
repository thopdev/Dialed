using Dialed.Domain.Import;
using Dialed.Domain.Model;

namespace Dialed.Client.Data;

public sealed record ImportResult(int Beans, int Bags, int Roasters);

/// <summary>Saves a planned CSV import: roasters are found by name (or created), then each bean and its bag.</summary>
public static class BeanImporter
{
    public static async Task<ImportResult> ApplyAsync(
        ImportPlan plan,
        IRepository<Bean> beans,
        IRepository<Bag> bags,
        IRepository<Vendor> vendors,
        CancellationToken cancellationToken = default)
    {
        var roasters = (await vendors.ListAsync(cancellationToken)).ToDictionary(v => v.Name.Trim().ToLowerInvariant(), v => v);
        var createdRoasters = 0;
        var savedBags = 0;

        foreach (var planned in plan.Beans)
        {
            string? roasterId = null;
            if (!string.IsNullOrWhiteSpace(planned.RoasterName))
            {
                var key = planned.RoasterName.Trim().ToLowerInvariant();
                if (!roasters.TryGetValue(key, out var roaster))
                {
                    roaster = await vendors.SaveAsync(new Vendor { Name = planned.RoasterName.Trim(), Kinds = VendorKinds.Roaster }, cancellationToken);
                    roasters[key] = roaster;
                    createdRoasters++;
                }

                roasterId = roaster.Id;
            }

            var bean = await beans.SaveAsync(planned.Bean with { RoasterId = roasterId }, cancellationToken);
            if (planned.Bag is { } bag)
            {
                await bags.SaveAsync(bag with { BeanId = bean.Id }, cancellationToken);
                savedBags++;
            }
        }

        return new ImportResult(plan.Beans.Count, savedBags, createdRoasters);
    }
}
