using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using PasskeySpike.Storage;

namespace PasskeySpike;

public sealed record SignUpOptionsRequest(string Email, string DisplayName);

public sealed record CeremonyCompleteRequest(string State, JsonElement Credential);

public sealed record CeremonyOptionsResponse(JsonNode Options, string State);

public sealed record TokenResponse(string AccessToken, DateTimeOffset ExpiresAt, string UserId, string DisplayName);

/// <summary>
/// Cookie-less WebAuthn ceremonies. Identity's <see cref="PasskeyHandler{TUser}"/> does the WebAuthn verification;
/// its attestation/assertion state travels to the client inside a Data Protection–protected token together with
/// the origin, RP ID, expiry and a single-use nonce, instead of in an auth cookie.
/// </summary>
public sealed class PasskeyCeremonies(
    UserManager<SpikeUser> users,
    IdentityCosmos cosmos,
    OriginPolicy origins,
    TokenIssuer tokens,
    IDataProtectionProvider dataProtection,
    IOptions<PasskeyOptions> options,
    TimeProvider time)
{
    private const string SignUp = "signup";
    private const string SignIn = "signin";

    private readonly IDataProtector protector = dataProtection.CreateProtector("Dialed.Passkeys.Ceremony.v1");

    public async Task<IResult> SignUpOptionsAsync(HttpContext http, SignUpOptionsRequest request)
    {
        if (!origins.TryResolve(Origin(http), out var rpId))
        {
            return Problem(StatusCodes.Status400BadRequest, "Origin not allowed.");
        }

        var email = request.Email?.Trim() ?? "";
        var displayName = string.IsNullOrWhiteSpace(request.DisplayName) ? email : request.DisplayName.Trim();
        if (email.Length is < 3 or > 256 || !email.Contains('@', StringComparison.Ordinal))
        {
            return Problem(StatusCodes.Status400BadRequest, "A valid email address is required.");
        }

        // Note: this reveals whether an email is registered (enumeration). Acceptable for M0, revisit in #30.
        if (await users.FindByEmailAsync(email) is not null)
        {
            return Problem(StatusCodes.Status409Conflict, "This email address already has an account.");
        }

        var userId = Guid.NewGuid().ToString("N");
        var handler = CreateHandler(rpId, Origin(http)!);
        var result = await handler.MakeCreationOptionsAsync(
            new PasskeyUserEntity { Id = userId, Name = email, DisplayName = displayName }, http);

        var state = Protect(new CeremonyState(SignUp, Origin(http)!, rpId, result.AttestationState, userId, email, displayName));
        return Results.Ok(new CeremonyOptionsResponse(JsonNode.Parse(result.CreationOptionsJson)!, state));
    }

    public async Task<IResult> SignUpAsync(HttpContext http, CeremonyCompleteRequest request)
    {
        var (state, error) = await OpenAsync(http, request.State, SignUp);
        if (state is null)
        {
            return error!;
        }

        var handler = CreateHandler(state.RpId, state.Origin);
        var result = await handler.PerformAttestationAsync(new PasskeyAttestationContext
        {
            HttpContext = http,
            CredentialJson = request.Credential.GetRawText(),
            AttestationState = state.HandlerState,
        });

        if (!result.Succeeded || result.UserEntity?.Id != state.UserId)
        {
            return Problem(StatusCodes.Status400BadRequest, result.Failure?.Message ?? "Passkey registration failed.");
        }

        result.Passkey!.Name = "First passkey";
        var user = new SpikeUser { Id = state.UserId!, Email = state.Email, UserName = state.Email, DisplayName = state.DisplayName! };
        user.Passkeys.Add(StoredPasskey.From(result.Passkey));

        var created = await users.CreateAsync(user);
        if (!created.Succeeded)
        {
            var duplicate = created.Errors.Any(e => e.Code is nameof(IdentityErrorDescriber.DuplicateEmail) or nameof(IdentityErrorDescriber.DuplicateUserName));
            return Problem(duplicate ? StatusCodes.Status409Conflict : StatusCodes.Status400BadRequest,
                string.Join(" ", created.Errors.Select(e => e.Description)));
        }

        return Results.Ok(tokens.Issue(user));
    }

    public async Task<IResult> SignInOptionsAsync(HttpContext http)
    {
        if (!origins.TryResolve(Origin(http), out var rpId))
        {
            return Problem(StatusCodes.Status400BadRequest, "Origin not allowed.");
        }

        // No user: discoverable credentials, so the browser/OS offers the user's passkeys (and autofill can work).
        var handler = CreateHandler(rpId, Origin(http)!);
        var result = await handler.MakeRequestOptionsAsync(null, http);

        var state = Protect(new CeremonyState(SignIn, Origin(http)!, rpId, result.AssertionState, null, null, null));
        return Results.Ok(new CeremonyOptionsResponse(JsonNode.Parse(result.RequestOptionsJson)!, state));
    }

    public async Task<IResult> SignInAsync(HttpContext http, CeremonyCompleteRequest request)
    {
        var (state, error) = await OpenAsync(http, request.State, SignIn);
        if (state is null)
        {
            return error!;
        }

        var handler = CreateHandler(state.RpId, state.Origin);
        var result = await handler.PerformAssertionAsync(new PasskeyAssertionContext
        {
            HttpContext = http,
            CredentialJson = request.Credential.GetRawText(),
            AssertionState = state.HandlerState,
        });

        if (!result.Succeeded)
        {
            return Problem(StatusCodes.Status401Unauthorized, result.Failure?.Message ?? "Passkey sign-in failed.");
        }

        // Persist the new sign count / backup state.
        await users.AddOrUpdatePasskeyAsync(result.User!, result.Passkey!);
        return Results.Ok(tokens.Issue(result.User!));
    }

    /// <summary>
    /// A handler per request: the RP ID (<see cref="IdentityPasskeyOptions.ServerDomain"/>) is a singleton option
    /// in Identity, but we need one per calling origin (localhost, dev tunnel, preview, production).
    /// </summary>
    private PasskeyHandler<SpikeUser> CreateHandler(string rpId, string origin) =>
        new(users, Options.Create(new IdentityPasskeyOptions
        {
            ServerDomain = rpId,
            AuthenticatorTimeout = options.Value.CeremonyLifetime,
            ResidentKeyRequirement = "required", // discoverable: sign in without typing an email
            UserVerificationRequirement = "required",
            ValidateOrigin = context => ValueTask.FromResult(
                !context.CrossOrigin && string.Equals(context.Origin, origin, StringComparison.Ordinal)),
        }));

    private string Protect(CeremonyState state)
    {
        var withExpiry = state with
        {
            Nonce = Guid.NewGuid().ToString("N"),
            ExpiresAt = time.GetUtcNow().Add(options.Value.CeremonyLifetime),
        };
        return protector.Protect(JsonSerializer.Serialize(withExpiry));
    }

    private async Task<(CeremonyState? State, IResult? Error)> OpenAsync(HttpContext http, string token, string kind)
    {
        CeremonyState? state;
        try
        {
            state = JsonSerializer.Deserialize<CeremonyState>(protector.Unprotect(token));
        }
        catch (Exception ex) when (ex is System.Security.Cryptography.CryptographicException or JsonException or FormatException)
        {
            return (null, Problem(StatusCodes.Status400BadRequest, "Invalid ceremony state."));
        }

        if (state is null || state.Kind != kind)
        {
            return (null, Problem(StatusCodes.Status400BadRequest, "Invalid ceremony state."));
        }

        if (state.ExpiresAt < time.GetUtcNow())
        {
            return (null, Problem(StatusCodes.Status400BadRequest, "The ceremony expired, start again."));
        }

        // The completing request must come from the same origin that started the ceremony.
        if (!string.Equals(Origin(http), state.Origin, StringComparison.Ordinal))
        {
            return (null, Problem(StatusCodes.Status400BadRequest, "Origin mismatch."));
        }

        // Single use: the first completion claims the nonce; replays get a conflict. TTL cleans the marker up.
        var ttl = (int)Math.Ceiling(options.Value.CeremonyLifetime.TotalSeconds) + 60;
        if (!await cosmos.TryCreateLookupAsync("ceremony:" + state.Nonce, state.UserId ?? "", ttl, http.RequestAborted))
        {
            return (null, Problem(StatusCodes.Status400BadRequest, "This ceremony was already used."));
        }

        return (state, null);
    }

    private static string? Origin(HttpContext http) => http.Request.Headers.Origin is [var origin] ? origin : null;

    private static IResult Problem(int status, string detail) => Results.Problem(detail: detail, statusCode: status);

    internal sealed record CeremonyState(
        string Kind,
        string Origin,
        string RpId,
        string? HandlerState,
        string? UserId,
        string? Email,
        string? DisplayName)
    {
        public string Nonce { get; init; } = "";
        public DateTimeOffset ExpiresAt { get; init; }
    }
}
