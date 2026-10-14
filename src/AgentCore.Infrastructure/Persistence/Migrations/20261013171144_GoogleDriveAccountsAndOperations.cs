using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgentCore.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class GoogleDriveAccountsAndOperations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "DriveAccountProfiles",
                columns: table => new
                {
                    CredentialId = table.Column<string>(type: "TEXT", nullable: false),
                    ProfileJson = table.Column<string>(type: "TEXT", nullable: false),
                    GoogleSubject = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DriveAccountProfiles", x => x.CredentialId);
                    table.ForeignKey(
                        name: "FK_DriveAccountProfiles_Credentials_CredentialId",
                        column: x => x.CredentialId,
                        principalTable: "Credentials",
                        principalColumn: "CredentialId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "DriveOperations",
                columns: table => new
                {
                    OperationId = table.Column<string>(type: "TEXT", nullable: false),
                    AgentInstanceId = table.Column<string>(type: "TEXT", nullable: false),
                    CredentialId = table.Column<string>(type: "TEXT", nullable: false),
                    EffectHash = table.Column<string>(type: "TEXT", nullable: false),
                    AuthEpoch = table.Column<long>(type: "INTEGER", nullable: false),
                    Tool = table.Column<string>(type: "TEXT", nullable: false),
                    FileId = table.Column<string>(type: "TEXT", nullable: false),
                    SnapshotJson = table.Column<string>(type: "TEXT", nullable: false),
                    State = table.Column<string>(type: "TEXT", nullable: false),
                    Revision = table.Column<long>(type: "INTEGER", nullable: false),
                    CreatedAtUtc = table.Column<long>(type: "INTEGER", nullable: false),
                    ResultJson = table.Column<string>(type: "TEXT", nullable: true),
                    ProtectedUploadSession = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DriveOperations", x => x.OperationId);
                    table.ForeignKey(
                        name: "FK_DriveOperations_AgentInstances_AgentInstanceId",
                        column: x => x.AgentInstanceId,
                        principalTable: "AgentInstances",
                        principalColumn: "InstanceId",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_DriveOperations_Credentials_CredentialId",
                        column: x => x.CredentialId,
                        principalTable: "Credentials",
                        principalColumn: "CredentialId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SyntheticDriveFiles",
                columns: table => new
                {
                    CredentialId = table.Column<string>(type: "TEXT", nullable: false),
                    FileId = table.Column<string>(type: "TEXT", nullable: false),
                    MetadataJson = table.Column<string>(type: "TEXT", nullable: false),
                    Bytes = table.Column<byte[]>(type: "BLOB", nullable: false),
                    Version = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SyntheticDriveFiles", x => new { x.CredentialId, x.FileId });
                    table.ForeignKey(
                        name: "FK_SyntheticDriveFiles_Credentials_CredentialId",
                        column: x => x.CredentialId,
                        principalTable: "Credentials",
                        principalColumn: "CredentialId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_DriveAccountProfiles_GoogleSubject",
                table: "DriveAccountProfiles",
                column: "GoogleSubject",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DriveOperations_AgentInstanceId_CredentialId_EffectHash",
                table: "DriveOperations",
                columns: new[] { "AgentInstanceId", "CredentialId", "EffectHash" },
                unique: true,
                filter: "State IN ('Planned','Dispatched','Indeterminate')");

            migrationBuilder.CreateIndex(
                name: "IX_DriveOperations_CredentialId",
                table: "DriveOperations",
                column: "CredentialId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DriveAccountProfiles");

            migrationBuilder.DropTable(
                name: "DriveOperations");

            migrationBuilder.DropTable(
                name: "SyntheticDriveFiles");
        }
    }
}
