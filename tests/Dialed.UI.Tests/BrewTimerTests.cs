using Bunit;
using Dialed.Client;
using Dialed.Client.Data;
using Dialed.Client.Platform;
using Dialed.Domain.Model;
using Dialed.UI.Pages;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace Dialed.UI.Tests;

public sealed class BrewTimerTests : UiTestContext
{
    private static CancellationToken Ct => Xunit.TestContext.Current.CancellationToken;

    private readonly ManualClock clock = new();
    private readonly RecordingSound sound = new();
    private readonly RecordingWakeLock wake = new();

    public BrewTimerTests()
    {
        Services.AddSingleton<TimeProvider>(clock);
        Services.AddSingleton<ISoundCue>(sound);
        Services.AddSingleton<IWakeLock>(wake);
    }

    private IRepository<Recipe> Recipes => Services.GetRequiredService<IRepository<Recipe>>();

    private async Task<Recipe> V60() => await Recipes.SaveAsync(new Recipe
    {
        MethodCode = "v60",
        Name = "Morning",
        DoseG = 15m,
        WaterG = 240m,
        TimeS = 180,
        Steps = [new RecipeStep(RecipeStepKind.Bloom, 0, 30, 45m), new RecipeStep(RecipeStepKind.Pour, 45, 30, 150m), new RecipeStep(RecipeStepKind.Pour, 90, 40, 240m)],
    }, Ct);

    private async Task<Recipe> Espresso() => await Recipes.SaveAsync(new Recipe { MethodCode = "espresso", Name = "House", DoseG = 18m, YieldG = 36m, TimeS = 28 }, Ct);

    private IRenderedComponent<BrewTimer> RenderTimer(Recipe recipe) =>
        Render<BrewTimer>(p => p.Add(x => x.BagId, "bag1").Add(x => x.RecipeId, recipe.Id));

    private async Task Advance(IRenderedComponent<BrewTimer> page, int seconds)
    {
        clock.Now = clock.Now.AddSeconds(seconds);
        await page.InvokeAsync(() => page.Instance.Refresh());
    }

    // ---- Guided timer -------------------------------------------------------------------------

    [Fact]
    public async Task Shows_a_zeroed_clock_and_a_start_button_before_starting()
    {
        var page = RenderTimer(await V60());

        page.Find("#timer-clock").TextContent.ShouldBe("0:00");
        page.Find("#timer-start").ShouldNotBeNull();
        page.FindAll("#timer-finish").ShouldBeEmpty();
        page.Find(".dialed-timer").GetAttribute("data-mode").ShouldBe("guided");
    }

    [Fact]
    public async Task Starting_keeps_the_screen_awake_cues_the_sound_and_shows_the_first_step_and_target()
    {
        var page = RenderTimer(await V60());

        page.Find("#timer-start").Click();

        wake.Acquired.ShouldBe(1);
        sound.Cues.ShouldBe([SoundCue.Tick]);
        page.Find("#timer-step").TextContent.ShouldBe("Bloom");
        page.Find("#timer-target").TextContent.ShouldBe("Pour to 45 g");
        page.Find("#timer-next").TextContent.ShouldContain("Next: Pour in 0:45");
        page.Find("#timer-next").TextContent.ShouldContain("Pour to 150 g");
        page.FindAll("#timer-finish").Count.ShouldBe(1);
    }

    [Fact]
    public async Task Each_new_step_changes_the_display_and_gives_a_cue()
    {
        var page = RenderTimer(await V60());
        page.Find("#timer-start").Click();

        await Advance(page, 20);
        sound.Cues.Count.ShouldBe(1);
        page.Find("#timer-clock").TextContent.ShouldBe("0:20");
        page.Find("#timer-next").TextContent.ShouldContain("in 0:25");

        await Advance(page, 30);
        page.Find("#timer-clock").TextContent.ShouldBe("0:50");
        page.Find("#timer-step").TextContent.ShouldBe("Pour");
        page.Find("#timer-target").TextContent.ShouldBe("Pour to 150 g");
        sound.Cues.Count.ShouldBe(2);

        await Advance(page, 1);
        sound.Cues.Count.ShouldBe(2);
    }

    [Fact]
    public async Task The_end_of_the_recipe_time_shows_done_once_with_its_own_cue()
    {
        var page = RenderTimer(await V60());
        page.Find("#timer-start").Click();

        await Advance(page, 181);
        await Advance(page, 5);

        page.Find("#timer-done").TextContent.ShouldContain("Done");
        page.FindAll("#timer-next").ShouldBeEmpty();
        page.Find(".dialed-timer").ClassList.ShouldContain("finished");
        sound.Cues.Count(c => c == SoundCue.Done).ShouldBe(1);
    }

    [Fact]
    public async Task Finishing_releases_the_screen_hands_the_time_to_the_log_and_opens_it()
    {
        var recipe = await V60();
        var page = RenderTimer(recipe);
        page.Find("#timer-start").Click();
        await Advance(page, 171);

        page.Find("#timer-finish").Click();

        // Stopping the tick loop completes asynchronously, so wait for the navigation that ends the handler.
        page.WaitForAssertion(() => Services.GetRequiredService<NavigationManager>().Uri.ShouldEndWith($"brew/bag1/log/recipe/{recipe.Id}"));
        wake.Released.ShouldBeGreaterThan(0);
        Services.GetRequiredService<BrewTimerHandoff>().Take(recipe.Id).ShouldBe(171);
    }

    [Fact]
    public async Task The_log_opens_with_the_measured_time()
    {
        var bean = await Services.GetRequiredService<IRepository<Bean>>().SaveAsync(new Bean { Name = "Gesha" }, Ct);
        var bag = await Services.GetRequiredService<IRepository<Bag>>().SaveAsync(new Bag { BeanId = bean.Id, WeightG = 250m }, Ct);
        var recipe = await V60();
        Services.GetRequiredService<BrewTimerHandoff>().Set(recipe.Id, 171);

        var log = Render<BrewLog>(p => p.Add(x => x.BagId, bag.Id).Add(x => x.RecipeId, recipe.Id));

        log.Find("#brew-time").GetAttribute("value").ShouldBe("171");
        Services.GetRequiredService<BrewTimerHandoff>().Take(recipe.Id).ShouldBeNull();
    }

    [Fact]
    public async Task Reset_stops_the_timer_and_lets_go_of_the_screen()
    {
        var page = RenderTimer(await V60());
        page.Find("#timer-start").Click();
        await Advance(page, 50);

        page.Find("#timer-reset").Click();

        page.WaitForAssertion(() => page.Find("#timer-clock").TextContent.ShouldBe("0:00"));
        page.Find("#timer-start").ShouldNotBeNull();
        wake.Released.ShouldBeGreaterThan(0);
    }

    [Fact]
    public async Task A_refused_wake_lock_or_failing_sound_does_not_stop_the_timer()
    {
        wake.Refuse = true;
        sound.Fail = true;
        var page = RenderTimer(await V60());

        page.Find("#timer-start").Click();
        await Advance(page, 50);

        page.Find("#timer-step").TextContent.ShouldBe("Pour");
    }

    // ---- Espresso -----------------------------------------------------------------------------

    [Fact]
    public async Task Espresso_gets_a_shot_timer_with_the_target_band()
    {
        var page = RenderTimer(await Espresso());

        page.Find(".dialed-timer").GetAttribute("data-mode").ShouldBe("espresso");
        page.Find("#timer-band").TextContent.ShouldBe("Target 25-30 s");

        page.Find("#timer-start").Click();
        await Advance(page, 10);
        page.Find("#timer-shot-status").TextContent.ShouldBe("Too short");

        await Advance(page, 17);
        page.Find("#timer-shot-status").TextContent.ShouldBe("In the target window");
        page.Find(".dialed-timer").ClassList.ShouldContain("shot-inband");

        await Advance(page, 13);
        page.Find("#timer-shot-status").TextContent.ShouldBe("Too long");
        page.Find(".dialed-timer").ClassList.ShouldContain("shot-toolong");
    }

    // ---- Page basics --------------------------------------------------------------------------

    [Fact]
    public void Unknown_recipe_shows_a_not_found_message()
    {
        Render<BrewTimer>(p => p.Add(x => x.BagId, "bag1").Add(x => x.RecipeId, "nope")).Markup.ShouldContain("no longer exists");
    }

    [Fact]
    public async Task The_brew_chooser_offers_a_timer_per_recipe()
    {
        var bean = await Services.GetRequiredService<IRepository<Bean>>().SaveAsync(new Bean { Name = "Gesha" }, Ct);
        var bag = await Services.GetRequiredService<IRepository<Bag>>().SaveAsync(new Bag { BeanId = bean.Id, WeightG = 250m, Status = Dialed.Domain.Inventory.BagStatus.Open }, Ct);
        var recipe = await V60();

        var page = Render<BrewHome>(p => p.Add(x => x.BagId, bag.Id));

        page.Find($"a[href='brew/{bag.Id}/timer/{recipe.Id}']").TextContent.Trim().ShouldBe("Timer");
    }

    // ---- Progress and step list (#213) --------------------------------------------------------

    [Fact]
    public async Task Guided_timer_lists_every_step_and_marks_the_running_one()
    {
        var page = RenderTimer(await V60());

        var steps = page.FindAll("#timer-steps li");
        steps.Count.ShouldBe(3);
        steps.Select(s => s.QuerySelector(".dialed-timer-step-at")!.TextContent).ShouldBe(["0:00", "0:45", "1:30"]);
        page.FindAll("#timer-steps li[aria-current]").ShouldBeEmpty();

        page.Find("#timer-start").Click();
        await Advance(page, 50);

        var current = page.Find("#timer-steps li[aria-current=step]");
        current.TextContent.ShouldContain("Pour to 150 g");
        page.FindAll("#timer-steps li.done").Count.ShouldBe(1);
    }

    [Fact]
    public async Task Progress_bar_fills_towards_the_recipe_time()
    {
        var page = RenderTimer(await V60());
        page.Find("#timer-start").Click();

        await Advance(page, 90);

        var meter = page.Find("#timer-progress");
        meter.GetAttribute("role").ShouldBe("meter");
        meter.GetAttribute("aria-valuenow").ShouldBe("50");
        meter.GetAttribute("aria-valuetext").ShouldBe("1:30 of 3:00");
    }

    [Fact]
    public async Task Espresso_progress_runs_to_the_top_of_the_shot_band_and_has_no_step_list()
    {
        var page = RenderTimer(await Espresso());

        page.Find("#timer-progress").GetAttribute("aria-valuetext").ShouldBe("0:00 of 0:30");
        page.FindAll("#timer-steps").ShouldBeEmpty();
    }

    // ---- Fakes --------------------------------------------------------------------------------

    private sealed class ManualClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 11, 8, 8, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => Now;

        // The tests move the clock and call Refresh themselves, so the real tick loop must stay quiet.
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) => new SilentTimer();
    }

    private sealed class SilentTimer : ITimer
    {
        public bool Change(TimeSpan dueTime, TimeSpan period) => true;

        public void Dispose()
        {
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class RecordingSound : ISoundCue
    {
        public List<SoundCue> Cues { get; } = [];

        public bool Fail { get; set; }

        public Task PlayAsync(SoundCue cue)
        {
            Cues.Add(cue);
            return Fail ? throw new InvalidOperationException("no audio") : Task.CompletedTask;
        }
    }

    private sealed class RecordingWakeLock : IWakeLock
    {
        public int Acquired { get; private set; }

        public int Released { get; private set; }

        public bool Refuse { get; set; }

        public bool IsHeld => Acquired > Released;

        public Task<bool> AcquireAsync()
        {
            Acquired++;
            return Refuse ? throw new InvalidOperationException("denied") : Task.FromResult(true);
        }

        public Task ReleaseAsync()
        {
            Released++;
            return Task.CompletedTask;
        }
    }
}
