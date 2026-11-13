using Bunit;
using Dialed.UI.Components;

namespace Dialed.UI.Tests;

public sealed class EspressoPrepTests : UiTestContext
{
    [Theory]
    [InlineData(EspressoPhase.Grind, "dialed-espresso-grain")]
    [InlineData(EspressoPhase.Wdt, "dialed-espresso-needle")]
    [InlineData(EspressoPhase.Shot, "dialed-pour-cup-coffee")]
    public void Each_phase_draws_its_own_part(EspressoPhase phase, string expected)
    {
        var figure = Render<EspressoFigure>(p => p.Add(x => x.Phase, phase).Add(x => x.Fraction, 0.5));

        figure.FindAll("." + expected).ShouldNotBeEmpty();
        figure.Find("svg").GetAttribute("aria-label").ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public void The_shot_gets_crema_when_it_reaches_its_window()
    {
        Render<EspressoFigure>(p => p.Add(x => x.Phase, EspressoPhase.Shot).Add(x => x.Finished, true)).FindAll(".dialed-pour-crema").Count.ShouldBe(1);
        Render<EspressoFigure>(p => p.Add(x => x.Phase, EspressoPhase.Shot)).FindAll(".dialed-pour-crema").ShouldBeEmpty();
    }
}
