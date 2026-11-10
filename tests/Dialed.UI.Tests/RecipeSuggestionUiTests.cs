using Bunit;
using Dialed.Client.Data;
using Dialed.Client.Recipes;
using Dialed.Domain.Equipment;
using Dialed.Domain.Model;
using Dialed.UI.Components;
using Dialed.UI.Pages;
using Microsoft.Extensions.DependencyInjection;

namespace Dialed.UI.Tests;

/// <summary>"Suggest a recipe" on the bean page (#87).</summary>
public sealed class RecipeSuggestionUiTests : UiTestContext
{
    private static CancellationToken Ct => Xunit.TestContext.Current.CancellationToken;

    private IRepository<Bean> Beans => Services.GetRequiredService<IRepository<Bean>>();

    private IRepository<Bag> Bags => Services.GetRequiredService<IRepository<Bag>>();

    private IRepository<Recipe> Recipes => Services.GetRequiredService<IRepository<Recipe>>();

    private IRepository<Brew> Brews => Services.GetRequiredService<IRepository<Brew>>();

    private IRepository<EquipmentItem> Gear => Services.GetRequiredService<IRepository<EquipmentItem>>();

    private async Task<Bean> Natural() =>
        await Beans.SaveAsync(new Bean { Name = "Guji", Origin = new BeanOrigin("Ethiopia"), Process = "Natural", RoastLevel = RoastLevel.Light, Varieties = ["Heirloom"] }, Ct);

    private IRenderedComponent<RecipeSuggestionPanel> Panel(Bean bean) => Render<RecipeSuggestionPanel>(p => p.Add(x => x.Bean, bean));

    [Fact]
    public async Task The_bean_page_offers_the_panel()
    {
        var bean = await Natural();

        var page = Render<Beans>(p => p.Add(x => x.Id, bean.Id));

        page.Find("#suggest #suggest-go").TextContent.ShouldBe("Suggest a recipe");
    }

    [Fact]
    public async Task A_suggestion_shows_each_number_with_its_reason()
    {
        var panel = Panel(await Natural());

        panel.Find("#suggest-go").Click();

        panel.FindAll("#suggest-result .dialed-suggest-row").Select(r => r.GetAttribute("data-parameter")).ShouldBe(["dose", "water", "grind", "temperature", "time"]);
        panel.Find("[data-parameter=dose] strong").TextContent.ShouldBe("15 g");
        panel.Find("[data-parameter=temperature] strong").TextContent.ShouldBe("94 °C");
        panel.Find("[data-parameter=time] strong").TextContent.ShouldBe("3:00");
        panel.Find("[data-parameter=temperature] .dialed-muted").TextContent.ShouldBe("A classic starting point for this method.");
        panel.Find("#suggest-go").TextContent.ShouldBe("Suggest again");
    }

    [Fact]
    public async Task The_request_carries_the_bean_attributes_days_since_roast_and_the_grinders_scale()
    {
        var bean = await Natural();
        await Bags.SaveAsync(new Bag { BeanId = bean.Id, WeightG = 250m, RoastDate = DateOnly.FromDateTime(DateTime.Today.AddDays(-6)) }, Ct);
        var grinder = await Gear.SaveAsync(new EquipmentItem { Kind = EquipmentKind.Grinder, Brand = "Eureka", Model = "Mignon", GrindScale = GrindScale.Clicks(40) }, Ct);
        var panel = Panel(bean);

        panel.Find("#suggest-grinder").Change(grinder.Id);
        panel.Find("#suggest-method").Change("espresso");
        panel.Find("#suggest-go").Click();

        var request = Assistant.Requests.Single();
        request.MethodCode.ShouldBe("espresso");
        request.BeanOrigin.ShouldBe("Ethiopia");
        request.BeanProcess.ShouldBe("Natural");
        request.RoastLevel.ShouldBe("Light");
        request.Varieties.ShouldBe("Heirloom");
        request.DaysSinceRoast.ShouldBe(6);
        request.GrinderName.ShouldBe("Eureka Mignon");
        (request.GrindMin, request.GrindMax, request.GrindStep).ShouldBe((0m, 40m, 1m));
        request.Similar.ShouldBeNull();
    }

    [Fact]
    public async Task The_best_recipe_for_a_similar_bean_goes_in_with_its_average_rating()
    {
        var bean = await Natural();
        var other = await Beans.SaveAsync(new Bean { Name = "Hambela", Origin = new BeanOrigin("Ethiopia"), Process = "Natural" }, Ct);
        var recipe = await Recipes.SaveAsync(new Recipe { MethodCode = "espresso", Name = "House", BeanId = other.Id, DoseG = 18.5m, YieldG = 38m, GrindSetting = 12m, TempC = 94m, TimeS = 29 }, Ct);
        var bag = await Bags.SaveAsync(new Bag { BeanId = other.Id, WeightG = 250m }, Ct);
        await Brews.SaveAsync(new Brew { BagId = bag.Id, MethodCode = "espresso", DoseG = 18.5m, RecipeId = recipe.Id, Rating = 4, BrewedAt = DateTimeOffset.UtcNow }, Ct);
        await Brews.SaveAsync(new Brew { BagId = bag.Id, MethodCode = "espresso", DoseG = 18.5m, RecipeId = recipe.Id, Rating = 5, BrewedAt = DateTimeOffset.UtcNow }, Ct);
        var panel = Panel(bean);

        panel.Find("#suggest-method").Change("espresso");
        panel.Find("#suggest-go").Click();

        var similar = Assistant.Requests.Single().Similar!;
        (similar.DoseG, similar.YieldG, similar.GrindSetting, similar.TempC, similar.TimeS, similar.AverageRating).ShouldBe((18.5m, 38m, 12m, 94m, 29, 4.5));
    }

    [Fact]
    public async Task Saving_creates_a_recipe_for_the_bean_with_the_prompt_version()
    {
        var bean = await Natural();
        var panel = Panel(bean);
        panel.Find("#suggest-go").Click();

        panel.Find("#suggest-save").Click();

        var saved = (await Recipes.ListAsync(Ct)).Single();
        (saved.BeanId, saved.DoseG, saved.WaterG, saved.GrindSetting, saved.TempC, saved.TimeS, saved.SuggestedBy).ShouldBe((bean.Id, 15m, 250m, 20m, 94m, 180, "recipe-rules-1"));
        saved.Name.ShouldBe("Guji · " + Dialed.Domain.BrewMethods.BrewMethodCatalog.Default.Methods[0].Name);
        panel.Find("#suggest-open").GetAttribute("href").ShouldBe($"recipes/{saved.Id}");
        panel.FindAll("#suggest-save").ShouldBeEmpty();
    }

    [Theory]
    [InlineData(AssistantProblem.QuotaUsed, "all suggestions for today")]
    [InlineData(AssistantProblem.Disabled, "switched off")]
    [InlineData(AssistantProblem.Offline, "offline")]
    [InlineData(AssistantProblem.SignedOut, "sign in")]
    [InlineData(AssistantProblem.Unavailable, "No suggestion right now")]
    public async Task A_problem_is_explained_and_leaves_no_result(AssistantProblem problem, string text)
    {
        Assistant.Problem = problem;
        var panel = Panel(await Natural());

        panel.Find("#suggest-go").Click();

        panel.Find("#suggest-error").TextContent.ShouldContain(text);
        panel.Find("#suggest-error").GetAttribute("role").ShouldBe("alert");
        panel.FindAll("#suggest-result").ShouldBeEmpty();
    }

    [Fact]
    public async Task The_model_s_own_sentence_is_shown_instead_of_a_code()
    {
        Assistant.Result = Assistant.Result with { Reasons = [new("dose", "Baseline", "Natural Ethiopians like a little more coffee.")] };
        var panel = Panel(await Natural());

        panel.Find("#suggest-go").Click();

        panel.Find("[data-parameter=dose] .dialed-muted").TextContent.ShouldBe("Natural Ethiopians like a little more coffee.");
    }
}
