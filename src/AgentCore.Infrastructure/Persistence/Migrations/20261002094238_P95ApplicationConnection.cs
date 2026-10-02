using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgentCore.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class P95ApplicationConnection : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ApplicationConnections",
                columns: table => new
                {
                    ConnectionId = table.Column<string>(type: "TEXT", maxLength: 36, nullable: false),
                    AgentInstanceId = table.Column<string>(type: "TEXT", maxLength: 36, nullable: false),
                    Kind = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    DisplayName = table.Column<string>(type: "TEXT", maxLength: 80, nullable: false),
                    BaseUrl = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    TrustedOriginsJson = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    ProfileKey = table.Column<string>(type: "TEXT", maxLength: 36, nullable: false),
                    Revision = table.Column<long>(type: "INTEGER", nullable: false),
                    CreatedAtUtc = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedAtUtc = table.Column<long>(type: "INTEGER", nullable: false),
                    StatusDetail = table.Column<string>(type: "TEXT", maxLength: 80, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ApplicationConnections", x => x.ConnectionId);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ApplicationConnections_AgentInstanceId",
                table: "ApplicationConnections",
                column: "AgentInstanceId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ApplicationConnections");
        }
    }
}
