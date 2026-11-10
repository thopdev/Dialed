using Dialed.Domain.Model;

namespace Dialed.Domain.Tests;

/// <summary>What a friend's item becomes in my household (#77).</summary>
public sealed class FriendCopyTests
{
    private static readonly CopiedFrom From = new("carol", "Carol", "bean-1");

    private static Bean TheirBean() => new()
    {
        Id = "bean-1",
        HouseholdId = "their-household",
        CreatedBy = "carol",
        UpdatedBy = "carol",
        CreatedAt = DateTimeOffset.UnixEpoch,
        UpdatedAt = DateTimeOffset.UnixEpoch,
        Version = 7,
        Name = "Kenya AA",
        RoasterId = "their-vendor",
        Origin = new BeanOrigin("Kenya", "Kiambu"),
        Varieties = ["SL28"],
        Process = "Washed",
        TastingNotes = ["blackcurrant"],
        PhotoIds = ["photo-1"],
        Rating = 5,
        SourceScanId = "scan-1",
        Visibility = Visibility.Friends,
    };

    [Fact]
    public void A_copied_bean_is_a_fresh_record_with_the_facts_kept()
    {
        var copy = FriendCopy.OfBean(TheirBean(), From, "my-vendor", Visibility.Household);

        copy.Id.ShouldNotBe("bean-1");
        copy.Version.ShouldBe(0);
        copy.HouseholdId.ShouldBeEmpty();
        copy.CreatedBy.ShouldBeEmpty();
        copy.Name.ShouldBe("Kenya AA");
        copy.Origin.Country.ShouldBe("Kenya");
        copy.Varieties.ShouldBe(["SL28"]);
        copy.TastingNotes.ShouldBe(["blackcurrant"]);
        copy.Process.ShouldBe("Washed");
        copy.RoasterId.ShouldBe("my-vendor");
        copy.CopiedFrom.ShouldBe(From);
    }

    [Fact]
    public void A_copied_bean_leaves_photos_rating_and_scan_behind_and_follows_my_visibility()
    {
        var copy = FriendCopy.OfBean(TheirBean(), From, null, Visibility.Private);

        copy.PhotoIds.ShouldBeEmpty();
        copy.Rating.ShouldBeNull();
        copy.SourceScanId.ShouldBeNull();
        copy.Visibility.ShouldBe(Visibility.Private);
        copy.RoasterId.ShouldBeNull();
    }

    [Fact]
    public void A_copied_recipe_drops_their_gear_versions_and_favourite()
    {
        var theirs = new Recipe
        {
            Id = "recipe-1",
            Version = 3,
            MethodCode = "v60",
            Name = "Morning",
            BeanId = "their-bean",
            GrinderId = "their-grinder",
            BrewerId = "their-brewer",
            DoseG = 15m,
            WaterG = 250m,
            GrindSetting = 22m,
            TempC = 94m,
            Steps = [new RecipeStep(RecipeStepKind.Bloom, 0, 45, 45m)],
            ParentRecipeId = "older",
            IsSuperseded = true,
            IsFavourite = true,
            IsDefaultTemplate = true,
        };

        var copy = FriendCopy.OfRecipe(theirs, new CopiedFrom("carol", "Carol", "recipe-1"), "my-bean", Visibility.Household);

        copy.Id.ShouldNotBe("recipe-1");
        copy.Version.ShouldBe(0);
        copy.BeanId.ShouldBe("my-bean");
        copy.GrinderId.ShouldBeNull();
        copy.BrewerId.ShouldBeNull();
        copy.ParentRecipeId.ShouldBeNull();
        copy.IsSuperseded.ShouldBeFalse();
        copy.IsFavourite.ShouldBeFalse();
        copy.IsDefaultTemplate.ShouldBeFalse();
        copy.DoseG.ShouldBe(15m);
        copy.GrindSetting.ShouldBe(22m);
        copy.Steps.Count.ShouldBe(1);
        copy.CopiedFrom!.ItemId.ShouldBe("recipe-1");
    }

    [Theory]
    [InlineData("carol", "bean-1", true)]
    [InlineData("carol", "bean-2", false)]
    [InlineData("dave", "bean-1", false)]
    public void The_same_friend_item_is_recognised(string ownerId, string itemId, bool expected)
    {
        FriendCopy.IsCopyOf(From, ownerId, itemId).ShouldBe(expected);
    }

    [Fact]
    public void My_own_items_are_not_copies()
    {
        FriendCopy.IsCopyOf(null, "carol", "bean-1").ShouldBeFalse();
    }
}
