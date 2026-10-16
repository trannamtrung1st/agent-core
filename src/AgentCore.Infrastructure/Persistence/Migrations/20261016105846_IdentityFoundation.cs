using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgentCore.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class IdentityFoundation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Accounts",
                columns: table => new
                {
                    AccountId = table.Column<Guid>(type: "TEXT", nullable: false),
                    LoginKey = table.Column<string>(type: "TEXT", maxLength: 254, nullable: false),
                    DisplayName = table.Column<string>(type: "TEXT", nullable: false),
                    PasswordHash = table.Column<string>(type: "TEXT", maxLength: 2048, nullable: false),
                    SecurityStamp = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Accounts", x => x.AccountId);
                });

            migrationBuilder.CreateTable(
                name: "IdentityBootstrap",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    CompletedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IdentityBootstrap", x => x.Id);
                    table.CheckConstraint("CK_IdentityBootstrap_One", "Id = 1");
                });

            migrationBuilder.CreateTable(
                name: "Tenants",
                columns: table => new
                {
                    TenantId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    Slug = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    Revision = table.Column<long>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Tenants", x => x.TenantId);
                });

            migrationBuilder.CreateTable(
                name: "AuthenticationSessions",
                columns: table => new
                {
                    SessionId = table.Column<Guid>(type: "TEXT", nullable: false),
                    AccountId = table.Column<Guid>(type: "TEXT", nullable: false),
                    SecurityStamp = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    ExpiresAt = table.Column<long>(type: "INTEGER", nullable: false),
                    RevokedAt = table.Column<long>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AuthenticationSessions", x => x.SessionId);
                    table.ForeignKey(
                        name: "FK_AuthenticationSessions_Accounts_AccountId",
                        column: x => x.AccountId,
                        principalTable: "Accounts",
                        principalColumn: "AccountId",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "AccessRoles",
                columns: table => new
                {
                    RoleId = table.Column<Guid>(type: "TEXT", nullable: false),
                    TenantId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Key = table.Column<string>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    IsBuiltin = table.Column<bool>(type: "INTEGER", nullable: false),
                    HumanOnly = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AccessRoles", x => x.RoleId);
                    table.ForeignKey(
                        name: "FK_AccessRoles_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "TenantId",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "AuthorizationAudit",
                columns: table => new
                {
                    AuditId = table.Column<Guid>(type: "TEXT", nullable: false),
                    TenantId = table.Column<Guid>(type: "TEXT", nullable: true),
                    ActorPrincipalId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Action = table.Column<string>(type: "TEXT", nullable: false),
                    Target = table.Column<string>(type: "TEXT", nullable: false),
                    Decision = table.Column<string>(type: "TEXT", nullable: false),
                    ReasonCode = table.Column<string>(type: "TEXT", nullable: false),
                    At = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AuthorizationAudit", x => x.AuditId);
                    table.ForeignKey(
                        name: "FK_AuthorizationAudit_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "TenantId",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Principals",
                columns: table => new
                {
                    PrincipalId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Kind = table.Column<int>(type: "INTEGER", nullable: false),
                    TenantId = table.Column<Guid>(type: "TEXT", nullable: true),
                    AccountId = table.Column<Guid>(type: "TEXT", nullable: true),
                    UserId = table.Column<Guid>(type: "TEXT", nullable: true),
                    SystemIdentityId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Status = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Principals", x => x.PrincipalId);
                    table.CheckConstraint("CK_Principal_Kind", "(Kind = 0 AND AccountId IS NOT NULL AND SystemIdentityId IS NULL) OR (Kind = 1 AND AccountId IS NULL AND UserId IS NULL AND SystemIdentityId IS NOT NULL)");
                    table.ForeignKey(
                        name: "FK_Principals_Accounts_AccountId",
                        column: x => x.AccountId,
                        principalTable: "Accounts",
                        principalColumn: "AccountId",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Principals_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "TenantId",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "AccessRolePermissions",
                columns: table => new
                {
                    RoleId = table.Column<Guid>(type: "TEXT", nullable: false),
                    PermissionKey = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AccessRolePermissions", x => new { x.RoleId, x.PermissionKey });
                    table.ForeignKey(
                        name: "FK_AccessRolePermissions_AccessRoles_RoleId",
                        column: x => x.RoleId,
                        principalTable: "AccessRoles",
                        principalColumn: "RoleId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AccessRoleAssignments",
                columns: table => new
                {
                    AssignmentId = table.Column<Guid>(type: "TEXT", nullable: false),
                    TenantId = table.Column<Guid>(type: "TEXT", nullable: true),
                    PrincipalId = table.Column<Guid>(type: "TEXT", nullable: false),
                    RoleId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ScopeKind = table.Column<int>(type: "INTEGER", nullable: false),
                    ResourceType = table.Column<string>(type: "TEXT", nullable: false),
                    ResourceId = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedByPrincipalId = table.Column<Guid>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AccessRoleAssignments", x => x.AssignmentId);
                    table.CheckConstraint("CK_Assignment_Scope", "(ScopeKind = 0 AND TenantId IS NULL AND ResourceType = '' AND ResourceId = '') OR (ScopeKind = 1 AND TenantId IS NOT NULL AND ResourceType = '' AND ResourceId = '') OR (ScopeKind = 2 AND TenantId IS NOT NULL AND ResourceType <> '' AND ResourceId <> '')");
                    table.ForeignKey(
                        name: "FK_AccessRoleAssignments_AccessRoles_RoleId",
                        column: x => x.RoleId,
                        principalTable: "AccessRoles",
                        principalColumn: "RoleId",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_AccessRoleAssignments_Principals_PrincipalId",
                        column: x => x.PrincipalId,
                        principalTable: "Principals",
                        principalColumn: "PrincipalId",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_AccessRoleAssignments_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "TenantId",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "SystemIdentities",
                columns: table => new
                {
                    SystemIdentityId = table.Column<Guid>(type: "TEXT", nullable: false),
                    TenantId = table.Column<Guid>(type: "TEXT", nullable: true),
                    PrincipalId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Kind = table.Column<int>(type: "INTEGER", nullable: false),
                    AgentInstanceId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Status = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SystemIdentities", x => x.SystemIdentityId);
                    table.ForeignKey(
                        name: "FK_SystemIdentities_Principals_PrincipalId",
                        column: x => x.PrincipalId,
                        principalTable: "Principals",
                        principalColumn: "PrincipalId",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SystemIdentities_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "TenantId",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "TenantInvitations",
                columns: table => new
                {
                    InvitationId = table.Column<Guid>(type: "TEXT", nullable: false),
                    TenantId = table.Column<Guid>(type: "TEXT", nullable: false),
                    LoginKey = table.Column<string>(type: "TEXT", nullable: false),
                    TokenHash = table.Column<string>(type: "TEXT", nullable: false),
                    RoleId = table.Column<Guid>(type: "TEXT", nullable: false),
                    InvitedByPrincipalId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ExpiresAt = table.Column<long>(type: "INTEGER", nullable: false),
                    AcceptedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    Revoked = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TenantInvitations", x => x.InvitationId);
                    table.ForeignKey(
                        name: "FK_TenantInvitations_AccessRoles_RoleId",
                        column: x => x.RoleId,
                        principalTable: "AccessRoles",
                        principalColumn: "RoleId",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_TenantInvitations_Principals_InvitedByPrincipalId",
                        column: x => x.InvitedByPrincipalId,
                        principalTable: "Principals",
                        principalColumn: "PrincipalId",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_TenantInvitations_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "TenantId",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "TenantUsers",
                columns: table => new
                {
                    UserId = table.Column<Guid>(type: "TEXT", nullable: false),
                    TenantId = table.Column<Guid>(type: "TEXT", nullable: false),
                    AccountId = table.Column<Guid>(type: "TEXT", nullable: false),
                    PrincipalId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TenantUsers", x => x.UserId);
                    table.ForeignKey(
                        name: "FK_TenantUsers_Accounts_AccountId",
                        column: x => x.AccountId,
                        principalTable: "Accounts",
                        principalColumn: "AccountId",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_TenantUsers_Principals_PrincipalId",
                        column: x => x.PrincipalId,
                        principalTable: "Principals",
                        principalColumn: "PrincipalId",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_TenantUsers_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "TenantId",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AccessRoleAssignments_PrincipalId",
                table: "AccessRoleAssignments",
                column: "PrincipalId");

            migrationBuilder.CreateIndex(
                name: "IX_AccessRoleAssignments_RoleId",
                table: "AccessRoleAssignments",
                column: "RoleId");

            migrationBuilder.CreateIndex(
                name: "IX_AccessRoleAssignments_TenantId_PrincipalId_RoleId_ScopeKind_ResourceType_ResourceId",
                table: "AccessRoleAssignments",
                columns: new[] { "TenantId", "PrincipalId", "RoleId", "ScopeKind", "ResourceType", "ResourceId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AccessRoles_TenantId_Key",
                table: "AccessRoles",
                columns: new[] { "TenantId", "Key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Accounts_LoginKey",
                table: "Accounts",
                column: "LoginKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AuthenticationSessions_AccountId_ExpiresAt",
                table: "AuthenticationSessions",
                columns: new[] { "AccountId", "ExpiresAt" });

            migrationBuilder.CreateIndex(
                name: "IX_AuthorizationAudit_TenantId_At_AuditId",
                table: "AuthorizationAudit",
                columns: new[] { "TenantId", "At", "AuditId" });

            migrationBuilder.CreateIndex(
                name: "IX_Principals_AccountId",
                table: "Principals",
                column: "AccountId");

            migrationBuilder.CreateIndex(
                name: "IX_Principals_TenantId_PrincipalId",
                table: "Principals",
                columns: new[] { "TenantId", "PrincipalId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SystemIdentities_PrincipalId",
                table: "SystemIdentities",
                column: "PrincipalId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SystemIdentities_TenantId_AgentInstanceId",
                table: "SystemIdentities",
                columns: new[] { "TenantId", "AgentInstanceId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TenantInvitations_InvitedByPrincipalId",
                table: "TenantInvitations",
                column: "InvitedByPrincipalId");

            migrationBuilder.CreateIndex(
                name: "IX_TenantInvitations_RoleId",
                table: "TenantInvitations",
                column: "RoleId");

            migrationBuilder.CreateIndex(
                name: "IX_TenantInvitations_TenantId_LoginKey_ExpiresAt",
                table: "TenantInvitations",
                columns: new[] { "TenantId", "LoginKey", "ExpiresAt" });

            migrationBuilder.CreateIndex(
                name: "IX_TenantInvitations_TokenHash",
                table: "TenantInvitations",
                column: "TokenHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TenantUsers_AccountId",
                table: "TenantUsers",
                column: "AccountId");

            migrationBuilder.CreateIndex(
                name: "IX_TenantUsers_PrincipalId",
                table: "TenantUsers",
                column: "PrincipalId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TenantUsers_TenantId_AccountId",
                table: "TenantUsers",
                columns: new[] { "TenantId", "AccountId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Tenants_Slug",
                table: "Tenants",
                column: "Slug",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AccessRoleAssignments");

            migrationBuilder.DropTable(
                name: "AccessRolePermissions");

            migrationBuilder.DropTable(
                name: "AuthenticationSessions");

            migrationBuilder.DropTable(
                name: "AuthorizationAudit");

            migrationBuilder.DropTable(
                name: "IdentityBootstrap");

            migrationBuilder.DropTable(
                name: "SystemIdentities");

            migrationBuilder.DropTable(
                name: "TenantInvitations");

            migrationBuilder.DropTable(
                name: "TenantUsers");

            migrationBuilder.DropTable(
                name: "AccessRoles");

            migrationBuilder.DropTable(
                name: "Principals");

            migrationBuilder.DropTable(
                name: "Accounts");

            migrationBuilder.DropTable(
                name: "Tenants");
        }
    }
}
