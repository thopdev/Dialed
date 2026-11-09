using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Dialed.Web.Storage;

/// <summary>The app's local database: one table of JSON documents grouped by collection (see <c>ILocalStore</c>).</summary>
public sealed class LocalDbContext(DbContextOptions<LocalDbContext> options) : DbContext(options)
{
    public DbSet<DocumentRow> Documents => Set<DocumentRow>();

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder) =>
        // The migration is written by hand, without a model snapshot.
        optionsBuilder.ConfigureWarnings(warnings => warnings.Ignore(RelationalEventId.PendingModelChangesWarning));

    protected override void OnModelCreating(ModelBuilder modelBuilder) =>
        modelBuilder.Entity<DocumentRow>(document =>
        {
            document.ToTable("Documents");
            document.HasKey(d => new { d.Collection, d.Key });
        });
}

public sealed class DocumentRow
{
    public required string Collection { get; set; }

    public required string Key { get; set; }

    public required string Json { get; set; }
}
