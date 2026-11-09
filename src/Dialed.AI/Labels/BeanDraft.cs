namespace Dialed.AI.Labels;

/// <summary>One value read from a label and how sure the model is (0 to 1). <see cref="Value"/> is <c>null</c> when the label does not say.</summary>
public sealed record DraftField(string? Value, double Confidence)
{
    public static DraftField None { get; } = new(null, 0);

    public bool HasValue => !string.IsNullOrWhiteSpace(Value);
}

/// <summary>What the model read from a bean label. A draft: the person checks it before it becomes a bean.</summary>
public sealed record BeanDraft(
    DraftField Name,
    DraftField Roaster,
    DraftField Country,
    DraftField Region,
    DraftField Farm,
    DraftField Process,
    DraftField Varieties,
    DraftField RoastLevel,
    DraftField TastingNotes,
    DraftField RoastDate,
    DraftField WeightG)
{
    public static BeanDraft Empty { get; } = new(
        DraftField.None, DraftField.None, DraftField.None, DraftField.None, DraftField.None, DraftField.None,
        DraftField.None, DraftField.None, DraftField.None, DraftField.None, DraftField.None);

    /// <summary>The field names as used in the prompt and in the JSON schema.</summary>
    public static readonly string[] FieldNames =
        ["name", "roaster", "country", "region", "farm", "process", "varieties", "roastLevel", "tastingNotes", "roastDate", "weightG"];

    public static BeanDraft FromFields(IReadOnlyDictionary<string, DraftField> fields)
    {
        DraftField Get(string name) => fields.TryGetValue(name, out var field) ? field : DraftField.None;
        return new BeanDraft(Get("name"), Get("roaster"), Get("country"), Get("region"), Get("farm"), Get("process"),
            Get("varieties"), Get("roastLevel"), Get("tastingNotes"), Get("roastDate"), Get("weightG"));
    }
}

/// <summary>The result of reading one label, with what is needed to judge and reproduce it.</summary>
public sealed record LabelExtraction(BeanDraft Draft, string PromptVersion, string ModelId, int InputTokens, int OutputTokens);

public interface ILabelExtractor
{
    /// <summary>Reads a label from a JPEG photo.</summary>
    Task<LabelExtraction> ExtractAsync(byte[] jpeg, CancellationToken cancellationToken = default);
}

/// <summary>The model could not be reached or answered something unusable.</summary>
public sealed class LabelExtractionException(string message, Exception? inner = null) : Exception(message, inner);
