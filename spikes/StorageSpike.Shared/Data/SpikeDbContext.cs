using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace StorageSpike.Data;

/// <summary>A cut-down version of the planned offline store: synced rows, an outbox and a launch log.</summary>
public sealed class SpikeDbContext(DbContextOptions<SpikeDbContext> options) : DbContext(options)
{
    public DbSet<BrewRow> Brews => Set<BrewRow>();
    public DbSet<OutboxRow> Outbox => Set<OutboxRow>();
    public DbSet<LaunchRow> Launches => Set<LaunchRow>();

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder) =>
        // The migration below is hand-written without a model snapshot.
        optionsBuilder.ConfigureWarnings(warnings => warnings.Ignore(RelationalEventId.PendingModelChangesWarning));

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<BrewRow>(brew =>
        {
            brew.ToTable("Brews");
            brew.HasKey(b => b.Id);
            brew.HasIndex(b => new { b.HouseholdId, b.UpdatedAtMs });
        });

        modelBuilder.Entity<OutboxRow>(outbox =>
        {
            outbox.ToTable("Outbox");
            outbox.HasKey(o => o.Seq);
        });

        modelBuilder.Entity<LaunchRow>(launch =>
        {
            launch.ToTable("Launches");
            launch.HasKey(l => l.Id);
        });
    }
}

public sealed class BrewRow
{
    public required string Id { get; set; }
    public required string HouseholdId { get; set; }
    public long BrewedAtMs { get; set; }
    public double DoseG { get; set; }
    public double YieldG { get; set; }
    public int TimeS { get; set; }
    public int Rating { get; set; }
    public string? Notes { get; set; }
    public long UpdatedAtMs { get; set; }
    public string? Version { get; set; }
    public bool Deleted { get; set; }
}

public sealed class OutboxRow
{
    public long Seq { get; set; }
    public required string EntityType { get; set; }
    public required string EntityId { get; set; }
    public required string Operation { get; set; }
    public required string Payload { get; set; }
    public long CreatedAtMs { get; set; }
}

public sealed class LaunchRow
{
    public long Id { get; set; }
    public long LaunchedAtMs { get; set; }
    public bool Standalone { get; set; }
    public bool Persisted { get; set; }
}
