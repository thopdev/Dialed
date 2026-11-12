using Dialed.Domain.Flavors;

namespace Dialed.Domain.Tests;

public sealed class FlavorNotesTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Every_note_offered_for_a_branch_is_understood_as_that_branch(bool dutch)
    {
        foreach (var category in Enum.GetValues<FlavorCategory>())
        {
            var notes = FlavorWheel.NotesOf(category, dutch);
            notes.Count.ShouldBe(8);
            foreach (var note in notes)
            {
                FlavorWheel.Classify(note).ShouldBe(category, $"{note} ({(dutch ? "nl" : "en")})");
            }
        }
    }
}
