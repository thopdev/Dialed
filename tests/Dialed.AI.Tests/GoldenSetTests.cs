using Dialed.AI.Labels;
using Dialed.AI.Labels.Evaluation;

namespace Dialed.AI.Tests;

/// <summary>Keeps the committed golden set valid, and checks that broken cases are reported clearly.</summary>
public sealed class GoldenSetTests : IDisposable
{
    private readonly string _temp = Directory.CreateTempSubdirectory("dialed-golden-").FullName;

    public static string GoldenFolder => Path.Combine(AppContext.BaseDirectory, "golden");

    public void Dispose() => Directory.Delete(_temp, recursive: true);

    [Fact]
    public void The_committed_golden_set_loads()
    {
        var set = GoldenSet.Load(GoldenFolder);

        set.Cases.Count.ShouldBeGreaterThanOrEqualTo(10);
        set.Cases.Select(c => c.Id).ShouldBeUnique();
        set.Cases.ShouldAllBe(c => File.Exists(c.ImagePath) && c.License.Length > 0);
        set.Cases.Select(c => c.Id).ShouldBe(set.Cases.Select(c => c.Id).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Every_field_is_on_some_label_and_missing_from_another()
    {
        var set = GoldenSet.Load(GoldenFolder);

        foreach (var field in BeanDraft.FieldNames)
        {
            set.Cases.ShouldContain(c => c.ExpectedValue(field) != null, $"no label has {field}");
            if (field is not ("name" or "roaster"))
            {
                // Name and roaster are on every real bag; the rest is often left off, and guessing them must cost points.
                set.Cases.ShouldContain(c => c.ExpectedValue(field) == null, $"every label has {field}, so leaving it out is never tested");
            }
        }
    }

    [Fact]
    public void Images_stay_small_enough_for_the_api()
    {
        foreach (var file in Directory.EnumerateFiles(GoldenFolder, "*.jpg"))
        {
            new FileInfo(file).Length.ShouldBeLessThan(1_000_000, Path.GetFileName(file));
        }
    }

    [Fact]
    public void Problems_are_listed_together()
    {
        File.WriteAllBytes(Path.Combine(_temp, "ok.jpg"), [0xFF, 0xD8, 0xFF, 0xE0]);
        File.WriteAllBytes(Path.Combine(_temp, "png.jpg"), [0x89, 0x50, 0x4E, 0x47]);
        Write("wrong-id", """{ "id": "other", "image": "ok.jpg", "license": "CC0", "expected": { "name": "A" } }""");
        Write("no-image", """{ "id": "no-image", "image": "missing.jpg", "license": "CC0", "expected": { "name": "A" } }""");
        Write("not-jpeg", """{ "id": "not-jpeg", "image": "png.jpg", "license": "CC0", "expected": { "name": "A" } }""");
        Write("bad-values", """
            { "id": "bad-values", "image": "ok.jpg", "license": "CC0",
              "expected": { "altitude": "1800", "roastDate": "14-09-2026", "weightG": "250 g", "roastLevel": "city" } }
            """);
        Write("empty", """{ "id": "empty", "image": "", "expected": { "name": null } }""");
        Write("broken", "{ not json");
        Write("null", "null");

        var problems = Should.Throw<GoldenSetException>(() => GoldenSet.Load(_temp)).Message;

        problems.ShouldContain("wrong-id.json: 'id' must be the file name");
        problems.ShouldContain("no-image.json: image 'missing.jpg' not found");
        problems.ShouldContain("not-jpeg.json: image 'png.jpg' is not a JPEG");
        problems.ShouldContain("unknown field 'altitude'");
        problems.ShouldContain("roastDate '14-09-2026' must be yyyy-MM-dd");
        problems.ShouldContain("weightG '250 g' must be whole grams");
        problems.ShouldContain("roastLevel 'city' must be");
        problems.ShouldContain("empty.json: 'image' is missing");
        problems.ShouldContain("empty.json: 'license' is missing");
        problems.ShouldContain("empty.json: 'expected' has no values");
        problems.ShouldContain("broken.json: not valid JSON");
        problems.ShouldContain("null.json: empty");
    }

    [Fact]
    public void Duplicate_ids_and_missing_ids_are_reported()
    {
        File.WriteAllBytes(Path.Combine(_temp, "ok.jpg"), [0xFF, 0xD8, 0xFF, 0xE0]);
        Write("A", """{ "id": "A", "image": "ok.jpg", "license": "CC0", "expected": { "name": "A" } }""");
        Write("a", """{ "id": "a", "image": "ok.jpg", "license": "CC0", "expected": { "name": "A" } }""");
        Write("noid", """{ "image": "ok.jpg", "license": "CC0", "expected": { "name": "A" } }""");

        var problems = Should.Throw<GoldenSetException>(() => GoldenSet.Load(_temp)).Message;

        problems.ShouldContain("noid.json: 'id' is missing");
        if (!OperatingSystem.IsWindows())
        {
            // On Windows "A.json" and "a.json" are the same file.
            problems.ShouldContain("is used 2 times");
        }
    }

    [Fact]
    public void A_missing_folder_is_reported() =>
        Should.Throw<GoldenSetException>(() => GoldenSet.Load(Path.Combine(_temp, "nope"))).Message.ShouldContain("does not exist");

    private void Write(string id, string json) => File.WriteAllText(Path.Combine(_temp, id + ".json"), json);
}
