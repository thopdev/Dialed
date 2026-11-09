namespace Dialed.Client.Account;

/// <summary>What the API hands out after a sign-in or refresh.</summary>
public sealed record SessionTokens(
    string AccessToken,
    DateTimeOffset AccessTokenExpiresAt,
    string RefreshToken,
    string UserId,
    string DisplayName,
    string HouseholdId,
    IReadOnlyList<string>? RecoveryCodes = null);

/// <summary>One passkey of the account, as the server lists it.</summary>
public sealed record PasskeyInfo(string Id, string Name, DateTimeOffset CreatedAt, DateTimeOffset? LastUsedAt, bool BackedUp);

/// <summary>How well the account is protected against losing its passkeys.</summary>
public sealed record RecoveryStatus(int RecoveryCodesLeft, bool HasBackedUpPasskey, int Passkeys);

public enum AccountOutcome
{
    Success,

    /// <summary>The user dismissed the passkey prompt.</summary>
    Cancelled,

    /// <summary>This browser or device cannot do passkeys.</summary>
    NotSupported,

    /// <summary>The server refused (for example the email is already registered); see <see cref="AccountResult.Message"/>.</summary>
    Refused,

    /// <summary>The server or the network failed; trying again later may work.</summary>
    Unavailable,
}

public sealed record AccountResult(AccountOutcome Outcome, string? Message = null)
{
    public bool Succeeded => Outcome == AccountOutcome.Success;

    public static AccountResult Ok { get; } = new(AccountOutcome.Success);
}

/// <summary>The API could not be reached or answered something unexpected.</summary>
public sealed class AccountApiException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>The API refused a request with a reason worth showing.</summary>
public sealed class AccountRefusedException(int status, string message) : Exception(message)
{
    public int Status { get; } = status;
}
