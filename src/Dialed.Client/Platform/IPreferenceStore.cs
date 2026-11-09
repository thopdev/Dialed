namespace Dialed.Client.Platform;

/// <summary>Keeps the person's preferences on this device between launches. Web: localStorage.</summary>
public interface IPreferenceStore
{
    /// <summary>The saved preferences as JSON, or <c>null</c> when nothing was saved or storage is blocked.</summary>
    Task<string?> LoadAsync();

    /// <summary>Saves the preferences; a failure only means they last until the app closes.</summary>
    Task SaveAsync(string json);
}

/// <summary>Default for tests and hosts without storage: kept in memory.</summary>
public sealed class InMemoryPreferenceStore : IPreferenceStore
{
    public string? Json { get; private set; }

    public Task<string?> LoadAsync() => Task.FromResult(Json);

    public Task SaveAsync(string json)
    {
        Json = json;
        return Task.CompletedTask;
    }
}
