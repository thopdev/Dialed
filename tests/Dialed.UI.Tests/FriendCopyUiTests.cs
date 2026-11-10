using System.Net;
using Bunit;
using Dialed.Client.Account;
using Dialed.Client.Data;
using Dialed.Domain.Model;
using Dialed.UI.Pages;
using Microsoft.Extensions.DependencyInjection;

namespace Dialed.UI.Tests;

/// <summary>Copy a friend's bean or recipe to my household from their profile (#77).</summary>
public sealed class FriendCopyUiTests : UiTestContext
{
    private static CancellationToken Ct => Xunit.TestContext.Current.CancellationToken;

    private const string Profile = "{\"userId\":\"u3\",\"displayName\":\"Alice\",\"beans\":1,\"recipes\":1,\"brews\":1,\"followers\":0}";

    private static HttpResponseMessage Shared(params string[] names) =>
        TestFriendsServer.Json($"{{\"items\":[{string.Join(",", names.Select(n => $"{{\"id\":\"{n}-id\",\"name\":\"{n}\",\"methodCode\":\"v60\"}}"))}],\"total\":{names.Length},\"hasMore\":false}}");

    private const string BeanItem = """{"item":{"id":"Gesha-id","name":"Gesha","roasterId":"v","deleted":false,"visibility":2},"ownerName":"Alice","roasterName":"Friedhats","beanName":null}""";

    private async Task SignIn()
    {
        Tokens.Value = "tkn";
        await Services.GetRequiredService<AuthSession>().SetAsync(new SessionTokens("a", DateTimeOffset.MaxValue, "r", "me", "Thomas", "h-me"), Ct);
    }

    private void Script(Func<string, HttpResponseMessage?>? extra = null) =>
        FriendsServer.Respond = call => call switch
        {
            "GET friends/u3/profile" => TestFriendsServer.Json(Profile),
            "GET friends/u3/shared/beans?skip=0&pageSize=20" => Shared("Gesha"),
            "GET friends/u3/shared/recipes?skip=0&pageSize=20" => Shared("Morning"),
            "GET friends/u3/shared/brews?skip=0&pageSize=20" => Shared("Brew"),
            _ => extra?.Invoke(call),
        };

    private IRenderedComponent<FriendProfile> Open()
    {
        var page = Render<FriendProfile>(p => p.Add(x => x.UserId, "u3"));
        page.WaitForAssertion(() => page.FindAll(".friend-row").Count.ShouldBeGreaterThan(0));
        return page;
    }

    [Fact]
    public async Task Beans_and_recipes_get_a_Copy_button_named_for_the_item_and_brews_do_not()
    {
        Script();
        await SignIn();
        var page = Open();

        var button = page.Find(".friend-copy");
        button.TextContent.Trim().ShouldBe("Copy");
        button.GetAttribute("aria-label").ShouldBe("Copy Gesha to my household");

        page.Find("#friend-tab-recipes").Click();
        page.WaitForAssertion(() => page.Find(".friend-copy").GetAttribute("aria-label").ShouldBe("Copy Morning to my household"));

        page.Find("#friend-tab-brews").Click();
        page.WaitForAssertion(() => page.Find(".friend-item").TextContent.ShouldBe("Brew"));
        page.FindAll(".friend-copy").ShouldBeEmpty();
    }

    [Fact]
    public async Task Copying_a_bean_saves_it_in_my_household_and_links_to_it()
    {
        Script(call => call == "GET friends/u3/shared/beans/Gesha-id" ? TestFriendsServer.Json(BeanItem) : null);
        await SignIn();
        var page = Open();

        page.Find(".friend-copy").Click();

        page.WaitForAssertion(() => page.Find("#friend-copy-message").TextContent.ShouldContain("Copied “Gesha” to your household."));
        var saved = (await Services.GetRequiredService<IRepository<Bean>>().ListAsync(Ct)).Single();
        saved.Name.ShouldBe("Gesha");
        saved.CopiedFrom!.OwnerName.ShouldBe("Alice");
        page.Find("#friend-copy-message a").GetAttribute("href").ShouldBe($"beans/{saved.Id}");
        page.FindAll(".friend-copy").ShouldBeEmpty();
        page.Find(".friend-copied").TextContent.ShouldBe("In your household");
        page.Find(".friend-copied").GetAttribute("href").ShouldBe($"beans/{saved.Id}");
        (await Services.GetRequiredService<IRepository<Vendor>>().ListAsync(Ct)).Single().Name.ShouldBe("Friedhats");
    }

    [Fact]
    public async Task An_item_I_already_copied_shows_In_your_household_from_the_start()
    {
        Script();
        await SignIn();
        var saved = await Services.GetRequiredService<IRepository<Bean>>().SaveAsync(new Bean { Name = "Gesha", CopiedFrom = new CopiedFrom("u3", "Alice", "Gesha-id") }, Ct);

        var page = Open();

        page.FindAll(".friend-copy").ShouldBeEmpty();
        page.Find(".friend-copied").GetAttribute("href").ShouldBe($"beans/{saved.Id}");
    }

    [Fact]
    public async Task A_copy_that_is_no_longer_shared_says_so_and_saves_nothing()
    {
        Script(call => call == "GET friends/u3/shared/beans/Gesha-id" ? TestFriendsServer.Json("{\"detail\":\"Not found.\"}", HttpStatusCode.NotFound) : null);
        await SignIn();
        var page = Open();

        page.Find(".friend-copy").Click();

        page.WaitForAssertion(() => page.Find("#friend-copy-message").TextContent.ShouldContain("no longer shared"));
        (await Services.GetRequiredService<IRepository<Bean>>().ListAsync(Ct)).ShouldBeEmpty();
        page.FindAll(".friend-copy").Count.ShouldBe(1);
    }

    [Fact]
    public async Task Copying_while_offline_explains_and_keeps_the_button()
    {
        Script(call => call == "GET friends/u3/shared/beans/Gesha-id" ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) : null);
        await SignIn();
        var page = Open();

        page.Find(".friend-copy").Click();

        page.WaitForAssertion(() => page.Find("#friend-copy-message").TextContent.ShouldNotBeEmpty());
        (await Services.GetRequiredService<IRepository<Bean>>().ListAsync(Ct)).ShouldBeEmpty();
        page.FindAll(".friend-copy").Count.ShouldBe(1);
    }

    [Fact]
    public async Task A_copied_bean_and_recipe_say_who_they_came_from()
    {
        await SignIn();
        var bean = await Services.GetRequiredService<IRepository<Bean>>().SaveAsync(new Bean { Name = "Gesha", CopiedFrom = new CopiedFrom("u3", "Alice", "x") }, Ct);
        var recipe = await Services.GetRequiredService<IRepository<Recipe>>().SaveAsync(new Recipe { MethodCode = "v60", Name = "Morning", CopiedFrom = new CopiedFrom("u3", "Alice", "y") }, Ct);

        Render<Beans>(p => p.Add(x => x.Id, bean.Id)).Find("#bean-copied-from").TextContent.ShouldBe("Copied from Alice");
        Render<Recipes>(p => p.Add(x => x.Id, recipe.Id)).Find("#recipe-copied-from").TextContent.ShouldBe("Copied from Alice");
    }
}
