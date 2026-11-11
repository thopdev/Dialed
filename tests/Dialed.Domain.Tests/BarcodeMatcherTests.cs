using Dialed.Domain.Barcodes;
using Dialed.Domain.Model;

namespace Dialed.Domain.Tests;

public sealed class BarcodeMatcherTests
{
    private static Bean Bean(string name, params string[] codes) => new() { Name = name, Barcodes = codes };

    [Theory]
    [InlineData("4006381333931", true)]   // EAN-13 (a well-known test code)
    [InlineData("4006381333932", false)]
    [InlineData("96385074", true)]        // EAN-8
    [InlineData("96385075", false)]
    [InlineData("036000291452", true)]    // UPC-A
    [InlineData("036000291453", false)]
    public void The_check_digit_decides_whether_a_retail_code_is_valid(string digits, bool valid) =>
        BarcodeMatcher.HasValidCheckDigit(digits).ShouldBe(valid);

    [Fact]
    public void A_retail_code_is_cleaned_and_UPC_A_is_stored_as_EAN_13()
    {
        BarcodeMatcher.Normalize(" 4006 3813-33931 ").ShouldBe("4006381333931");
        BarcodeMatcher.Normalize("036000291452").ShouldBe("0036000291452");
        BarcodeMatcher.Normalize("0036000291452").ShouldBe("0036000291452");
    }

    [Fact]
    public void A_retail_code_with_a_wrong_check_digit_is_refused_so_a_half_read_scan_never_saves()
    {
        BarcodeMatcher.Normalize("4006381333932").ShouldBeNull();
        BarcodeMatcher.Normalize("036000291453").ShouldBeNull();
        BarcodeMatcher.Normalize("96385075").ShouldBeNull();
    }

    [Fact]
    public void Other_codes_are_kept_as_typed_and_nonsense_is_refused()
    {
        BarcodeMatcher.Normalize("  https://example.test/b/abc  ").ShouldBe("https://example.test/b/abc");
        BarcodeMatcher.Normalize("ROAST-2026-11").ShouldBe("ROAST-2026-11");
        BarcodeMatcher.Normalize("1234567").ShouldBe("1234567"); // a numeric code of another length stays as digits
        BarcodeMatcher.Normalize("123").ShouldBeNull();
        BarcodeMatcher.Normalize("   ").ShouldBeNull();
        BarcodeMatcher.Normalize(null).ShouldBeNull();
        BarcodeMatcher.Normalize(new string('x', BarcodeMatcher.MaxLength + 1)).ShouldBeNull();
    }

    [Fact]
    public void Find_matches_across_UPC_A_and_EAN_13_and_across_several_codes_per_bean()
    {
        var small = Bean("Guji 250 g", "4006381333931", "036000291452");
        var other = Bean("Kiambu", "96385074");

        BarcodeMatcher.Find("4006381333931", [small, other]).ShouldBe([small]);
        BarcodeMatcher.Find("0 36000 29145 2", [small, other]).ShouldBe([small]);
        BarcodeMatcher.Find("0036000291452", [small, other]).ShouldBe([small]);
        BarcodeMatcher.Find("96385074", [small, other]).ShouldBe([other]);
    }

    [Fact]
    public void A_code_on_two_beans_lists_both_by_name_and_deleted_beans_and_invalid_codes_match_nothing()
    {
        var b = Bean("B", "4006381333931");
        var a = Bean("a", "4006381333931");
        var gone = Bean("Gone", "4006381333931") with { Deleted = true };

        BarcodeMatcher.Find("4006381333931", [b, gone, a]).ShouldBe([a, b]);
        BarcodeMatcher.Find("4006381333932", [a, b]).ShouldBeEmpty();
        BarcodeMatcher.Find(null, [a, b]).ShouldBeEmpty();
        BarcodeMatcher.Find("9999999999994", [a, b]).ShouldBeEmpty();
    }

    [Fact]
    public void NormalizeAll_drops_invalid_codes_and_duplicates_but_keeps_the_order()
    {
        BarcodeMatcher.NormalizeAll(["036000291452", "0036000291452", "bad", "4006381333932", "ABC-123", " "]).ShouldBe(["0036000291452", "bad", "ABC-123"]);
    }
}
