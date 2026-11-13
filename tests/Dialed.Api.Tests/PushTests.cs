using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Dialed.Api.Auth;
using Dialed.Api.Friends;
using Dialed.Api.Households;
using Dialed.Api.Push;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Dialed.Api.Tests;

/// <summary>Records what would be pushed instead of calling a push service.</summary>
public sealed class RecordingPushSender : IPushSender
{
    public ConcurrentQueue<(PushSubscription To, PushPayload Payload)> Sent { get; } = new();

    public PushOutcome Outcome { get; set; } = PushOutcome.Sent;

    public bool Throw { get; set; }

    public Task<PushOutcome> SendAsync(PushSubscription subscription, PushPayload payload, CancellationToken cancellationToken = default)
    {
        if (Throw)
        {
            throw new InvalidOperationException("push service down");
        }

        Sent.Enqueue((subscription, payload));
        return Task.FromResult(Outcome);
    }
}

public sealed class PushApp : WebApplicationFactory<Program>
{
    public RecordingPushSender Sender { get; } = new();

    protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder) =>
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<IPushSender>();
            services.AddSingleton<IPushSender>(Sender);
        });
}

/// <summary>Push notifications, part 1 (#100): subscriptions, switches, and the follow and household events.</summary>
public sealed class PushTests(PushApp app) : IClassFixture<PushApp>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed record Person(AppUser User, HttpClient Client);

    private async Task<Person> NewPerson(string name)
    {
        var id = Guid.NewGuid().ToString("N");
        var user = new AppUser(id, $"{id}@example.com", name, HouseholdService.PersonalHouseholdId(id));
        await app.Services.GetRequiredService<IUserDirectory>().AddAsync(user, Ct);
        var tokens = await app.Services.GetRequiredService<RefreshTokenService>().SignInAsync(user, Ct);
        var client = app.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);
        return new Person(user, client);
    }

    private static string Endpoint(string name) => $"https://push.example.com/send/{name}-{Guid.NewGuid():N}";

    private static Task<HttpResponseMessage> Subscribe(Person person, string endpoint, string language = "en") =>
        person.Client.PutAsJsonAsync("/push/subscription", new PushSubscribeRequest(endpoint, "BNcRdreALRFXTkOOUHK1EtK2wtaz5Ry4YfYCA_0QTpQtUbVlUls0VJXg7A8u-Ts1XbjhazAkj7I99e8QcYP7DkM", "tBHItJI5svbpez7KI4CCXg", language), Ct);

    private static async Task<PushSettingsDto> Settings(Person person) => (await person.Client.GetFromJsonAsync<PushSettingsDto>("/push/settings", Ct))!;

    private async Task Settled() => await app.Services.GetRequiredService<PushNotifier>().WaitForIdleAsync();

    private IEnumerable<(PushSubscription To, PushPayload Payload)> SentTo(Person person) => app.Sender.Sent.Where(s => s.To.UserId == person.User.Id);

    private static async Task Follow(Person follower, Person followee)
    {
        var code = (await followee.Client.GetFromJsonAsync<FriendCodeDto>("/friends/code", Ct))!.Code;
        (await follower.Client.PostAsJsonAsync("/friends/follow", new FollowRequest(code), Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    // ---- Subscriptions ------------------------------------------------------------------------

    [Fact]
    public async Task The_public_key_is_available_to_signed_in_people_only()
    {
        var person = await NewPerson("Pia");

        var key = await person.Client.GetFromJsonAsync<PushKeyDto>("/push/key", Ct);

        key!.PublicKey.Length.ShouldBeGreaterThan(40);
        using var anonymous = app.CreateClient();
        (await anonymous.GetAsync("/push/key", Ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task A_device_subscribes_and_unsubscribes()
    {
        var person = await NewPerson("Pia");
        var endpoint = Endpoint("pia");

        (await Subscribe(person, endpoint)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await Settings(person)).Devices.ShouldBe(1);

        (await person.Client.PostAsJsonAsync("/push/unsubscribe", new PushUnsubscribeRequest(endpoint), Ct)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await Settings(person)).Devices.ShouldBe(0);
    }

    [Fact]
    public async Task Subscribing_twice_from_the_same_device_keeps_one_subscription()
    {
        var person = await NewPerson("Pia");
        var endpoint = Endpoint("pia");

        await Subscribe(person, endpoint);
        await Subscribe(person, endpoint, "nl");

        (await Settings(person)).Devices.ShouldBe(1);
    }

    [Fact]
    public async Task Nobody_can_remove_someone_elses_device()
    {
        var owner = await NewPerson("Pia");
        var other = await NewPerson("Quinn");
        var endpoint = Endpoint("pia");
        await Subscribe(owner, endpoint);

        (await other.Client.PostAsJsonAsync("/push/unsubscribe", new PushUnsubscribeRequest(endpoint), Ct)).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        (await Settings(owner)).Devices.ShouldBe(1);
    }

    [Fact]
    public async Task A_device_signed_in_as_someone_else_moves_to_that_person()
    {
        var first = await NewPerson("Pia");
        var second = await NewPerson("Quinn");
        var endpoint = Endpoint("shared-phone");
        await Subscribe(first, endpoint);

        await Subscribe(second, endpoint);

        (await Settings(first)).Devices.ShouldBe(0);
        (await Settings(second)).Devices.ShouldBe(1);
    }

    [Theory]
    [InlineData("http://push.example.com/insecure", "tBHItJI5svbpez7KI4CCXg")]
    [InlineData("not a url", "tBHItJI5svbpez7KI4CCXg")]
    [InlineData("https://push.example.com/ok", "has spaces and <script>")]
    public async Task A_subscription_that_is_not_valid_is_refused(string endpoint, string auth)
    {
        var person = await NewPerson("Pia");

        using var response = await person.Client.PutAsJsonAsync("/push/subscription", new PushSubscribeRequest(endpoint, "BNcRdreALRFXTkOOUHK1EtK2wtaz5Ry4YfYCA_0QTpQtUbVlUls0VJXg7A8u-Ts1XbjhazAkj7I99e8QcYP7DkM", auth, "en"), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await Settings(person)).Devices.ShouldBe(0);
    }

    [Fact]
    public async Task Only_ten_devices_per_person()
    {
        var person = await NewPerson("Pia");
        for (var i = 0; i < 10; i++)
        {
            (await Subscribe(person, Endpoint($"d{i}"))).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        }

        (await Subscribe(person, Endpoint("eleventh"))).StatusCode.ShouldBe(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Switches_start_on_and_change_at_once()
    {
        var person = await NewPerson("Pia");
        (await Settings(person)).ShouldBe(new PushSettingsDto(true, true, 0));

        using var response = await person.Client.PutAsJsonAsync("/push/settings", new PushSettingsRequest(false, true), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await Settings(person)).ShouldBe(new PushSettingsDto(false, true, 0));
    }

    // ---- Follow events ------------------------------------------------------------------------

    [Fact]
    public async Task A_follow_request_reaches_every_device_of_the_person_asked_and_nobody_else()
    {
        var asker = await NewPerson("Ada");
        var asked = await NewPerson("Ben");
        await Subscribe(asked, Endpoint("ben-phone"));
        await Subscribe(asked, Endpoint("ben-tablet"));
        await Subscribe(asker, Endpoint("ada-phone"));

        await Follow(asker, asked);
        await Settled();

        var received = SentTo(asked).ToList();
        received.Count.ShouldBe(2);
        received.ShouldAllBe(r => r.Payload.Body == "Ada wants to follow you." && r.Payload.Url == "people");
        SentTo(asker).ShouldBeEmpty();
    }

    [Fact]
    public async Task The_text_is_in_the_language_the_device_registered_with()
    {
        var asker = await NewPerson("Ada");
        var asked = await NewPerson("Ben");
        await Subscribe(asked, Endpoint("ben"), "nl");

        await Follow(asker, asked);
        await Settled();

        SentTo(asked).ShouldHaveSingleItem().Payload.Body.ShouldBe("Ada wil je volgen.");
    }

    [Fact]
    public async Task Accepting_tells_the_person_who_asked()
    {
        var asker = await NewPerson("Ada");
        var asked = await NewPerson("Ben");
        await Subscribe(asker, Endpoint("ada"));
        await Follow(asker, asked);

        (await asked.Client.PostAsync($"/friends/requests/{asker.User.Id}/accept", null, Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        await Settled();

        SentTo(asker).ShouldHaveSingleItem().Payload.Body.ShouldBe("Ben accepted your follow request.");
    }

    [Fact]
    public async Task Declining_tells_nobody()
    {
        var asker = await NewPerson("Ada");
        var asked = await NewPerson("Ben");
        await Subscribe(asker, Endpoint("ada"));
        await Follow(asker, asked);

        await asked.Client.PostAsync($"/friends/requests/{asker.User.Id}/decline", null, Ct);
        await Settled();

        SentTo(asker).ShouldBeEmpty();
    }

    [Fact]
    public async Task Switching_follow_pushes_off_stops_them()
    {
        var asker = await NewPerson("Ada");
        var asked = await NewPerson("Ben");
        await Subscribe(asked, Endpoint("ben"));
        await asked.Client.PutAsJsonAsync("/push/settings", new PushSettingsRequest(false, true), Ct);

        await Follow(asker, asked);
        await Settled();

        SentTo(asked).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_person_who_blocked_the_asker_hears_nothing()
    {
        var asker = await NewPerson("Ada");
        var asked = await NewPerson("Ben");
        await Subscribe(asked, Endpoint("ben"));
        var code = (await asked.Client.GetFromJsonAsync<FriendCodeDto>("/friends/code", Ct))!.Code;
        await asked.Client.PostAsync($"/friends/blocks/{asker.User.Id}", null, Ct);

        using var response = await asker.Client.PostAsJsonAsync("/friends/follow", new FollowRequest(code), Ct);
        await Settled();

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        SentTo(asked).ShouldBeEmpty();
    }

    [Fact]
    public async Task The_same_request_made_again_is_not_pushed_twice()
    {
        var asker = await NewPerson("Ada");
        var asked = await NewPerson("Ben");
        await Subscribe(asked, Endpoint("ben"));
        await Follow(asker, asked);
        await asker.Client.DeleteAsync($"/friends/following/{asked.User.Id}", Ct);

        await Follow(asker, asked);
        await Settled();

        SentTo(asked).Count().ShouldBe(1);
    }

    // ---- Household events ---------------------------------------------------------------------

    [Fact]
    public async Task Joining_a_household_tells_the_other_members_but_not_the_person_who_joined()
    {
        var owner = await NewPerson("Olga");
        var joiner = await NewPerson("Joe");
        await Subscribe(owner, Endpoint("olga"));
        await Subscribe(joiner, Endpoint("joe"), "nl");
        using var invite = await owner.Client.PostAsync("/households/mine/invites", null, Ct);
        var code = (await invite.Content.ReadFromJsonAsync<InviteDto>(Ct))!.Code;

        (await joiner.Client.PostAsJsonAsync("/households/mine/join", new JoinRequest(code), Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        await Settled();

        var received = SentTo(owner).ShouldHaveSingleItem();
        received.Payload.Body.ShouldBe("Joe joined your household.");
        received.Payload.Url.ShouldBe("settings/household");
        SentTo(joiner).ShouldBeEmpty();
    }

    [Fact]
    public async Task Switching_household_pushes_off_stops_them_without_touching_follow_pushes()
    {
        var owner = await NewPerson("Olga");
        var joiner = await NewPerson("Joe");
        await Subscribe(owner, Endpoint("olga"));
        await owner.Client.PutAsJsonAsync("/push/settings", new PushSettingsRequest(true, false), Ct);
        using var invite = await owner.Client.PostAsync("/households/mine/invites", null, Ct);
        var code = (await invite.Content.ReadFromJsonAsync<InviteDto>(Ct))!.Code;

        await joiner.Client.PostAsJsonAsync("/households/mine/join", new JoinRequest(code), Ct);
        await Follow(joiner, owner);
        await Settled();

        SentTo(owner).ShouldHaveSingleItem().Payload.Url.ShouldBe("people");
    }

    // ---- Content and failure ------------------------------------------------------------------

    [Fact]
    public async Task A_push_has_a_title_a_name_and_a_link_and_nothing_else()
    {
        var asker = await NewPerson("Ada");
        var asked = await NewPerson("Ben");
        await Subscribe(asked, Endpoint("ben"));

        await Follow(asker, asked);
        await Settled();

        var json = JsonSerializer.Serialize(SentTo(asked).Single().Payload);
        json.ShouldNotContain(asker.User.Email!);
        json.ShouldNotContain(asker.User.Id);
        JsonDocument.Parse(json).RootElement.EnumerateObject().Select(p => p.Name).Order().ShouldBe(["Body", "Tag", "Title", "Url"]);
    }

    [Fact]
    public async Task A_subscription_the_push_service_says_is_gone_is_removed()
    {
        var asker = await NewPerson("Ada");
        var asked = await NewPerson("Ben");
        await Subscribe(asked, Endpoint("ben"));
        app.Sender.Outcome = PushOutcome.Gone;
        try
        {
            await Follow(asker, asked);
            await Settled();
        }
        finally
        {
            app.Sender.Outcome = PushOutcome.Sent;
        }

        (await Settings(asked)).Devices.ShouldBe(0);
    }

    [Fact]
    public async Task A_push_service_that_fails_never_fails_the_follow()
    {
        var asker = await NewPerson("Ada");
        var asked = await NewPerson("Ben");
        await Subscribe(asked, Endpoint("ben"));
        app.Sender.Throw = true;
        try
        {
            await Follow(asker, asked);
            await Settled();
        }
        finally
        {
            app.Sender.Throw = false;
        }

        ((await asked.Client.GetFromJsonAsync<PersonDto[]>("/friends/requests", Ct))!).ShouldHaveSingleItem().DisplayName.ShouldBe("Ada");
    }

    [Fact]
    public async Task Deleting_the_account_removes_the_devices()
    {
        var person = await NewPerson("Pia");
        await Subscribe(person, Endpoint("pia"));

        await app.Services.GetRequiredService<IPushStore>().RemoveUserAsync(person.User.Id, Ct);

        (await app.Services.GetRequiredService<IPushStore>().ListAsync(person.User.Id, Ct)).ShouldBeEmpty();
    }
}
