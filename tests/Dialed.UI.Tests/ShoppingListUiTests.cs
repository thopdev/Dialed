using Bunit;
using Dialed.Client.Data;
using Dialed.Domain.Inventory;
using Dialed.Domain.Model;
using Dialed.UI.Pages;
using Microsoft.Extensions.DependencyInjection;

namespace Dialed.UI.Tests;

public sealed class ShoppingListUiTests : UiTestContext
{
    private static CancellationToken Ct => Xunit.TestContext.Current.CancellationToken;

    private async Task LowBag(string name)
    {
        var bean = await Services.GetRequiredService<IRepository<Bean>>().SaveAsync(new Bean { Name = name }, Ct);
        await Services.GetRequiredService<IRepository<Bag>>().SaveAsync(new Bag { BeanId = bean.Id, WeightG = 60m, Status = BagStatus.Open }, Ct);
    }

    [Fact]
    public async Task The_inventory_lists_what_to_buy_and_shares_it_as_text()
    {
        await LowBag("Guji");
        var page = Render<Stock>();

        var row = page.WaitForElement("#stock-shopping .dialed-row");
        row.TextContent.ShouldContain("Guji");
        row.TextContent.ShouldContain("Running low");
        page.Find("#stock-share-list").Click();

        var shared = Shares.Shared.ShouldHaveSingleItem();
        shared.Text.ShouldBe("To buy\n- Guji (60 g)");
        page.FindAll("#stock-list-text").ShouldBeEmpty();
    }

    [Fact]
    public async Task Without_a_share_sheet_the_text_is_shown_to_copy()
    {
        Shares.Supported = false;
        await LowBag("Guji");
        var page = Render<Stock>();
        page.WaitForElement("#stock-share-list").Click();

        page.Find("#stock-list-text").TextContent.ShouldContain("- Guji (60 g)");
    }

    [Fact]
    public async Task Nothing_to_buy_shows_no_list()
    {
        var bean = await Services.GetRequiredService<IRepository<Bean>>().SaveAsync(new Bean { Name = "Guji" }, Ct);
        await Services.GetRequiredService<IRepository<Bag>>().SaveAsync(new Bag { BeanId = bean.Id, WeightG = 250m, Status = BagStatus.Sealed }, Ct);

        var page = Render<Stock>();

        page.WaitForElement("[data-group=sealed]");
        page.FindAll("#stock-shopping").ShouldBeEmpty();
    }
}
