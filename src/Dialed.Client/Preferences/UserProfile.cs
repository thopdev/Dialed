using Dialed.Domain.Model;

namespace Dialed.Client.Preferences;

/// <summary>What the user chose about themselves: name, default visibility for new items, favourite brew methods and whether setup is done.</summary>
public sealed record UserProfile(
    string DisplayName,
    Visibility DefaultVisibility,
    IReadOnlyList<string> FavouriteMethods,
    bool OnboardingDone,
    string? AvatarPhotoId = null,
    bool TimerVoice = false)
{
    public static UserProfile Default { get; } = new("", Visibility.Household, [], false);

    public bool Equals(UserProfile? other) =>
        other is not null
        && DisplayName == other.DisplayName
        && DefaultVisibility == other.DefaultVisibility
        && OnboardingDone == other.OnboardingDone
        && AvatarPhotoId == other.AvatarPhotoId
        && TimerVoice == other.TimerVoice
        && FavouriteMethods.SequenceEqual(other.FavouriteMethods);

    public override int GetHashCode() => HashCode.Combine(DisplayName, DefaultVisibility, OnboardingDone, FavouriteMethods.Count, AvatarPhotoId, TimerVoice);
}
