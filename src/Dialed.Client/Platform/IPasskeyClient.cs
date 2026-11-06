namespace Dialed.Client.Platform;

public enum PasskeyStatus
{
    Success,
    Cancelled,
    NotSupported,
    Failed,
}

/// <param name="Status">What happened.</param>
/// <param name="ResponseJson">On success: the credential as JSON (WebAuthn registration/authentication response) to send to the API.</param>
/// <param name="Error">On failure: the platform's error name, for diagnostics.</param>
public sealed record PasskeyOutcome(PasskeyStatus Status, string? ResponseJson = null, string? Error = null);

/// <summary>
/// Talks to the platform's passkey API. Options and results are the JSON the API's passkey endpoints use
/// (ADR-0010), so this interface is the same on web (WebAuthn) and MAUI (ASAuthorization / Credential Manager).
/// </summary>
public interface IPasskeyClient
{
    Task<bool> IsSupportedAsync();

    /// <summary>True when passkeys can be offered in the email field's autofill.</summary>
    Task<bool> IsAutofillSupportedAsync();

    /// <summary>Creates a passkey from the API's registration options.</summary>
    Task<PasskeyOutcome> CreateAsync(string optionsJson);

    /// <summary>Signs in with a passkey. With <paramref name="autofill"/> the request waits quietly until the user picks a passkey from autofill.</summary>
    Task<PasskeyOutcome> GetAsync(string optionsJson, bool autofill = false);

    /// <summary>Stops a pending autofill request (only one passkey request can run at a time).</summary>
    Task CancelPendingAsync();
}
