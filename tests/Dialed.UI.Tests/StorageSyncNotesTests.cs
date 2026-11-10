using Bunit;
using Dialed.Client.Account;
using Dialed.Client.Data;
using Dialed.Client.Storage;
using Dialed.Client.Sync;
using Dialed.Domain.Model;
using Dialed.UI.Components;
using Microsoft.Extensions.DependencyInjection;

namespace Dialed.UI.Tests;

/// <summary>What the app says about storage and sync trouble (#114).</summary>
public sealed class StorageSyncNotesTests : UiTestContext
{
    private static CancellationToken Ct => Xunit.TestContext.Current.CancellationToken;

    private readonly ClockWithTicks clock = new();

    public StorageSyncNotesTests()
    {
        Services.AddSingleton<TimeProvider>(clock);
        _ = Sync;
    }

    private SyncCoordinator Sync => Services.GetRequiredService<SyncCoordinator>();

    private StorageStatus Storage => Services.GetRequiredService<StorageStatus>();

    private async Task SignIn()
    {
        Tokens.Value = "tkn";
        await Services.GetRequiredService<AuthSession>().SetAsync(new SessionTokens("a", DateTimeOffset.MaxValue, "r", "u1", "Thomas", "h1"), Ct);
    }

    [Fact]
    public async Task While_everything_is_loaded_from_the_account_the_count_is_shown_and_then_goes_away()
    {
        await SignIn();
        for (var i = 0; i < 5; i++)
        {
            Transport.ServerVendors.Add(new Vendor { Id = $"v{i}", Name = $"V{i}", HouseholdId = "h1" });
        }

        Transport.HoldSecondPage = new TaskCompletionSource();
        var indicator = Render<SyncIndicator>();

        var sync = Sync.SyncNowAsync(Ct);

        indicator.WaitForAssertion(() => indicator.Find("#sync-restoring").TextContent.ShouldContain("Loading your data from your account… 2 items so far."));
        indicator.Find("#sync-restoring").GetAttribute("role").ShouldBe("status");
        Transport.HoldSecondPage.SetResult();
        await sync;
        indicator.WaitForAssertion(() => indicator.FindAll("#sync-restoring").ShouldBeEmpty());
        (await Services.GetRequiredService<IRepository<Vendor>>().ListAsync(Ct)).Count.ShouldBe(5);
    }

    [Fact]
    public async Task Waiting_changes_show_a_plain_note_and_after_five_minutes_a_warning_that_they_are_safe_here()
    {
        await SignIn();
        Transport.Offline = true;
        await Services.GetRequiredService<IRepository<Vendor>>().SaveAsync(new Vendor { Name = "Waiting" }, Ct);
        await Sync.SyncNowAsync(Ct);
        var indicator = Render<SyncIndicator>();

        indicator.Find("#sync-indicator").GetAttribute("role").ShouldBe("status");
        indicator.Find("#sync-indicator").ClassList.ShouldNotContain("dialed-sync-stuck");

        clock.Now += TimeSpan.FromMinutes(6);
        clock.Tick();

        indicator.WaitForAssertion(() =>
        {
            var note = indicator.Find("#sync-indicator");
            note.GetAttribute("role").ShouldBe("alert");
            note.ClassList.ShouldContain("dialed-sync-stuck");
            note.TextContent.Trim().ShouldBe("1 changes have not reached your account for more than 5 minutes. They are safe on this device; check your connection.");
        });
    }

    [Fact]
    public async Task Without_an_account_nothing_is_said_about_waiting_changes()
    {
        await Services.GetRequiredService<IRepository<Vendor>>().SaveAsync(new Vendor { Name = "Local" }, Ct);
        await Sync.SyncNowAsync(Ct);
        clock.Now += TimeSpan.FromHours(1);
        var indicator = Render<SyncIndicator>();
        clock.Tick();

        indicator.FindAll("#sync-indicator, #sync-restoring").ShouldBeEmpty();
    }

    [Fact]
    public void A_failed_save_to_the_device_shows_a_warning_and_a_good_one_clears_it()
    {
        var warning = Render<StorageWarning>();
        warning.FindAll("#storage-warning").ShouldBeEmpty();

        warning.InvokeAsync(() => Storage.ReportWriteFailed());

        warning.WaitForAssertion(() =>
        {
            warning.Find("#storage-warning [role=alert]").TextContent.ShouldContain("could not save your last change");
            warning.Find("#storage-warning").TextContent.ShouldNotContain("sent to your account");
        });

        warning.InvokeAsync(() => Storage.ReportWriteSucceeded());

        warning.WaitForAssertion(() => warning.FindAll("#storage-warning").ShouldBeEmpty());
    }

    [Fact]
    public async Task Signed_in_the_warning_says_the_change_still_goes_to_the_account()
    {
        await SignIn();
        var warning = Render<StorageWarning>();

        await warning.InvokeAsync(() => Storage.ReportWriteFailed());

        warning.WaitForAssertion(() => warning.Find("#storage-warning").TextContent.ShouldContain("sent to your account"));
    }

    private sealed class ClockWithTicks : TimeProvider
    {
        private readonly List<(TimerCallback Callback, object? State)> callbacks = [];

        public DateTimeOffset Now { get; set; } = new(2026, 11, 8, 12, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => Now;

        /// <summary>Runs the timers components asked for, as if their interval had passed.</summary>
        public void Tick()
        {
            foreach (var callback in callbacks.ToList())
            {
                callback.Callback(callback.State);
            }
        }

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            callbacks.Add((callback, state));
            return new SilentTimer();
        }

        private sealed class SilentTimer : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period) => true;

            public void Dispose()
            {
            }

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
