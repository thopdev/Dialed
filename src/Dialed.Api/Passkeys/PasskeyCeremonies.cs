using System.Text.Json;
using System.Text.Json.Nodes;
using Dialed.Api.Auth;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

namespace Dialed.Api.Passkeys;

public sealed record SignUpOptionsRequest(string Email, string DisplayName, string? InviteCode = null);

public sealed record CeremonyCompleteRequest(string State, JsonElement Credential);

public sealed record CeremonyOptionsResponse(JsonNode Options, string State);

/// <summary>
/// Cookie-less WebAuthn ceremonies. Identity's <see cref="PasskeyHandler{TUser}"/> does the WebAuthn verification;
/// its attestation/assertion state travels to the client inside a Data Protection token together with
/// the origin, RP ID, expiry and a single-use nonce, instead of in an auth cookie. Success ends in the same
/// access + refresh tokens as every other sign-in.
/// </summary>
public sealed class PasskeyCeremonies(
    UserManager<AccountUser> users,
    IUserDirectory directory,
    ICeremonyNonces nonces,
    OriginPolicy origins,
    RefreshTokenService sessions,
    IDataProtectionProvider dataProtection,
    IOptions<PasskeyOptions> options,
    IBetaInviteStore invites,
    IOptions<BetaOptions> beta,
    TimeProvider time)
{
    private const string SignUp = "signup";
    private const string SignIn = "signin";
    private const string Reauth = "reauth";

    private readonly IDataProtector protector = dataProtection.CreateProtector("Dialed.Passkeys.Ceremony.v1");

    public async Task<IResult> SignUpOptionsAsync(HttpContext http, SignUpOptionsRequest request)
    {
        if (!origins.TryResolve(Origin(http), out var rpId))
        {
            return Problem(StatusCodes.Status400BadRequest, "Origin not allowed.");
        }

        var inviteCode = request.InviteCode?.Trim();
        if (beta.Value.InviteRequired && (string.IsNullOrEmpty(inviteCode) || !await invites.IsValidAsync(inviteCode, time.GetUtcNow(), http.RequestAborted)))
        {
            return Problem(StatusCodes.Status403Forbidden, "Sign-up is by invitation. Ask for an invite code.");
        }

        var email = request.Email?.Trim() ?? "";
        var displayName = string.IsNullOrWhiteSpace(request.DisplayName) ? email : request.DisplayName.Trim();
        if (email.Length is < 3 or > 256 || !email.Contains('@', StringComparison.Ordinal))
        {
            return Problem(StatusCodes.Status400BadRequest, "A valid email address is required.");
        }

        if (displayName.Length > 100)
        {
            return Problem(StatusCodes.Status400BadRequest, "The display name is too long.");
        }

        // Note: this reveals whether an email is registered (enumeration), which sign-up forms need anyway.
        if (await users.FindByEmailAsync(email) is not null)
        {
            return Problem(StatusCodes.Status409Conflict, "This email address already has an account.");
        }

        var userId = Guid.NewGuid().ToString("N");
        var handler = CreateHandler(rpId, Origin(http)!);
        var result = await handler.MakeCreationOptionsAsync(
            new PasskeyUserEntity { Id = userId, Name = email, DisplayName = displayName }, http);

        var state = Protect(new CeremonyState(SignUp, Origin(http)!, rpId, result.AttestationState, userId, email, displayName) { InviteCode = beta.Value.InviteRequired ? inviteCode : null });
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

        // The last use of a code can only go to one sign-up: take it before the account exists.
        if (state.InviteCode is not null && !await invites.TryUseAsync(state.InviteCode, time.GetUtcNow(), http.RequestAborted))
        {
            return Problem(StatusCodes.Status403Forbidden, "That invite code is no longer valid.");
        }

        result.Passkey!.Name = "First passkey";
        var account = new AccountUser { Id = state.UserId!, Email = state.Email, UserName = state.Email, DisplayName = state.DisplayName! };
        account.Passkeys.Add(StoredPasskey.From(result.Passkey));

        var created = await users.CreateAsync(account);
        if (!created.Succeeded)
        {
            var duplicate = created.Errors.Any(e => e.Code is nameof(IdentityErrorDescriber.DuplicateEmail) or nameof(IdentityErrorDescriber.DuplicateUserName));
            return Problem(duplicate ? StatusCodes.Status409Conflict : StatusCodes.Status400BadRequest,
                string.Join(" ", created.Errors.Select(e => e.Description)));
        }

        // Everyone starts in a household of their own; joining another one comes with households (#70).
        var person = new AppUser(account.Id, account.Email, account.DisplayName, "h-" + account.Id);
        await directory.AddAsync(person, http.RequestAborted);
        return Results.Ok(await sessions.SignInAsync(person, http.RequestAborted));
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

        var person = await directory.FindByIdAsync(result.User!.Id, http.RequestAborted);
        if (person is null)
        {
            return Problem(StatusCodes.Status401Unauthorized, "Passkey sign-in failed.");
        }

        // Persist the new sign count / backup state.
        await users.AddOrUpdatePasskeyAsync(result.User, result.Passkey!);
        return Results.Ok(await sessions.SignInAsync(person, http.RequestAborted));
    }

    /// <summary>Starts proving it is really the signed-in person (before something irreversible such as deleting the account).</summary>
    public async Task<IResult> ReauthOptionsAsync(HttpContext http)
    {
        if (!origins.TryResolve(Origin(http), out var rpId))
        {
            return Problem(StatusCodes.Status400BadRequest, "Origin not allowed.");
        }

        var handler = CreateHandler(rpId, Origin(http)!);
        var result = await handler.MakeRequestOptionsAsync(null, http);
        var state = Protect(new CeremonyState(Reauth, Origin(http)!, rpId, result.AssertionState, null, null, null));
        return Results.Ok(new CeremonyOptionsResponse(JsonNode.Parse(result.RequestOptionsJson)!, state));
    }

    /// <summary>Checks the passkey belongs to <paramref name="expectedUserId"/>. Returns <c>null</c> when it does, otherwise the error to send back. Signs nobody in.</summary>
    public async Task<IResult?> VerifyReauthAsync(HttpContext http, CeremonyCompleteRequest request, string expectedUserId)
    {
        var (state, error) = await OpenAsync(http, request.State, Reauth);
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

        if (!result.Succeeded || result.User?.Id != expectedUserId)
        {
            return Problem(StatusCodes.Status401Unauthorized, "That passkey does not belong to this account.");
        }

        await users.AddOrUpdatePasskeyAsync(result.User, result.Passkey!);
        return null;
    }

    /// <summary>
    /// A handler per request: the RP ID (<see cref="IdentityPasskeyOptions.ServerDomain"/>) is a singleton option
    /// in Identity, but we need one per calling origin (localhost, dev tunnel, preview, production).
    /// </summary>
    private PasskeyHandler<AccountUser> CreateHandler(string rpId, string origin) =>
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
            state = JsonSerializer.Deserialize<CeremonyState>(protector.Unprotect(token ?? ""));
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

        // Single use: the first completion claims the nonce; replays are refused.
        if (!await nonces.TryClaimAsync(state.Nonce, options.Value.CeremonyLifetime + TimeSpan.FromMinutes(1), http.RequestAborted))
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

        public string? InviteCode { get; init; }

        public DateTimeOffset ExpiresAt { get; init; }
    }
}
