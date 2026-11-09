using System.Net;
using System.Text;
using Dialed.Client.Account;
using Dialed.Client.Data;
using Dialed.Client.Platform;
using Dialed.Client.Sync;
using Dialed.Domain.Model;

namespace Dialed.Client.Tests;

public sealed class AccountTests
{
    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class Stub(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<string> Paths { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Paths.Add(request.RequestUri!.AbsolutePath.TrimStart('/'));
            return Task.FromResult(respond(request));
        }
    }

    private sealed class Passkeys(PasskeyOutcome outcome) : IPasskeyClient
    {
        public Task<bool> IsSupportedAsync() => Task.FromResult(true);

        public Task<bool> IsAutofillSupportedAsync() => Task.FromResult(false);

        public Task<PasskeyOutcome> CreateAsync(string optionsJson) => Task.FromResult(outcome);

        public Task<PasskeyOutcome> GetAsync(string optionsJson, bool autofill = false) => Task.FromResult(outcome);

        public Task CancelPendingAsync() => Task.CompletedTask;
    }

    private sealed class Network : IConnectivity
    {
        public bool IsOnline => true;

        public event Action<bool>? Changed
        {
            add { }
            remove { }
        }
    }

    private sealed class Lifecycle : IAppLifecycle
    {
        public bool IsForeground => true;

        public event Action? Resumed
        {
            add { }
            remove { }
        }

        public event Action? Paused
        {
            add { }
            remove { }
        }
    }

    private static CancellationToken Ct => Xunit.TestContext.Current.CancellationToken;

    private static HttpResponseMessage Json(HttpStatusCode status, string json) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static string TokensJson(string access, string refresh, DateTimeOffset expires) =>
        $$"""{"accessToken":"{{access}}","accessTokenExpiresAt":"{{expires:O}}","refreshToken":"{{refresh}}","userId":"u1","displayName":"Thomas","householdId":"h-u1"}""";

    private static SessionTokens Tokens(DateTimeOffset expires, string refresh = "r1") => new("old", expires, refresh, "u1", "Thomas", "h-u1");

    // ---- AccountApi / token provider ------------------------------------------------------------

    [Fact]
    public async Task Refresh_returns_null_when_the_token_is_no_longer_valid()
    {
        var api = new AccountApi(new HttpClient(new Stub(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized))) { BaseAddress = new Uri("https://api.test/") });
        (await api.RefreshAsync("x", Ct)).ShouldBeNull();
    }

    [Fact]
    public async Task Server_refusals_carry_the_reason()
    {
        var api = new AccountApi(new HttpClient(new Stub(_ => Json(HttpStatusCode.Conflict, """{"detail":"Already registered."}"""))) { BaseAddress = new Uri("https://api.test/") });

        var error = await Should.ThrowAsync<AccountRefusedException>(() => api.SignUpOptionsAsync("a@b.c", "A", cancellationToken: Ct));

        error.Message.ShouldBe("Already registered.");
        error.Status.ShouldBe(409);
    }

    [Fact]
    public async Task A_valid_access_token_is_used_as_it_is()
    {
        var time = new Clock(new DateTimeOffset(2026, 11, 8, 12, 0, 0, TimeSpan.Zero));
        var stub = new Stub(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError));
        var session = new AuthSession(new InMemorySessionStore());
        await session.SetAsync(Tokens(time.Now.AddMinutes(20)), Ct);
        var provider = new SessionTokenProvider(session, new AccountApi(new HttpClient(stub) { BaseAddress = new Uri("https://api.test/") }), time);

        (await provider.GetAccessTokenAsync(Ct)).ShouldBe("old");
        stub.Paths.ShouldBeEmpty();
    }

    [Fact]
    public async Task An_expiring_access_token_is_renewed_and_kept()
    {
        var time = new Clock(new DateTimeOffset(2026, 11, 8, 12, 0, 0, TimeSpan.Zero));
        var stub = new Stub(_ => Json(HttpStatusCode.OK, TokensJson("new", "r2", time.Now.AddMinutes(30))));
        var session = new AuthSession(new InMemorySessionStore());
        await session.SetAsync(Tokens(time.Now.AddSeconds(20)), Ct);
        var provider = new SessionTokenProvider(session, new AccountApi(new HttpClient(stub) { BaseAddress = new Uri("https://api.test/") }), time);

        (await provider.GetAccessTokenAsync(Ct)).ShouldBe("new");
        session.Tokens!.RefreshToken.ShouldBe("r2");
    }

    [Fact]
    public async Task A_refused_refresh_signs_the_person_out()
    {
        var time = new Clock(new DateTimeOffset(2026, 11, 8, 12, 0, 0, TimeSpan.Zero));
        var session = new AuthSession(new InMemorySessionStore());
        await session.SetAsync(Tokens(time.Now.AddSeconds(-5)), Ct);
        var provider = new SessionTokenProvider(session, new AccountApi(new HttpClient(new Stub(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized))) { BaseAddress = new Uri("https://api.test/") }), time);

        (await provider.GetAccessTokenAsync(Ct)).ShouldBeNull();
        session.IsSignedIn.ShouldBeFalse();
    }

    [Fact]
    public async Task Renewal_that_cannot_reach_the_server_keeps_the_sign_in_and_stops_the_sync()
    {
        var time = new Clock(new DateTimeOffset(2026, 11, 8, 12, 0, 0, TimeSpan.Zero));
        var session = new AuthSession(new InMemorySessionStore());
        await session.SetAsync(Tokens(time.Now.AddSeconds(-5)), Ct);
        var provider = new SessionTokenProvider(session, new AccountApi(new HttpClient(new Stub(_ => throw new HttpRequestException("offline"))) { BaseAddress = new Uri("https://api.test/") }), time);

        await Should.ThrowAsync<SyncTransportException>(() => provider.GetAccessTokenAsync(Ct));
        session.IsSignedIn.ShouldBeTrue();
    }

    [Fact]
    public async Task No_session_means_no_token()
    {
        var session = new AuthSession(new InMemorySessionStore());
        var provider = new SessionTokenProvider(session, new AccountApi(new HttpClient(new Stub(_ => new HttpResponseMessage())) { BaseAddress = new Uri("https://api.test/") }), TimeProvider.System);
        (await provider.GetAccessTokenAsync(Ct)).ShouldBeNull();
    }

    [Fact]
    public async Task The_session_is_picked_up_from_the_store_at_start()
    {
        var store = new InMemorySessionStore();
        await store.SaveAsync(Tokens(DateTimeOffset.MaxValue), Ct);
        var session = new AuthSession(store);

        await session.InitializeAsync(Ct);

        session.IsSignedIn.ShouldBeTrue();
        session.DisplayName.ShouldBe("Thomas");
    }

    [Fact]
    public async Task Records_use_the_signed_in_household_and_fall_back_to_local()
    {
        var session = new AuthSession(new InMemorySessionStore());
        var context = new SessionContext(session);
        context.HouseholdId.ShouldBe("local-household");

        await session.SetAsync(Tokens(DateTimeOffset.MaxValue), Ct);

        context.HouseholdId.ShouldBe("h-u1");
        context.UserId.ShouldBe("u1");
    }

    // ---- Account service ---------------------------------------------------------------------------

    private sealed class Fixture
    {
        public Fixture(PasskeyOutcome outcome, Func<HttpRequestMessage, HttpResponseMessage>? respond = null)
        {
            Session = new AuthSession(new InMemorySessionStore());
            Context = new SessionContext(Session);
            Vendors = new InMemoryRepository<Vendor>(Context, TimeProvider.System);
            State = new InMemorySyncState();
            Server = new FakeSyncServer(TimeProvider.System);
            Engine = new SyncEngine([new SyncTable<Vendor>("vendors", Vendors, State, Server, TimeProvider.System).Attach()], State);
            Stub = new Stub(respond ?? (request => request.RequestUri!.AbsolutePath switch
            {
                "/auth/signup/options" or "/auth/signin/options" => Json(HttpStatusCode.OK, """{"options":{"challenge":"c"},"state":"s"}"""),
                "/auth/signout" => new HttpResponseMessage(HttpStatusCode.NoContent),
                _ => Json(HttpStatusCode.OK, TokensJson("acc", "ref", DateTimeOffset.UtcNow.AddMinutes(30))),
            }));
            var api = new AccountApi(new HttpClient(Stub) { BaseAddress = new Uri("https://api.test/") });
            var coordinator = new SyncCoordinator(Engine, State, new SessionTokenProvider(Session, api, TimeProvider.System), new Network(), new Lifecycle(), TimeProvider.System, debounce: TimeSpan.FromHours(1));
            Service = new AccountService(api, new Passkeys(outcome), Session, Engine, coordinator);
        }

        public AuthSession Session { get; }

        public SessionContext Context { get; }

        public InMemoryRepository<Vendor> Vendors { get; }

        public InMemorySyncState State { get; }

        public FakeSyncServer Server { get; }

        public SyncEngine Engine { get; }

        public Stub Stub { get; }

        public AccountService Service { get; }
    }

    [Fact]
    public async Task Signing_up_signs_in_and_hands_earlier_local_data_to_the_new_household()
    {
        var f = new Fixture(new PasskeyOutcome(PasskeyStatus.Success, "{\"id\":\"c\"}"));
        var vendor = await f.Vendors.SaveAsync(new Vendor { Name = "Made before signing in" }, Ct);
        vendor.HouseholdId.ShouldBe("local-household");

        var result = await f.Service.SignUpAsync("a@b.c", "Thomas", cancellationToken: Ct);

        result.Succeeded.ShouldBeTrue();
        f.Session.IsSignedIn.ShouldBeTrue();
        var adopted = (await f.Vendors.GetAsync(vendor.Id, Ct))!;
        adopted.HouseholdId.ShouldBe("h-u1");
        adopted.CreatedBy.ShouldBe("u1");
        (await f.Engine.PendingCountAsync(Ct)).ShouldBe(1);
    }

    [Fact]
    public async Task Signing_in_hands_out_the_session_too()
    {
        var f = new Fixture(new PasskeyOutcome(PasskeyStatus.Success, "{\"id\":\"c\"}"));
        (await f.Service.SignInAsync(Ct)).Succeeded.ShouldBeTrue();
        f.Stub.Paths.ShouldContain("auth/signin");
    }

    [Theory]
    [InlineData(PasskeyStatus.Cancelled, AccountOutcome.Cancelled)]
    [InlineData(PasskeyStatus.NotSupported, AccountOutcome.NotSupported)]
    [InlineData(PasskeyStatus.Failed, AccountOutcome.Unavailable)]
    public async Task Passkey_trouble_leaves_the_person_signed_out(PasskeyStatus status, AccountOutcome expected)
    {
        var f = new Fixture(new PasskeyOutcome(status));

        var result = await f.Service.SignUpAsync("a@b.c", "T", cancellationToken: Ct);

        result.Outcome.ShouldBe(expected);
        f.Session.IsSignedIn.ShouldBeFalse();
        f.Stub.Paths.ShouldNotContain("auth/signup");
    }

    [Fact]
    public async Task Sign_out_clears_the_session_even_when_the_server_cannot_be_reached()
    {
        var f = new Fixture(new PasskeyOutcome(PasskeyStatus.Success, "{\"id\":\"c\"}"), request =>
            request.RequestUri!.AbsolutePath == "/auth/signout"
                ? throw new HttpRequestException("offline")
                : Json(HttpStatusCode.OK, TokensJson("acc", "ref", DateTimeOffset.UtcNow.AddMinutes(30))));
        await f.Session.SetAsync(Tokens(DateTimeOffset.MaxValue), Ct);

        await f.Service.SignOutAsync(Ct);

        f.Session.IsSignedIn.ShouldBeFalse();
    }
}
