using System.Buffers.Binary;
using System.Buffers.Text;
using System.Formats.Cbor;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace PasskeySpike.Tests;

/// <summary>
/// A minimal WebAuthn platform authenticator (ES256, "none" attestation, discoverable credentials) for tests.
/// Modelled on ASP.NET Core's own passkey test helpers.
/// </summary>
public sealed class SoftwareAuthenticator
{
    private const byte UserPresent = 1 << 0;
    private const byte UserVerified = 1 << 2;
    private const byte BackupEligible = 1 << 3;
    private const byte BackedUp = 1 << 4;
    private const byte AttestedCredentialData = 1 << 6;

    private readonly List<Credential> credentials = [];

    public sealed record Credential(byte[] Id, string RpId, byte[] UserHandle, ECDsa Key)
    {
        public uint SignCount { get; set; }
    }

    public IReadOnlyList<Credential> Credentials => credentials;

    /// <summary>navigator.credentials.create(): returns the credential JSON the browser would send.</summary>
    public JsonObject Create(JsonNode options, string origin, string? overrideRpId = null)
    {
        var rpId = overrideRpId ?? options["rp"]!["id"]!.GetValue<string>();
        var userHandle = Base64Url.DecodeFromChars(options["user"]!["id"]!.GetValue<string>());
        var credential = new Credential(RandomNumberGenerator.GetBytes(16), rpId, userHandle, ECDsa.Create(ECCurve.NamedCurves.nistP256));
        credentials.Add(credential);

        var attestedCredentialData = AttestedData(credential);
        var authenticatorData = AuthenticatorData(rpId, (byte)(UserPresent | UserVerified | BackupEligible | BackedUp | AttestedCredentialData), 0, attestedCredentialData);
        var clientData = ClientData("webauthn.create", options["challenge"]!.GetValue<string>(), origin);

        var attestation = new CborWriter(CborConformanceMode.Ctap2Canonical);
        attestation.WriteStartMap(3);
        attestation.WriteTextString("fmt");
        attestation.WriteTextString("none");
        attestation.WriteTextString("attStmt");
        attestation.WriteStartMap(0);
        attestation.WriteEndMap();
        attestation.WriteTextString("authData");
        attestation.WriteByteString(authenticatorData);
        attestation.WriteEndMap();

        return new JsonObject
        {
            ["id"] = Base64Url.EncodeToString(credential.Id),
            ["rawId"] = Base64Url.EncodeToString(credential.Id),
            ["type"] = "public-key",
            ["authenticatorAttachment"] = "platform",
            ["clientExtensionResults"] = new JsonObject(),
            ["response"] = new JsonObject
            {
                ["attestationObject"] = Base64Url.EncodeToString(attestation.Encode()),
                ["clientDataJSON"] = Base64Url.EncodeToString(clientData),
                ["transports"] = new JsonArray("internal", "hybrid"),
            },
        };
    }

    /// <summary>navigator.credentials.get() with a discoverable credential for the options' RP ID.</summary>
    public JsonObject Get(JsonNode options, string origin, Credential? credential = null)
    {
        var rpId = options["rpId"]!.GetValue<string>();
        credential ??= credentials.Last(c => c.RpId == rpId);
        credential.SignCount++;

        // A real authenticator signs for the RP ID the credential was created for.
        var authenticatorData = AuthenticatorData(credential.RpId, UserPresent | UserVerified | BackupEligible | BackedUp, credential.SignCount, attestedCredentialData: null);
        var clientData = ClientData("webauthn.get", options["challenge"]!.GetValue<string>(), origin);
        byte[] signed = [.. authenticatorData, .. SHA256.HashData(clientData)];
        var signature = credential.Key.SignData(signed, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);

        return new JsonObject
        {
            ["id"] = Base64Url.EncodeToString(credential.Id),
            ["rawId"] = Base64Url.EncodeToString(credential.Id),
            ["type"] = "public-key",
            ["authenticatorAttachment"] = "platform",
            ["clientExtensionResults"] = new JsonObject(),
            ["response"] = new JsonObject
            {
                ["authenticatorData"] = Base64Url.EncodeToString(authenticatorData),
                ["clientDataJSON"] = Base64Url.EncodeToString(clientData),
                ["signature"] = Base64Url.EncodeToString(signature),
                ["userHandle"] = Base64Url.EncodeToString(credential.UserHandle),
            },
        };
    }

    private static byte[] ClientData(string type, string challenge, string origin) =>
        Encoding.UTF8.GetBytes(new JsonObject { ["type"] = type, ["challenge"] = challenge, ["origin"] = origin, ["crossOrigin"] = false }.ToJsonString());

    private static byte[] AuthenticatorData(string rpId, byte flags, uint signCount, byte[]? attestedCredentialData)
    {
        var rpIdHash = SHA256.HashData(Encoding.UTF8.GetBytes(rpId));
        var counter = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(counter, signCount);
        return [.. rpIdHash, flags, .. counter, .. attestedCredentialData ?? []];
    }

    private static byte[] AttestedData(Credential credential)
    {
        var parameters = credential.Key.ExportParameters(false);
        var key = new CborWriter(CborConformanceMode.Ctap2Canonical);
        key.WriteStartMap(5);
        key.WriteInt32(1);  // kty
        key.WriteInt32(2);  // EC2
        key.WriteInt32(3);  // alg
        key.WriteInt32(-7); // ES256
        key.WriteInt32(-1); // crv
        key.WriteInt32(1);  // P-256
        key.WriteInt32(-2); // x
        key.WriteByteString(parameters.Q.X!);
        key.WriteInt32(-3); // y
        key.WriteByteString(parameters.Q.Y!);
        key.WriteEndMap();

        var length = new byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(length, (ushort)credential.Id.Length);
        return [.. new byte[16], .. length, .. credential.Id, .. key.Encode()]; // AAGUID zero = "none" attestation
    }
}
