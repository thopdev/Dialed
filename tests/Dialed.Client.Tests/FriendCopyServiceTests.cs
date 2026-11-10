using System.Net;
using System.Text;
using Dialed.Client.Data;
using Dialed.Client.Friends;
using Dialed.Client.Platform;
using Dialed.Client.Sync;
using Dialed.Client.Preferences;
using Dialed.Domain.Model;

namespace Dialed.Client.Tests;

/// <summary>Copying a friend's bean or recipe into my household (#77).</summary>
public sealed class FriendCopyServiceTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly Context context = new();
    private readonly InMemoryRepository<Bean> beans;
    private readonly InMemoryRepository<Recipe> recipes;
    private readonly InMemoryRepository<Vendor> vendors;
    private readonly InMemoryUserPreferences preferences = new();
    private readonly StubServer server = new();

    public FriendCopyServiceTests()
    {
        beans = new(context, TimeProvider.System);
        recipes = new(context, TimeProvider.System);
        vendors = new(context, TimeProvider.System);
    }

    private FriendCopyService Service() =>
        new(new FriendsApi(new HttpClient(server) { BaseAddress = new Uri("https://api.test/") }, new Tokens()), beans, recipes, vendors, preferences);

    private const string KenyaBean = """
        {"item":{"id":"b1","householdId":"theirs","createdBy":"carol","version":4,"name":"Kenya AA","roasterId":"v1","origin":{"country":"Kenya"},
                 "tastingNotes":["blackcurrant"],"photoIds":["p1"],"rating":5,"visibility":2,"deleted":false},
         "ownerName":"Carol","roasterName":"Friedhats Coffee Roasters","beanName":null}
        """;

    [Fact]
    public async Task A_bean_is_copied_with_a_new_roaster_and_remembers_where_it_came_from()
    {
        server.Answer("friends/carol/shared/beans/b1", KenyaBean);

        var result = await Service().CopyAsync("carol", "beans", "b1", Ct);

        result.Outcome.ShouldBe(CopyOutcome.Copied);
        var bean = (await beans.GetAsync(result.Id!, Ct))!;
        bean.Name.ShouldBe("Kenya AA");
        bean.Id.ShouldNotBe("b1");
        bean.HouseholdId.ShouldBe("mine");
        bean.CopiedFrom.ShouldBe(new CopiedFrom("carol", "Carol", "b1"));
        bean.PhotoIds.ShouldBeEmpty();
        bean.Rating.ShouldBeNull();
        var roaster = (await vendors.GetAsync(bean.RoasterId!, Ct))!;
        roaster.Name.ShouldBe("Friedhats Coffee Roasters");
        roaster.Kinds.ShouldBe(VendorKinds.Roaster);
    }

    [Fact]
    public async Task A_roaster_I_already_have_is_reused_even_when_written_a_little_differently()
    {
        var mine = await vendors.SaveAsync(new Vendor { Name = "Friedhats", Kinds = VendorKinds.Roaster }, Ct);
        server.Answer("friends/carol/shared/beans/b1", KenyaBean);

        var result = await Service().CopyAsync("carol", "beans", "b1", Ct);

        (await beans.GetAsync(result.Id!, Ct))!.RoasterId.ShouldBe(mine.Id);
        (await vendors.ListAsync(Ct)).Count.ShouldBe(1);
    }

    [Fact]
    public async Task The_new_bean_follows_my_default_visibility()
    {
        await preferences.SetProfileAsync(UserProfile.Default with { DefaultVisibility = Visibility.Private });
        server.Answer("friends/carol/shared/beans/b1", KenyaBean);

        var result = await Service().CopyAsync("carol", "beans", "b1", Ct);

        (await beans.GetAsync(result.Id!, Ct))!.Visibility.ShouldBe(Visibility.Private);
    }

    [Fact]
    public async Task Copying_the_same_item_twice_makes_one_copy_without_asking_the_server_again()
    {
        server.Answer("friends/carol/shared/beans/b1", KenyaBean);
        var first = await Service().CopyAsync("carol", "beans", "b1", Ct);
        server.Calls.Clear();

        var second = await Service().CopyAsync("carol", "beans", "b1", Ct);

        second.Outcome.ShouldBe(CopyOutcome.AlreadyCopied);
        second.Id.ShouldBe(first.Id);
        server.Calls.ShouldBeEmpty();
        (await beans.ListAsync(Ct)).Count.ShouldBe(1);
    }

    [Fact]
    public async Task A_recipe_links_to_my_bean_of_the_same_name_and_drops_their_gear()
    {
        var mine = await beans.SaveAsync(new Bean { Name = "kenya aa" }, Ct);
        server.Answer("friends/carol/shared/recipes/r1", """
            {"item":{"id":"r1","createdBy":"carol","methodCode":"v60","name":"Morning","beanId":"b1","grinderId":"g1","brewerId":"br1",
                     "doseG":15,"waterG":250,"grindSetting":22,"isFavourite":true,"visibility":2,"deleted":false},
             "ownerName":"Carol","roasterName":null,"beanName":"Kenya AA"}
            """);

        var result = await Service().CopyAsync("carol", "recipes", "r1", Ct);

        result.Outcome.ShouldBe(CopyOutcome.Copied);
        var recipe = (await recipes.GetAsync(result.Id!, Ct))!;
        recipe.BeanId.ShouldBe(mine.Id);
        recipe.GrinderId.ShouldBeNull();
        recipe.BrewerId.ShouldBeNull();
        recipe.IsFavourite.ShouldBeFalse();
        recipe.DoseG.ShouldBe(15m);
        recipe.CopiedFrom!.OwnerName.ShouldBe("Carol");
    }

    [Fact]
    public async Task A_recipe_for_a_bean_I_do_not_have_is_copied_without_a_bean()
    {
        server.Answer("friends/carol/shared/recipes/r1", """{"item":{"id":"r1","methodCode":"v60","name":"Morning","beanId":"b1","deleted":false},"ownerName":"Carol","roasterName":null,"beanName":"Kenya AA"}""");

        var result = await Service().CopyAsync("carol", "recipes", "r1", Ct);

        (await recipes.GetAsync(result.Id!, Ct))!.BeanId.ShouldBeNull();
        (await beans.ListAsync(Ct)).ShouldBeEmpty();
    }

    [Fact]
    public async Task An_item_that_is_gone_is_reported_and_nothing_is_saved()
    {
        var result = await Service().CopyAsync("carol", "beans", "gone", Ct);

        result.Outcome.ShouldBe(CopyOutcome.Unavailable);
        result.Id.ShouldBeNull();
        (await beans.ListAsync(Ct)).ShouldBeEmpty();
    }

    [Fact]
    public async Task Only_beans_and_recipes_can_be_copied()
    {
        await Should.ThrowAsync<ArgumentOutOfRangeException>(() => Service().CopyAsync("carol", "brews", "x", Ct));
    }

    private sealed class Context : ICurrentContext
    {
        public string UserId => "me";

        public string HouseholdId => "mine";
    }

    private sealed class Tokens : IAccessTokenProvider
    {
        public Task<string?> GetAccessTokenAsync(CancellationToken cancellationToken = default) => Task.FromResult<string?>("token");
    }

    private sealed class StubServer : HttpMessageHandler
    {
        private readonly Dictionary<string, string> answers = [];

        public List<string> Calls { get; } = [];

        public void Answer(string path, string json) => answers[path] = json;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath.TrimStart('/');
            Calls.Add(path);
            return Task.FromResult(answers.TryGetValue(path, out var json)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") }
                : new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("{\"detail\":\"Not found.\"}", Encoding.UTF8, "application/json") });
        }
    }
}
