using Dialed.Domain.Model;

namespace Dialed.Domain.Tests;

public sealed class SimilarBeanRecipesTests
{
    private static Bean Bean(string name, string? country = null, string? process = null) =>
        new() { Name = name, Origin = new BeanOrigin(country), Process = process };

    private static Recipe Recipe(Bean bean, string method = "v60", string name = "R") =>
        new() { MethodCode = method, Name = name, BeanId = bean.Id, DoseG = 15m };

    [Fact]
    public void The_beans_own_recipe_beats_a_similar_beans()
    {
        var bean = Bean("A", "Kenya", "Washed");
        var other = Bean("B", "Kenya", "Washed");
        var own = Recipe(bean, name: "own");
        var theirs = Recipe(other, name: "theirs");

        SimilarBeanRecipes.Best(bean, "v60", [bean, other], [theirs, own])!.Name.ShouldBe("own");
    }

    [Fact]
    public void Same_country_and_process_beats_same_country_beats_same_process()
    {
        var bean = Bean("A", "Kenya", "Washed");
        var both = Bean("B", "Kenya", "Washed");
        var country = Bean("C", "Kenya", "Natural");
        var process = Bean("D", "Brazil", "Washed");
        var recipes = new[] { Recipe(process, name: "process"), Recipe(country, name: "country"), Recipe(both, name: "both") };

        SimilarBeanRecipes.Best(bean, "v60", [bean, both, country, process], recipes)!.Name.ShouldBe("both");
        SimilarBeanRecipes.Best(bean, "v60", [bean, country, process], recipes)!.Name.ShouldBe("country");
        SimilarBeanRecipes.Best(bean, "v60", [bean, process], recipes)!.Name.ShouldBe("process");
    }

    [Fact]
    public void Within_a_group_the_best_rating_wins()
    {
        var bean = Bean("A");
        var low = Recipe(bean, name: "low");
        var high = Recipe(bean, name: "high");

        SimilarBeanRecipes.Best(bean, "v60", [bean], [low, high], new Dictionary<string, decimal> { [low.Id] = 3m, [high.Id] = 4.5m })!.Name.ShouldBe("high");
    }

    [Fact]
    public void Other_methods_unrelated_beans_templates_and_old_versions_are_ignored()
    {
        var bean = Bean("A", "Kenya", "Washed");
        var stranger = Bean("B", "Peru", "Natural");
        Recipe[] recipes =
        [
            Recipe(bean, "espresso"),
            Recipe(stranger),
            Recipe(bean) with { IsDefaultTemplate = true },
            Recipe(bean) with { IsSuperseded = true },
            Recipe(bean) with { Deleted = true },
        ];

        SimilarBeanRecipes.Best(bean, "v60", [bean, stranger], recipes).ShouldBeNull();
    }
}
