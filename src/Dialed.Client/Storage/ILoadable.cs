namespace Dialed.Client.Storage;

/// <summary>Something that keeps its data in an <see cref="ILocalStore"/> and has to read it back once when the app starts.</summary>
public interface ILoadable
{
    /// <summary>Replaces the in-memory working copy with what the store holds.</summary>
    Task LoadAsync(CancellationToken cancellationToken = default);
}
