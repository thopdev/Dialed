using Dialed.Domain.DialIn;
using Dialed.Domain.Model;

namespace Dialed.Domain.Tests;

public sealed class DialInCoachTests
{
    private static Brew B(int day, int? rating, string method = "v60", Taste? taste = null, string bag = "b1") => new()
    {
        BagId = bag,
        MethodCode = method,
        DoseG = 18m,
        BrewedAt = new DateTimeOffset(2026, 1, day, 8, 0, 0, TimeSpan.Zero),
        Rating = rating,
        Taste = taste ?? new Taste(),
    };

    [Fact]
    public void No_brews_means_not_started() =>
        DialInCoach.Build("b1", [B(1, 3, bag: "other")]).Stage.ShouldBe(CoachStage.NotStarted);

    [Fact]
    public void Low_ratings_keep_working_and_count_tries_and_best()
    {
        var progress = DialInCoach.Build("b1", [B(1, 2), B(2, 3), B(3, null)]);

        progress.Stage.ShouldBe(CoachStage.Working);
        progress.Attempts.ShouldBe(3);
        progress.BestRating.ShouldBe(3);
        progress.MethodCode.ShouldBe("v60");
    }

    [Fact]
    public void A_good_balanced_latest_brew_is_dialed_in() =>
        DialInCoach.Build("b1", [B(1, 2), B(2, 4)]).Stage.ShouldBe(CoachStage.DialedIn);

    [Fact]
    public void A_good_rating_with_an_off_taste_is_not_there_yet() =>
        DialInCoach.Build("b1", [B(1, 5, taste: new Taste(Acidity: 1))]).Stage.ShouldBe(CoachStage.Working);

    [Fact]
    public void Only_the_method_of_the_latest_brew_counts()
    {
        var progress = DialInCoach.Build("b1", [B(1, 5, "espresso"), B(2, 2), B(3, 3)]);

        progress.Attempts.ShouldBe(2);
        progress.BestRating.ShouldBe(3);
    }
}
