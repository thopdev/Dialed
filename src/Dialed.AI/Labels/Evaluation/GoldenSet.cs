using System.Text.Json;
using System.Text.Json.Serialization;

namespace Dialed.AI.Labels.Evaluation;

/// <summary>
/// One label in the golden set: a JPEG next to a JSON file with what the label says. A field that is left out or <c>null</c> in
/// <see cref="Expected"/> is not on the label, so the extractor should leave it out too.
/// </summary>
public sealed record GoldenCase(
    string Id,
    string Image,
    IReadOnlyDictionary<string, string?> Expected,
    string Source = "",
    string License = "",
    string Notes = "",
    IReadOnlyList<string>? Tags = null)
{
    /// <summary>Full path of the image, set by <see cref="GoldenSet.Load"/>.</summary>
    [JsonIgnore]
    public string ImagePath { get; init; } = "";

    public string? ExpectedValue(string field) => Expected.TryGetValue(field, out var value) && !string.IsNullOrWhiteSpace(value) ? value : null;

    public Task<byte[]> ReadImageAsync(CancellationToken cancellationToken = default) => File.ReadAllBytesAsync(ImagePath, cancellationToken);
}

/// <summary>A folder of golden cases (<c>*.json</c> plus the images they name).</summary>
public sealed class GoldenSet(string directory, IReadOnlyList<GoldenCase> cases)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { ReadCommentHandling = JsonCommentHandling.Skip };

    public string Directory { get; } = directory;

    public IReadOnlyList<GoldenCase> Cases { get; } = cases;

    /// <summary>Reads every <c>*.json</c> in <paramref name="directory"/>, sorted by id. Throws <see cref="GoldenSetException"/> listing every problem found.</summary>
    public static GoldenSet Load(string directory)
    {
        if (!System.IO.Directory.Exists(directory))
        {
            throw new GoldenSetException($"The golden set folder '{directory}' does not exist.");
        }

        var cases = new List<GoldenCase>();
        var problems = new List<string>();
        foreach (var file in System.IO.Directory.EnumerateFiles(directory, "*.json").Order(StringComparer.Ordinal))
        {
            GoldenCase? item;
            try
            {
                item = JsonSerializer.Deserialize<GoldenCase>(File.ReadAllText(file), Json);
            }
            catch (JsonException exception)
            {
                problems.Add($"{Path.GetFileName(file)}: not valid JSON ({exception.Message}).");
                continue;
            }

            if (item is null)
            {
                problems.Add($"{Path.GetFileName(file)}: empty.");
                continue;
            }

            item = item with { ImagePath = Path.Combine(directory, item.Image ?? ""), Expected = item.Expected ?? new Dictionary<string, string?>() };
            problems.AddRange(Validate(item, Path.GetFileName(file)));
            cases.Add(item);
        }

        foreach (var duplicate in cases.Where(c => !string.IsNullOrWhiteSpace(c.Id)).GroupBy(c => c.Id, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
        {
            problems.Add($"Id '{duplicate.Key}' is used {duplicate.Count()} times.");
        }

        if (problems.Count > 0)
        {
            throw new GoldenSetException("The golden set has problems:" + Environment.NewLine + string.Join(Environment.NewLine, problems.Select(p => " - " + p)));
        }

        return new GoldenSet(directory, cases.OrderBy(c => c.Id, StringComparer.Ordinal).ToList());
    }

    /// <summary>Checks one case: id and file name agree, the image exists and is a JPEG, field names are known and values have the expected format.</summary>
    public static IEnumerable<string> Validate(GoldenCase item, string fileName)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (string.IsNullOrWhiteSpace(item.Id))
        {
            yield return $"{fileName}: 'id' is missing.";
        }
        else if (!string.Equals(Path.GetFileNameWithoutExtension(fileName), item.Id, StringComparison.Ordinal))
        {
            yield return $"{fileName}: 'id' must be the file name without .json ('{item.Id}').";
        }

        if (string.IsNullOrWhiteSpace(item.Image))
        {
            yield return $"{fileName}: 'image' is missing.";
        }
        else if (!File.Exists(item.ImagePath))
        {
            yield return $"{fileName}: image '{item.Image}' not found next to it.";
        }
        else if (!IsJpeg(item.ImagePath))
        {
            yield return $"{fileName}: image '{item.Image}' is not a JPEG.";
        }

        if (string.IsNullOrWhiteSpace(item.License))
        {
            yield return $"{fileName}: 'license' is missing: say why the image may be in the repository.";
        }

        foreach (var (field, value) in item.Expected)
        {
            if (!FieldMatcher.FieldNames.Contains(field))
            {
                yield return $"{fileName}: unknown field '{field}' (known: {string.Join(", ", BeanDraft.FieldNames)}).";
            }
            else if (value is not null && field == "roastDate" && !FieldMatcher.TryParseDate(value, out _))
            {
                yield return $"{fileName}: roastDate '{value}' must be yyyy-MM-dd.";
            }
            else if (value is not null && field == "weightG" && !int.TryParse(value, out _))
            {
                yield return $"{fileName}: weightG '{value}' must be whole grams, for example \"250\".";
            }
            else if (value is not null && field == "roastLevel" && value is not ("light" or "medium-light" or "medium" or "medium-dark" or "dark"))
            {
                yield return $"{fileName}: roastLevel '{value}' must be light, medium-light, medium, medium-dark or dark.";
            }
        }

        if (!item.Expected.Values.Any(v => !string.IsNullOrWhiteSpace(v)))
        {
            yield return $"{fileName}: 'expected' has no values.";
        }
    }

    private static bool IsJpeg(string path)
    {
        using var stream = File.OpenRead(path);
        Span<byte> header = stackalloc byte[3];
        return stream.Read(header) == 3 && header[0] == 0xFF && header[1] == 0xD8 && header[2] == 0xFF;
    }
}

public sealed class GoldenSetException(string message) : Exception(message);
