using Dialed.Domain.Geography;

namespace Dialed.Domain.Tests;

public sealed class CountryCatalogTests
{
    [Theory]
    [InlineData("Ethiopia", "ET")]
    [InlineData("  ethiopië ", "ET")]
    [InlineData("COSTA   RICA", "CR")]
    [InlineData("Brasil", "BR")]
    [InlineData("Ivoorkust", "CI")]
    [InlineData("Côte d'Ivoire", "CI")]
    [InlineData("Hawaii", "US")]
    [InlineData("DR Congo", "CD")]
    public void Finds_a_country_by_its_name_in_either_language(string text, string iso)
    {
        CountryCatalog.Find(text)!.Iso2.ShouldBe(iso);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("Atlantis")]
    [InlineData("Blend")]
    public void Unknown_text_is_not_a_country(string? text)
    {
        CountryCatalog.Find(text).ShouldBeNull();
    }

    [Fact]
    public void Every_country_lands_on_the_map_and_names_are_unique()
    {
        foreach (var place in CountryCatalog.All)
        {
            var (x, y) = WorldMapProjection.Project(place.Latitude, place.Longitude);
            x.ShouldBeInRange(0, WorldMapProjection.Width);
            y.ShouldBeInRange(0, WorldMapProjection.Height);
        }

        CountryCatalog.All.Select(p => p.Name).Distinct().Count().ShouldBe(CountryCatalog.All.Count);
        CountryCatalog.All.Select(p => p.Iso2).Distinct().Count().ShouldBe(CountryCatalog.All.Count);
    }
}
