namespace Dialed.Api.Protection;

/// <summary>Limits that protect the API from abuse (#92). Configured under <c>Protection</c>; development settings are far looser.</summary>
public sealed class ProtectionOptions
{
    public const string Section = "Protection";

    /// <summary>Requests per minute for one signed-in user (or one address when anonymous) on ordinary endpoints.</summary>
    public int RequestsPerMinute { get; set; } = 300;

    /// <summary>Requests per minute for one address on sign-up, sign-in and token endpoints (<c>/auth</c>).</summary>
    public int AuthRequestsPerMinute { get; set; } = 20;

    /// <summary>Requests per minute for one user on the AI endpoints (<c>/ai</c>), which cost money.</summary>
    public int AiRequestsPerMinute { get; set; } = 10;

    /// <summary>Requests per minute for one user on the friend endpoints (<c>/friends</c>), which is stricter so friend codes cannot be guessed.</summary>
    public int FriendsRequestsPerMinute { get; set; } = 30;

    /// <summary>Send <c>Strict-Transport-Security</c>. On everywhere except local development, where the API runs over plain HTTP.</summary>
    public bool Hsts { get; set; } = true;

    /// <summary>Largest accepted request body. Photos do not go through the API (they use direct upload URLs).</summary>
    public long MaxRequestBodyBytes { get; set; } = 1_048_576;
}
