namespace Dialed.Domain.BrewMethods;

public enum BrewMethodCategory
{
    Espresso,
    PourOver,
    Immersion,
    ColdBrew,
    Other,
}

/// <summary>An input a recipe or brew of a method asks for. Ratio is never asked for: it is derived from dose and water.</summary>
public enum BrewParameter
{
    Dose,
    Water,
    Yield,
    Grind,
    Temperature,
    Time,
    Preinfusion,
    Steps,
    Filter,
}

/// <summary>A brew method with the parameters that matter for it, so forms only ask for those.</summary>
public sealed record BrewMethod(string Code, string Name, BrewMethodCategory Category, IReadOnlyList<BrewParameter> Parameters)
{
    public bool Uses(BrewParameter parameter) => Parameters.Contains(parameter);
}
