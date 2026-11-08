using System.Text;
using System.Text.Json;
using Dialed.Domain.BrewMethods;

namespace Dialed.Domain.Tests;

public sealed class BrewMethodCatalogTests
{
    private static readonly string[] RequiredCodes =
    [
        "espresso", "v60", "kalita-wave", "chemex", "origami", "aeropress", "french-press",
        "clever-dripper", "moka-pot", "cold-brew", "siphon", "cezve", "batch-brewer", "other",
    ];

    [Fact]
    public void Bundled_catalog_has_all_required_methods()
    {
        BrewMethodCatalog.Default.Methods.Select(method => method.Code).ShouldBe(RequiredCodes, ignoreOrder: true);
    }

    [Fact]
    public void Every_method_asks_at_least_for_dose_and_grind()
    {
        foreach (var method in BrewMethodCatalog.Default.Methods)
        {
            method.Uses(BrewParameter.Dose).ShouldBeTrue(method.Code);
            method.Uses(BrewParameter.Grind).ShouldBeTrue(method.Code);
        }
    }

    [Theory]
    [InlineData("espresso", BrewMethodCategory.Espresso)]
    [InlineData("v60", BrewMethodCategory.PourOver)]
    [InlineData("chemex", BrewMethodCategory.PourOver)]
    [InlineData("aeropress", BrewMethodCategory.Immersion)]
    [InlineData("french-press", BrewMethodCategory.Immersion)]
    [InlineData("cold-brew", BrewMethodCategory.ColdBrew)]
    [InlineData("moka-pot", BrewMethodCategory.Other)]
    public void Methods_have_their_category(string code, BrewMethodCategory category)
    {
        BrewMethodCatalog.Default.Get(code).Category.ShouldBe(category);
    }

    [Fact]
    public void Espresso_asks_for_yield_and_preinfusion_but_pour_over_asks_for_water_and_steps()
    {
        var espresso = BrewMethodCatalog.Default.Get("espresso");
        var v60 = BrewMethodCatalog.Default.Get("v60");

        espresso.Uses(BrewParameter.Yield).ShouldBeTrue();
        espresso.Uses(BrewParameter.Preinfusion).ShouldBeTrue();
        espresso.Uses(BrewParameter.Water).ShouldBeFalse();
        v60.Uses(BrewParameter.Water).ShouldBeTrue();
        v60.Uses(BrewParameter.Steps).ShouldBeTrue();
        v60.Uses(BrewParameter.Yield).ShouldBeFalse();
    }

    [Fact]
    public void Find_ignores_case_and_returns_null_for_unknown_codes()
    {
        BrewMethodCatalog.Default.Find("V60").ShouldNotBeNull();
        BrewMethodCatalog.Default.Find("nope").ShouldBeNull();
        Should.Throw<KeyNotFoundException>(() => BrewMethodCatalog.Default.Get("nope"));
    }

    [Fact]
    public void Bundled_catalog_has_a_version()
    {
        BrewMethodCatalog.Default.Version.ShouldBeGreaterThan(0);
    }

    [Fact]
    public void Parse_reads_a_valid_catalog()
    {
        var catalog = Parse("""{ "version": 3, "methods": [ { "code": "x", "name": "X", "category": "coldBrew", "parameters": ["dose", "dose", "time"] } ] }""");

        catalog.Version.ShouldBe(3);
        var method = catalog.Get("x");
        method.Category.ShouldBe(BrewMethodCategory.ColdBrew);
        method.Parameters.ShouldBe([BrewParameter.Dose, BrewParameter.Time]);
    }

    [Theory]
    [InlineData("""{ "version": 1, "methods": [] }""")]
    [InlineData("""{ "version": 1 }""")]
    [InlineData("""{ "version": 1, "methods": [ { "code": "", "name": "X", "category": "other", "parameters": ["dose"] } ] }""")]
    [InlineData("""{ "version": 1, "methods": [ { "code": "x", "name": "X", "category": "other", "parameters": [] } ] }""")]
    [InlineData("""{ "version": 1, "methods": [ { "code": "x", "name": "X", "category": "other", "parameters": ["dose"] }, { "code": "X", "name": "Y", "category": "other", "parameters": ["dose"] } ] }""")]
    [InlineData("""{ "version": 1, "methods": [ { "code": "x", "name": "X", "category": "other", "parameters": ["flavour"] } ] }""")]
    [InlineData("""{ "version": 1, "methods": [ { "code": "x", "name": "X", "category": "other", "parameters": ["dose"], "extra": 1 } ] }""")]
    public void Parse_rejects_invalid_catalogs(string json)
    {
        Should.Throw<JsonException>(() => Parse(json));
    }

    private static BrewMethodCatalog Parse(string json) => BrewMethodCatalog.Parse(new MemoryStream(Encoding.UTF8.GetBytes(json)));
}
