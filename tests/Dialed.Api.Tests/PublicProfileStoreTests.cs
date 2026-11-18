using Dialed.Api.PublicProfiles;

namespace Dialed.Api.Tests;

/// <summary>What every public profile store does (#38, #106): profiles off by default, the open and suspended lists, deduplicated reports.</summary>
public abstract class PublicProfileStoreContract
{
    protected static CancellationToken Ct => TestContext.Current.CancellationToken;

    protected static readonly DateTimeOffset Start = new(2026, 11, 10, 9, 0, 0, TimeSpan.Zero);

    protected abstract IPublicProfileStore NewStore();

    protected static string NewUser() => Guid.NewGuid().ToString("N");

    protected static Report NewReport(string reporter, string owner, ReportTarget target = ReportTarget.Profile, string? item = null, int minutes = 0) =>
        new(Guid.NewGuid().ToString("N"), reporter, owner, target, item, ReportReason.Spam, "note", Start.AddMinutes(minutes));

    [Fact]
    public async Task A_profile_is_off_until_saved()
    {
        var store = NewStore();
        var alice = NewUser();

        (await store.GetAsync(alice, Ct)).ShouldBe(new PublicProfile(alice));
        await store.SaveAsync(new PublicProfile(alice, Enabled: true), Ct);
        (await store.GetAsync(alice, Ct)).IsOpen.ShouldBeTrue();
    }

    [Fact]
    public async Task Open_and_suspended_lists_follow_the_profiles()
    {
        var store = NewStore();
        var (open, off, suspended, suspendedOff) = (NewUser(), NewUser(), NewUser(), NewUser());
        await store.SaveAsync(new PublicProfile(open, Enabled: true), Ct);
        await store.SaveAsync(new PublicProfile(off), Ct);
        await store.SaveAsync(new PublicProfile(suspended, Enabled: true, Suspended: true), Ct);
        await store.SaveAsync(new PublicProfile(suspendedOff, Suspended: true), Ct);

        (await store.OpenUserIdsAsync(Ct)).ShouldContain(open);
        (await store.OpenUserIdsAsync(Ct)).ShouldNotContain(off);
        (await store.OpenUserIdsAsync(Ct)).ShouldNotContain(suspended);
        (await store.SuspendedUserIdsAsync(Ct)).Where(id => id == suspended || id == suspendedOff)
            .ShouldBe(new[] { suspended, suspendedOff }.Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task A_report_is_kept_once_per_reporter_and_target_newest_first()
    {
        var store = NewStore();
        var (alice, bob, carol) = (NewUser(), NewUser(), NewUser());

        (await store.AddReportAsync(NewReport(alice, carol), Ct)).ShouldBeTrue();
        (await store.AddReportAsync(NewReport(alice, carol, minutes: 5), Ct)).ShouldBeFalse();
        (await store.AddReportAsync(NewReport(alice, carol, ReportTarget.Bean, "bean-1", minutes: 1), Ct)).ShouldBeTrue();
        (await store.AddReportAsync(NewReport(alice, carol, ReportTarget.Bean, "bean-2", minutes: 2), Ct)).ShouldBeTrue();
        (await store.AddReportAsync(NewReport(bob, carol, minutes: 3), Ct)).ShouldBeTrue();

        var reports = (await store.ReportsAsync(Ct)).Where(r => r.OwnerId == carol).ToList();
        reports.Select(r => (r.ReporterId, r.ItemId)).ShouldBe([(bob, null), (alice, "bean-2"), (alice, "bean-1"), (alice, null)]);
        reports[0].Reason.ShouldBe(ReportReason.Spam);
        reports[0].Note.ShouldBe("note");
        reports[0].CreatedAt.ShouldBe(Start.AddMinutes(3));
    }

    [Fact]
    public async Task Removing_a_person_removes_their_profile_and_reports_by_or_about_them()
    {
        var store = NewStore();
        var (alice, bob, carol) = (NewUser(), NewUser(), NewUser());
        await store.SaveAsync(new PublicProfile(alice, Enabled: true), Ct);
        await store.AddReportAsync(NewReport(alice, bob), Ct);
        await store.AddReportAsync(NewReport(bob, alice), Ct);
        await store.AddReportAsync(NewReport(carol, bob), Ct);

        await store.RemoveUserAsync(alice, Ct);

        (await store.GetAsync(alice, Ct)).ShouldBe(new PublicProfile(alice));
        (await store.OpenUserIdsAsync(Ct)).ShouldNotContain(alice);
        (await store.ReportsAsync(Ct)).Where(r => r.ReporterId == alice || r.OwnerId == alice).ShouldBeEmpty();
        (await store.ReportsAsync(Ct)).ShouldContain(r => r.ReporterId == carol && r.OwnerId == bob);
    }
}

public sealed class InMemoryPublicProfileStoreTests : PublicProfileStoreContract
{
    protected override IPublicProfileStore NewStore() => new InMemoryPublicProfileStore();
}

/// <summary>The Cosmos DB public profile store against a real Cosmos DB; skips without <c>COSMOS_CONNECTION_STRING</c>.</summary>
public sealed class CosmosPublicProfileStoreTests : PublicProfileStoreContract, IAsyncDisposable
{
    private readonly CosmosTestDatabase database = new();

    protected override IPublicProfileStore NewStore() => new CosmosPublicProfileStore(database.NewCosmos());

    public ValueTask DisposeAsync() => database.DisposeAsync();

    [Fact]
    public async Task Profiles_and_reports_hold_for_a_new_instance()
    {
        var (alice, bob) = (NewUser(), NewUser());
        await NewStore().SaveAsync(new PublicProfile(alice, Enabled: true, Suspended: true), Ct);
        await NewStore().AddReportAsync(NewReport(bob, alice), Ct);

        (await NewStore().GetAsync(alice, Ct)).ShouldBe(new PublicProfile(alice, true, true));
        (await NewStore().AddReportAsync(NewReport(bob, alice), Ct)).ShouldBeFalse();
    }
}
