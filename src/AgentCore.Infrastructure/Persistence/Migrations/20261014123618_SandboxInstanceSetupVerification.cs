using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgentCore.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class SandboxInstanceSetupVerification : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "SandboxInstanceVerifications",
                columns: table => new
                {
                    InstanceId = table.Column<string>(type: "TEXT", nullable: false),
                    InstanceRevision = table.Column<long>(type: "INTEGER", nullable: false),
                    Json = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SandboxInstanceVerifications", x => new { x.InstanceId, x.InstanceRevision });
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SandboxInstanceVerifications");
        }
    }
}
