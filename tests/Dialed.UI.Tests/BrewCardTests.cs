using Bunit;
using Dialed.Client.Data;
using Dialed.Domain.Model;
using Dialed.UI.Cards;
using Dialed.UI.Pages;
using Microsoft.Extensions.DependencyInjection;

namespace Dialed.UI.Tests;

public sealed class BrewCardTests : UiTestContext
{
    private static CancellationToken Ct => Xunit.TestContext.Current.CancellationToken;

    private static BrewCardContent Content(string title = "Ethiopia Guji", int? rating = 4) =>
        new(title, "Ethiopia, Guji", rating, "Flat white", [("Dose", "18 g"), ("Water", "288 g"), ("Time", "3:02")], "blueberry · jasmine", "Brewed 11 Nov 2026 with Dialed", Drink: Domain.Model.DrinkType.FlatWhite);

    [Fact]
    public void The_card_is_a_valid_svg_of_the_share_size_with_the_text_on_it()
    {
        var svg = BrewCard.Build(Content());

        var xml = System.Xml.Linq.XDocument.Parse(svg).Root!;
        xml.Attribute("width")!.Value.ShouldBe("1080");
        xml.Attribute("height")!.Value.ShouldBe("1350");
        svg.ShouldContain("Ethiopia Guji");
        svg.ShouldContain("Flat white");
        svg.ShouldContain("288 g");
        svg.ShouldContain("blueberry · jasmine");
        svg.ShouldContain("4 / 5");
        svg.ShouldContain("latte-art-tulip");
        svg.ShouldNotContain("<polygon");
    }

    [Fact]
    public void Text_is_escaped_so_a_name_cannot_break_the_picture()
    {
        var svg = BrewCard.Build(Content("Tom & Jerry's <Blend>"));

        System.Xml.Linq.XDocument.Parse(svg).ShouldNotBeNull();
        svg.ShouldNotContain("<Blend>");
        svg.ShouldContain("&amp;");
    }

    [Fact]
    public void A_long_title_wraps_to_at_most_two_lines()
    {
        var svg = BrewCard.Build(Content("Finca La Esperanza Reserva Especial Lot Number Seventeen Washed Geisha Honey Anaerobic"));

        System.Xml.Linq.XDocument.Parse(svg).Descendants().Count(e => e.Name.LocalName == "text" && e.Attribute("font-size")!.Value == "72").ShouldBeLessThanOrEqualTo(2);
    }

    [Theory]
    [InlineData(5, "rosetta")]
    [InlineData(4, "tulip")]
    [InlineData(3, "heart")]
    [InlineData(2, "dot")]
    [InlineData(1, "crema")]
    public void The_rating_decides_which_latte_art_is_in_the_foam(int rating, string art)
    {
        var svg = BrewCard.Build(Content(rating: rating));

        System.Xml.Linq.XDocument.Parse(svg).ShouldNotBeNull();
        svg.ShouldContain($"latte-art-{art}");
    }

    [Theory]
    [InlineData(Domain.Model.DrinkType.Filter, "cup-plain")]
    [InlineData(Domain.Model.DrinkType.Espresso, "cup-crema")]
    [InlineData(Domain.Model.DrinkType.ColdBrew, "cup-plain")]
    public void Drinks_without_milk_get_no_latte_art_but_five_stars_for_the_rating(Domain.Model.DrinkType drink, string cup)
    {
        var svg = BrewCard.Build(Content(rating: 4) with { Drink = drink });

        System.Xml.Linq.XDocument.Parse(svg).ShouldNotBeNull();
        svg.ShouldNotContain("latte-art");
        svg.ShouldContain(cup);
        System.Xml.Linq.XDocument.Parse(svg).Descendants().Count(e => e.Name.LocalName == "polygon").ShouldBe(5);
        svg.ShouldContain("4 / 5");
    }

    [Theory]
    [InlineData(Domain.Model.DrinkType.Cortado)]
    [InlineData(Domain.Model.DrinkType.Cappuccino)]
    [InlineData(Domain.Model.DrinkType.Latte)]
    public void Milk_drinks_all_get_latte_art(Domain.Model.DrinkType drink) =>
        BrewCard.Build(Content(rating: 5) with { Drink = drink }).ShouldContain("latte-art-rosetta");

    [Fact]
    public void The_roast_level_colours_the_coffee_and_no_rating_gives_plain_crema_without_a_score()
    {
        var dark = BrewCard.Build(Content() with { Roast = Domain.Model.RoastLevel.Dark });
        var light = BrewCard.Build(Content() with { Roast = Domain.Model.RoastLevel.Light });

        dark.ShouldContain("#3d2112");
        light.ShouldContain("#a8742f");
        BrewCard.Build(Content(rating: null)).ShouldNotContain(" / 5");
    }

    private async Task<Brew> SavedBrew()
    {
        var bean = await Services.GetRequiredService<IRepository<Bean>>().SaveAsync(new Bean { Name = "Gesha", Origin = new BeanOrigin("Panama"), TastingNotes = ["jasmine"] }, Ct);
        var bag = await Services.GetRequiredService<IRepository<Bag>>().SaveAsync(new Bag { BeanId = bean.Id, WeightG = 250m }, Ct);
        return await Services.GetRequiredService<IRepository<Brew>>().SaveAsync(
            new Brew { BagId = bag.Id, MethodCode = "v60", DoseG = 15m, WaterG = 240m, Rating = 5, BrewedAt = DateTimeOffset.UtcNow }, Ct);
    }

    [Fact]
    public async Task Sharing_a_brew_draws_the_card_and_hands_the_png_to_the_share_sheet()
    {
        var brew = await SavedBrew();
        var page = Render<History>(p => p.Add(x => x.Id, brew.Id));

        page.Find("#history-share-card").Click();

        page.WaitForAssertion(() => ExportedFiles.Saved.ShouldHaveSingleItem());
        ExportedFiles.Saved[0].Name.ShouldBe("dialed-brew.png");
        ExportedFiles.Saved[0].ContentType.ShouldBe("image/png");
        var (svg, width, height) = ImageRenderer.Rendered.ShouldHaveSingleItem();
        (width, height).ShouldBe((1080, 1350));
        svg.ShouldContain("Gesha");
        svg.ShouldContain("Panama");
        svg.ShouldContain("jasmine");
    }

    [Fact]
    public async Task A_device_that_cannot_draw_it_says_so_instead_of_failing_silently()
    {
        var brew = await SavedBrew();
        ImageRenderer.Fail = true;
        var page = Render<History>(p => p.Add(x => x.Id, brew.Id));

        page.Find("#history-share-card").Click();

        page.WaitForAssertion(() => page.Markup.ShouldContain("could not be made on this device"));
        ExportedFiles.Saved.ShouldBeEmpty();
    }
}
