using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.WebUtilities;

namespace Dialed.Api.Passkeys;

/// <summary>Sends email. The real sender (Azure Communication Services Email, #24) replaces <see cref="OutboxEmailSender"/>.</summary>
public interface IEmailSender
{
    Task SendAsync(string to, string subject, string body, CancellationToken cancellationToken = default);
}

/// <summary>Keeps sent mail in memory and logs that it was sent (never the body). Stand-in until email is connected.</summary>
public sealed class OutboxEmailSender(ILogger<OutboxEmailSender> logger) : IEmailSender
{
    private readonly Lock gate = new();
    private readonly List<(string To, string Subject, string Body)> sent = [];

    public IReadOnlyList<(string To, string Subject, string Body)> Sent
    {
        get
        {
            lock (gate)
            {
                return [.. sent];
            }
        }
    }

    public Task SendAsync(string to, string subject, string body, CancellationToken cancellationToken = default)
    {
        lock (gate)
        {
            sent.Add((to, subject, body));
        }

        logger.LogInformation("Email queued for sending: {Subject}", subject);
        return Task.CompletedTask;
    }
}

/// <summary>One-time email codes for account recovery: hashed, short-lived, and only a few guesses.</summary>
public interface IEmailRecoveryStore
{
    Task PutAsync(string accountId, string codeHash, DateTimeOffset expiresAt, CancellationToken cancellationToken = default);

    /// <summary>Checks the code for the account. Every call counts as an attempt; too many attempts burn the code.</summary>
    Task<bool> CheckAsync(string accountId, string codeHash, DateTimeOffset now, bool consume, CancellationToken cancellationToken = default);
}

public sealed class InMemoryEmailRecoveryStore : IEmailRecoveryStore
{
    public const int MaxAttempts = 5;

    private sealed record Entry(string Hash, DateTimeOffset ExpiresAt, int Attempts);

    private readonly Lock gate = new();
    private readonly Dictionary<string, Entry> entries = new(StringComparer.Ordinal);

    public Task PutAsync(string accountId, string codeHash, DateTimeOffset expiresAt, CancellationToken cancellationToken = default)
    {
        lock (gate)
        {
            entries[accountId] = new Entry(codeHash, expiresAt, 0);
        }

        return Task.CompletedTask;
    }

    public Task<bool> CheckAsync(string accountId, string codeHash, DateTimeOffset now, bool consume, CancellationToken cancellationToken = default)
    {
        lock (gate)
        {
            if (!entries.TryGetValue(accountId, out var entry) || entry.ExpiresAt <= now || entry.Attempts >= MaxAttempts)
            {
                return Task.FromResult(false);
            }

            if (!CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(entry.Hash), Encoding.UTF8.GetBytes(codeHash)))
            {
                entries[accountId] = entry with { Attempts = entry.Attempts + 1 };
                return Task.FromResult(false);
            }

            if (consume)
            {
                entries.Remove(accountId);
            }

            return Task.FromResult(true);
        }
    }
}

public sealed record PasskeyDto(string Id, string Name, DateTimeOffset CreatedAt, DateTimeOffset? LastUsedAt, bool BackedUp);

public sealed record RenamePasskeyRequest(string Name);

public sealed record RecoverEmailRequest(string Email);

public sealed record RecoverOptionsRequest(string Email, string Code);

public sealed record RecoveryStatusDto(int RecoveryCodesLeft, bool HasBackedUpPasskey, int Passkeys);

public sealed record RecoveryCodesDto(IReadOnlyList<string> Codes);

/// <summary>Recovery codes, email codes and the passkeys of one account (#112).</summary>
public sealed class RecoveryService(UserManager<AccountUser> accounts, IEmailRecoveryStore emailCodes, IEmailSender email, TimeProvider time)
{
    public const int RecoveryCodeCount = 8;
    public const int MinPasskeysToKeep = 1;
    private const string Alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
    private static readonly TimeSpan EmailCodeLifetime = TimeSpan.FromMinutes(15);

    /// <summary>New codes like <c>ABCD-2345</c>; only their hashes are kept.</summary>
    public static IReadOnlyList<string> NewCodes()
    {
        var codes = new List<string>();
        for (var c = 0; c < RecoveryCodeCount; c++)
        {
            Span<char> chars = stackalloc char[9];
            for (var i = 0; i < 9; i++)
            {
                chars[i] = i == 4 ? '-' : Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)];
            }

            codes.Add(new string(chars));
        }

        return codes;
    }

    public static string Hash(string code) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Normalize(code))));

    private static string Normalize(string code) => (code ?? "").Trim().ToUpperInvariant().Replace("-", "", StringComparison.Ordinal).Replace(" ", "", StringComparison.Ordinal);

    /// <summary>Sends a one-time code to the address. Nothing is said about whether the account exists.</summary>
    public async Task SendEmailCodeAsync(string address, CancellationToken ct = default)
    {
        if (await accounts.FindByEmailAsync(address.Trim()) is not { } account)
        {
            return;
        }

        var code = RandomNumberGenerator.GetInt32(0, 100_000_000).ToString("D8", System.Globalization.CultureInfo.InvariantCulture);
        await emailCodes.PutAsync(account.Id, Hash(code), time.GetUtcNow().Add(EmailCodeLifetime), ct);
        await email.SendAsync(
            address.Trim(),
            "Your Dialed recovery code",
            $"Your code is {code}. It works for 15 minutes. If you did not ask for it, you can ignore this email.",
            ct);
    }

    /// <summary>The account the code (a recovery code or an email code) belongs to, without using the code up. <c>null</c> when it does not match.</summary>
    public async Task<AccountUser?> CheckAsync(string address, string code, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(address) || string.IsNullOrWhiteSpace(code) || await accounts.FindByEmailAsync(address.Trim()) is not { } account)
        {
            return null;
        }

        var hash = Hash(code);
        if (account.RecoveryCodeHashes.Contains(hash))
        {
            return account;
        }

        return await emailCodes.CheckAsync(account.Id, hash, time.GetUtcNow(), consume: false, ct) ? account : null;
    }

    /// <summary>Uses the code up. <c>false</c> when it no longer matches (someone else used it first).</summary>
    public async Task<bool> ConsumeAsync(string accountId, string code, CancellationToken ct = default)
    {
        if (await accounts.FindByIdAsync(accountId) is not { } account)
        {
            return false;
        }

        var hash = Hash(code);
        if (account.RecoveryCodeHashes.Remove(hash))
        {
            return (await accounts.UpdateAsync(account)).Succeeded;
        }

        return await emailCodes.CheckAsync(accountId, hash, time.GetUtcNow(), consume: true, ct);
    }

    /// <summary>Replaces all recovery codes with new ones and returns them (shown once).</summary>
    public async Task<IReadOnlyList<string>?> RegenerateCodesAsync(string accountId, CancellationToken ct = default)
    {
        if (await accounts.FindByIdAsync(accountId) is not { } account)
        {
            return null;
        }

        var codes = NewCodes();
        account.RecoveryCodeHashes = [.. codes.Select(Hash)];
        return (await accounts.UpdateAsync(account)).Succeeded ? codes : null;
    }

    public async Task<RecoveryStatusDto?> StatusAsync(string accountId, CancellationToken ct = default) =>
        await accounts.FindByIdAsync(accountId) is { } account
            ? new RecoveryStatusDto(account.RecoveryCodeHashes.Count, account.Passkeys.Any(p => p.IsBackedUp), account.Passkeys.Count)
            : null;

    public async Task<IReadOnlyList<PasskeyDto>?> ListPasskeysAsync(string accountId, CancellationToken ct = default) =>
        await accounts.FindByIdAsync(accountId) is { } account
            ? [.. account.Passkeys.OrderBy(p => p.CreatedAt).Select(p => new PasskeyDto(Base64UrlTextEncoder.Encode(p.CredentialId), p.Name ?? "Passkey", p.CreatedAt, p.LastUsedAt, p.IsBackedUp))]
            : null;

    public async Task<bool> RenamePasskeyAsync(string accountId, string id, string name, CancellationToken ct = default)
    {
        name = name?.Trim() ?? "";
        if (name.Length is 0 or > 60 || await accounts.FindByIdAsync(accountId) is not { } account || Find(account, id) is not { } passkey)
        {
            return false;
        }

        passkey.Name = name;
        return (await accounts.UpdateAsync(account)).Succeeded;
    }

    /// <summary>Removes a passkey. The last one cannot be removed: that would lock the account.</summary>
    public async Task<RemoveResult> RemovePasskeyAsync(string accountId, string id, CancellationToken ct = default)
    {
        if (await accounts.FindByIdAsync(accountId) is not { } account || Find(account, id) is not { } passkey)
        {
            return RemoveResult.NotFound;
        }

        if (account.Passkeys.Count <= MinPasskeysToKeep)
        {
            return RemoveResult.LastOne;
        }

        account.Passkeys.Remove(passkey);
        return (await accounts.UpdateAsync(account)).Succeeded ? RemoveResult.Removed : RemoveResult.NotFound;
    }

    private static StoredPasskey? Find(AccountUser account, string id)
    {
        try
        {
            var raw = Base64UrlTextEncoder.Decode(id);
            return account.Passkeys.Find(p => p.CredentialId.AsSpan().SequenceEqual(raw));
        }
        catch (FormatException)
        {
            return null;
        }
    }
}

public enum RemoveResult { Removed, NotFound, LastOne }
