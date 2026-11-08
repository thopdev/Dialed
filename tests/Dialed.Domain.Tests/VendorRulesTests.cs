using Dialed.Domain.Model;

namespace Dialed.Domain.Tests;

public sealed class VendorRulesTests
{
    [Theory]
    [InlineData("friedhats.com", "https://friedhats.com")]
    [InlineData("  https://friedhats.com/ ", "https://friedhats.com")]
    [InlineData("http://example.org/shop?x=1", "http://example.org/shop?x=1")]
    [InlineData("www.tim-wendelboe.no/en", "https://www.tim-wendelboe.no/en")]
    [InlineData("", null)]
    [InlineData("   ", null)]
    [InlineData(null, null)]
    public void Websites_are_normalised_to_https_urls(string? input, string? expected)
    {
        VendorRules.TryNormalizeWebsite(input, out var website).ShouldBeTrue();
        website.ShouldBe(expected);
    }

    [Theory]
    [InlineData("not a url")]
    [InlineData("localhost")]
    [InlineData("ftp://files.example.com")]
    [InlineData("javascript:alert(1)")]
    [InlineData("https://")]
    public void Unusable_websites_are_rejected(string input)
    {
        VendorRules.TryNormalizeWebsite(input, out var website).ShouldBeFalse();
        website.ShouldBeNull();
    }

    [Fact]
    public void Usage_counts_beans_and_bags_that_point_at_the_vendor()
    {
        Bean[] beans =
        [
            new() { Name = "A", RoasterId = "v1" },
            new() { Name = "B", RoasterId = "v1" },
            new() { Name = "C", RoasterId = "v2" },
            new() { Name = "D", RoasterId = "v1", Deleted = true },
        ];
        Bag[] bags =
        [
            new() { BeanId = "b", WeightG = 250m, SellerId = "v1" },
            new() { BeanId = "b", WeightG = 250m, SellerId = "v2" },
        ];

        var usage = VendorRules.UsageOf("v1", beans, bags);

        usage.ShouldBe(new VendorUsage(2, 1));
        usage.IsUsed.ShouldBeTrue();
        VendorRules.UsageOf("nobody", beans, bags).IsUsed.ShouldBeFalse();
    }

    [Fact]
    public void Kind_filter_matches_vendors_that_have_the_kind()
    {
        var roaster = new Vendor { Name = "R", Kinds = VendorKinds.Roaster };
        var both = new Vendor { Name = "B", Kinds = VendorKinds.Roaster | VendorKinds.Seller };

        VendorRules.MatchesKind(roaster, VendorKinds.None).ShouldBeTrue();
        VendorRules.MatchesKind(roaster, VendorKinds.Seller).ShouldBeFalse();
        VendorRules.MatchesKind(both, VendorKinds.Seller).ShouldBeTrue();
        VendorRules.MatchesKind(both, VendorKinds.Roaster).ShouldBeTrue();
    }

    [Fact]
    public void Usage_rejects_null_arguments()
    {
        Should.Throw<ArgumentNullException>(() => VendorRules.UsageOf("v", null!, []));
        Should.Throw<ArgumentNullException>(() => VendorRules.UsageOf("v", [], null!));
    }
}
