using System.Collections.Concurrent;
using System.Security.Cryptography;
using Azure.Core;
using Azure.Security.KeyVault.Keys;
using Azure.Security.KeyVault.Keys.Cryptography;
using Microsoft.IdentityModel.Tokens;

namespace Dialed.Api.Auth;

/// <summary>Public half of one version of the signing key; <see cref="KeyId"/> is the Key Vault key version.</summary>
public sealed record JwtPublicKey(string KeyId, RSAParameters Parameters);

/// <summary>The RSA key that signs access tokens. Signing happens remotely, so the private key is never in the API.</summary>
public interface IJwtKeySource
{
    /// <summary>Version (kid) of the key that signs new tokens.</summary>
    Task<string> GetCurrentKeyIdAsync(CancellationToken cancellationToken);

    /// <summary>Public keys of every enabled version, so tokens signed before a rotation stay valid.</summary>
    Task<IReadOnlyList<JwtPublicKey>> GetPublicKeysAsync(CancellationToken cancellationToken);

    /// <summary>RS256 signature of <paramref name="data"/> (hashed by the source) with key version <paramref name="keyId"/>.</summary>
    byte[] Sign(string keyId, byte[] data);
}

/// <summary>Key Vault key <c>Jwt:KeyVaultKeyName</c> in <c>KeyVault:Uri</c>, reached with the managed identity (Key Vault Crypto User).</summary>
public sealed class AzureKeyVaultJwtKeySource(Uri vaultUri, string keyName, TokenCredential credential) : IJwtKeySource
{
    private readonly KeyClient keys = new(vaultUri, credential);
    private readonly ConcurrentDictionary<string, CryptographyClient> signers = new(StringComparer.Ordinal);

    public async Task<string> GetCurrentKeyIdAsync(CancellationToken cancellationToken)
    {
        var key = await keys.GetKeyAsync(keyName, cancellationToken: cancellationToken).ConfigureAwait(false);
        return key.Value.Properties.Version;
    }

    public async Task<IReadOnlyList<JwtPublicKey>> GetPublicKeysAsync(CancellationToken cancellationToken)
    {
        var result = new List<JwtPublicKey>();
        await foreach (var version in keys.GetPropertiesOfKeyVersionsAsync(keyName, cancellationToken).ConfigureAwait(false))
        {
            if (version.Enabled != true || version.ExpiresOn < DateTimeOffset.UtcNow)
            {
                continue;
            }

            var key = await keys.GetKeyAsync(keyName, version.Version, cancellationToken).ConfigureAwait(false);
            using var rsa = key.Value.Key.ToRSA();
            result.Add(new JwtPublicKey(version.Version, rsa.ExportParameters(includePrivateParameters: false)));
        }

        return result;
    }

    public byte[] Sign(string keyId, byte[] data)
    {
        var signer = signers.GetOrAdd(keyId, version => new CryptographyClient(new Uri(vaultUri, $"keys/{keyName}/{version}"), credential));
        return signer.SignData(SignatureAlgorithm.RS256, data).Signature;
    }
}

/// <summary>
/// RS256 signing with a key that stays in Key Vault (#30): new tokens carry the current key version as <c>kid</c>,
/// validation uses the cached public keys of all enabled versions and reloads them when an unknown <c>kid</c> shows up.
/// </summary>
public sealed class KeyVaultJwtSigningKeys(IJwtKeySource source, TimeProvider time) : IJwtSigningKeys
{
    private static readonly TimeSpan MinimumReloadInterval = TimeSpan.FromMinutes(5);

    private readonly Lock gate = new();
    private Dictionary<string, SecurityKey> validationKeys = new(StringComparer.Ordinal);
    private SigningCredentials? signingCredentials;
    private DateTimeOffset lastLoad = DateTimeOffset.MinValue;

    public SigningCredentials SigningCredentials =>
        signingCredentials ?? throw new InvalidOperationException("The Key Vault signing key is not loaded yet.");

    /// <summary>Loads the current key version and the public keys. Runs at startup and then every hour.</summary>
    public async Task LoadAsync(CancellationToken cancellationToken)
    {
        var currentKeyId = await source.GetCurrentKeyIdAsync(cancellationToken).ConfigureAwait(false);
        var publicKeys = await source.GetPublicKeysAsync(cancellationToken).ConfigureAwait(false);

        var keys = publicKeys.ToDictionary(
            key => key.KeyId,
            key => (SecurityKey)new RsaSecurityKey(key.Parameters) { KeyId = key.KeyId },
            StringComparer.Ordinal);

        var signingKey = new KeyVaultSecurityKey(currentKeyId, source)
        {
            CryptoProviderFactory = new CryptoProviderFactory
            {
                CustomCryptoProvider = new KeyVaultCryptoProvider(),
                CacheSignatureProviders = false,
            },
        };

        lock (gate)
        {
            validationKeys = keys;
            signingCredentials = new SigningCredentials(signingKey, SecurityAlgorithms.RsaSha256);
            lastLoad = time.GetUtcNow();
        }
    }

    public IEnumerable<SecurityKey> ValidationKeys(string? keyId)
    {
        Dictionary<string, SecurityKey> keys;
        lock (gate)
        {
            keys = validationKeys;
        }

        if (string.IsNullOrEmpty(keyId))
        {
            return keys.Values;
        }

        if (keys.TryGetValue(keyId, out var key))
        {
            return [key];
        }

        // A new key version (rotation) or a forged kid: reload, but not more often than every few minutes.
        bool reload;
        lock (gate)
        {
            reload = time.GetUtcNow() - lastLoad >= MinimumReloadInterval;
            if (reload)
            {
                lastLoad = time.GetUtcNow();
            }
        }

        if (!reload)
        {
            return [];
        }

        LoadAsync(CancellationToken.None).GetAwaiter().GetResult();
        lock (gate)
        {
            return validationKeys.TryGetValue(keyId, out key) ? [key] : [];
        }
    }

    private sealed class KeyVaultSecurityKey : SecurityKey
    {
        public KeyVaultSecurityKey(string keyId, IJwtKeySource source)
        {
            KeyId = keyId;
            Source = source;
        }

        public IJwtKeySource Source { get; }

        public override int KeySize => 2048;
    }

    private sealed class KeyVaultCryptoProvider : ICryptoProvider
    {
        public bool IsSupportedAlgorithm(string algorithm, params object[] args) =>
            algorithm == SecurityAlgorithms.RsaSha256 && args is [KeyVaultSecurityKey, ..];

        public object Create(string algorithm, params object[] args) =>
            new KeyVaultSignatureProvider((KeyVaultSecurityKey)args[0], algorithm);

        public void Release(object cryptoInstance)
        {
            if (cryptoInstance is IDisposable disposable)
            {
                disposable.Dispose();
            }
        }
    }

    private sealed class KeyVaultSignatureProvider(KeyVaultSecurityKey key, string algorithm) : SignatureProvider(key, algorithm)
    {
        public override byte[] Sign(byte[] input) => key.Source.Sign(key.KeyId, input);

        public override byte[] Sign(byte[] input, int offset, int count) => Sign(input.AsSpan(offset, count).ToArray());

        // JsonWebTokenHandler signs through this overload.
        public override bool Sign(ReadOnlySpan<byte> data, Span<byte> destination, out int bytesWritten)
        {
            var signature = Sign(data.ToArray());
            if (signature.Length > destination.Length)
            {
                bytesWritten = 0;
                return false;
            }

            signature.CopyTo(destination);
            bytesWritten = signature.Length;
            return true;
        }

        public override bool Verify(byte[] input, byte[] signature) =>
            throw new NotSupportedException("Tokens are validated with the cached public keys, not through Key Vault.");

        public override bool Verify(byte[] input, int inputOffset, int inputLength, byte[] signature, int signatureOffset, int signatureLength) =>
            throw new NotSupportedException("Tokens are validated with the cached public keys, not through Key Vault.");

        protected override void Dispose(bool disposing)
        {
        }
    }
}

/// <summary>Loads the Key Vault signing key before the API takes requests, then refreshes it every hour (key rotation).</summary>
public sealed class KeyVaultJwtKeyLoader(KeyVaultJwtSigningKeys keys, ILogger<KeyVaultJwtKeyLoader> logger) : BackgroundService
{
    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        await keys.LoadAsync(cancellationToken).ConfigureAwait(false);
        await base.StartAsync(cancellationToken).ConfigureAwait(false);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromHours(1));
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            try
            {
                await keys.LoadAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogWarning(exception, "Reloading the JWT signing key from Key Vault failed; keeping the loaded keys.");
            }
        }
    }
}
