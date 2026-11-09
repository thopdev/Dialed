using Bunit;
using Dialed.Client.Data;
using Dialed.Domain.Model;
using Dialed.UI.Pages;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.Extensions.DependencyInjection;

namespace Dialed.UI.Tests;

public sealed class ImportTests : UiTestContext
{
    private const string Csv = "Name,Roaster,Weight,Price\nGuji,Friedhats,250,\"12,50\"\nHuila,Friedhats,,\n";

    private IRenderedComponent<Import> Upload(string csv)
    {
        var page = Render<Import>();
        page.FindComponent<InputFile>().UploadFiles(InputFileContent.FromString(csv, "beans.csv"));
        return page;
    }

    [Fact]
    public void A_file_is_previewed_with_detected_columns()
    {
        var page = Upload(Csv);

        page.WaitForAssertion(() => page.Find("#import-counts").TextContent.ShouldContain("2 beans will be added, 1 of them with a bag"));
        page.Find("#map-Name").GetAttribute("value").ShouldBe("0");
        page.Find("#map-Roaster").GetAttribute("value").ShouldBe("1");
    }

    [Fact]
    public async Task Importing_saves_beans_bags_and_one_new_roaster()
    {
        var page = Upload(Csv);
        page.WaitForAssertion(() => page.Find("#import-run").HasAttribute("disabled").ShouldBeFalse());

        page.Find("#import-run").Click();

        page.WaitForAssertion(() => page.Find("#import-done").TextContent.ShouldContain("Imported 2 beans, 1 bags and 1 new roasters"));
        var ct = Xunit.TestContext.Current.CancellationToken;
        (await Services.GetRequiredService<IRepository<Bean>>().ListAsync(ct)).Count.ShouldBe(2);
        (await Services.GetRequiredService<IRepository<Bag>>().ListAsync(ct)).ShouldHaveSingleItem().WeightG.ShouldBe(250m);
        (await Services.GetRequiredService<IRepository<Vendor>>().ListAsync(ct)).ShouldHaveSingleItem().Name.ShouldBe("Friedhats");
    }

    [Fact]
    public void A_file_without_rows_is_refused()
    {
        var page = Upload("Name\n");

        page.WaitForAssertion(() => page.Find("#import-error").TextContent.ShouldContain("no rows"));
    }
}
