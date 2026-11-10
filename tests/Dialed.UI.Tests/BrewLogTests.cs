using Bunit;
using Dialed.Client.Data;
using Dialed.Domain.DialIn;
using Dialed.Domain.Equipment;
using Dialed.Domain.Inventory;
using Dialed.Domain.Model;
using Dialed.UI.Components;
using Dialed.UI.Pages;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace Dialed.UI.Tests;

public sealed class BrewLogTests : UiTestContext
{
    private static CancellationToken Ct => Xunit.TestContext.Current.CancellationToken;

    private IRepository<Bean> Beans => Services.GetRequiredService<IRepository<Bean>>();

    private IRepository<Bag> Bags => Services.GetRequiredService<IRepository<Bag>>();

    private IRepository<Recipe> Recipes => Services.GetRequiredService<IRepository<Recipe>>();

    private IRepository<Brew> Brews => Services.GetRequiredService<IRepository<Brew>>();

    private async Task<(Bean Bean, Bag Bag)> SomeBag(decimal weight = 250m, BagStatus status = BagStatus.Open)
    {
        var bean = await Beans.SaveAsync(new Bean { Name = "Gesha" }, Ct);
        var bag = await Bags.SaveAsync(new Bag { BeanId = bean.Id, WeightG = weight, Status = status }, Ct);
        return (bean, bag);
    }

    private IRenderedComponent<BrewForm> RenderForm(Brew initial, Action<Brew>? onSubmit = null, IReadOnlyList<EquipmentItem>? gear = null) =>
        Render<BrewForm>(p => p
            .Add(x => x.Initial, initial)
            .Add(x => x.Gear, gear ?? [])
            .Add(x => x.OnSubmit, EventCallback.Factory.Create<Brew>(this, b => onSubmit?.Invoke(b))));

    private static Brew Blank(string method = "v60", decimal dose = 0m) => new() { BagId = "bag", MethodCode = method, DoseG = dose, DrinkType = DrinkTypes.DefaultFor(method) };

    // ---- Form ---------------------------------------------------------------------------------

    [Fact]
    public void Form_requires_a_dose()
    {
        Brew? submitted = null;
        var form = RenderForm(Blank(), b => submitted = b);

        form.Find("form").Submit();

        form.Markup.ShouldContain("Enter the dose.");
        submitted.ShouldBeNull();
    }

    [Fact]
    public void A_new_brew_can_be_shared_with_friends_and_starts_with_the_default_visibility()
    {
        Brew? submitted = null;
        var form = RenderForm(Blank("v60", 15m), b => submitted = b);
        form.Find("#brew-visibility").GetAttribute("value").ShouldBe(nameof(Visibility.Household));

        form.Find("#brew-visibility").Change(nameof(Visibility.Friends));
        form.Find("form").Submit();

        submitted!.Visibility.ShouldBe(Visibility.Friends);
    }

    [Fact]
    public void An_existing_brew_keeps_its_own_visibility()
    {
        Brew? submitted = null;
        var form = RenderForm(Blank("v60", 15m) with { Version = 2, Visibility = Visibility.Private }, b => submitted = b);

        form.Find("#brew-visibility").GetAttribute("value").ShouldBe(nameof(Visibility.Private));
        form.Find("form").Submit();

        submitted!.Visibility.ShouldBe(Visibility.Private);
    }

    [Fact]
    public void Form_is_prefilled_and_saves_without_changes()
    {
        Brew? submitted = null;
        var form = RenderForm(Blank("v60", 15m) with { WaterG = 240m, TempC = 94m, TimeS = 180 }, b => submitted = b);

        form.Find("#brew-dose").GetAttribute("value").ShouldBe("15");
        form.Find("#brew-liquid").GetAttribute("value").ShouldBe("240");
        form.Find("form").Submit();

        submitted.ShouldNotBeNull();
        submitted.DoseG.ShouldBe(15m);
        submitted.WaterG.ShouldBe(240m);
        submitted.TempC.ShouldBe(94m);
        submitted.TimeS.ShouldBe(180);
    }

    [Fact]
    public void Actual_values_can_be_changed_and_feedback_added()
    {
        Brew? submitted = null;
        var form = RenderForm(Blank("espresso", 18m) with { YieldG = 36m }, b => submitted = b);

        form.Find("#brew-dose").Change("18.5");
        form.Find("#brew-liquid").Change("40");
        form.Find("#brew-time").Change("31");
        form.Find("#brew-notes").Change(" a bit harsh ");
        form.Find("[role=radio][aria-label='4 of 5 stars']").Click();
        form.FindAll(".dialed-taste input[type=range]")[0].Change("1");
        form.FindAll(".dialed-taste input[type=range]")[1].Change("-1");

        form.Find("form").Submit();

        submitted!.DoseG.ShouldBe(18.5m);
        submitted.YieldG.ShouldBe(40m);
        submitted.WaterG.ShouldBeNull();
        submitted.TimeS.ShouldBe(31);
        submitted.Rating.ShouldBe(4);
        submitted.Notes.ShouldBe("a bit harsh");
        submitted.Taste.ShouldBe(new Taste(Acidity: 1, Strength: -1));
    }

    [Fact]
    public void Milk_fields_only_appear_for_milk_drinks()
    {
        Brew? submitted = null;
        var form = RenderForm(Blank("espresso", 18m), b => submitted = b);
        form.FindAll("#brew-milk-ml").ShouldBeEmpty();

        form.Find("#brew-drink").Change("Cappuccino");
        form.Find("#brew-milk-ml").Change("120");
        form.Find("#brew-milk-type").Change("Oat");
        form.Find("form").Submit();

        submitted!.DrinkType.ShouldBe(DrinkType.Cappuccino);
        submitted.MilkMl.ShouldBe(120);
        submitted.MilkType.ShouldBe(MilkType.Oat);
    }

    [Fact]
    public void Milk_is_dropped_when_the_drink_has_none()
    {
        Brew? submitted = null;
        var form = RenderForm(Blank("espresso", 18m) with { DrinkType = DrinkType.Latte, MilkMl = 200, MilkType = MilkType.Whole }, b => submitted = b);

        form.Find("#brew-drink").Change("Espresso");
        form.Find("form").Submit();

        submitted!.MilkMl.ShouldBeNull();
        submitted.MilkType.ShouldBeNull();
    }

    [Fact]
    public void Grind_input_follows_the_chosen_grinder()
    {
        var grinder = new EquipmentItem { Id = "g1", Kind = EquipmentKind.Grinder, Brand = "Comandante", GrindScale = GrindScale.Clicks(40m) };
        var form = RenderForm(Blank("v60", 15m) with { GrinderId = "g1", GrindSetting = 22m }, gear: [grinder]);

        form.Find(".dialed-grind-input input").GetAttribute("value").ShouldBe("22");
    }

    // ---- Pages --------------------------------------------------------------------------------

    [Fact]
    public void Brew_tab_without_bags_asks_for_one()
    {
        var page = Render<BrewHome>();

        page.Markup.ShouldContain("No bags to brew from yet");
        page.Find("a[href='bags/new']").ShouldNotBeNull();
    }

    [Fact]
    public async Task Brew_tab_lists_active_bags_with_what_is_left()
    {
        var (_, bag) = await SomeBag();
        await Brews.SaveAsync(new Brew { BagId = bag.Id, MethodCode = "v60", DoseG = 15m, BrewedAt = DateTimeOffset.UtcNow }, Ct);
        var (_, finished) = await SomeBag(status: BagStatus.Finished);

        var page = Render<BrewHome>();

        page.FindAll(".dialed-row-title").Count.ShouldBe(1);
        page.Markup.ShouldContain("235 g left");
    }

    [Fact]
    public async Task Chooser_offers_recipes_for_the_bean_first_and_brewing_without_one()
    {
        var (bean, bag) = await SomeBag();
        await Recipes.SaveAsync(new Recipe { MethodCode = "v60", Name = "Generic" }, Ct);
        var forBean = await Recipes.SaveAsync(new Recipe { MethodCode = "v60", Name = "For Gesha", BeanId = bean.Id }, Ct);

        var page = Render<BrewHome>(p => p.Add(x => x.BagId, bag.Id));

        page.FindAll(".dialed-detail-pane .dialed-row-title").Select(t => t.TextContent).ShouldBe(["For Gesha", "Generic"]);
        page.Find($"a[href='brew/{bag.Id}/log/recipe/{forBean.Id}']").ShouldNotBeNull();
        page.Find("#brew-blank").GetAttribute("href").ShouldBe($"brew/{bag.Id}/log");
        page.FindAll("#brew-again").ShouldBeEmpty();
    }

    [Fact]
    public async Task Chooser_offers_brew_again_after_a_brew()
    {
        var (_, bag) = await SomeBag();
        var brew = await Brews.SaveAsync(new Brew { BagId = bag.Id, MethodCode = "v60", DoseG = 15m, WaterG = 240m, BrewedAt = DateTimeOffset.UtcNow }, Ct);

        var page = Render<BrewHome>(p => p.Add(x => x.BagId, bag.Id));

        page.Find("#brew-again").GetAttribute("href").ShouldBe($"brew/{bag.Id}/log/again/{brew.Id}");
    }

    [Fact]
    public async Task Logging_from_a_recipe_prefills_and_saves_a_brew_that_leaves_the_bag()
    {
        var (_, bag) = await SomeBag();
        var recipe = await Recipes.SaveAsync(new Recipe { MethodCode = "v60", Name = "Morning", DoseG = 15m, WaterG = 240m, TempC = 94m, TimeS = 180 }, Ct);
        var page = Render<BrewLog>(p => p.Add(x => x.BagId, bag.Id).Add(x => x.RecipeId, recipe.Id));

        page.Find("#brew-dose").GetAttribute("value").ShouldBe("15");
        page.Find("form").Submit();

        var saved = (await Brews.ListAsync(Ct)).Single();
        saved.BagId.ShouldBe(bag.Id);
        saved.RecipeId.ShouldBe(recipe.Id);
        saved.DoseG.ShouldBe(15m);
        saved.WaterG.ShouldBe(240m);
        Services.GetRequiredService<NavigationManager>().Uri.ShouldEndWith($"brew/{bag.Id}");

        var bagPage = Render<Bags>(p => p.Add(x => x.Id, bag.Id));
        bagPage.Markup.ShouldContain("235 g of 250 g left");
    }

    [Fact]
    public async Task Repeat_brew_takes_one_save_after_choosing_brew_again()
    {
        var (_, bag) = await SomeBag();
        var previous = await Brews.SaveAsync(new Brew
        {
            BagId = bag.Id, MethodCode = "espresso", DoseG = 18m, YieldG = 36m, Rating = 5, Notes = "great",
            Taste = new Taste(Acidity: 2), BrewedAt = DateTimeOffset.UtcNow.AddDays(-1),
        }, Ct);
        var page = Render<BrewLog>(p => p.Add(x => x.BagId, bag.Id).Add(x => x.BrewId, previous.Id));

        page.Find("form").Submit();

        var all = await Brews.ListAsync(Ct);
        all.Count.ShouldBe(2);
        var again = all.Single(b => b.Id != previous.Id);
        again.DoseG.ShouldBe(18m);
        again.YieldG.ShouldBe(36m);
        again.Rating.ShouldBeNull();
        again.Notes.ShouldBeNull();
        again.Taste.IsBalanced.ShouldBeTrue();
    }

    [Fact]
    public async Task Applying_a_dial_in_suggestion_fills_the_next_brew_with_the_changed_value()
    {
        var (_, bag) = await SomeBag();
        var previous = await Brews.SaveAsync(new Brew
        {
            BagId = bag.Id, MethodCode = "espresso", DoseG = 18m, YieldG = 36m, Rating = 2,
            Taste = new Taste(Strength: -1), BrewedAt = DateTimeOffset.UtcNow.AddDays(-1),
        }, Ct);
        Services.GetRequiredService<NavigationManager>().NavigateTo($"/brew/{bag.Id}/log/again/{previous.Id}?apply=primary");
        var page = Render<BrewLog>(p => p.Add(x => x.BagId, bag.Id).Add(x => x.BrewId, previous.Id));

        page.Find("#brew-advice-applied").TextContent.ShouldBe("Applied from your last brew: Use more coffee.");
        page.Find("#brew-dose").GetAttribute("value").ShouldBe("18.5");
        page.Find("form").Submit();

        (await Brews.ListAsync(Ct)).Single(b => b.Id != previous.Id).DoseG.ShouldBe(18.5m);
    }

    [Fact]
    public async Task The_brew_made_with_an_applied_suggestion_is_linked_to_it_with_its_rating()
    {
        var (_, bag) = await SomeBag();
        var previous = await Brews.SaveAsync(new Brew { BagId = bag.Id, MethodCode = "espresso", DoseG = 18m, YieldG = 36m, Rating = 2, Taste = new Taste(Strength: -1), BrewedAt = DateTimeOffset.UtcNow.AddDays(-1) }, Ct);
        var feedback = Services.GetRequiredService<IRepository<DialInFeedback>>();
        var applied = await feedback.SaveAsync(new DialInFeedback { BrewId = previous.Id, Kind = SuggestionKind.IncreaseDose, Outcome = DialInOutcome.Applied, PromptVersion = "rules-1", RatingBefore = 2 }, Ct);
        Services.GetRequiredService<NavigationManager>().NavigateTo($"/brew/{bag.Id}/log/again/{previous.Id}?apply=primary");
        var page = Render<BrewLog>(p => p.Add(x => x.BagId, bag.Id).Add(x => x.BrewId, previous.Id));

        page.Find("[aria-label='4 of 5 stars']").Click();
        page.Find("form").Submit();

        var next = (await Brews.ListAsync(Ct)).Single(b => b.Id != previous.Id);
        var linked = (await feedback.ListAsync(Ct)).Single(f => f.Id == applied.Id);
        linked.NextBrewId.ShouldBe(next.Id);
        linked.RatingBefore.ShouldBe(2);
        linked.RatingAfter.ShouldBe(4);
    }

    [Fact]
    public async Task A_plain_brew_again_does_not_touch_the_feedback()
    {
        var (_, bag) = await SomeBag();
        var previous = await Brews.SaveAsync(new Brew { BagId = bag.Id, MethodCode = "espresso", DoseG = 18m, YieldG = 36m, BrewedAt = DateTimeOffset.UtcNow.AddDays(-1) }, Ct);
        var feedback = Services.GetRequiredService<IRepository<DialInFeedback>>();
        await feedback.SaveAsync(new DialInFeedback { BrewId = previous.Id, Kind = SuggestionKind.IncreaseDose, Outcome = DialInOutcome.Applied, PromptVersion = "rules-1" }, Ct);

        Render<BrewLog>(p => p.Add(x => x.BagId, bag.Id).Add(x => x.BrewId, previous.Id)).Find("form").Submit();

        (await feedback.ListAsync(Ct)).Single().NextBrewId.ShouldBeNull();
    }

    [Fact]
    public async Task Brew_again_without_an_applied_suggestion_keeps_the_values_of_the_last_brew()
    {
        var (_, bag) = await SomeBag();
        var previous = await Brews.SaveAsync(new Brew { BagId = bag.Id, MethodCode = "espresso", DoseG = 18m, YieldG = 36m, Taste = new Taste(Strength: -1), BrewedAt = DateTimeOffset.UtcNow.AddDays(-1) }, Ct);

        var page = Render<BrewLog>(p => p.Add(x => x.BagId, bag.Id).Add(x => x.BrewId, previous.Id));

        page.FindAll("#brew-advice-applied").ShouldBeEmpty();
        page.Find("#brew-dose").GetAttribute("value").ShouldBe("18");
    }

    [Fact]
    public async Task Brewing_the_bag_empty_offers_to_finish_it()
    {
        var (_, bag) = await SomeBag(weight: 30m);
        await Brews.SaveAsync(new Brew { BagId = bag.Id, MethodCode = "v60", DoseG = 30m, BrewedAt = DateTimeOffset.UtcNow }, Ct);

        var page = Render<Bags>(p => p.Add(x => x.Id, bag.Id));

        page.Find("#bag-offer-finish").TextContent.ShouldContain("Mark it finished?");
    }

    [Fact]
    public void Unknown_bag_shows_a_not_found_message()
    {
        Render<BrewLog>(p => p.Add(x => x.BagId, "nope")).Markup.ShouldContain("no longer exists");
    }
}
