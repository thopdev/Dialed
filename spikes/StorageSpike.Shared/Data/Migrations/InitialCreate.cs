using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace StorageSpike.Data.Migrations;

/// <summary>Hand-written so the spike can check that EF Core migrations run in the browser.</summary>
[DbContext(typeof(SpikeDbContext))]
[Migration("20261105000000_InitialCreate")]
public sealed class InitialCreate : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "Brews",
            columns: table => new
            {
                Id = table.Column<string>(type: "TEXT", nullable: false),
                HouseholdId = table.Column<string>(type: "TEXT", nullable: false),
                BrewedAtMs = table.Column<long>(type: "INTEGER", nullable: false),
                DoseG = table.Column<double>(type: "REAL", nullable: false),
                YieldG = table.Column<double>(type: "REAL", nullable: false),
                TimeS = table.Column<int>(type: "INTEGER", nullable: false),
                Rating = table.Column<int>(type: "INTEGER", nullable: false),
                Notes = table.Column<string>(type: "TEXT", nullable: true),
                UpdatedAtMs = table.Column<long>(type: "INTEGER", nullable: false),
                Version = table.Column<string>(type: "TEXT", nullable: true),
                Deleted = table.Column<bool>(type: "INTEGER", nullable: false),
            },
            constraints: table => table.PrimaryKey("PK_Brews", x => x.Id));

        migrationBuilder.CreateIndex(
            name: "IX_Brews_HouseholdId_UpdatedAtMs",
            table: "Brews",
            columns: ["HouseholdId", "UpdatedAtMs"]);

        migrationBuilder.CreateTable(
            name: "Outbox",
            columns: table => new
            {
                Seq = table.Column<long>(type: "INTEGER", nullable: false).Annotation("Sqlite:Autoincrement", true),
                EntityType = table.Column<string>(type: "TEXT", nullable: false),
                EntityId = table.Column<string>(type: "TEXT", nullable: false),
                Operation = table.Column<string>(type: "TEXT", nullable: false),
                Payload = table.Column<string>(type: "TEXT", nullable: false),
                CreatedAtMs = table.Column<long>(type: "INTEGER", nullable: false),
            },
            constraints: table => table.PrimaryKey("PK_Outbox", x => x.Seq));

        migrationBuilder.CreateTable(
            name: "Launches",
            columns: table => new
            {
                Id = table.Column<long>(type: "INTEGER", nullable: false).Annotation("Sqlite:Autoincrement", true),
                LaunchedAtMs = table.Column<long>(type: "INTEGER", nullable: false),
                Standalone = table.Column<bool>(type: "INTEGER", nullable: false),
                Persisted = table.Column<bool>(type: "INTEGER", nullable: false),
            },
            constraints: table => table.PrimaryKey("PK_Launches", x => x.Id));
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "Brews");
        migrationBuilder.DropTable(name: "Outbox");
        migrationBuilder.DropTable(name: "Launches");
    }
}
