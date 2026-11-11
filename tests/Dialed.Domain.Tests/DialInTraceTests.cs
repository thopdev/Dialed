using Dialed.Domain.Model;
using Dialed.Domain.Statistics;

namespace Dialed.Domain.Tests;

public sealed class DialInTraceTests
{
    private static Brew B(int day, decimal? grind, int? rating = null, string method = "v60", int? time = null, bool deleted = false) => new()
    {
        BagId = "bag",
        MethodCode = method,
        DoseG = 18m,
        GrindSetting = grind,
        Rating = rating,
        TimeS = time,
        Deleted = deleted,
        BrewedAt = new DateTimeOffset(2026, 11, day, 8, 0, 0, TimeSpan.Zero),
    };

    [Fact]
    public void Orders_points_oldest_first_and_ignores_brews_without_a_grind_or_deleted_ones()
    {
        var series = DialInTrace.Build([B(5, 14), B(1, 18), B(3, 16), B(4, null), B(2, 17, deleted: true)]).ShouldHaveSingleItem();

        series.Points.Select(p => p.Grind).ShouldBe([18m, 16m, 14m]);
    }

    [Fact]
    public void A_method_needs_three_brews_with_a_grind_to_make_a_trace()
    {
        DialInTrace.Build([B(1, 15), B(2, 16)]).ShouldBeEmpty();
        DialInTrace.Build([B(1, 15), B(2, 16), B(3, 15, method: "espresso")]).ShouldBeEmpty();
    }

    [Fact]
    public void Methods_get_their_own_trace_the_busiest_first()
    {
        var series = DialInTrace.Build([B(1, 15), B(2, 15), B(3, 15), B(1, 8, method: "espresso"), B(2, 9, method: "espresso"), B(3, 9, method: "espresso"), B(4, 9, method: "espresso")]);

        series.Select(s => s.MethodCode).ShouldBe(["espresso", "v60"]);
    }

    [Fact]
    public void The_best_point_is_the_highest_rating_and_among_equals_the_latest()
    {
        var series = DialInTrace.Build([B(1, 18, 3), B(2, 16, 5), B(3, 15, 4), B(4, 16, 5)]).Single();

        series.Best!.Grind.ShouldBe(16m);
        series.Best.At.Day.ShouldBe(4);
    }

    [Fact]
    public void Without_any_rating_there_is_no_best_point()
    {
        DialInTrace.Build([B(1, 18), B(2, 16), B(3, 15)]).Single().Best.ShouldBeNull();
    }

    [Fact]
    public void Settled_means_the_last_three_brews_stayed_close()
    {
        DialInTrace.Build([B(1, 20), B(2, 18), B(3, 16), B(4, 16), B(5, 16)]).Single().Settled.ShouldBeTrue();
        DialInTrace.Build([B(1, 20), B(2, 18), B(3, 16)]).Single().Settled.ShouldBeFalse();
    }
}
