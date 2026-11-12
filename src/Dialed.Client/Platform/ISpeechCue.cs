namespace Dialed.Client.Platform;

/// <summary>Speaks a short phrase aloud, so the brew timer can say the next step while your hands are busy. Web: speech synthesis.</summary>
public interface ISpeechCue
{
    /// <summary>Whether this device can speak. When it cannot, the timer does not offer the switch.</summary>
    Task<bool> IsSupportedAsync();

    /// <summary>Says the text in the given language (<c>nl</c> or <c>en</c>), replacing anything still being said. Must first be called from a tap.</summary>
    Task SpeakAsync(string text, string language);
}
