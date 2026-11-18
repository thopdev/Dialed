using Dialed.Api.Auth;
using Dialed.Api.Passkeys;
using Microsoft.AspNetCore.Identity;

namespace Dialed.Api.Tests;

// The identity stores (#38): one contract per store, run against memory and against Cosmos DB (docs/cosmos-stores.md).
// The Cosmos classes add what memory cannot show: a new instance sees the state, and two instances racing.

internal sealed class TestClock(DateTimeOffset now) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = now;

    public override DateTimeOffset GetUtcNow() => Now;
}

// ---- Users and passkeys ---------------------------------------------------------------------------

public abstract class UserStoreContract
{
    protected static CancellationToken Ct => TestContext.Current.CancellationToken;

    internal TestClock Time { get; } = new(new DateTimeOffset(2026, 11, 18, 9, 0, 0, TimeSpan.Zero));

    protected abstract AccountUserStore NewStore();

    protected static AccountUser NewUser(params byte[][] credentialIds)
    {
        var id = Guid.NewGuid().ToString("N");
        var email = $"u-{id}@example.com";
        return new AccountUser
        {
            Id = id,
            Email = email,
            UserName = email,
            NormalizedEmail = email.ToUpperInvariant(),
            NormalizedUserName = email.ToUpperInvariant(),
            DisplayName = "Tester",
            Passkeys = [.. credentialIds.Select(Passkey)],
            RecoveryCodeHashes = ["HASH-1", "HASH-2"],
        };
    }

    protected static byte[] NewCredentialId() => Guid.NewGuid().ToByteArray().Concat(Guid.NewGuid().ToByteArray()).ToArray();

    protected static StoredPasskey Passkey(byte[] credentialId) => new()
    {
        CredentialId = credentialId,
        PublicKey = [1, 2, 3, 4],
        Name = "Phone",
        CreatedAt = new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero),
        SignCount = 7,
        Transports = ["internal", "hybrid"],
        IsUserVerified = true,
        IsBackupEligible = true,
        IsBackedUp = true,
        AttestationObject = [9, 9],
        ClientDataJson = [8, 8],
    };

    [Fact]
    public async Task A_created_user_is_found_by_id_email_name_and_passkey_with_everything_kept()
    {
        var store = NewStore();
        var credential = NewCredentialId();
        var user = NewUser(credential);

        (await store.CreateAsync(user, Ct)).Succeeded.ShouldBeTrue();
        user.Version.ShouldBe(1);
        user.CreatedAt.ShouldBe(Time.Now);

        var found = (await store.FindByIdAsync(user.Id, Ct))!;
        found.Email.ShouldBe(user.Email);
        found.DisplayName.ShouldBe("Tester");
        found.RecoveryCodeHashes.ShouldBe(["HASH-1", "HASH-2"]);
        found.CreatedAt.ShouldBe(Time.Now);
        var passkey = found.Passkeys.ShouldHaveSingleItem();
        passkey.CredentialId.ShouldBe(credential);
        passkey.PublicKey.ShouldBe(new byte[] { 1, 2, 3, 4 });
        passkey.SignCount.ShouldBe(7u);
        passkey.Transports.ShouldBe(["internal", "hybrid"]);
        passkey.IsBackedUp.ShouldBeTrue();

        (await store.FindByEmailAsync(user.NormalizedEmail!, Ct))!.Id.ShouldBe(user.Id);
        (await store.FindByNameAsync(user.NormalizedUserName!, Ct))!.Id.ShouldBe(user.Id);
        (await store.FindByPasskeyIdAsync(credential, Ct))!.Id.ShouldBe(user.Id);
        (await store.FindByEmailAsync("NOBODY@EXAMPLE.COM", Ct)).ShouldBeNull();
        (await store.FindByPasskeyIdAsync(NewCredentialId(), Ct)).ShouldBeNull();
        (await store.FindByIdAsync("nobody", Ct)).ShouldBeNull();
    }

    [Fact]
    public async Task An_email_can_only_be_used_once()
    {
        var store = NewStore();
        var first = NewUser();
        (await store.CreateAsync(first, Ct)).Succeeded.ShouldBeTrue();

        var second = NewUser();
        second.Email = first.Email;
        second.NormalizedEmail = first.NormalizedEmail;
        var result = await store.CreateAsync(second, Ct);

        result.Succeeded.ShouldBeFalse();
        result.Errors.ShouldContain(e => e.Code == nameof(IdentityErrorDescriber.DuplicateEmail));
        (await store.FindByEmailAsync(first.NormalizedEmail!, Ct))!.Id.ShouldBe(first.Id);
        (await store.FindByIdAsync(second.Id, Ct)).ShouldBeNull();
    }

    [Fact]
    public async Task A_user_without_an_email_is_refused()
    {
        var user = NewUser();
        user.NormalizedEmail = null;

        (await NewStore().CreateAsync(user, Ct)).Succeeded.ShouldBeFalse();
    }

    [Fact]
    public async Task Saving_a_stale_copy_fails()
    {
        var store = NewStore();
        var user = NewUser();
        await store.CreateAsync(user, Ct);
        var a = (await store.FindByIdAsync(user.Id, Ct))!;
        var b = (await store.FindByIdAsync(user.Id, Ct))!;

        a.DisplayName = "A";
        (await store.UpdateAsync(a, Ct)).Succeeded.ShouldBeTrue();
        b.DisplayName = "B";
        var stale = await store.UpdateAsync(b, Ct);

        stale.Errors.ShouldContain(e => e.Code == nameof(IdentityErrorDescriber.ConcurrencyFailure));
        (await store.FindByIdAsync(user.Id, Ct))!.DisplayName.ShouldBe("A");
        (await store.UpdateAsync(a, Ct)).Succeeded.ShouldBeTrue(); // a's version moved on with its save
    }

    [Fact]
    public async Task Adding_and_removing_passkeys_moves_the_passkey_lookups()
    {
        var store = NewStore();
        var first = NewCredentialId();
        var user = NewUser(first);
        await store.CreateAsync(user, Ct);

        var second = NewCredentialId();
        var loaded = (await store.FindByIdAsync(user.Id, Ct))!;
        await store.AddOrUpdatePasskeyAsync(loaded, Passkey(second).ToInfo(), Ct);
        await store.RemovePasskeyAsync(loaded, first, Ct);
        (await store.UpdateAsync(loaded, Ct)).Succeeded.ShouldBeTrue();

        (await store.FindByPasskeyIdAsync(first, Ct)).ShouldBeNull();
        (await store.FindByPasskeyIdAsync(second, Ct))!.Id.ShouldBe(user.Id);
        (await store.GetPasskeysAsync((await store.FindByIdAsync(user.Id, Ct))!, Ct)).ShouldHaveSingleItem().CredentialId.ShouldBe(second);
    }

    [Fact]
    public async Task A_sign_in_updates_the_sign_count_and_last_use()
    {
        var store = NewStore();
        var credential = NewCredentialId();
        var user = NewUser(credential);
        await store.CreateAsync(user, Ct);

        var loaded = (await store.FindByPasskeyIdAsync(credential, Ct))!;
        var used = Passkey(credential).ToInfo();
        used.SignCount = 8;
        await store.AddOrUpdatePasskeyAsync(loaded, used, Ct);
        (await store.UpdateAsync(loaded, Ct)).Succeeded.ShouldBeTrue();

        var passkey = (await store.FindByIdAsync(user.Id, Ct))!.Passkeys.ShouldHaveSingleItem();
        passkey.SignCount.ShouldBe(8u);
        passkey.LastUsedAt.ShouldBe(Time.Now);
    }

    [Fact]
    public async Task Changing_the_email_moves_the_email_lookup_and_frees_the_old_address()
    {
        var store = NewStore();
        var user = NewUser();
        await store.CreateAsync(user, Ct);
        var oldEmail = user.NormalizedEmail!;

        var loaded = (await store.FindByIdAsync(user.Id, Ct))!;
        loaded.Email = "moved-" + user.Email;
        loaded.NormalizedEmail = loaded.Email.ToUpperInvariant();
        (await store.UpdateAsync(loaded, Ct)).Succeeded.ShouldBeTrue();

        (await store.FindByEmailAsync(oldEmail, Ct)).ShouldBeNull();
        (await store.FindByEmailAsync(loaded.NormalizedEmail, Ct))!.Id.ShouldBe(user.Id);
        var other = NewUser();
        other.Email = user.Email;
        other.NormalizedEmail = oldEmail;
        (await store.CreateAsync(other, Ct)).Succeeded.ShouldBeTrue();
    }

    [Fact]
    public async Task Deleting_a_user_removes_the_lookups_and_frees_the_email()
    {
        var store = NewStore();
        var credential = NewCredentialId();
        var user = NewUser(credential);
        await store.CreateAsync(user, Ct);

        (await store.DeleteAsync(user, Ct)).Succeeded.ShouldBeTrue();

        (await store.FindByIdAsync(user.Id, Ct)).ShouldBeNull();
        (await store.FindByEmailAsync(user.NormalizedEmail!, Ct)).ShouldBeNull();
        (await store.FindByPasskeyIdAsync(credential, Ct)).ShouldBeNull();
        var again = NewUser();
        again.NormalizedEmail = user.NormalizedEmail;
        (await store.CreateAsync(again, Ct)).Succeeded.ShouldBeTrue();
        (await store.DeleteAsync(user, Ct)).Succeeded.ShouldBeTrue(); // twice is fine
    }
}

public sealed class InMemoryUserStoreTests : UserStoreContract
{
    protected override AccountUserStore NewStore() => new InMemoryUserStore(Time);
}

public sealed class CosmosUserStoreTests : UserStoreContract, IAsyncDisposable
{
    private readonly CosmosTestDatabase database = new();

    protected override AccountUserStore NewStore() => new CosmosUserStore(database.New(), Time);

    public ValueTask DisposeAsync() => database.DisposeAsync();

    [Fact]
    public async Task A_new_instance_finds_the_user_by_email_and_passkey()
    {
        var credential = NewCredentialId();
        var user = NewUser(credential);
        await NewStore().CreateAsync(user, Ct);

        (await NewStore().FindByEmailAsync(user.NormalizedEmail!, Ct))!.Id.ShouldBe(user.Id);
        (await NewStore().FindByPasskeyIdAsync(credential, Ct))!.Id.ShouldBe(user.Id);
    }

    [Fact]
    public async Task Two_instances_signing_up_the_same_email_create_one_user()
    {
        var a = NewUser();
        var b = NewUser();
        b.Email = a.Email;
        b.NormalizedEmail = a.NormalizedEmail;

        var results = await Task.WhenAll(NewStore().CreateAsync(a, Ct), NewStore().CreateAsync(b, Ct));

        results.Count(r => r.Succeeded).ShouldBe(1);
        var winner = results[0].Succeeded ? a : b;
        var loser = results[0].Succeeded ? b : a;
        (await NewStore().FindByEmailAsync(a.NormalizedEmail!, Ct))!.Id.ShouldBe(winner.Id);
        (await NewStore().FindByIdAsync(loser.Id, Ct)).ShouldBeNull();
    }

    [Fact]
    public async Task Many_instances_signing_up_the_same_email_create_one_user()
    {
        var users = Enumerable.Range(0, 6).Select(_ => NewUser()).ToList();
        foreach (var user in users.Skip(1))
        {
            user.Email = users[0].Email;
            user.NormalizedEmail = users[0].NormalizedEmail;
        }

        var results = await Task.WhenAll(users.Select(u => NewStore().CreateAsync(u, Ct)));

        results.Count(r => r.Succeeded).ShouldBe(1);
    }

    [Fact]
    public async Task Two_instances_saving_the_same_version_let_one_win()
    {
        var user = NewUser();
        await NewStore().CreateAsync(user, Ct);
        var a = (await NewStore().FindByIdAsync(user.Id, Ct))!;
        var b = (await NewStore().FindByIdAsync(user.Id, Ct))!;
        a.DisplayName = "A";
        b.DisplayName = "B";

        var results = await Task.WhenAll(NewStore().UpdateAsync(a, Ct), NewStore().UpdateAsync(b, Ct));

        results.Count(r => r.Succeeded).ShouldBe(1);
    }

    [Fact]
    public async Task An_email_lookup_left_by_an_unfinished_sign_up_does_not_block_the_address()
    {
        var store = NewStore();
        var user = NewUser();
        await store.CreateAsync(user, Ct);

        // The user document is gone but its email lookup stayed (a crash between the two deletes).
        var container = await Dialed.Api.Data.CosmosIdentity.ContainerAsync(database.New(), Ct);
        await container.DeleteItemAsync<object>("user:" + user.Id, new Microsoft.Azure.Cosmos.PartitionKey("user:" + user.Id), cancellationToken: Ct);

        (await store.FindByEmailAsync(user.NormalizedEmail!, Ct)).ShouldBeNull();
        var again = NewUser();
        again.NormalizedEmail = user.NormalizedEmail;

        // A young claim may be a sign-up still writing its user: not taken over yet.
        (await store.CreateAsync(again, Ct)).Succeeded.ShouldBeFalse();

        Time.Now += CosmosUserStore.OrphanedClaimAge;
        (await store.CreateAsync(again, Ct)).Succeeded.ShouldBeTrue();
        (await store.FindByEmailAsync(user.NormalizedEmail!, Ct))!.Id.ShouldBe(again.Id);
    }
}

// ---- Directory (profiles and households) ---------------------------------------------------------

public abstract class UserDirectoryContract
{
    protected static CancellationToken Ct => TestContext.Current.CancellationToken;

    protected abstract IUserDirectory NewDirectory(UserDirectorySeed? seed = null);

    protected static AppUser NewPerson(string name, string? household = null)
    {
        var id = "u-" + Guid.NewGuid().ToString("N");
        return new AppUser(id, $"{id}@example.com", name, household ?? "h-" + id);
    }

    [Fact]
    public async Task A_person_is_found_and_listed_by_household_sorted_by_name()
    {
        var directory = NewDirectory();
        var household = "h-" + Guid.NewGuid().ToString("N");
        var bea = NewPerson("bea", household);
        var adam = NewPerson("Adam", household);
        await directory.AddAsync(bea, Ct);
        await directory.AddAsync(adam, Ct);
        await directory.AddAsync(NewPerson("Elsewhere"), Ct);

        (await directory.FindByIdAsync(bea.Id, Ct)).ShouldBe(bea with { HouseholdIds = [] }, new AppUserComparer());
        (await directory.ListByHouseholdAsync(household, Ct)).Select(u => u.DisplayName).ShouldBe(["Adam", "bea"]);
        (await directory.FindByIdAsync("nobody", Ct)).ShouldBeNull();
    }

    [Fact]
    public async Task Switching_households_keeps_the_old_one_as_a_membership()
    {
        var directory = NewDirectory();
        var person = NewPerson("Switcher");
        await directory.AddAsync(person, Ct);
        var shared = "h-shared-" + Guid.NewGuid().ToString("N");

        var moved = (await directory.SetHouseholdAsync(person.Id, shared, Ct))!;

        moved.HouseholdId.ShouldBe(shared);
        moved.HouseholdIds.ShouldBe([person.HouseholdId]);
        (await directory.FindByIdAsync(person.Id, Ct))!.AllHouseholdIds.ShouldBe([shared, person.HouseholdId]);
        (await directory.ListByHouseholdAsync(person.HouseholdId, Ct)).ShouldHaveSingleItem().Id.ShouldBe(person.Id);
        (await directory.ListByHouseholdAsync(shared, Ct)).ShouldHaveSingleItem().Id.ShouldBe(person.Id);
        (await directory.SetHouseholdAsync("nobody", shared, Ct)).ShouldBeNull();
    }

    [Fact]
    public async Task Leaving_a_household_ends_the_membership_but_never_the_last_one()
    {
        var directory = NewDirectory();
        var person = NewPerson("Leaver");
        await directory.AddAsync(person, Ct);
        var shared = "h-shared-" + Guid.NewGuid().ToString("N");
        await directory.SetHouseholdAsync(person.Id, shared, Ct);

        var left = (await directory.RemoveMembershipAsync(person.Id, shared, Ct))!;
        left.HouseholdId.ShouldBe(person.HouseholdId);
        left.HouseholdIds.ShouldBeEmpty();
        (await directory.ListByHouseholdAsync(shared, Ct)).ShouldBeEmpty();

        (await directory.RemoveMembershipAsync(person.Id, person.HouseholdId, Ct))!.HouseholdId.ShouldBe(person.HouseholdId);
        (await directory.RemoveMembershipAsync("nobody", shared, Ct)).ShouldBeNull();
    }

    [Fact]
    public async Task Removing_a_person_forgets_them()
    {
        var directory = NewDirectory();
        var person = NewPerson("Gone");
        await directory.AddAsync(person, Ct);

        (await directory.RemoveAsync(person.Id, Ct)).ShouldBeTrue();
        (await directory.RemoveAsync(person.Id, Ct)).ShouldBeFalse();
        (await directory.FindByIdAsync(person.Id, Ct)).ShouldBeNull();
        (await directory.ListByHouseholdAsync(person.HouseholdId, Ct)).ShouldBeEmpty();
    }

    [Fact]
    public async Task The_seed_users_are_there_from_the_start()
    {
        var household = "h-seed-" + Guid.NewGuid().ToString("N");
        var seeded = NewPerson("Seeded", household);
        var directory = NewDirectory(new UserDirectorySeed([seeded]));

        (await directory.ListByHouseholdAsync(household, Ct)).ShouldHaveSingleItem().Id.ShouldBe(seeded.Id);
        (await directory.FindByIdAsync(seeded.Id, Ct)).ShouldNotBeNull();
    }

    protected sealed class AppUserComparer : IEqualityComparer<AppUser?>
    {
        public bool Equals(AppUser? x, AppUser? y) =>
            x is not null && y is not null && x.Id == y.Id && x.Email == y.Email && x.DisplayName == y.DisplayName
            && x.HouseholdId == y.HouseholdId && x.HouseholdIds.SequenceEqual(y.HouseholdIds);

        public int GetHashCode(AppUser? obj) => obj?.Id.GetHashCode(StringComparison.Ordinal) ?? 0;
    }
}

public sealed class InMemoryUserDirectoryTests : UserDirectoryContract
{
    protected override IUserDirectory NewDirectory(UserDirectorySeed? seed = null) => new InMemoryUserDirectory(seed);
}

public sealed class CosmosUserDirectoryTests : UserDirectoryContract, IAsyncDisposable
{
    private readonly CosmosTestDatabase database = new();

    protected override IUserDirectory NewDirectory(UserDirectorySeed? seed = null) =>
        new CosmosUserDirectory(database.New(), seed ?? new UserDirectorySeed([]));

    public ValueTask DisposeAsync() => database.DisposeAsync();

    [Fact]
    public async Task A_new_instance_sees_the_household_change_and_does_not_reseed_over_it()
    {
        var person = NewPerson("Dev");
        var seed = new UserDirectorySeed([person]);
        var shared = "h-shared-" + Guid.NewGuid().ToString("N");
        await NewDirectory(seed).SetHouseholdAsync(person.Id, shared, Ct);

        (await NewDirectory(seed).FindByIdAsync(person.Id, Ct))!.HouseholdId.ShouldBe(shared);
    }

    [Fact]
    public async Task Two_instances_joining_households_at_once_keep_both_memberships()
    {
        var person = NewPerson("Joiner");
        await NewDirectory().AddAsync(person, Ct);

        await Task.WhenAll(
            NewDirectory().SetHouseholdAsync(person.Id, "h-one-" + person.Id, Ct),
            NewDirectory().SetHouseholdAsync(person.Id, "h-two-" + person.Id, Ct));

        (await NewDirectory().FindByIdAsync(person.Id, Ct))!.AllHouseholdIds.Count.ShouldBe(3);
    }
}

// ---- Refresh tokens ------------------------------------------------------------------------------

public abstract class RefreshTokenStoreContract
{
    protected static CancellationToken Ct => TestContext.Current.CancellationToken;

    internal TestClock Time { get; } = new(new DateTimeOffset(2026, 11, 18, 9, 0, 0, TimeSpan.Zero));

    protected abstract IRefreshTokenStore NewStore();

    protected RefreshTokenRecord NewToken(string? user = null, string? family = null) =>
        new(RefreshTokenService.Hash(Guid.NewGuid().ToString()), user ?? "u-" + Guid.NewGuid().ToString("N"), family ?? Guid.NewGuid().ToString(), Time.Now.AddDays(30));

    [Fact]
    public async Task A_token_is_found_by_its_hash()
    {
        var store = NewStore();
        var token = NewToken();
        await store.AddAsync(token, Ct);

        (await store.FindAsync(token.TokenHash, Ct)).ShouldBe(token);
        (await store.FindAsync("unknown", Ct)).ShouldBeNull();
    }

    [Fact]
    public async Task A_token_can_be_used_once()
    {
        var store = NewStore();
        var token = NewToken();
        await store.AddAsync(token, Ct);

        (await store.TryConsumeAsync(token.TokenHash, Ct)).ShouldBeTrue();
        (await store.TryConsumeAsync(token.TokenHash, Ct)).ShouldBeFalse();
        (await store.FindAsync(token.TokenHash, Ct))!.Used.ShouldBeTrue();
        (await store.TryConsumeAsync("unknown", Ct)).ShouldBeFalse();
    }

    [Fact]
    public async Task Revoking_a_family_revokes_only_that_sign_in()
    {
        var store = NewStore();
        var user = "u-" + Guid.NewGuid().ToString("N");
        var family = Guid.NewGuid().ToString();
        var used = NewToken(user, family) with { Used = true };
        var current = NewToken(user, family);
        var otherDevice = NewToken(user);
        foreach (var token in new[] { used, current, otherDevice })
        {
            await store.AddAsync(token, Ct);
        }

        await store.RevokeFamilyAsync(family, Ct);

        (await store.FindAsync(used.TokenHash, Ct))!.Revoked.ShouldBeTrue();
        (await store.FindAsync(current.TokenHash, Ct))!.Revoked.ShouldBeTrue();
        (await store.FindAsync(otherDevice.TokenHash, Ct))!.Revoked.ShouldBeFalse();
    }

    [Fact]
    public async Task Revoking_a_person_revokes_every_device()
    {
        var store = NewStore();
        var user = "u-" + Guid.NewGuid().ToString("N");
        var phone = NewToken(user);
        var laptop = NewToken(user);
        var someoneElse = NewToken();
        foreach (var token in new[] { phone, laptop, someoneElse })
        {
            await store.AddAsync(token, Ct);
        }

        await store.RevokeUserAsync(user, Ct);

        (await store.FindAsync(phone.TokenHash, Ct))!.Revoked.ShouldBeTrue();
        (await store.FindAsync(laptop.TokenHash, Ct))!.Revoked.ShouldBeTrue();
        (await store.FindAsync(someoneElse.TokenHash, Ct))!.Revoked.ShouldBeFalse();
    }
}

public sealed class InMemoryRefreshTokenStoreTests : RefreshTokenStoreContract
{
    protected override IRefreshTokenStore NewStore() => new InMemoryRefreshTokenStore();
}

public sealed class CosmosRefreshTokenStoreTests : RefreshTokenStoreContract, IAsyncDisposable
{
    private readonly CosmosTestDatabase database = new();

    protected override IRefreshTokenStore NewStore() => new CosmosRefreshTokenStore(database.New(), Time);

    public ValueTask DisposeAsync() => database.DisposeAsync();

    [Fact]
    public async Task A_token_from_before_a_restart_still_works_once()
    {
        var token = NewToken();
        await NewStore().AddAsync(token, Ct);

        (await NewStore().TryConsumeAsync(token.TokenHash, Ct)).ShouldBeTrue();
        (await NewStore().TryConsumeAsync(token.TokenHash, Ct)).ShouldBeFalse();
    }

    [Fact]
    public async Task Two_instances_rotating_the_same_token_let_only_one_rotate()
    {
        var token = NewToken();
        await NewStore().AddAsync(token, Ct);
        var instances = Enumerable.Range(0, 4).Select(_ => NewStore()).ToList();

        var results = await Task.WhenAll(instances.Select(s => s.TryConsumeAsync(token.TokenHash, Ct)));

        results.Count(r => r).ShouldBe(1);
    }
}

// ---- Ceremony nonces -----------------------------------------------------------------------------

public abstract class CeremonyNoncesContract
{
    protected static CancellationToken Ct => TestContext.Current.CancellationToken;

    internal TestClock Time { get; } = new(new DateTimeOffset(2026, 11, 18, 9, 0, 0, TimeSpan.Zero));

    protected abstract ICeremonyNonces NewNonces();

    [Fact]
    public async Task A_nonce_is_claimed_once()
    {
        var nonces = NewNonces();
        var nonce = Guid.NewGuid().ToString("N");

        (await nonces.TryClaimAsync(nonce, TimeSpan.FromMinutes(5), Ct)).ShouldBeTrue();
        (await nonces.TryClaimAsync(nonce, TimeSpan.FromMinutes(5), Ct)).ShouldBeFalse();
        (await nonces.TryClaimAsync(Guid.NewGuid().ToString("N"), TimeSpan.FromMinutes(5), Ct)).ShouldBeTrue();
    }

    [Fact]
    public async Task A_claim_is_forgotten_after_it_expires()
    {
        var nonces = NewNonces();
        var nonce = Guid.NewGuid().ToString("N");
        await nonces.TryClaimAsync(nonce, TimeSpan.FromMinutes(5), Ct);

        Time.Now += TimeSpan.FromMinutes(4);
        (await nonces.TryClaimAsync(nonce, TimeSpan.FromMinutes(5), Ct)).ShouldBeFalse();
        Time.Now += TimeSpan.FromMinutes(1);
        (await nonces.TryClaimAsync(nonce, TimeSpan.FromMinutes(5), Ct)).ShouldBeTrue();
    }
}

public sealed class InMemoryCeremonyNoncesTests : CeremonyNoncesContract
{
    protected override ICeremonyNonces NewNonces() => new InMemoryCeremonyNonces(Time);
}

public sealed class CosmosCeremonyNoncesTests : CeremonyNoncesContract, IAsyncDisposable
{
    private readonly CosmosTestDatabase database = new();

    protected override ICeremonyNonces NewNonces() => new CosmosCeremonyNonces(database.New(), Time);

    public ValueTask DisposeAsync() => database.DisposeAsync();

    [Fact]
    public async Task A_replay_against_another_instance_is_refused()
    {
        var nonce = Guid.NewGuid().ToString("N");
        var results = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => NewNonces().TryClaimAsync(nonce, TimeSpan.FromMinutes(5), Ct)));

        results.Count(r => r).ShouldBe(1);
        (await NewNonces().TryClaimAsync(nonce, TimeSpan.FromMinutes(5), Ct)).ShouldBeFalse();
    }
}

// ---- Beta invites --------------------------------------------------------------------------------

public abstract class BetaInviteStoreContract
{
    protected static CancellationToken Ct => TestContext.Current.CancellationToken;

    internal TestClock Time { get; } = new(new DateTimeOffset(2026, 11, 18, 9, 0, 0, TimeSpan.Zero));

    protected abstract IBetaInviteStore NewStore();

    protected static string NewCode() => "C" + Guid.NewGuid().ToString("N")[..10].ToUpperInvariant();

    [Fact]
    public async Task A_code_works_until_its_uses_are_gone_whatever_the_case_or_spaces()
    {
        var store = NewStore();
        var code = NewCode();
        await store.AddAsync(new BetaInvite(code, 2, 0, Time.Now.AddDays(14)), Ct);

        (await store.IsValidAsync($" {code.ToLowerInvariant()} ", Time.Now, Ct)).ShouldBeTrue();
        (await store.TryUseAsync(code.ToLowerInvariant(), Time.Now, Ct)).ShouldBeTrue();
        (await store.TryUseAsync(code, Time.Now, Ct)).ShouldBeTrue();
        (await store.TryUseAsync(code, Time.Now, Ct)).ShouldBeFalse();
        (await store.IsValidAsync(code, Time.Now, Ct)).ShouldBeFalse();
        (await store.IsValidAsync("UNKNOWN", Time.Now, Ct)).ShouldBeFalse();
    }

    [Fact]
    public async Task An_expired_code_does_not_work()
    {
        var store = NewStore();
        var code = NewCode();
        await store.AddAsync(new BetaInvite(code, 5, 0, Time.Now.AddDays(1)), Ct);

        var later = Time.Now.AddDays(1);
        (await store.IsValidAsync(code, later, Ct)).ShouldBeFalse();
        (await store.TryUseAsync(code, later, Ct)).ShouldBeFalse();
    }
}

public sealed class InMemoryBetaInviteStoreTests : BetaInviteStoreContract
{
    protected override IBetaInviteStore NewStore() => new InMemoryBetaInviteStore();
}

public sealed class CosmosBetaInviteStoreTests : BetaInviteStoreContract, IAsyncDisposable
{
    private readonly CosmosTestDatabase database = new();

    protected override IBetaInviteStore NewStore() => new CosmosBetaInviteStore(database.New(), Time);

    public ValueTask DisposeAsync() => database.DisposeAsync();

    [Fact]
    public async Task Two_sign_ups_on_two_instances_cannot_share_the_last_use()
    {
        var code = NewCode();
        await NewStore().AddAsync(new BetaInvite(code, 1, 0, Time.Now.AddDays(14)), Ct);

        var results = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => NewStore().TryUseAsync(code, Time.Now, Ct)));

        results.Count(r => r).ShouldBe(1);
    }
}
