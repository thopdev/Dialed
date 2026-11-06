namespace Dialed.Client.Platform;

/// <summary>Whether the device has a network. A hint only: "online" does not guarantee the API is reachable.</summary>
public interface IConnectivity
{
    bool IsOnline { get; }

    /// <summary>Raised when <see cref="IsOnline"/> changes.</summary>
    event Action<bool>? Changed;
}
