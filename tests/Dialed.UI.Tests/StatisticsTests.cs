using Bunit;
using Dialed.Client.Data;
using Dialed.Domain.Model;
using Dialed.UI.Pages;
using Microsoft.Extensions.DependencyInjection;

namespace Dialed.UI.Tests;

public sealed class StatisticsTests : UiTestContext
{
    private static CancellationToken Ct => Xunit.TestContext.Current.CancellationToken;

    [Fact]
    public void Without_brews_the_screen_invites_you_to_brew()
    {
        Render<Statistics>().Markup.ShouldContain("Brew some coffee");
    }

    [Fact]
    public async Task Brews_show_up_in_consumption_origins_and_spend()
    {
        var bean = await Services.GetRequiredService<IRepository<Bean>>().SaveAsync(new Bean { Name = "Guji", Origin = new BeanOrigin(Country: "Ethiopia"), Process = "Washed", TastingNotes = ["blueberry", "jasmine"] }, Ct);
        var bag = await Services.GetRequiredService<IRepository<Bag>>().SaveAsync(
            new Bag { BeanId = bean.Id, WeightG = 250m, PriceCents = 1450, Currency = "EUR", PurchasedOn = DateOnly.FromDateTime(DateTime.Now) }, Ct);
        await Services.GetRequiredService<IRepository<Brew>>().SaveAsync(
            new Brew { BagId = bag.Id, MethodCode = "v60", DoseG = 18m, Rating = 4, BrewedAt = DateTimeOffset.Now }, Ct);

        var page = Render<Statistics>();

        page.WaitForAssertion(() => page.Find("#stats-weekly").TextContent.ShouldContain("18 g"));
        page.Find("#stats-origins").TextContent.ShouldContain("Ethiopia");
        page.Find("#stats-origin-map svg").GetAttribute("aria-label")!.ShouldContain("Ethiopia");
        page.Find("#stats-flavor-wheel").TextContent.ShouldContain("Fruity · 1");
        page.Find("#stats-processes").TextContent.ShouldContain("Washed");
        page.Find("#stats-spend").TextContent.ShouldContain("14");
    }
}
