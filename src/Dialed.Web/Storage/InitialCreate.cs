using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Dialed.Web.Storage;

[DbContext(typeof(LocalDbContext))]
[Migration("20261109000000_InitialCreate")]
public sealed class InitialCreate : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) =>
        migrationBuilder.CreateTable(
            name: "Documents",
            columns: table => new
            {
                Collection = table.Column<string>(type: "TEXT", nullable: false),
                Key = table.Column<string>(type: "TEXT", nullable: false),
                Json = table.Column<string>(type: "TEXT", nullable: false),
            },
            constraints: table => table.PrimaryKey("PK_Documents", x => new { x.Collection, x.Key }));

    protected override void Down(MigrationBuilder migrationBuilder) =>
        migrationBuilder.DropTable(name: "Documents");
}
