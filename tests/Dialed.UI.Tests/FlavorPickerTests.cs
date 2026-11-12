using Bunit;
using Dialed.UI.Components;

namespace Dialed.UI.Tests;

public sealed class FlavorPickerTests : UiTestContext
{
    [Fact]
    public void The_wheel_starts_with_the_eight_branches_and_zooms_into_one()
    {
        var picker = Render<FlavorPicker>();

        picker.FindAll(".dialed-fw-slice").Count.ShouldBe(8);
        picker.Find("[data-slice='Fruity']").Click();

        picker.FindAll(".dialed-fw-slice").Select(s => s.GetAttribute("data-slice")).ShouldBe(["blueberry", "strawberry", "raspberry", "cherry", "citrus", "apple", "peach", "grape"]);
        picker.Find("#" + picker.Find(".dialed-flavor-picker").Id + "-hint").TextContent.ShouldContain("Fruity");
    }

    [Fact]
    public void Picking_notes_in_the_zoom_adds_and_removes_them_and_the_middle_goes_back()
    {
        IReadOnlyList<string> values = [];
        var picker = Render<FlavorPicker>(p => p.Add(x => x.Values, values).Add(x => x.ValuesChanged, v => { values = v; }));

        picker.Find("[data-slice='Fruity']").Click();
        picker.Find("[data-slice='blueberry']").Click();
        values.ShouldBe(["blueberry"]);

        picker.Render(p => p.Add(x => x.Values, values));
        picker.Find("[data-slice='blueberry']").GetAttribute("aria-pressed").ShouldBe("true");
        picker.Find("[data-slice='blueberry']").Click();
        values.ShouldBeEmpty();

        picker.Find(".dialed-fw-centre").Click();
        picker.FindAll(".dialed-fw-slice").Count.ShouldBe(8);
    }

    [Fact]
    public void A_branch_with_a_note_is_marked_on_in_the_overview()
    {
        var picker = Render<FlavorPicker>(p => p.Add(x => x.Values, ["caramel"]));

        picker.Find("[data-slice='Sweet']").ClassList.ShouldContain("on");
        picker.Find("[data-slice='Fruity']").ClassList.ShouldNotContain("on");
        picker.Find(".dialed-fw-core-title").TextContent.ShouldBe("1 tasted");
    }

    [Fact]
    public void Slices_are_keyboard_operable()
    {
        var picker = Render<FlavorPicker>();

        picker.Find("[data-slice='Floral']").KeyDown(new Microsoft.AspNetCore.Components.Web.KeyboardEventArgs { Key = "Enter" });

        picker.FindAll(".dialed-fw-slice").First().GetAttribute("data-slice").ShouldBe("jasmine");
    }
}
