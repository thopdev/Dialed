using System.Security.Cryptography;
using Dialed.Api.Auth;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Dialed.Api.Tests;

/// <summary>Key Vault signing (#30) against a local RSA stand-in for the vault: sign remotely, validate with the public keys.</summary>
public sealed class KeyVaultJwtSigningTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly FakeVault vault = new();
    private readonly Clock clock = new(new DateTimeOffset(2026, 11, 18, 12, 0, 0, TimeSpan.Zero));

    public void Dispose() => vault.Dispose();

    private static AppUser Alice => new("u-alice", "alice@example.invalid", "Alice", "h-1");

    private TokenIssuer Issuer(IJwtSigningKeys keys) => new(Options.Create(new JwtOptions()), keys, clock);

    private async Task<TokenValidationResult> Validate(IJwtSigningKeys keys, string token) =>
        await new JsonWebTokenHandler().ValidateTokenAsync(token, new TokenValidationParameters
        {
            ValidIssuer = "dialed-api",
            ValidAudience = "dialed-app",
            IssuerSigningKeyResolver = (_, _, keyId, _) => keys.ValidationKeys(keyId),
            LifetimeValidator = (_, _, _, _) => true,
        });

    [Fact]
    public async Task Tokens_are_RS256_with_the_key_version_as_kid_and_validate_with_the_public_key()
    {
        var keys = new KeyVaultJwtSigningKeys(vault, clock);
        await keys.LoadAsync(Ct);

        var (token, _) = Issuer(keys).IssueAccessToken(Alice);

        var header = new JsonWebToken(token);
        header.Alg.ShouldBe(SecurityAlgorithms.RsaSha256);
        header.Kid.ShouldBe("v1");
        vault.SignCalls.ShouldBe(1);
        (await Validate(keys, token)).IsValid.ShouldBeTrue();
    }

    [Fact]
    public async Task A_tampered_token_or_a_foreign_key_is_rejected()
    {
        var keys = new KeyVaultJwtSigningKeys(vault, clock);
        await keys.LoadAsync(Ct);
        var (token, _) = Issuer(keys).IssueAccessToken(Alice);

        var parts = token.Split('.');
        var tampered = $"{parts[0]}.{Base64UrlEncoder.Encode("{\"sub\":\"u-mallory\"}")}.{parts[2]}";
        (await Validate(keys, tampered)).IsValid.ShouldBeFalse();

        using var foreign = RSA.Create(2048);
        var forged = new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = "dialed-api",
            Audience = "dialed-app",
            SigningCredentials = new SigningCredentials(new RsaSecurityKey(foreign) { KeyId = "v1" }, SecurityAlgorithms.RsaSha256),
        });
        (await Validate(keys, forged)).IsValid.ShouldBeFalse();
    }

    [Fact]
    public async Task After_a_rotation_old_tokens_stay_valid_and_new_ones_use_the_new_version()
    {
        var keys = new KeyVaultJwtSigningKeys(vault, clock);
        await keys.LoadAsync(Ct);
        var (oldToken, _) = Issuer(keys).IssueAccessToken(Alice);

        vault.Rotate();
        await keys.LoadAsync(Ct);
        var (newToken, _) = Issuer(keys).IssueAccessToken(Alice);

        new JsonWebToken(newToken).Kid.ShouldBe("v2");
        (await Validate(keys, oldToken)).IsValid.ShouldBeTrue();
        (await Validate(keys, newToken)).IsValid.ShouldBeTrue();
    }

    [Fact]
    public async Task An_unknown_kid_reloads_the_keys_at_most_every_few_minutes()
    {
        var keys = new KeyVaultJwtSigningKeys(vault, clock);
        await keys.LoadAsync(Ct);
        vault.Rotate();
        var otherInstance = new KeyVaultJwtSigningKeys(vault, clock);
        await otherInstance.LoadAsync(Ct);
        var (tokenFromOtherInstance, _) = Issuer(otherInstance).IssueAccessToken(Alice);

        // Just loaded: an unknown kid does not hit Key Vault again yet.
        var loadsBefore = vault.PublicKeyLoads;
        (await Validate(keys, tokenFromOtherInstance)).IsValid.ShouldBeFalse();
        vault.PublicKeyLoads.ShouldBe(loadsBefore);

        clock.Advance(TimeSpan.FromMinutes(6));
        (await Validate(keys, tokenFromOtherInstance)).IsValid.ShouldBeTrue();
        vault.PublicKeyLoads.ShouldBe(loadsBefore + 1);
    }

    [Fact]
    public void Signing_before_the_key_is_loaded_fails_clearly()
    {
        var keys = new KeyVaultJwtSigningKeys(vault, clock);
        Should.Throw<InvalidOperationException>(() => Issuer(keys).IssueAccessToken(Alice))
            .Message.ShouldContain("not loaded");
    }

    /// <summary>Holds RSA keys like Key Vault versions; only the public halves leave it.</summary>
    private sealed class FakeVault : IJwtKeySource, IDisposable
    {
        private readonly List<(string Id, RSA Key)> versions = [("v1", RSA.Create(2048))];

        public int SignCalls { get; private set; }

        public int PublicKeyLoads { get; private set; }

        public void Rotate() => versions.Add(($"v{versions.Count + 1}", RSA.Create(2048)));

        public Task<string> GetCurrentKeyIdAsync(CancellationToken cancellationToken) => Task.FromResult(versions[^1].Id);

        public Task<IReadOnlyList<JwtPublicKey>> GetPublicKeysAsync(CancellationToken cancellationToken)
        {
            PublicKeyLoads++;
            IReadOnlyList<JwtPublicKey> keys = versions.Select(v => new JwtPublicKey(v.Id, v.Key.ExportParameters(false))).ToList();
            return Task.FromResult(keys);
        }

        public byte[] Sign(string keyId, byte[] data)
        {
            SignCalls++;
            return versions.Single(v => v.Id == keyId).Key.SignData(data, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        }

        public void Dispose()
        {
            foreach (var (_, key) in versions)
            {
                key.Dispose();
            }
        }
    }

    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset current = now;

        public void Advance(TimeSpan by) => current += by;

        public override DateTimeOffset GetUtcNow() => current;
    }
}
