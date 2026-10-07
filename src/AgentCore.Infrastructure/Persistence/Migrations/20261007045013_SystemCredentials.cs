using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgentCore.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class SystemCredentials : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ApplicationConnections");

            migrationBuilder.CreateTable(
                name: "Credentials",
                columns: table => new
                {
                    CredentialId = table.Column<string>(type: "TEXT", nullable: false),
                    DisplayName = table.Column<string>(type: "TEXT", maxLength: 120, nullable: false),
                    Kind = table.Column<string>(type: "TEXT", nullable: false),
                    Status = table.Column<string>(type: "TEXT", nullable: false),
                    MetadataJson = table.Column<string>(type: "TEXT", maxLength: 16384, nullable: false),
                    AllowedOriginsJson = table.Column<string>(type: "TEXT", nullable: false),
                    ProtectedPayload = table.Column<string>(type: "TEXT", nullable: false),
                    ProtectionVersion = table.Column<int>(type: "INTEGER", nullable: false),
                    Revision = table.Column<long>(type: "INTEGER", nullable: false),
                    CreatedAtUtc = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedAtUtc = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Credentials", x => x.CredentialId);
                });

            migrationBuilder.CreateTable(
                name: "AgentCredentialBindings",
                columns: table => new
                {
                    BindingId = table.Column<string>(type: "TEXT", nullable: false),
                    AgentInstanceId = table.Column<string>(type: "TEXT", nullable: false),
                    CredentialId = table.Column<string>(type: "TEXT", nullable: false),
                    Reference = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Revision = table.Column<long>(type: "INTEGER", nullable: false),
                    CreatedAtUtc = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedAtUtc = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AgentCredentialBindings", x => x.BindingId);
                    table.ForeignKey(
                        name: "FK_AgentCredentialBindings_AgentInstances_AgentInstanceId",
                        column: x => x.AgentInstanceId,
                        principalTable: "AgentInstances",
                        principalColumn: "InstanceId",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_AgentCredentialBindings_Credentials_CredentialId",
                        column: x => x.CredentialId,
                        principalTable: "Credentials",
                        principalColumn: "CredentialId",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AgentCredentialBindings_AgentInstanceId_CredentialId",
                table: "AgentCredentialBindings",
                columns: new[] { "AgentInstanceId", "CredentialId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AgentCredentialBindings_AgentInstanceId_Reference",
                table: "AgentCredentialBindings",
                columns: new[] { "AgentInstanceId", "Reference" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AgentCredentialBindings_CredentialId",
                table: "AgentCredentialBindings",
                column: "CredentialId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AgentCredentialBindings");

            migrationBuilder.DropTable(
                name: "Credentials");

            migrationBuilder.CreateTable(
                name: "ApplicationConnections",
                columns: table => new
                {
                    ConnectionId = table.Column<string>(type: "TEXT", maxLength: 36, nullable: false),
                    AgentInstanceId = table.Column<string>(type: "TEXT", maxLength: 36, nullable: false),
                    BaseUrl = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    CreatedAtUtc = table.Column<long>(type: "INTEGER", nullable: false),
                    DisplayName = table.Column<string>(type: "TEXT", maxLength: 80, nullable: false),
                    Kind = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    ProfileKey = table.Column<string>(type: "TEXT", maxLength: 36, nullable: false),
                    Revision = table.Column<long>(type: "INTEGER", nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    StatusDetail = table.Column<string>(type: "TEXT", maxLength: 80, nullable: true),
                    TrustedOriginsJson = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    UpdatedAtUtc = table.Column<long>(type: "INTEGER", nullable: false)
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
    }
}
