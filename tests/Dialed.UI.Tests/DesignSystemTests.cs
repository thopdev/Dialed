using Bunit;
using Dialed.UI.Components;
using Dialed.UI.Pages;
using Microsoft.AspNetCore.Components;

namespace Dialed.UI.Tests;

public sealed class StarRatingTests : UiTestContext
{
    [Fact]
    public void Editable_rating_is_a_radio_group_with_one_button_per_star()
    {
        var rating = Render<StarRating>(p => p.Add(c => c.Value, 3));

        rating.Find("[role=radiogroup]").GetAttribute("aria-label").ShouldBe("Rating");
        var stars = rating.FindAll("button[role=radio]");
        stars.Count.ShouldBe(5);
        stars.Select(s => s.GetAttribute("aria-checked")).ShouldBe(["false", "false", "true", "false", "false"]);
        stars[3].GetAttribute("aria-label").ShouldBe("4 of 5 stars");
        rating.FindAll(".dialed-star.on").Count.ShouldBe(3);
    }

    [Fact]
    public void Tapping_a_star_sets_the_value()
    {
        var value = 0;
        var rating = Render<StarRating>(p => p
            .Add(c => c.Value, 0)
            .Add(c => c.ValueChanged, EventCallback.Factory.Create<int>(this, v => value = v)));

        rating.FindAll("button[role=radio]")[3].Click();

        value.ShouldBe(4);
    }

    [Fact]
    public void Tapping_the_current_rating_clears_it()
    {
        var value = 4;
        var rating = Render<StarRating>(p => p
            .Add(c => c.Value, 4)
            .Add(c => c.ValueChanged, EventCallback.Factory.Create<int>(this, v => value = v)));

        rating.FindAll("button[role=radio]")[3].Click();

        value.ShouldBe(0);
    }

    [Fact]
    public void Read_only_rating_is_an_image_with_a_text_alternative_and_no_buttons()
    {
        var rating = Render<StarRating>(p => p.Add(c => c.Value, 4).Add(c => c.ReadOnly, true));

        rating.Find("[role=img]").GetAttribute("aria-label").ShouldBe("4 of 5 stars");
        rating.FindAll("button").ShouldBeEmpty();
        rating.FindAll(".dialed-star.on").Count.ShouldBe(4);
    }
}

public sealed class TasteSliderTests : UiTestContext
{
    [Fact]
    public void Shows_label_and_both_ends_and_links_label_to_input()
    {
        var slider = Render<TasteSlider>(p => p
            .Add(c => c.Label, "Acidity")
            .Add(c => c.LowLabel, "Sour")
            .Add(c => c.HighLabel, "Bitter"));

        var input = slider.Find("input[type=range]");
        slider.Find("label").GetAttribute("for").ShouldBe(input.Id);
        slider.FindAll(".dialed-taste-end").Select(e => e.TextContent).ShouldBe(["Sour", "Bitter"]);
        input.GetAttribute("min").ShouldBe("0");
        input.GetAttribute("max").ShouldBe("10");
    }

    [Theory]
    [InlineData(5, "Sour / Bitter: balanced")]
    [InlineData(2, "Sour 3")]
    [InlineData(9, "Bitter 4")]
    public void Describes_the_value_for_screen_readers(int value, string expected)
    {
        var slider = Render<TasteSlider>(p => p
            .Add(c => c.Label, "Taste")
            .Add(c => c.LowLabel, "Sour")
            .Add(c => c.HighLabel, "Bitter")
            .Add(c => c.Value, value));

        slider.Find("input").GetAttribute("aria-valuetext").ShouldBe(expected);
    }

    [Theory]
    [InlineData("7", 7)]
    [InlineData("99", 10)]
    [InlineData("-4", 0)]
    public void Reports_changes_within_the_range(string input, int expected)
    {
        var value = -1;
        var slider = Render<TasteSlider>(p => p
            .Add(c => c.Label, "Taste")
            .Add(c => c.LowLabel, "Sour")
            .Add(c => c.HighLabel, "Bitter")
            .Add(c => c.ValueChanged, EventCallback.Factory.Create<int>(this, v => value = v)));

        slider.Find("input").Change(input);

        value.ShouldBe(expected);
    }

    [Fact]
    public void Ignores_a_value_that_is_not_a_number()
    {
        var called = false;
        var slider = Render<TasteSlider>(p => p
            .Add(c => c.Label, "Taste")
            .Add(c => c.LowLabel, "Sour")
            .Add(c => c.HighLabel, "Bitter")
            .Add(c => c.ValueChanged, EventCallback.Factory.Create<int>(this, _ => called = true)));

        slider.Find("input").Change("abc");

        called.ShouldBeFalse();
    }
}

public sealed class ChipTests : UiTestContext
{
    [Fact]
    public void Chip_with_a_click_handler_toggles_and_reports_its_state()
    {
        var clicks = 0;
        var chip = Render<Chip>(p => p
            .Add(c => c.Selected, true)
            .Add(c => c.OnClick, EventCallback.Factory.Create<Microsoft.AspNetCore.Components.Web.MouseEventArgs>(this, _ => clicks++))
            .AddChildContent("V60"));

        var button = chip.Find("button.dialed-chip");
        button.GetAttribute("aria-pressed").ShouldBe("true");
        button.ClassList.ShouldContain("selected");
        button.Click();

        clicks.ShouldBe(1);
    }

    [Fact]
    public void Chip_without_a_handler_is_a_plain_label()
    {
        var chip = Render<Chip>(p => p.AddChildContent("Washed"));

        chip.FindAll("button").ShouldBeEmpty();
        chip.Find("span.dialed-chip").TextContent.ShouldBe("Washed");
    }

    [Fact]
    public void Chip_group_labels_its_chips()
    {
        var group = Render<ChipGroup>(p => p.Add(c => c.Label, "Brew method").AddChildContent("<span>x</span>"));

        group.Find("[role=group]").GetAttribute("aria-label").ShouldBe("Brew method");
    }
}

public sealed class SurfaceTests : UiTestContext
{
    [Fact]
    public void Card_is_a_link_only_with_an_href()
    {
        Render<Card>(p => p.AddChildContent("x")).FindAll("a").ShouldBeEmpty();
        Render<Card>(p => p.Add(c => c.Href, "beans").AddChildContent("x")).Find("a.dialed-card-link").GetAttribute("href").ShouldBe("beans");
    }

    [Fact]
    public void Card_passes_extra_attributes_through()
    {
        var card = Render<Card>(p => p.AddUnmatched("data-test", "yes").AddChildContent("x"));

        card.Find(".dialed-card").GetAttribute("data-test").ShouldBe("yes");
    }

    [Fact]
    public void Row_shows_title_subtitle_and_slots()
    {
        var row = Render<ListRow>(p => p
            .Add(c => c.Title, "Ethiopia")
            .Add(c => c.Subtitle, "Light roast")
            .Add(c => c.Leading, b => b.AddContent(0, "L"))
            .Add(c => c.Trailing, b => b.AddContent(0, "T")));

        row.Find(".dialed-row-title").TextContent.ShouldBe("Ethiopia");
        row.Find(".dialed-row-subtitle").TextContent.ShouldBe("Light roast");
        row.Find(".dialed-row-leading").TextContent.ShouldBe("L");
        row.Find(".dialed-row-trailing").TextContent.ShouldBe("T");
        row.FindAll(".dialed-row-chevron").ShouldBeEmpty();
        row.FindAll("a").ShouldBeEmpty();
    }

    [Fact]
    public void Row_with_href_is_a_link_with_a_chevron()
    {
        var row = Render<ListRow>(p => p.Add(c => c.Title, "Ethiopia").Add(c => c.Href, "beans/1"));

        row.Find("a.dialed-row-link").GetAttribute("href").ShouldBe("beans/1");
        row.Find(".dialed-row-chevron").GetAttribute("aria-hidden").ShouldBe("true");
        row.FindAll(".dialed-row-subtitle").ShouldBeEmpty();
    }

    [Fact]
    public void Empty_state_shows_icon_title_and_message_when_given()
    {
        var plain = Render<EmptyState>(p => p.Add(c => c.Message, "Nothing"));
        var full = Render<EmptyState>(p => p
            .Add(c => c.Message, "Nothing")
            .Add(c => c.Title, "Empty")
            .Add(c => c.Icon, new Microsoft.FluentUI.AspNetCore.Components.Icons.Regular.Size24.Box()));

        plain.FindAll(".dialed-empty-icon, .dialed-empty-title").ShouldBeEmpty();
        full.Find(".dialed-empty-title").TextContent.ShouldBe("Empty");
        full.Find(".dialed-empty-icon").ShouldNotBeNull();
        full.Find("p").TextContent.ShouldBe("Nothing");
    }
}

public sealed class FeedbackTests : UiTestContext
{
    [Theory]
    [InlineData(Skeleton.SkeletonShape.Line, "dialed-skeleton-line")]
    [InlineData(Skeleton.SkeletonShape.Circle, "dialed-skeleton-circle")]
    [InlineData(Skeleton.SkeletonShape.Card, "dialed-skeleton-card")]
    public void Skeleton_has_a_shape_and_announces_loading(Skeleton.SkeletonShape shape, string cssClass)
    {
        var skeleton = Render<Skeleton>(p => p.Add(c => c.Shape, shape));

        var element = skeleton.Find(".dialed-skeleton");
        element.ClassList.ShouldContain(cssClass);
        element.GetAttribute("role").ShouldBe("status");
        element.GetAttribute("aria-label").ShouldBe("Loading…");
    }

    [Fact]
    public void Skeleton_applies_the_requested_size()
    {
        var skeleton = Render<Skeleton>(p => p.Add(c => c.Width, "60%").Add(c => c.Height, "2rem"));

        skeleton.Find(".dialed-skeleton").GetAttribute("style").ShouldBe("width:60%;height:2rem");
    }

    [Fact]
    public void Banner_is_an_alert_with_message_and_retry()
    {
        var retries = 0;
        var banner = Render<ErrorBanner>(p => p
            .Add(c => c.Message, "Couldn't sync.")
            .Add(c => c.OnRetry, EventCallback.Factory.Create(this, () => retries++)));

        banner.Find("[role=alert]").TextContent.ShouldContain("Couldn't sync.");
        banner.Find("button.dialed-banner-action:not(.dialed-banner-dismiss)").Click();

        retries.ShouldBe(1);
    }

    [Fact]
    public void Banner_can_be_dismissed_and_comes_back_with_a_new_message()
    {
        var banner = Render<ErrorBanner>(p => p.Add(c => c.Message, "First"));

        banner.Find(".dialed-banner-dismiss").Click();
        banner.FindAll(".dialed-banner").ShouldBeEmpty();

        banner.Render(p => p.Add(c => c.Message, "Second"));

        banner.Find(".dialed-banner").TextContent.ShouldContain("Second");
    }

    [Fact]
    public void Banner_without_retry_or_dismiss_shows_no_buttons_and_warning_has_its_own_style()
    {
        var banner = Render<ErrorBanner>(p => p
            .Add(c => c.Message, "Offline")
            .Add(c => c.Severity, ErrorBanner.BannerSeverity.Warning)
            .Add(c => c.Dismissible, false));

        banner.FindAll("button").ShouldBeEmpty();
        banner.Find(".dialed-banner").ClassList.ShouldContain("dialed-banner-warning");
    }
}

public sealed class GalleryTests : UiTestContext
{
    [Fact]
    public void Gallery_shows_every_component_in_development()
    {
        Environment.IsDevelopment = true;

        var gallery = Render<Gallery>();

        gallery.Find("h1").TextContent.ShouldBe("Component gallery");
        gallery.FindAll(".dialed-card").Count.ShouldBeGreaterThan(0);
        gallery.FindAll(".dialed-row").Count.ShouldBeGreaterThan(0);
        gallery.FindAll(".dialed-stars").Count.ShouldBeGreaterThan(0);
        gallery.FindAll(".dialed-taste").Count.ShouldBeGreaterThan(0);
        gallery.FindAll(".dialed-chip").Count.ShouldBeGreaterThan(0);
        gallery.FindAll(".dialed-skeleton").Count.ShouldBeGreaterThan(0);
        gallery.FindAll(".dialed-banner").Count.ShouldBeGreaterThan(0);
    }

    [Fact]
    public void Gallery_is_hidden_in_production()
    {
        Environment.IsDevelopment = false;

        var gallery = Render<Gallery>();

        gallery.FindAll(".dialed-gallery").ShouldBeEmpty();
        gallery.Markup.ShouldContain("nothing here");
    }

    [Fact]
    public void Gallery_chips_and_rating_are_interactive()
    {
        Environment.IsDevelopment = true;
        var gallery = Render<Gallery>();

        gallery.FindAll("button.dialed-chip").First(c => c.TextContent == "Espresso").Click();
        gallery.FindAll("button.dialed-chip").First(c => c.TextContent == "Espresso").ClassList.ShouldContain("selected");

        gallery.FindAll(".dialed-stars-edit button[role=radio]")[4].Click();
        gallery.Markup.ShouldContain("Rating: 5");
    }

    [Fact]
    public void More_page_links_to_the_gallery_only_in_development()
    {
        Environment.IsDevelopment = false;
        Render<More>().FindAll("a[href='dev/gallery']").ShouldBeEmpty();

        Environment.IsDevelopment = true;
        Render<More>().Find("a[href='dev/gallery']").ShouldNotBeNull();
    }
}
