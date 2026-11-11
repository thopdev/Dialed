using Dialed.Domain.Flavors;
using Dialed.Domain.Model;
using Dialed.Domain.Statistics;

namespace Dialed.Domain.Tests;

public sealed class FlavorWheelTests
{
    [Theory]
    [InlineData("blueberry", FlavorCategory.Fruity)]
    [InlineData("Blueberries", FlavorCategory.Fruity)]
    [InlineData("red berries", FlavorCategory.Fruity)]
    [InlineData("  JASMINE ", FlavorCategory.Floral)]
    [InlineData("black tea", FlavorCategory.Floral)]
    [InlineData("dark chocolate", FlavorCategory.NuttyCocoa)]
    [InlineData("hazelnut", FlavorCategory.NuttyCocoa)]
    [InlineData("brown sugar", FlavorCategory.Sweet)]
    [InlineData("honey", FlavorCategory.Sweet)]
    [InlineData("cinnamon", FlavorCategory.Spices)]
    [InlineData("smoky", FlavorCategory.Roasted)]
    [InlineData("herbal", FlavorCategory.GreenVegetative)]
    [InlineData("winey", FlavorCategory.SourFermented)]
    [InlineData("bosbes", FlavorCategory.Fruity)]
    [InlineData("jasmijn", FlavorCategory.Floral)]
    [InlineData("pure chocolade", FlavorCategory.NuttyCocoa)]
    [InlineData("karamel", FlavorCategory.Sweet)]
    public void Sorts_a_note_into_its_branch_of_the_wheel(string note, FlavorCategory expected)
    {
        FlavorWheel.Classify(note).ShouldBe(expected);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("mysterious")]
    public void A_note_it_cannot_place_is_left_out(string? note)
    {
        FlavorWheel.Classify(note).ShouldBeNull();
    }

    [Fact]
    public void Tallies_the_biggest_branch_first_and_skips_what_it_cannot_place()
    {
        var tally = FlavorWheel.Tally(["blueberry", "peach", "jasmine", "mysterious", "cherry", "honey", "jasmine"]);

        tally.ShouldBe([new(FlavorCategory.Fruity, 3), new(FlavorCategory.Floral, 2), new(FlavorCategory.Sweet, 1)]);
    }

    [Fact]
    public void Statistics_count_a_beans_notes_once_per_brew()
    {
        var bean = new Bean { Id = "bean", Name = "Guji", TastingNotes = ["blueberry", "jasmine"] };
        var bag = new Bag { Id = "bag", BeanId = "bean", WeightG = 250m };
        Brew Brewed(int day) => new() { BagId = "bag", MethodCode = "v60", DoseG = 18m, BrewedAt = new DateTimeOffset(2026, 11, day, 8, 0, 0, TimeSpan.Zero) };

        var stats = CoffeeStatistics.Calculate([Brewed(1), Brewed(2), Brewed(3)], [bag], [bean], [], [], new DateOnly(2026, 11, 10));

        stats.Flavours.ShouldBe([new(FlavorCategory.Fruity, 3), new(FlavorCategory.Floral, 3)]);
    }
}
