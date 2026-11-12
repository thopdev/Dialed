using System.Xml.Linq;
using Bunit;
using Dialed.Client.Data;
using Dialed.Domain.Model;
using Dialed.UI.Cards;
using Dialed.UI.Pages;
using Microsoft.Extensions.DependencyInjection;

namespace Dialed.UI.Tests;

public sealed class BeanCardTests : UiTestContext
{
    private static CancellationToken Ct => Xunit.TestContext.Current.CancellationToken;

    private static BeanCardContent Content(string? iso = "ET", string title = "Ethiopia Guji", int? rating = 4) =>
        new(title, "Manhattan Coffee Roasters", "Ethiopia, Guji", rating, iso, [("Process", "Natural"), ("Roast level", "Light"), ("Altitude", "2100 m")], "blueberry · jasmine · peach", "Kept in Dialed");

    [Fact]
    public void The_card_is_a_valid_svg_of_the_share_size_with_the_text_and_the_country()
    {
        var svg = BeanCard.Build(Content());

        var root = XDocument.Parse(svg).Root!;
        root.Attribute("width")!.Value.ShouldBe("1080");
        root.Attribute("height")!.Value.ShouldBe("1350");
        svg.ShouldContain("Ethiopia Guji");
        svg.ShouldContain("Manhattan Coffee Roasters · Ethiopia, Guji");
        svg.ShouldContain("2100 m");
        svg.ShouldContain("blueberry · jasmine · peach");
        svg.ShouldContain("★★★★"); // four lit stars drawn over five dim ones
        XDocument.Parse(svg).Descendants().Count(e => e.Name.LocalName == "path").ShouldBe(2); // the neighbours and the country
    }

    [Fact]
    public void The_rating_is_five_dim_stars_with_the_lit_ones_drawn_exactly_over_them()
    {
        var stars = XDocument.Parse(BeanCard.Build(Content(rating: 3))).Descendants().Where(e => e.Name.LocalName == "text" && e.Value.Contains('★')).Select(e => e.Value).ToList();

        stars.ShouldBe(["★★★★★", "★★★"]); // overlapping glyphs, so no spacing guess can misalign them
    }

    [Fact]
    public void An_unknown_country_leaves_the_map_panel_empty_and_the_card_valid()
    {
        var svg = BeanCard.Build(Content(iso: null));

        XDocument.Parse(svg).Descendants().Count(e => e.Name.LocalName == "path").ShouldBe(0);
        svg.ShouldContain("Ethiopia Guji");
    }

    [Fact]
    public void Text_is_escaped_and_a_long_title_wraps_to_two_lines()
    {
        var svg = BeanCard.Build(Content(title: "Tom & Jerry's <Blend> Finca La Esperanza Reserva Especial Lot Seventeen"));

        XDocument.Parse(svg).ShouldNotBeNull();
        svg.ShouldNotContain("<Blend>");
        XDocument.Parse(svg).Descendants().Count(e => e.Name.LocalName == "text" && e.Attribute("font-size")!.Value == "78").ShouldBeLessThanOrEqualTo(2);
    }

    [Fact]
    public void Without_a_rating_there_are_no_stars()
    {
        BeanCard.Build(Content(rating: null)).ShouldNotContain("★");
    }

    private async Task<Bean> SavedBean() =>
        await Services.GetRequiredService<IRepository<Bean>>().SaveAsync(
            new Bean { Name = "Guji", Origin = new BeanOrigin("Ethiopia", "Guji"), Process = "Natural", TastingNotes = ["peach"], Rating = 5 }, Ct);

    [Fact]
    public async Task Sharing_a_bean_draws_the_card_with_the_country_and_hands_the_png_to_the_share_sheet()
    {
        var bean = await SavedBean();
        var page = Render<Beans>(p => p.Add(b => b.Id, bean.Id));
        page.WaitForAssertion(() => page.Find("#bean-share-card"));

        page.Find("#bean-share-card").Click();

        page.WaitForAssertion(() => ExportedFiles.Saved.ShouldHaveSingleItem());
        ExportedFiles.Saved[0].Name.ShouldBe("dialed-bean.png");
        ExportedFiles.Saved[0].ContentType.ShouldBe("image/png");
        var (svg, width, height) = ImageRenderer.Rendered.ShouldHaveSingleItem();
        (width, height).ShouldBe((1080, 1350));
        svg.ShouldContain("Guji");
        svg.ShouldContain("Ethiopia, Guji");
        svg.ShouldContain("peach");
        svg.ShouldContain("Natural");
    }

    [Fact]
    public async Task A_device_that_cannot_draw_it_says_so()
    {
        var bean = await SavedBean();
        ImageRenderer.Fail = true;
        var page = Render<Beans>(p => p.Add(b => b.Id, bean.Id));
        page.WaitForAssertion(() => page.Find("#bean-share-card"));

        page.Find("#bean-share-card").Click();

        page.WaitForAssertion(() => page.Markup.ShouldContain("could not be made on this device"));
        ExportedFiles.Saved.ShouldBeEmpty();
    }
}
