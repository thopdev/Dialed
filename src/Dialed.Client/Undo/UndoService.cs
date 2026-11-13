using Dialed.Client.Data;
using Dialed.Domain.Model;

namespace Dialed.Client.Undo;

/// <summary>What the toast shows: the message and a way to take the action back.</summary>
public sealed record UndoOffer(long Id, string Message, Func<Task> Undo);

/// <summary>
/// Delete first, offer undo after (no confirm box). One offer at a time: a new one replaces the old, which then can no
/// longer be undone. Undo restores the soft-deleted record as a new version, so sync sees an ordinary edit.
/// </summary>
public sealed class UndoService
{
    private long next;

    public UndoOffer? Current { get; private set; }

    public event Action? Changed;

    /// <summary>Soft-deletes the record and offers to bring it back. <paramref name="alsoUndo"/> runs after the restore for related changes.</summary>
    public async Task DeleteAsync<T>(IRepository<T> store, string id, string message, Func<Task>? alsoUndo = null)
        where T : SyncEntity
    {
        ArgumentNullException.ThrowIfNull(store);
        await store.DeleteAsync(id);
        Offer(message, async () =>
        {
            await store.RestoreAsync(id);
            if (alsoUndo is not null)
            {
                await alsoUndo();
            }
        });
    }

    public void Offer(string message, Func<Task> undo)
    {
        Current = new UndoOffer(++next, message, undo);
        Changed?.Invoke();
    }

    /// <summary>Runs the undo of the offer that is showing. Does nothing when it was replaced or dismissed meanwhile.</summary>
    public async Task UndoAsync(long id)
    {
        if (Current is not { } offer || offer.Id != id)
        {
            return;
        }

        Current = null;
        Changed?.Invoke();
        await offer.Undo();
    }

    public void Dismiss(long id)
    {
        if (Current?.Id == id)
        {
            Current = null;
            Changed?.Invoke();
        }
    }
}
