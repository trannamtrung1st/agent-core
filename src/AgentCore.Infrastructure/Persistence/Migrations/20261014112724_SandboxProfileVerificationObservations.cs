using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgentCore.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class SandboxProfileVerificationObservations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "SandboxProfileVerifications",
                columns: table => new
                {
                    ProfileId = table.Column<string>(type: "TEXT", nullable: false),
                    ProfileRevision = table.Column<long>(type: "INTEGER", nullable: false),
                    Json = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SandboxProfileVerifications", x => new { x.ProfileId, x.ProfileRevision });
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SandboxProfileVerifications");
        }
    }
}
