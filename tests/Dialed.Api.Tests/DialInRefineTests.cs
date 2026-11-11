using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Dialed.AI.DialIn;
using Dialed.Api.Ai;
using Dialed.Api.Auth;
using Dialed.Domain.Model;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Dialed.Api.Tests;

public sealed class DialInRefineTests(WebApplicationFactory<Program> factory) : IClassFixture<WebApplicationFactory<Program>>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<HttpClient> SignedIn(WebApplicationFactory<Program> f, string user)
    {
        var client = f.CreateClient();
        using var login = await client.PostAsJsonAsync("/auth/dev", new DevLoginRequest(user), Ct);
        var tokens = (await login.Content.ReadFromJsonAsync<TokenResponse>(Ct))!;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);
        return client;
    }

    private static DialInContext Sour() =>
        new("espresso", new DialInBrewSummary(18m, null, 36m, 12m, 93m, 28, 2, new Taste(Acidity: -2)), GrindMin: 0m, GrindMax: 40m, GrindStep: 1m);

    [Fact]
    public async Task Dial_in_needs_a_signed_in_user()
    {
        using var response = await factory.CreateClient().PostAsJsonAsync("/ai/dial-in", Sour(), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task A_sour_shot_gets_changes_as_from_to_with_the_prompt_version_and_model()
    {
        var client = await SignedIn(factory, "alice");

        using var response = await client.PostAsJsonAsync("/ai/dial-in", Sour(), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var result = (await response.Content.ReadFromJsonAsync<DialInRefinement>(Ct))!;
        result.Changes.Count.ShouldBeInRange(1, 2);
        result.Changes[0].ShouldBe(new DialInChange("GrindFiner", 12m, 11m));
        result.PromptVersion.ShouldBe(RulesDialInRefiner.PromptVersion);
        result.ModelId.ShouldBe(RulesDialInRefiner.ModelId);
    }

    [Fact]
    public async Task The_fake_answer_is_deterministic_and_has_an_english_why_and_a_reason_code()
    {
        var client = await SignedIn(factory, "carol");

        var first = (await (await client.PostAsJsonAsync("/ai/dial-in", Sour(), Ct)).Content.ReadFromJsonAsync<DialInRefinement>(Ct))!;
        var second = (await (await client.PostAsJsonAsync("/ai/dial-in", Sour(), Ct)).Content.ReadFromJsonAsync<DialInRefinement>(Ct))!;

        second.Changes.ShouldBe(first.Changes);
        first.Why.ShouldContain("sour");
        first.ReasonCode.ShouldBe("Sour");
    }

    [Fact]
    public async Task When_the_last_brew_already_ground_finer_without_a_better_cup_it_leads_with_the_other_change()
    {
        var client = await SignedIn(factory, "bob");
        var earlier = new DialInBrewSummary(18m, null, 36m, 13m, 93m, 26, 2, new Taste(Acidity: -2));

        using var response = await client.PostAsJsonAsync("/ai/dial-in", Sour() with { Recent = [earlier] }, Ct);

        var result = (await response.Content.ReadFromJsonAsync<DialInRefinement>(Ct))!;
        result.Changes.ShouldBe([new DialInChange("HotterWater", 93m, 94m)]);
        result.ReasonCode.ShouldBe(RulesDialInRefiner.AlreadyTriedCode);
        result.Why.ShouldContain("did not give a better cup");
    }

    [Fact]
    public async Task An_unknown_method_or_a_missing_dose_is_a_bad_request()
    {
        var client = await SignedIn(factory, "bob");

        (await client.PostAsJsonAsync("/ai/dial-in", Sour() with { MethodCode = "made-up" }, Ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await client.PostAsJsonAsync("/ai/dial-in", Sour() with { Brew = Sour().Brew with { DoseG = 0m } }, Ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task The_kill_switch_and_the_daily_quota_refuse_with_a_clear_code()
    {
        using var off = factory.WithWebHostBuilder(b => b.UseSetting("Ai:Enabled", "false"));
        var client = await SignedIn(off, "carol");
        using var disabled = await client.PostAsJsonAsync("/ai/dial-in", Sour(), Ct);
        disabled.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        (await disabled.Content.ReadAsStringAsync(Ct)).ShouldContain("ai-disabled");

        using var limited = factory.WithWebHostBuilder(b => b.UseSetting("Ai:SuggestionsPerDay", "1"));
        var other = await SignedIn(limited, "alice");
        (await other.PostAsJsonAsync("/ai/dial-in", Sour(), Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        using var second = await other.PostAsJsonAsync("/ai/dial-in", Sour(), Ct);
        second.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
        second.Headers.RetryAfter.ShouldNotBeNull();
    }

    private sealed class BrokenRefiner : IDialInRefiner
    {
        public Task<DialInRefinement> RefineAsync(DialInContext context, CancellationToken cancellationToken = default) => throw new DialInRefinementException("down");
    }

    [Fact]
    public async Task When_the_model_fails_the_answer_is_a_friendly_503_and_the_call_is_not_counted()
    {
        using var broken = factory.WithWebHostBuilder(b => b.UseSetting("Ai:SuggestionsPerDay", "1").ConfigureServices(s => s.AddSingleton<IDialInRefiner, BrokenRefiner>()));
        var client = await SignedIn(broken, "bob");

        using var first = await client.PostAsJsonAsync("/ai/dial-in", Sour(), Ct);
        using var second = await client.PostAsJsonAsync("/ai/dial-in", Sour(), Ct);

        first.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        (await first.Content.ReadAsStringAsync(Ct)).ShouldContain("built-in advice");
        second.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable); // refunded, so still not "quota"
    }
}
