using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgentCore.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class SpaceFileResources : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "SpaceFileResources",
                columns: table => new
                {
                    TenantId = table.Column<Guid>(type: "TEXT", nullable: false),
                    SpaceId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ResourceId = table.Column<Guid>(type: "TEXT", nullable: false),
                    LogicalPath = table.Column<string>(type: "TEXT", nullable: false),
                    Kind = table.Column<int>(type: "INTEGER", nullable: false),
                    MediaType = table.Column<string>(type: "TEXT", nullable: false),
                    ContentSha256 = table.Column<string>(type: "TEXT", nullable: false),
                    ByteLength = table.Column<long>(type: "INTEGER", nullable: false),
                    SourcePath = table.Column<string>(type: "TEXT", nullable: false),
                    Revision = table.Column<long>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    DeletedAt = table.Column<long>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SpaceFileResources", x => new { x.TenantId, x.SpaceId, x.ResourceId });
                    table.CheckConstraint("CK_SpaceFileResources_Values", "Revision >= 1 AND Kind BETWEEN 1 AND 4 AND ByteLength BETWEEN 1 AND 8388608 AND length(LogicalPath) BETWEEN 1 AND 240 AND substr(LogicalPath,1,1) <> '/' AND instr(LogicalPath,char(92))=0 AND instr(LogicalPath,':')=0 AND instr('/'||LogicalPath||'/','/../')=0 AND instr('/'||LogicalPath||'/','/./')=0 AND instr(LogicalPath,'//')=0 AND substr(LogicalPath,-1)<>'/' AND length(LogicalPath)=length(trim(LogicalPath)) AND length(MediaType) BETWEEN 1 AND 256 AND length(ContentSha256)=64 AND ContentSha256 NOT GLOB '*[^0-9a-f]*' AND substr(SourcePath,1,6)='/home/' AND length(SourcePath) BETWEEN 7 AND 4095 AND instr(SourcePath,char(92))=0 AND instr(SourcePath,'/../')=0 AND instr(SourcePath,'/./')=0 AND instr(SourcePath,'//')=0 AND substr(SourcePath,-1)<>'/' AND UpdatedAt >= CreatedAt AND (DeletedAt IS NULL OR DeletedAt >= CreatedAt)");
                    table.ForeignKey(
                        name: "FK_SpaceFileResources_Spaces_TenantId_SpaceId",
                        columns: x => new { x.TenantId, x.SpaceId },
                        principalTable: "Spaces",
                        principalColumns: new[] { "TenantId", "SpaceId" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SpaceFileResources_TenantId_SpaceId_LogicalPath",
                table: "SpaceFileResources",
                columns: new[] { "TenantId", "SpaceId", "LogicalPath" },
                unique: true,
                filter: "DeletedAt IS NULL");
            migrationBuilder.Sql("""
                CREATE TRIGGER TR_SpaceResources_Insert BEFORE INSERT ON SpaceFileResources BEGIN
                  SELECT RAISE(ABORT,'Space source requires an active owner and initial revision') WHERE NEW.Revision<>1 OR NEW.DeletedAt IS NOT NULL
                    OR NOT EXISTS(SELECT 1 FROM Spaces s WHERE s.TenantId=NEW.TenantId AND s.SpaceId=NEW.SpaceId AND s.DeletedAt IS NULL);
                  SELECT RAISE(ABORT,'Space source bounds exceeded') WHERE (SELECT count(*) FROM SpaceFileResources r WHERE r.TenantId=NEW.TenantId AND r.SpaceId=NEW.SpaceId AND r.DeletedAt IS NULL)>=64
                    OR NEW.ByteLength+COALESCE((SELECT sum(ByteLength) FROM SpaceFileResources r WHERE r.TenantId=NEW.TenantId AND r.SpaceId=NEW.SpaceId AND r.DeletedAt IS NULL),0)>67108864;
                END;
                CREATE TRIGGER TR_SpaceResources_Update BEFORE UPDATE ON SpaceFileResources BEGIN
                  SELECT RAISE(ABORT,'Space source identity and history are immutable') WHERE NEW.TenantId<>OLD.TenantId OR NEW.SpaceId<>OLD.SpaceId OR NEW.ResourceId<>OLD.ResourceId
                    OR NEW.CreatedAt<>OLD.CreatedAt OR OLD.DeletedAt IS NOT NULL OR NEW.Revision<>OLD.Revision+1 OR NEW.UpdatedAt<OLD.UpdatedAt
                    OR NOT EXISTS(SELECT 1 FROM Spaces s WHERE s.TenantId=NEW.TenantId AND s.SpaceId=NEW.SpaceId AND s.DeletedAt IS NULL);
                  SELECT RAISE(ABORT,'Space source is still bound') WHERE NEW.DeletedAt IS NOT NULL AND EXISTS(SELECT 1 FROM SpaceMembershipAddedBindings b
                    WHERE b.TenantId=OLD.TenantId AND b.SpaceId=OLD.SpaceId AND b.Kind=2 AND b.ResourceKey='space:'||lower(OLD.SpaceId)||':'||lower(OLD.ResourceId));
                  SELECT RAISE(ABORT,'Space source bounds exceeded') WHERE NEW.DeletedAt IS NULL AND NEW.ByteLength+COALESCE((SELECT sum(ByteLength) FROM SpaceFileResources r
                    WHERE r.TenantId=NEW.TenantId AND r.SpaceId=NEW.SpaceId AND r.ResourceId<>NEW.ResourceId AND r.DeletedAt IS NULL),0)>67108864;
                END;
                CREATE TRIGGER TR_SpaceResources_Delete BEFORE DELETE ON SpaceFileResources BEGIN
                  SELECT RAISE(ABORT,'Retain immutable Space source history');
                END;
                CREATE TRIGGER TR_SpaceBinding_Source_Insert BEFORE INSERT ON SpaceMembershipAddedBindings WHEN NEW.Kind=2 BEGIN
                  SELECT RAISE(ABORT,'Space binding requires its actual active source') WHERE NOT EXISTS(SELECT 1 FROM SpaceFileResources r
                    JOIN Spaces s ON s.TenantId=r.TenantId AND s.SpaceId=r.SpaceId
                    WHERE r.TenantId=NEW.TenantId AND r.SpaceId=NEW.SpaceId AND r.DeletedAt IS NULL AND s.DeletedAt IS NULL
                      AND NEW.ResourceKey='space:'||lower(r.SpaceId)||':'||lower(r.ResourceId));
                END;
                CREATE TRIGGER TR_SpaceBinding_Source_Update BEFORE UPDATE ON SpaceMembershipAddedBindings WHEN NEW.Kind=2 BEGIN
                  SELECT RAISE(ABORT,'Space binding requires its actual active source') WHERE NOT EXISTS(SELECT 1 FROM SpaceFileResources r
                    JOIN Spaces s ON s.TenantId=r.TenantId AND s.SpaceId=r.SpaceId
                    WHERE r.TenantId=NEW.TenantId AND r.SpaceId=NEW.SpaceId AND r.DeletedAt IS NULL AND s.DeletedAt IS NULL
                      AND NEW.ResourceKey='space:'||lower(r.SpaceId)||':'||lower(r.ResourceId));
                END;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                CREATE TEMP TABLE SpaceSourceDowngradeGuard(Rows INTEGER CHECK(Rows=0));
                INSERT INTO SpaceSourceDowngradeGuard SELECT count(*) FROM SpaceFileResources;
                DROP TABLE SpaceSourceDowngradeGuard;
                DROP TRIGGER TR_SpaceBinding_Source_Insert;
                DROP TRIGGER TR_SpaceBinding_Source_Update;
                DROP TRIGGER TR_SpaceResources_Insert;
                DROP TRIGGER TR_SpaceResources_Update;
                DROP TRIGGER TR_SpaceResources_Delete;
                """);
            migrationBuilder.DropTable(
                name: "SpaceFileResources");
        }
    }
}
