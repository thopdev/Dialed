using Dialed.Domain.BrewMethods;
using Dialed.Domain.Model;

namespace Dialed.Domain.Tests;

public sealed class RecipeVersionsTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 1, 0, 0, 0, TimeSpan.Zero);

    private static Recipe R(string id, string? parent = null, int day = 0, bool superseded = false) =>
        new() { Id = id, MethodCode = "v60", Name = "x", ParentRecipeId = parent, IsSuperseded = superseded, CreatedAt = T0.AddDays(day) };

    private static Brew Br(string recipeId, int? rating = null, bool deleted = false) =>
        new() { BagId = "b", MethodCode = "v60", DoseG = 15m, RecipeId = recipeId, Rating = rating, Deleted = deleted };

    [Fact]
    public void A_recipe_without_versions_is_a_family_of_one()
    {
        var family = RecipeVersions.Family(R("1"), [R("1"), R("9")]);

        family.Select(r => r.Id).ShouldBe(["1"]);
        RecipeVersions.Current(family)!.Id.ShouldBe("1");
    }

    [Fact]
    public void Family_follows_parents_and_children_from_any_member_oldest_first()
    {
        Recipe[] all = [R("3", "2", 2), R("1", null, 0, true), R("2", "1", 1, true), R("other", null, 5)];

        foreach (var start in all.Take(3))
        {
            RecipeVersions.Family(start, all).Select(r => r.Id).ShouldBe(["1", "2", "3"]);
        }
    }

    [Fact]
    public void Current_is_the_version_that_is_not_superseded()
    {
        Recipe[] family = [R("1", null, 0, true), R("2", "1", 1, false)];

        RecipeVersions.Current(family)!.Id.ShouldBe("2");
    }

    [Fact]
    public void Saving_as_a_new_version_links_to_the_old_one_and_supersedes_it()
    {
        var current = R("1") with { DoseG = 15m, IsFavourite = true };
        var edited = current with { DoseG = 16m };

        var (created, superseded) = RecipeVersions.NewVersion(current, edited);

        created.Id.ShouldNotBe("1");
        created.ParentRecipeId.ShouldBe("1");
        created.DoseG.ShouldBe(16m);
        created.IsSuperseded.ShouldBeFalse();
        created.IsFavourite.ShouldBeTrue();
        created.Version.ShouldBe(0);
        superseded.Id.ShouldBe("1");
        superseded.IsSuperseded.ShouldBeTrue();
        superseded.DoseG.ShouldBe(15m);
    }

    [Fact]
    public void Making_an_older_version_current_supersedes_the_others()
    {
        Recipe[] family = [R("1", null, 0, true), R("2", "1", 1, true), R("3", "2", 2, false)];

        var changes = RecipeVersions.MakeCurrent(family[0], family);

        changes.Select(r => (r.Id, r.IsSuperseded)).ShouldBe([("1", false), ("3", true)]);
        RecipeVersions.MakeCurrent(family[2], family).ShouldBeEmpty();
    }

    [Fact]
    public void Stats_average_the_rated_brews_of_a_version()
    {
        Brew[] brews = [Br("1", 4), Br("1", 5), Br("1"), Br("1", 1, deleted: true), Br("2", 3)];

        RecipeVersions.Stats("1", brews).ShouldBe(new VersionStats(4.5m, 3));
        RecipeVersions.Stats("2", brews).ShouldBe(new VersionStats(3m, 1));
        RecipeVersions.Stats("none", brews).ShouldBe(new VersionStats(null, 0));
        RecipeVersions.Stats("1", [Br("1")]).AverageRating.ShouldBeNull();
    }

    [Fact]
    public void HasBrews_ignores_deleted_brews()
    {
        RecipeVersions.HasBrews("1", [Br("1")]).ShouldBeTrue();
        RecipeVersions.HasBrews("1", [Br("1", deleted: true), Br("2")]).ShouldBeFalse();
    }

    [Fact]
    public void Diff_lists_only_what_changed()
    {
        var from = R("1") with { DoseG = 15m, WaterG = 240m, GrindSetting = 20m, TempC = 94m };
        var to = from with { GrindSetting = 18m, WaterG = 250m };

        var diff = RecipeVersions.Diff(from, to);

        diff.Parameters.Select(p => (p.Parameter, p.Planned, p.Actual)).ShouldBe(
            [(BrewParameter.Water, (decimal?)240m, (decimal?)250m), (BrewParameter.Grind, (decimal?)20m, (decimal?)18m)]);
        diff.StepsChanged.ShouldBeFalse();
        diff.GrinderChanged.ShouldBeFalse();
        diff.IsEmpty.ShouldBeFalse();
        RecipeVersions.Diff(from, from).IsEmpty.ShouldBeTrue();
    }

    [Fact]
    public void Diff_notices_steps_and_grinder()
    {
        var from = R("1") with { Steps = [new RecipeStep(RecipeStepKind.Bloom, 0, 30, 45m)], GrinderId = "g1" };

        var diff = RecipeVersions.Diff(from, from with { Steps = [new RecipeStep(RecipeStepKind.Bloom, 0, 40, 45m)], GrinderId = "g2" });

        diff.StepsChanged.ShouldBeTrue();
        diff.GrinderChanged.ShouldBeTrue();
        diff.Parameters.ShouldBeEmpty();
    }
}
