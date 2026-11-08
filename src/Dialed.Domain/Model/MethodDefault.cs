namespace Dialed.Domain.Model;

/// <summary>The household's usual grinder and brewer for one brew method (by <see cref="BrewMethods.BrewMethod.Code"/>).</summary>
public sealed record MethodDefault : SyncEntity
{
    public required string MethodCode { get; init; }

    public string? GrinderId { get; init; }

    public string? BrewerId { get; init; }

    public bool IsEmpty => GrinderId is null && BrewerId is null;
}
