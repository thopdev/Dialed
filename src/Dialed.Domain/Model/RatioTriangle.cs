namespace Dialed.Domain.Model;

public enum RatioField
{
    Dose,
    Liquid,
    Ratio,
}

/// <summary>
/// Dose, liquid (water or yield) and ratio belong together: edit any two and the third follows. The field edited
/// longest ago is the one that gets recalculated, so the last two things a person typed always stay as typed.
/// </summary>
public sealed class RatioTriangle
{
    // Most recently set first.
    private readonly List<RatioField> recent = [];

    public RatioTriangle(decimal? dose = null, decimal? liquid = null, decimal? ratio = null)
    {
        Dose = dose;
        Liquid = liquid;
        Ratio = ratio;

        // Dose and liquid are what a recipe stores, so they count as the last typed; ratio follows from them.
        if (liquid is not null)
        {
            recent.Add(RatioField.Liquid);
        }

        if (dose is not null)
        {
            recent.Add(RatioField.Dose);
        }

        if (dose is not null && liquid is not null)
        {
            Recalculate();
        }
        else if (ratio is not null)
        {
            recent.Add(RatioField.Ratio);
            Recalculate();
        }
    }

    public decimal? Dose { get; private set; }

    public decimal? Liquid { get; private set; }

    public decimal? Ratio { get; private set; }

    public void Set(RatioField field, decimal? value)
    {
        switch (field)
        {
            case RatioField.Dose: Dose = value; break;
            case RatioField.Liquid: Liquid = value; break;
            default: Ratio = value; break;
        }

        recent.Remove(field);
        if (value is null)
        {
            return;
        }

        recent.Insert(0, field);
        Recalculate();
    }

    private void Recalculate()
    {
        if (recent.Count < 2)
        {
            return;
        }

        var target = Enum.GetValues<RatioField>().First(f => f != recent[0] && f != recent[1]);
        switch (target)
        {
            case RatioField.Ratio when Dose is > 0m && Liquid is not null:
                Ratio = Math.Round(Liquid.Value / Dose.Value, 2);
                break;
            case RatioField.Liquid when Dose is not null && Ratio is not null:
                Liquid = Math.Round(Dose.Value * Ratio.Value, 1);
                break;
            case RatioField.Dose when Liquid is not null && Ratio is > 0m:
                Dose = Math.Round(Liquid.Value / Ratio.Value, 1);
                break;
        }
    }
}
