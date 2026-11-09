using Dialed.Domain.Model;
using Dialed.Domain.Text;

namespace Dialed.Domain.Tests;

public sealed class LabelMatcherTests
{
    private static readonly Vendor Friedhats = new() { Name = "Friedhats Coffee Roasters" };
    private static readonly Vendor Lot61 = new() { Name = "Lot Sixty One" };
    private static readonly Vendor Shop = new() { Name = "Friedhats Shop", Kinds = VendorKinds.Seller };

    private static Bean Bean(string name, Vendor? roaster = null, string? country = null) =>
        new() { Name = name, RoasterId = roaster?.Id, Origin = new BeanOrigin(Country: country) };

    [Theory]
    [InlineData("Friedhats", true)]
    [InlineData("FRIEDHATS COFFEE ROASTERS", true)]
    [InlineData("friedhat", true)]
    [InlineData("Friedhats Roastery", true)]
    [InlineData("Lot 61", false)]
    public void Roasters_match_regardless_of_case_noise_words_and_small_typos(string scanned, bool likely)
    {
        var match = LabelMatcher.Find(scanned, null, null, [Friedhats, Lot61], []);

        (match.BestRoaster?.Item == Friedhats).ShouldBe(likely);
    }

    [Fact]
    public void Only_roasters_are_candidates_and_deleted_ones_are_ignored()
    {
        var match = LabelMatcher.Find("Friedhats", null, null, [Shop, Friedhats with { Deleted = true }], []);

        match.Roasters.ShouldBeEmpty();
    }

    [Fact]
    public void Beans_match_on_name_with_the_roaster_and_origin_taken_into_account()
    {
        var guji = Bean("Guji Hambela", Friedhats, "Ethiopia");
        var otherGuji = Bean("Guji Hambela", Lot61, "Ethiopia");
        var colombia = Bean("Huila", Friedhats, "Colombia");

        var match = LabelMatcher.Find("Friedhats", "guji hambela", "Ethiopia", [Friedhats, Lot61], [guji, otherGuji, colombia]);

        match.Beans.Select(c => c.Item.Id).ShouldBe([guji.Id, otherGuji.Id]);
        match.BestBean!.Item.Id.ShouldBe(guji.Id);
        match.BestBean.Score.ShouldBe(1d);
        match.Beans[1].IsLikelySame.ShouldBeFalse();
    }

    [Fact]
    public void A_different_origin_lowers_the_score_below_a_sure_match()
    {
        var match = LabelMatcher.Find(null, "Gesha Village", "Panama", [], [Bean("Gesha Village", country: "Ethiopia")]);

        match.Beans.ShouldHaveSingleItem().Score.ShouldBe(0.8d);
        match.BestBean.ShouldBeNull();
    }

    [Fact]
    public void Candidates_are_limited_and_best_first()
    {
        var beans = Enumerable.Range(0, 8).Select(i => Bean($"Kenya AA {i}")).Append(Bean("Kenya AA")).ToList();

        var match = LabelMatcher.Find(null, "Kenya AA", null, [], beans);

        match.Beans.Count.ShouldBe(LabelMatcher.MaxCandidates);
        match.Beans[0].Item.Name.ShouldBe("Kenya AA");
    }

    [Fact]
    public void A_scan_that_read_nothing_matches_nothing()
    {
        var match = LabelMatcher.Find(null, null, null, [Friedhats], [Bean("Guji")]);

        match.Roasters.ShouldBeEmpty();
        match.Beans.ShouldBeEmpty();
    }

    [Fact]
    public void The_roaster_key_drops_noise_but_keeps_names_made_only_of_noise()
    {
        LabelMatcher.RoasterKey("The Friedhats Coffee Roasters").ShouldBe("friedhats");
        LabelMatcher.RoasterKey("Coffee Company").ShouldNotBeEmpty();
    }
}
