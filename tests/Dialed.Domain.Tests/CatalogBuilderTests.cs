using System.Text.Json;
using Dialed.Domain.Catalog;
using Dialed.Domain.Model;

namespace Dialed.Domain.Tests;

/// <summary>The shared catalog built from what people share publicly (#105).</summary>
public sealed class CatalogBuilderTests
{
    private static CatalogBeanInput Bean(string who, string name, string? roaster = "Friedhats", string? country = "Ethiopia", string? region = null, string? process = null,
        string[]? varieties = null, RoastLevel? roast = null, string[]? notes = null) =>
        new(who, name, roaster, country, region, process, varieties ?? [], roast, notes ?? []);

    private static BeanCatalog Build(IEnumerable<CatalogBeanInput> beans, IEnumerable<CatalogRoasterInput>? roasters = null, int min = 2, CatalogRules? rules = null) =>
        CatalogBuilder.Build(beans, roasters ?? [], min, rules);

    // ---- The quality gate ---------------------------------------------------------------------

    [Fact]
    public void A_bean_only_appears_when_two_different_people_made_it_public()
    {
        Build([Bean("a", "Guji Natural")]).Beans.ShouldBeEmpty();
        Build([Bean("a", "Guji Natural"), Bean("b", "Guji Natural")]).Beans.ShouldHaveSingleItem().People.ShouldBe(2);
    }

    [Fact]
    public void The_same_person_twice_counts_once()
    {
        Build([Bean("a", "Guji Natural"), Bean("a", "Guji Natural"), Bean("a", "guji natural")]).Beans.ShouldBeEmpty();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(-5)]
    public void The_threshold_can_be_raised_but_never_lowered_below_two(int setting)
    {
        Build([Bean("a", "Guji Natural")], min: setting).Beans.ShouldBeEmpty();
    }

    [Fact]
    public void A_higher_threshold_keeps_more_back()
    {
        var beans = new[] { Bean("a", "Guji"), Bean("b", "Guji") };

        Build(beans, min: 3).Beans.ShouldBeEmpty();
        Build([.. beans, Bean("c", "Guji")], min: 3).Beans.ShouldHaveSingleItem();
    }

    [Fact]
    public void A_roaster_needs_two_people_as_well()
    {
        var one = Build([Bean("a", "Guji", roaster: "Solo Roasters")]);
        one.Roasters.ShouldBeEmpty();

        var two = Build([Bean("a", "Guji", roaster: "Friedhats"), Bean("b", "Other", roaster: "Friedhats")]);
        two.Roasters.ShouldHaveSingleItem().Name.ShouldBe("Friedhats");
    }

    // ---- De-duplication -----------------------------------------------------------------------

    [Fact]
    public void Spelling_accents_case_and_roaster_suffixes_do_not_split_entries()
    {
        var catalog = Build(
        [
            Bean("a", "Ethiopia Guji Natural", roaster: "Friedhats Coffee Roasters"),
            Bean("b", "ethiopia guji natural", roaster: "Friedhats"),
            Bean("c", "Ethiopïa  Guji Natural!", roaster: "FRIEDHATS"),
        ]);

        var bean = catalog.Beans.ShouldHaveSingleItem();
        bean.People.ShouldBe(3);
        catalog.Roasters.ShouldHaveSingleItem().People.ShouldBe(3);
    }

    [Fact]
    public void A_small_typo_still_lands_in_the_same_entry()
    {
        var catalog = Build([Bean("a", "Kenya Kiambu AA"), Bean("b", "Kenya Kiambu AA"), Bean("c", "Kenya Kiambu AAA")]);

        catalog.Beans.ShouldHaveSingleItem().People.ShouldBe(3);
    }

    [Fact]
    public void A_name_that_only_starts_like_another_is_another_coffee()
    {
        var catalog = Build([Bean("a", "Guji"), Bean("b", "Guji"), Bean("a", "Guji Natural"), Bean("b", "Guji Natural"), Bean("a", "Guji Washed"), Bean("b", "Guji Washed")]);

        catalog.Beans.Select(b => b.Name).Order().ShouldBe(["Guji", "Guji Natural", "Guji Washed"]);
    }

    [Fact]
    public void Short_names_tolerate_no_typo_and_longer_ones_a_few()
    {
        CatalogBuilder.SameName("Natural", "Naturel").ShouldBeTrue(); // one typo in a 7 letter name
        CatalogBuilder.SameName("Lot 12", "Lot 13").ShouldBeFalse(); // numbers matter
        CatalogBuilder.SameName("Dak", "Dok").ShouldBeFalse();
        CatalogBuilder.SameName("Ethiopia Guji Natural", "Ethiopia Gujj Natural").ShouldBeTrue();
        CatalogBuilder.SameName("Ethiopia Guji Natural", "Ethiopia Guji Washed").ShouldBeFalse();
        CatalogBuilder.SameName("", "x").ShouldBeFalse();
    }

    [Fact]
    public void The_same_name_from_another_roaster_is_another_coffee()
    {
        var catalog = Build(
        [
            Bean("a", "House Blend", roaster: "Friedhats", country: null),
            Bean("b", "House Blend", roaster: "Friedhats", country: null),
            Bean("c", "House Blend", roaster: "Manhattan Coffee", country: null),
            Bean("d", "House Blend", roaster: "Manhattan Coffee", country: null),
        ]);

        catalog.Beans.Count.ShouldBe(2);
    }

    [Fact]
    public void The_same_name_from_another_country_is_another_coffee_and_an_unknown_country_does_not_glue_them_together()
    {
        var catalog = Build(
        [
            Bean("a", "Natural", country: "Ethiopia"),
            Bean("b", "Natural", country: "Ethiopia"),
            Bean("c", "Natural", country: "Brazil"),
            Bean("d", "Natural", country: "Brazil"),
            Bean("e", "Natural", country: null),
        ]);

        catalog.Beans.Select(b => (b.Country, b.People)).Order().ShouldBe([("Brazil", 2), ("Ethiopia", 2)]); // the one without a country could be either, so it joins neither
    }

    [Fact]
    public void A_bean_without_a_country_joins_the_one_coffee_it_could_be()
    {
        var catalog = Build([Bean("a", "Natural", country: "Ethiopia"), Bean("b", "Natural", country: "Ethiopia"), Bean("c", "Natural", country: null)]);

        catalog.Beans.Single().People.ShouldBe(3);
    }

    [Fact]
    public void Different_coffees_stay_apart()
    {
        var catalog = Build([Bean("a", "Guji Natural"), Bean("b", "Guji Natural"), Bean("a", "Yirgacheffe Washed"), Bean("b", "Yirgacheffe Washed")]);

        catalog.Beans.Select(b => b.Name).Order().ShouldBe(["Guji Natural", "Yirgacheffe Washed"]);
    }

    // ---- Field voting -------------------------------------------------------------------------

    [Fact]
    public void Field_values_are_the_most_common_among_contributors()
    {
        var catalog = Build(
        [
            Bean("a", "Guji", process: "Natural", region: "Guji"),
            Bean("b", "Guji", process: "Natural", region: "Oromia"),
            Bean("c", "Guji", process: "Washed", region: "Guji"),
        ]);

        var bean = catalog.Beans.Single();
        bean.Process.ShouldBe("Natural");
        bean.Region.ShouldBe("Guji");
    }

    [Fact]
    public void A_tie_goes_to_the_alphabetically_first_so_rebuilds_are_stable()
    {
        var one = Build([Bean("a", "Guji", process: "Washed"), Bean("b", "Guji", process: "Natural")]).Beans.Single();
        var two = Build([Bean("b", "Guji", process: "Natural"), Bean("a", "Guji", process: "Washed")]).Beans.Single();

        one.Process.ShouldBe("Natural");
        JsonSerializer.Serialize(two).ShouldBe(JsonSerializer.Serialize(one));
    }

    [Fact]
    public void The_roast_level_is_the_most_common_one()
    {
        var catalog = Build([Bean("a", "Guji", roast: RoastLevel.Light), Bean("b", "Guji", roast: RoastLevel.Light), Bean("c", "Guji", roast: RoastLevel.Dark), Bean("d", "Guji")]);

        catalog.Beans.Single().RoastLevel.ShouldBe(RoastLevel.Light);
    }

    [Fact]
    public void Free_text_keeps_only_terms_at_least_two_people_used()
    {
        var catalog = Build(
        [
            Bean("a", "Guji", notes: ["Jasmine", "blueberry", "my cat's name"], varieties: ["Heirloom"]),
            Bean("b", "Guji", notes: ["jasmine", "Blueberry"], varieties: ["Heirloom", "Typica"]),
            Bean("c", "Guji", notes: ["lemon"], varieties: ["Typica", "Heirloom"]),
        ]);

        var bean = catalog.Beans.Single();
        bean.TastingNotes.ShouldBe(["Blueberry", "Jasmine"], ignoreOrder: true);
        bean.Varieties.ShouldBe(["Heirloom", "Typica"]);
    }

    [Fact]
    public void A_term_one_person_repeats_is_still_one_person()
    {
        var catalog = Build([Bean("a", "Guji", notes: ["jasmine", "Jasmine"]), Bean("b", "Guji")]);

        catalog.Beans.Single().TastingNotes.ShouldBeEmpty();
    }

    [Fact]
    public void Roaster_details_are_voted_from_public_vendors()
    {
        var roasters = new[]
        {
            new CatalogRoasterInput("a", "Friedhats", "Netherlands", "Amsterdam", "https://friedhats.com"),
            new CatalogRoasterInput("b", "Friedhats Coffee Roasters", "Netherlands", "Amsterdam", "https://friedhats.com"),
            new CatalogRoasterInput("c", "Friedhats", "Holland", null, null),
        };

        var roaster = Build([], roasters).Roasters.ShouldHaveSingleItem();

        roaster.People.ShouldBe(3);
        (roaster.Country, roaster.City, roaster.Website).ShouldBe(("Netherlands", "Amsterdam", "https://friedhats.com"));
    }

    [Fact]
    public void A_bean_points_to_its_roaster_entry_when_there_is_one()
    {
        var catalog = Build([Bean("a", "Guji"), Bean("b", "Guji")]);

        catalog.Beans.Single().RoasterId.ShouldBe(catalog.Roasters.Single().Id);
        catalog.Beans.Single().RoasterName.ShouldBe("Friedhats");
    }

    // ---- Stability, moderation and privacy ----------------------------------------------------

    [Fact]
    public void An_entry_keeps_its_id_when_more_people_join_and_whatever_the_order()
    {
        var two = Build([Bean("a", "Guji Natural"), Bean("b", "Guji Natural")]).Beans.Single();
        var four = Build([Bean("d", "Guji Natural"), Bean("c", "Guji Natural"), Bean("b", "Guji Natural"), Bean("a", "Guji Natural")]).Beans.Single();

        four.Id.ShouldBe(two.Id);
        four.People.ShouldBe(4);
    }

    [Fact]
    public void A_hidden_key_hides_the_entry_and_the_rule_survives_a_rebuild()
    {
        var beans = new[] { Bean("a", "Junk Bean"), Bean("b", "Junk Bean"), Bean("a", "Good Bean"), Bean("b", "Good Bean") };
        var before = Build(beans);
        var junk = before.Beans.Single(b => b.Name == "Junk Bean");
        var rules = new CatalogRules(junk.Keys.ToHashSet(), []);

        Build(beans, rules: rules).Beans.Select(b => b.Name).ShouldBe(["Good Bean"]);
        Build([.. beans, Bean("c", "Junk Bean")], rules: rules).Beans.Select(b => b.Name).ShouldBe(["Good Bean"]);
    }

    [Fact]
    public void A_merge_rule_joins_two_entries_into_one()
    {
        var beans = new[]
        {
            Bean("a", "Guji Hambela Natural"), Bean("b", "Guji Hambela Natural"),
            Bean("c", "Hambela Estate Lot 7"), Bean("d", "Hambela Estate Lot 7"),
        };
        var before = Build(beans);
        before.Beans.Count.ShouldBe(2);
        var rules = new CatalogRules(new HashSet<string>(), [(before.Beans[0].Keys[0], before.Beans[1].Keys[0])]);

        var after = Build(beans, rules: rules);

        after.Beans.ShouldHaveSingleItem().People.ShouldBe(4);
    }

    [Fact]
    public void Nothing_in_the_catalog_points_back_to_a_contributor()
    {
        var catalog = Build(
        [
            Bean("secret-user-id-1", "Guji", notes: ["jasmine"]),
            Bean("secret-user-id-2", "Guji", notes: ["jasmine"]),
        ],
        [new CatalogRoasterInput("secret-user-id-1", "Friedhats", null, null, null), new CatalogRoasterInput("secret-user-id-2", "Friedhats", null, null, null)]);

        JsonSerializer.Serialize(catalog).ShouldNotContain("secret-user-id");
    }

    [Fact]
    public void Empty_names_and_empty_contributors_are_ignored_and_an_empty_input_gives_an_empty_catalog()
    {
        Build([]).ShouldBe(BeanCatalog.Empty, new CatalogComparer());
        Build([Bean("a", " "), Bean("", "Guji"), Bean("b", "Guji")]).Beans.ShouldBeEmpty();
    }

    private sealed class CatalogComparer : IEqualityComparer<BeanCatalog>
    {
        public bool Equals(BeanCatalog? x, BeanCatalog? y) => x!.Roasters.Count == y!.Roasters.Count && x.Beans.Count == y.Beans.Count;

        public int GetHashCode(BeanCatalog obj) => 0;
    }
}
