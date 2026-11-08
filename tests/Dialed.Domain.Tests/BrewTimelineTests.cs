using Dialed.Domain.Model;

namespace Dialed.Domain.Tests;

public sealed class BrewTimelineTests
{
    private static readonly RecipeStep[] V60 =
    [
        new(RecipeStepKind.Bloom, 0, 30, 45m),
        new(RecipeStepKind.Pour, 45, 30, 150m),
        new(RecipeStepKind.Stir, 80, 5),
        new(RecipeStepKind.Pour, 90, 40, 240m),
    ];

    [Fact]
    public void At_the_start_the_first_step_runs_with_a_countdown_to_the_second()
    {
        var state = new BrewTimeline(V60, 180).At(0);

        state.CurrentIndex.ShouldBe(0);
        state.Current!.Kind.ShouldBe(RecipeStepKind.Bloom);
        state.Next!.Kind.ShouldBe(RecipeStepKind.Pour);
        state.SecondsToNext.ShouldBe(45);
        state.TargetWaterG.ShouldBe(45m);
        state.IsFinished.ShouldBeFalse();
    }

    [Fact]
    public void Between_steps_the_earlier_step_still_runs()
    {
        var state = new BrewTimeline(V60, 180).At(40);

        state.CurrentIndex.ShouldBe(0);
        state.SecondsToNext.ShouldBe(5);
        state.TargetWaterG.ShouldBe(45m);
    }

    [Fact]
    public void A_step_without_water_keeps_the_previous_target()
    {
        var state = new BrewTimeline(V60, 180).At(82);

        state.Current!.Kind.ShouldBe(RecipeStepKind.Stir);
        state.TargetWaterG.ShouldBe(150m);
        state.Next!.AtS.ShouldBe(90);
        state.SecondsToNext.ShouldBe(8);
    }

    [Fact]
    public void After_the_last_step_there_is_no_next_and_the_time_runs_out_at_the_total()
    {
        var timeline = new BrewTimeline(V60, 180);

        var late = timeline.At(150);
        late.Next.ShouldBeNull();
        late.SecondsToNext.ShouldBeNull();
        late.TargetWaterG.ShouldBe(240m);
        late.IsFinished.ShouldBeFalse();
        timeline.At(180).IsFinished.ShouldBeTrue();
    }

    [Fact]
    public void Total_is_the_later_of_the_recipe_time_and_the_last_step_end()
    {
        new BrewTimeline(V60, 100).TotalS.ShouldBe(130);
        new BrewTimeline(V60, 200).TotalS.ShouldBe(200);
        new BrewTimeline(V60).TotalS.ShouldBe(130);
        new BrewTimeline([], 28).TotalS.ShouldBe(28);
        new BrewTimeline([]).TotalS.ShouldBeNull();
    }

    [Fact]
    public void Before_the_first_step_nothing_runs_yet()
    {
        var state = new BrewTimeline([new RecipeStep(RecipeStepKind.Pour, 10, 20, 100m)]).At(3);

        state.CurrentIndex.ShouldBe(-1);
        state.Current.ShouldBeNull();
        state.Next!.AtS.ShouldBe(10);
        state.SecondsToNext.ShouldBe(7);
        state.TargetWaterG.ShouldBeNull();
    }

    [Fact]
    public void Unordered_steps_are_sorted_and_negative_time_is_zero()
    {
        var timeline = new BrewTimeline([V60[1], V60[0]]);

        timeline.Steps.Select(s => s.AtS).ShouldBe([0, 45]);
        timeline.At(-5).ElapsedS.ShouldBe(0);
    }

    [Fact]
    public void Step_start_is_detected_between_two_moments()
    {
        var timeline = new BrewTimeline(V60, 180);

        timeline.StepStartedBetween(44, 45).ShouldBeTrue();
        timeline.StepStartedBetween(45, 46).ShouldBeFalse();
        timeline.StepStartedBetween(0, 100).ShouldBeTrue();
    }

    [Fact]
    public void For_a_recipe_uses_its_steps_and_time()
    {
        var timeline = BrewTimeline.For(new Recipe { MethodCode = "v60", Name = "x", Steps = V60, TimeS = 180 });

        timeline.Steps.Count.ShouldBe(4);
        timeline.TotalS.ShouldBe(180);
    }

    [Fact]
    public void Shot_band_is_25_to_30_by_default_and_follows_the_recipe_time()
    {
        ShotBand.For(null).ShouldBe(new ShotBand(25, 30));
        ShotBand.For(new Recipe { MethodCode = "espresso", Name = "x" }).ShouldBe(new ShotBand(25, 30));
        ShotBand.For(new Recipe { MethodCode = "espresso", Name = "x", TimeS = 28 }).ShouldBe(new ShotBand(25, 30));
        ShotBand.For(new Recipe { MethodCode = "espresso", Name = "x", TimeS = 35 }).ShouldBe(new ShotBand(32, 37));
    }

    [Theory]
    [InlineData(10, ShotStatus.TooShort)]
    [InlineData(24, ShotStatus.TooShort)]
    [InlineData(25, ShotStatus.InBand)]
    [InlineData(30, ShotStatus.InBand)]
    [InlineData(31, ShotStatus.TooLong)]
    public void Shot_status(int elapsed, ShotStatus expected) => ShotBand.Default.StatusAt(elapsed).ShouldBe(expected);
}
