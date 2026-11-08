namespace Dialed.Client.Tests;

public sealed class BrewTimerHandoffTests
{
    [Fact]
    public void The_time_is_given_once_to_the_recipe_it_was_timed_for()
    {
        var handoff = new BrewTimerHandoff();
        handoff.Set("r1", 183);

        handoff.Take("r1").ShouldBe(183);
        handoff.Take("r1").ShouldBeNull();
    }

    [Fact]
    public void Another_recipe_does_not_get_it_and_clears_it()
    {
        var handoff = new BrewTimerHandoff();
        handoff.Set("r1", 183);

        handoff.Take("r2").ShouldBeNull();
        handoff.Take("r1").ShouldBeNull();
    }

    [Fact]
    public void Nothing_set_gives_nothing() => new BrewTimerHandoff().Take(null).ShouldBeNull();
}
