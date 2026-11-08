namespace Dialed.Client;

/// <summary>
/// Carries the time of a finished timer run to the brew log, so the log opens with the measured time filled in.
/// The log takes it once (<see cref="Take"/>) so an old time never reappears in a later brew.
/// </summary>
public sealed class BrewTimerHandoff
{
    private int? elapsedS;
    private string? recipeId;

    public void Set(string? recipeId, int elapsedS)
    {
        this.recipeId = recipeId;
        this.elapsedS = elapsedS;
    }

    /// <summary>The measured seconds for this recipe (or for no recipe), cleared afterwards. <c>null</c> when there is none.</summary>
    public int? Take(string? forRecipeId)
    {
        var result = elapsedS is not null && recipeId == forRecipeId ? elapsedS : null;
        elapsedS = null;
        recipeId = null;
        return result;
    }
}
