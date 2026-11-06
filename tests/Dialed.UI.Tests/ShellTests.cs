using Bunit;
using Dialed.UI.Components;
using Dialed.UI.Layout;
using Microsoft.AspNetCore.Components;

namespace Dialed.UI.Tests;

public sealed class ShellTests : UiTestContext
{
    [Fact]
    public void Layout_has_sidebar_content_and_tab_bar()
    {
        var layout = Render<MainLayout>(parameters => parameters.Add(p => p.Body, Text("Hello")));

        layout.Find(".dialed-sidebar").ShouldNotBeNull();
        layout.Find(".dialed-tabbar").ShouldNotBeNull();
        layout.Find("main.dialed-content").TextContent.ShouldContain("Hello");
    }

    [Fact]
    public void A_failing_screen_shows_the_error_panel_and_can_retry()
    {
        Thrower.Fail = true;
        var layout = Render<MainLayout>(parameters => parameters.Add(p => p.Body, builder =>
        {
            builder.OpenComponent<Thrower>(0);
            builder.CloseComponent();
        }));

        layout.Find(".dialed-error h1").TextContent.ShouldBe("Something went wrong");
        layout.Find(".dialed-sidebar").ShouldNotBeNull(); // navigation stays usable

        Thrower.Fail = false;
        layout.Find(".dialed-error fluent-button").Click();

        layout.WaitForAssertion(() => layout.Find("main.dialed-content").TextContent.ShouldContain("recovered"));
    }

    [Fact]
    public void Loading_shows_a_status_until_done()
    {
        var loading = Render<Loading>(parameters => parameters
            .Add(p => p.IsLoading, true)
            .Add(p => p.ChildContent, Text("Ready")));

        loading.Find("[role=status]").TextContent.ShouldContain("Loading…");
        loading.Markup.ShouldNotContain("Ready");

        loading.Render(parameters => parameters.Add(p => p.IsLoading, false));

        loading.Markup.ShouldContain("Ready");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void List_detail_marks_a_selection(bool hasSelection)
    {
        var layout = Render<ListDetailLayout>(parameters => parameters
            .Add(p => p.HasSelection, hasSelection)
            .Add(p => p.List, Text("list"))
            .Add(p => p.Detail, Text("detail")));

        layout.Find(".dialed-list-detail").ClassList.Contains("has-selection").ShouldBe(hasSelection);
        layout.Find(".dialed-list-pane").TextContent.ShouldBe("list");
        layout.Find(".dialed-detail-pane").TextContent.ShouldBe("detail");
    }

    [Fact]
    public void Page_header_shows_a_back_link_only_when_asked()
    {
        var plain = Render<PageHeader>(parameters => parameters.Add(p => p.Title, "Beans"));
        var withBack = Render<PageHeader>(parameters => parameters.Add(p => p.Title, "Bag").Add(p => p.BackHref, "beans"));

        plain.FindAll("a.dialed-back").ShouldBeEmpty();
        withBack.Find("a.dialed-back").GetAttribute("href").ShouldBe("beans");
        withBack.Find("h1").TextContent.ShouldBe("Bag");
    }

    private static RenderFragment Text(string text) => builder => builder.AddContent(0, text);

    private sealed class Thrower : ComponentBase
    {
        public static bool Fail { get; set; }

        protected override void BuildRenderTree(Microsoft.AspNetCore.Components.Rendering.RenderTreeBuilder builder)
        {
            if (Fail)
            {
                throw new InvalidOperationException("boom");
            }

            builder.AddContent(0, "recovered");
        }
    }
}
