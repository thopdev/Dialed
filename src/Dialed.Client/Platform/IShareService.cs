namespace Dialed.Client.Platform;

public sealed record ShareRequest(string? Title = null, string? Text = null, string? Url = null);

/// <summary>Opens the system share sheet (share a recipe or a household invite). Web: Web Share API.</summary>
public interface IShareService
{
    /// <summary>False when the platform has no share sheet; offer "copy link" instead.</summary>
    Task<bool> IsSupportedAsync();

    /// <summary>Returns <c>false</c> when the user dismissed the sheet or sharing is not possible.</summary>
    Task<bool> ShareAsync(ShareRequest request);
}
