namespace Dialed.Domain.Model;

[Flags]
public enum VendorKinds
{
    None = 0,
    Roaster = 1,
    Seller = 2,
}

/// <summary>A roaster and/or a shop that sells coffee.</summary>
public sealed record Vendor : SyncEntity
{
    public required string Name { get; init; }

    public VendorKinds Kinds { get; init; } = VendorKinds.Roaster;

    public string? Country { get; init; }

    public string? City { get; init; }

    public string? Website { get; init; }

    public string? Notes { get; init; }

    public string? LogoPhotoId { get; init; }
}
