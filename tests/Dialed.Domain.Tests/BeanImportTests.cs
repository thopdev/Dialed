using Dialed.Domain.Import;
using Dialed.Domain.Model;

namespace Dialed.Domain.Tests;

public sealed class BeanImportTests
{
    [Fact]
    public void Csv_handles_quotes_embedded_delimiters_newlines_and_a_bom()
    {
        var rows = Csv.Parse("﻿name,notes\r\n\"Guji, Natural\",\"line 1\nline \"\"2\"\"\"\r\nHuila,\r\n");

        rows.Count.ShouldBe(3);
        rows[1].ShouldBe(["Guji, Natural", "line 1\nline \"2\""]);
        rows[2].ShouldBe(["Huila", ""]);
    }

    [Theory]
    [InlineData("name;price\nA;1,5", ";")]
    [InlineData("name\tprice\nA\t1", "\t")]
    public void Csv_detects_semicolons_and_tabs(string text, string delimiter)
    {
        Csv.Parse(text)[0].ShouldBe(["name", "price"]);
        delimiter.ShouldNotBeEmpty();
    }

    [Fact]
    public void Empty_text_has_no_rows() => Csv.Parse("  \n ").ShouldBeEmpty();

    [Fact]
    public void Headers_are_matched_in_english_and_dutch_without_reusing_a_column()
    {
        var mapping = BeanImport.DetectMapping(["Naam", "Brander", "Land", "Roast", "Roast date", "Gewicht", "Prijs"]);

        mapping[ImportField.Name].ShouldBe(0);
        mapping[ImportField.Roaster].ShouldBe(1);
        mapping[ImportField.Country].ShouldBe(2);
        mapping[ImportField.RoastLevel].ShouldBe(3);
        mapping[ImportField.RoastDate].ShouldBe(4);
        mapping[ImportField.WeightG].ShouldBe(5);
        mapping[ImportField.Price].ShouldBe(6);
    }

    [Theory]
    [InlineData("250", 250)]
    [InlineData("250 g", 250)]
    [InlineData("0,25 kg", 250)]
    [InlineData("12 oz", 340)]
    public void Weights_become_grams(string text, int grams) => BeanImport.ParseWeightG(text).ShouldBe(grams);

    [Theory]
    [InlineData("abc")]
    [InlineData("0")]
    [InlineData("")]
    public void Weights_that_make_no_sense_are_null(string text) => BeanImport.ParseWeightG(text).ShouldBeNull();

    [Theory]
    [InlineData("12,50", 1250, null)]
    [InlineData("€ 12.50", 1250, "EUR")]
    [InlineData("$9", 900, "USD")]
    [InlineData("14 EUR", 1400, "EUR")]
    public void Prices_become_cents_and_currency(string text, int cents, string? currency) => BeanImport.ParsePrice(text).ShouldBe((cents, currency));

    [Theory]
    [InlineData("2026-03-04", 2026, 3, 4)]
    [InlineData("04-03-2026", 2026, 3, 4)]
    [InlineData("4/3/2026", 2026, 3, 4)]
    [InlineData("4.3.2026", 2026, 3, 4)]
    public void Dates_are_read_in_common_formats(string text, int year, int month, int day) => BeanImport.ParseDate(text).ShouldBe(new DateOnly(year, month, day));

    [Fact]
    public void Plan_builds_beans_and_bags_skips_rows_without_name_and_known_beans_and_warns()
    {
        var rows = Csv.Parse("""
            Name,Roaster,Country,Process,Roast,Rating,Weight,Price,Date
            Guji,Friedhats,Ethiopia,Washed,Light,5,250,"12,50",2026-10-01
            ,Nobody,Kenya,,,,,,
            Huila,Friedhats,Colombia,Natural,medium,9,abc,,
            Already,Here,,,,,,,
            Guji,Friedhats,Ethiopia,,,,,,
            """);
        var mapping = BeanImport.DetectMapping(rows[0]);

        var plan = BeanImport.Plan([.. rows.Skip(1)], mapping, [("already", "here")]);

        plan.Beans.Select(b => b.Bean.Name).ShouldBe(["Guji", "Huila"]);
        plan.AlreadyThere.ShouldBe(2);
        var guji = plan.Beans[0];
        guji.RoasterName.ShouldBe("Friedhats");
        guji.Bean.Origin.Country.ShouldBe("Ethiopia");
        guji.Bean.RoastLevel.ShouldBe(RoastLevel.Light);
        guji.Bean.Rating.ShouldBe(5);
        guji.Bag.ShouldNotBeNull();
        guji.Bag.WeightG.ShouldBe(250m);
        guji.Bag.PriceCents.ShouldBe(1250);
        guji.Bag.Currency.ShouldBe("EUR");
        guji.Bag.PurchasedOn.ShouldBe(new DateOnly(2026, 10, 1));
        plan.Beans[1].Bag.ShouldBeNull();
        plan.Beans[1].Bean.Rating.ShouldBeNull();
        plan.Warnings.Count.ShouldBe(3); // no name, rating out of range, weight not understood
    }
}
