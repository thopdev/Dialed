using Bunit;
using Dialed.Client.Data;
using Dialed.Domain.Model;
using Dialed.UI.Pages;
using Microsoft.Extensions.DependencyInjection;

namespace Dialed.UI.Tests;

public sealed class DialInCoachUiTests : UiTestContext
{
    private CancellationToken Ct => Xunit.TestContext.Current.CancellationToken;

    private async Task<Bag> BagWithBrewsAsync(params int[] ratings)
    {
        var bean = await Services.GetRequiredService<IRepository<Bean>>().SaveAsync(new Bean { Name = "Guji" }, Ct);
        var bag = await Services.GetRequiredService<IRepository<Bag>>().SaveAsync(new Bag { BeanId = bean.Id, WeightG = 250m }, Ct);
        var brews = Services.GetRequiredService<IRepository<Brew>>();
        for (var i = 0; i < ratings.Length; i++)
        {
            await brews.SaveAsync(new Brew
            {
                BagId = bag.Id,
                MethodCode = "v60",
                DoseG = 15m,
                WaterG = 250m,
                GrindSetting = 20m,
                BrewedAt = DateTimeOffset.UtcNow.AddHours(i - 10),
                Rating = ratings[i],
                Taste = ratings[i] < 4 ? new Taste(Acidity: 1) : new Taste(),
            }, Ct);
        }

        return bag;
    }

    [Fact]
    public async Task A_bag_still_being_dialed_in_shows_the_try_count_and_a_next_step()
    {
        var bag = await BagWithBrewsAsync(2, 3);

        var page = Render<BrewHome>(p => p.Add(x => x.BagId, bag.Id));

        page.WaitForElement("#brew-coach");
        page.Find("#brew-coach-progress").TextContent.ShouldContain("try 2");
        page.Find("#brew-coach-apply").GetAttribute("href")!.ShouldEndWith("?apply=primary");
    }

    [Fact]
    public async Task A_dialed_in_bag_says_so()
    {
        var bag = await BagWithBrewsAsync(2, 5);

        var page = Render<BrewHome>(p => p.Add(x => x.BagId, bag.Id));

        page.WaitForElement("#brew-coach").TextContent.ShouldContain("Dialled in");
        page.FindAll("#brew-coach-apply").ShouldBeEmpty();
    }

    [Fact]
    public async Task A_bag_without_brews_has_no_coach()
    {
        var bag = await BagWithBrewsAsync();

        var page = Render<BrewHome>(p => p.Add(x => x.BagId, bag.Id));

        page.FindAll("#brew-coach").ShouldBeEmpty();
    }
}
