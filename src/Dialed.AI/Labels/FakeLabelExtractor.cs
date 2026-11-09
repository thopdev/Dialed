namespace Dialed.AI.Labels;

/// <summary>Used in development and tests, and until a Claude endpoint is configured: returns a fixed, plausible draft.</summary>
public sealed class FakeLabelExtractor : ILabelExtractor
{
    public const string ModelId = "fake";

    public Task<LabelExtraction> ExtractAsync(byte[] jpeg, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(jpeg);

        var draft = BeanDraft.Empty with
        {
            Name = new DraftField("Guji Hambela", 0.95),
            Roaster = new DraftField("Example Roasters", 0.9),
            Country = new DraftField("Ethiopia", 0.98),
            Process = new DraftField("Natural", 0.8),
            RoastLevel = new DraftField("light", 0.6),
            WeightG = new DraftField("250", 0.9),
        };
        return Task.FromResult(new LabelExtraction(draft, LabelPrompt.Version, ModelId, 0, 0));
    }
}
