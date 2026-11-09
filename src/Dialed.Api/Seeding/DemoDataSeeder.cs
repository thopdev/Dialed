using System.Text.Json;
using System.Text.Json.Nodes;
using Dialed.Api.Auth;
using Dialed.Api.Sync;
using Dialed.Domain.Inventory;
using Dialed.Domain.Model;

namespace Dialed.Api.Seeding;

/// <summary>
/// Demo coffee for the household Alice and Bob share (#228): roasters, beans from light to dark, bags in every state, gear,
/// recipes (one with an older version), and two weeks of brews, one of them Bob's. Dates are relative to today so freshness
/// and "Today" look right whenever it runs. Ids are fixed: a second run is refused as a version conflict and changes nothing.
/// </summary>
public sealed class DemoDataSeeder(ISyncStore store, TimeProvider time, ILogger<DemoDataSeeder> logger) : IDevelopmentSeeder
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private static readonly AppUser Alice = DevUsers.Find("alice")!;

    private static readonly AppUser Bob = DevUsers.Find("bob")!;

    public int Order => 100;

    public async Task SeedAsync(CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        var today = DateOnly.FromDateTime(now.UtcDateTime);
        var stored = 0;

        async Task Put<T>(string table, T entity, AppUser? by = null)
            where T : SyncEntity
        {
            var json = JsonSerializer.SerializeToNode(entity, Json)!.AsObject();
            var result = await store.PushAsync(Alice.HouseholdId, (by ?? Alice).Id, table, entity.Id, json, ifMatch: null, cancellationToken);
            if (result.Outcome == StoreOutcome.Stored)
            {
                stored++;
            }
        }

        // Vendors
        var friedhats = new Vendor { Id = "demo-vendor-friedhats", Name = "Friedhats", Kinds = VendorKinds.Roaster, Country = "NL", City = "Amsterdam", Website = "https://friedhats.com" };
        var manhattan = new Vendor { Id = "demo-vendor-manhattan", Name = "Manhattan Coffee Roasters", Kinds = VendorKinds.Roaster, Country = "NL", City = "Rotterdam" };
        var lot61 = new Vendor { Id = "demo-vendor-lot61", Name = "Lot61", Kinds = VendorKinds.Roaster | VendorKinds.Seller, Country = "NL", City = "Amsterdam" };
        var shop = new Vendor { Id = "demo-vendor-shop", Name = "Bocca Shop", Kinds = VendorKinds.Seller, Country = "NL", City = "Utrecht" };
        foreach (var vendor in new[] { friedhats, manhattan, lot61, shop })
        {
            await Put("vendors", vendor);
        }

        // Beans, light to dark
        var kenya = new Bean { Id = "demo-bean-kenya", Name = "Kenya Kiambu AA", RoasterId = friedhats.Id, Origin = new BeanOrigin("Kenya", "Kiambu", "Gatomboya"), Varieties = ["SL28", "SL34"], Process = "Washed", AltitudeM = 1800, RoastLevel = RoastLevel.Light, RoastStyle = RoastStyle.Filter, TastingNotes = ["blackcurrant", "grapefruit", "brown sugar"], Rating = 5 };
        var ethiopia = new Bean { Id = "demo-bean-ethiopia", Name = "Ethiopia Guji Hambela", RoasterId = manhattan.Id, Origin = new BeanOrigin("Ethiopia", "Guji"), Varieties = ["Heirloom"], Process = "Natural", AltitudeM = 2100, RoastLevel = RoastLevel.Light, RoastStyle = RoastStyle.Filter, TastingNotes = ["blueberry", "jasmine", "peach"], Rating = 4 };
        var brazil = new Bean { Id = "demo-bean-brazil", Name = "Brazil Cerrado Mineiro", RoasterId = lot61.Id, Origin = new BeanOrigin("Brazil", "Cerrado Mineiro"), Varieties = ["Yellow Bourbon"], Process = "Pulped natural", RoastLevel = RoastLevel.MediumDark, RoastStyle = RoastStyle.Espresso, TastingNotes = ["dark chocolate", "hazelnut"], Rating = 4 };
        var decaf = new Bean { Id = "demo-bean-decaf", Name = "Colombia Huila Decaf", RoasterId = friedhats.Id, Origin = new BeanOrigin("Colombia", "Huila"), Varieties = ["Caturra"], Process = "Sugarcane EA", RoastLevel = RoastLevel.Medium, RoastStyle = RoastStyle.Omni, IsDecaf = true, TastingNotes = ["caramel", "red apple"], Rating = 3 };
        var guatemala = new Bean { Id = "demo-bean-guatemala", Name = "Guatemala Huehuetenango", RoasterId = manhattan.Id, Origin = new BeanOrigin("Guatemala", "Huehuetenango"), Varieties = ["Bourbon", "Caturra"], Process = "Washed", RoastLevel = RoastLevel.MediumLight, RoastStyle = RoastStyle.Omni, TastingNotes = ["milk chocolate", "orange"] };
        foreach (var bean in new[] { kenya, ethiopia, brazil, decaf, guatemala })
        {
            await Put("beans", bean);
        }

        // Bags in every state
        var kenyaBag = new Bag { Id = "demo-bag-kenya", BeanId = kenya.Id, SellerId = friedhats.Id, PriceCents = 1850, Currency = "EUR", WeightG = 250m, RoastDate = today.AddDays(-12), PurchasedOn = today.AddDays(-10), OpenedOn = today.AddDays(-8), Status = BagStatus.Open };
        var kenyaOld = new Bag { Id = "demo-bag-kenya-old", BeanId = kenya.Id, SellerId = friedhats.Id, PriceCents = 1850, Currency = "EUR", WeightG = 250m, RoastDate = today.AddDays(-40), PurchasedOn = today.AddDays(-38), OpenedOn = today.AddDays(-32), FinishedOn = today.AddDays(-10), Status = BagStatus.Finished };
        var ethiopiaBag = new Bag { Id = "demo-bag-ethiopia", BeanId = ethiopia.Id, SellerId = shop.Id, PriceCents = 2100, Currency = "EUR", WeightG = 250m, RoastDate = today.AddDays(-5), PurchasedOn = today.AddDays(-3), Status = BagStatus.Sealed };
        var brazilBag = new Bag { Id = "demo-bag-brazil", BeanId = brazil.Id, SellerId = lot61.Id, PriceCents = 2900, Currency = "EUR", WeightG = 500m, RoastDate = today.AddDays(-26), PurchasedOn = today.AddDays(-24), OpenedOn = today.AddDays(-19), Status = BagStatus.Open };
        var decafBag = new Bag { Id = "demo-bag-decaf", BeanId = decaf.Id, SellerId = friedhats.Id, PriceCents = 1650, Currency = "EUR", WeightG = 250m, RoastDate = today.AddDays(-55), PurchasedOn = today.AddDays(-52), FrozenOn = today.AddDays(-50), Storage = BagStorage.Freezer, Status = BagStatus.Frozen };
        foreach (var bag in new[] { kenyaBag, kenyaOld, ethiopiaBag, brazilBag, decafBag })
        {
            await Put("bags", bag);
        }

        // Gear
        var comandante = new EquipmentItem { Id = "demo-gear-comandante", Kind = EquipmentKind.Grinder, Brand = "Comandante", Model = "C40 MK4" };
        var eureka = new EquipmentItem { Id = "demo-gear-eureka", Kind = EquipmentKind.Grinder, Brand = "Eureka", Model = "Mignon Specialita" };
        var v60 = new EquipmentItem { Id = "demo-gear-v60", Kind = EquipmentKind.Brewer, Brand = "Hario", Model = "V60 02" };
        var lelit = new EquipmentItem { Id = "demo-gear-lelit", Kind = EquipmentKind.EspressoMachine, Brand = "Lelit", Model = "Bianca V3" };
        var kettle = new EquipmentItem { Id = "demo-gear-kettle", Kind = EquipmentKind.Kettle, Brand = "Fellow", Model = "Stagg EKG" };
        foreach (var item in new[] { comandante, eureka, v60, lelit, kettle })
        {
            await Put("equipment", item);
        }

        // Recipes: the V60 one has an older version
        RecipeStep[] pourOver = [new(RecipeStepKind.Bloom, 0, 45, 45m), new(RecipeStepKind.Pour, 45, null, 150m), new(RecipeStepKind.Pour, 90, null, 250m)];
        var v60Old = new Recipe { Id = "demo-recipe-v60-v1", MethodCode = "v60", Name = "Kenya V60", BeanId = kenya.Id, GrinderId = comandante.Id, BrewerId = v60.Id, DoseG = 15m, WaterG = 250m, GrindSetting = 24m, TempC = 96m, TimeS = 180, Steps = pourOver, Filter = "Hario tabbed", IsSuperseded = true };
        var v60New = v60Old with { Id = "demo-recipe-v60-v2", ParentRecipeId = v60Old.Id, IsSuperseded = false, GrindSetting = 22m, TempC = 94m, TimeS = 195, Notes = "Finer and a bit cooler: less astringent, more sweetness." };
        var espresso = new Recipe { Id = "demo-recipe-espresso", MethodCode = "espresso", Name = "Daily espresso", BeanId = brazil.Id, GrinderId = eureka.Id, BrewerId = lelit.Id, DoseG = 18m, YieldG = 36m, GrindSetting = 2.5m, TempC = 93m, TimeS = 28, PreinfusionS = 5 };
        var aeropress = new Recipe { Id = "demo-recipe-aeropress", MethodCode = "aeropress", Name = "Inverted AeroPress", GrinderId = comandante.Id, DoseG = 14m, WaterG = 200m, GrindSetting = 18m, TempC = 90m, TimeS = 120, Steps = [new(RecipeStepKind.Bloom, 0, 30, 50m), new(RecipeStepKind.Pour, 30, null, 200m), new(RecipeStepKind.Stir, 60), new(RecipeStepKind.Press, 90, 30)] };
        foreach (var recipe in new[] { v60Old, v60New, espresso, aeropress })
        {
            await Put("recipes", recipe);
        }

        // Two weeks of brews
        DateTimeOffset At(int daysAgo, int hour) => new DateTimeOffset(today.AddDays(-daysAgo).ToDateTime(new TimeOnly(hour, 0)), TimeSpan.Zero);
        Brew Shot(int daysAgo, int hour, int rating) => new()
        {
            Id = $"demo-brew-espresso-{daysAgo}-{hour}", BagId = brazilBag.Id, MethodCode = "espresso", RecipeId = espresso.Id, GrinderId = eureka.Id, BrewedAt = At(daysAgo, hour),
            DrinkType = DrinkType.Espresso, DoseG = 18m, YieldG = 36m, GrindSetting = 2.5m, TempC = 93m, TimeS = 28, Rating = rating,
        };
        Brew Pour(int daysAgo, int hour, int rating) => new()
        {
            Id = $"demo-brew-v60-{daysAgo}-{hour}", BagId = kenyaBag.Id, MethodCode = "v60", RecipeId = v60New.Id, GrinderId = comandante.Id, BrewedAt = At(daysAgo, hour),
            DrinkType = DrinkType.Filter, DoseG = 15m, WaterG = 250m, GrindSetting = 22m, TempC = 94m, TimeS = 190, Rating = rating,
        };

        Brew[] brews =
        [
            Shot(0, 7, 5) with { YieldG = 37m, TimeS = 29, Taste = new Taste(Body: 1, Sweetness: 1), Notes = "Best shot of the bag so far. Syrupy, long chocolate finish." },
            Shot(0, 13, 4) with { DrinkType = DrinkType.FlatWhite, MilkMl = 120, MilkType = MilkType.Oat },
            Pour(0, 9, 5) with { Taste = new Taste(Acidity: 1, Sweetness: 1), Notes = "Juicy blackcurrant, clean finish." },
            Shot(1, 7, 3) with { TimeS = 24, YieldG = 40m, GrindSetting = 2.75m, Taste = new Taste(Acidity: 1, Strength: -1, Body: -1), Notes = "Ran fast, a bit thin." },
            Shot(1, 15, 4) with { DrinkType = DrinkType.Cappuccino, MilkMl = 150, MilkType = MilkType.Whole },
            Shot(2, 7, 2) with { TimeS = 35, YieldG = 32m, GrindSetting = 2.25m, Taste = new Taste(Acidity: -1, Strength: 2, Body: 1, Sweetness: -1), Notes = "Choked, bitter." },
            Pour(2, 9, 4) with { TimeS = 205 },
            Pour(4, 9, 3) with { GrindSetting = 24m, TempC = 96m, Taste = new Taste(Acidity: 2, Strength: -1, Body: -1, Sweetness: -1), Notes = "Sour and thin; go finer." },
            Pour(6, 10, 4),
            .. Enumerable.Range(3, 11).Select(day => Shot(day, 7, day % 3 == 0 ? 4 : 3)),
            new Brew
            {
                Id = "demo-brew-aeropress", BagId = kenyaOld.Id, MethodCode = "aeropress", RecipeId = aeropress.Id, GrinderId = comandante.Id, BrewedAt = At(12, 10),
                DrinkType = DrinkType.Filter, DoseG = 14m, WaterG = 200m, GrindSetting = 18m, TempC = 90m, TimeS = 120, Taste = new Taste(Strength: 1, Body: 1), Rating = 4,
            },
        ];
        foreach (var brew in brews)
        {
            await Put("brews", brew);
        }

        var bobs = new Brew
        {
            Id = "demo-brew-bob", BagId = kenyaBag.Id, MethodCode = "v60", GrinderId = comandante.Id, BrewedAt = At(1, 10),
            DrinkType = DrinkType.Filter, DoseG = 16m, WaterG = 260m, GrindSetting = 23m, TempC = 95m, TimeS = 200, Taste = new Taste(Acidity: 1), Rating = 4,
        };
        await Put("brews", bobs, Bob);

        logger.LogInformation("Demo data: {Stored} records added to {Household}", stored, Alice.HouseholdId);
    }
}
