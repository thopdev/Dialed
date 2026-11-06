namespace Dialed.Client.Platform;

public enum SoundCue
{
    /// <summary>Short tick, for example a step change.</summary>
    Tick,

    /// <summary>Timer finished.</summary>
    Done,
}

/// <summary>Short audible cues for the brew timer. Web: Web Audio (no sound files needed).</summary>
public interface ISoundCue
{
    /// <summary>Plays the cue. Must first be called from a tap so the browser allows audio.</summary>
    Task PlayAsync(SoundCue cue);
}
