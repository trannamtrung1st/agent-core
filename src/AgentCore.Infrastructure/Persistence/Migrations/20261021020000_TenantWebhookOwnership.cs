using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable
namespace AgentCore.Infrastructure.Persistence.Migrations;

// Frozen rebuild: source/receipt guards are installed after both table renames.
public partial class TenantWebhookOwnership : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("PRAGMA foreign_keys = OFF;", suppressTransaction: true);
        migrationBuilder.Sql(UpgradeSql);
        migrationBuilder.Sql("PRAGMA foreign_keys = ON;", suppressTransaction: true);
    }
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("CREATE TEMP TABLE TenantWebhookDowngradeGuard (Rows INTEGER CHECK(Rows=0)); INSERT INTO TenantWebhookDowngradeGuard SELECT count(*) FROM WebhookEvents WHERE TenantId IS NOT NULL; INSERT INTO TenantWebhookDowngradeGuard SELECT count(*) FROM ExternalEvents WHERE TenantId IS NOT NULL; DROP TABLE TenantWebhookDowngradeGuard;");
        migrationBuilder.Sql("PRAGMA foreign_keys = OFF;", suppressTransaction: true);
        migrationBuilder.Sql(DowngradeSql);
        migrationBuilder.Sql("PRAGMA foreign_keys = ON;", suppressTransaction: true);
    }
    private const string UpgradeSql = """
        DROP INDEX "IX_WebhookEvents_EventKey";

        ALTER TABLE "WebhookEvents" ADD "CreatedByPrincipalId" TEXT NULL;

        ALTER TABLE "WebhookEvents" ADD "TenantId" TEXT NULL;

        ALTER TABLE "ExternalEvents" ADD "SourceRevision" INTEGER NULL;

        ALTER TABLE "ExternalEvents" ADD "TenantId" TEXT NULL;

        CREATE UNIQUE INDEX "IX_WebhookEvents_EventKey" ON "WebhookEvents" ("EventKey") WHERE TenantId IS NULL;

        CREATE INDEX "IX_WebhookEvents_TenantId_CreatedByPrincipalId" ON "WebhookEvents" ("TenantId", "CreatedByPrincipalId");

        CREATE UNIQUE INDEX "IX_WebhookEvents_TenantId_EventKey" ON "WebhookEvents" ("TenantId", "EventKey") WHERE TenantId IS NOT NULL;

        CREATE INDEX "IX_WebhookEvents_TenantId_ResourceId" ON "WebhookEvents" ("TenantId", "ResourceId");

        CREATE INDEX "IX_ExternalEvents_TenantId_ResourceId_AdmittedAtUtc_EventId" ON "ExternalEvents" ("TenantId", "ResourceId", "AdmittedAtUtc", "EventId");


        CREATE TABLE "ef_temp_WebhookEvents" (
            "ResourceId" TEXT NOT NULL CONSTRAINT "PK_WebhookEvents" PRIMARY KEY,
            "CreatedAtUtc" INTEGER NOT NULL,
            "CreatedByPrincipalId" TEXT NULL,
            "CredentialHash" TEXT NULL,
            "DisplayName" TEXT NOT NULL,
            "EventKey" TEXT NOT NULL,
            "Kind" INTEGER NOT NULL,
            "Revision" INTEGER NOT NULL,
            "Status" INTEGER NOT NULL,
            "TenantId" TEXT NULL,
            "UpdatedAtUtc" INTEGER NOT NULL,
            CONSTRAINT "CK_WebhookEvents_TenantOwner" CHECK ((TenantId IS NULL AND CreatedByPrincipalId IS NULL) OR COALESCE((TenantId IS NOT NULL AND CreatedByPrincipalId IS NOT NULL AND Kind = 0 AND Revision >= 1 AND UpdatedAtUtc >= CreatedAtUtc AND length(DisplayName) BETWEEN 1 AND 80 AND length(EventKey) BETWEEN 1 AND 64 AND substr(EventKey,1,1) GLOB '[a-z]' AND EventKey NOT GLOB '*[^a-z0-9._-]*' AND substr(EventKey,-1) GLOB '[a-z0-9]' AND DisplayName = trim(DisplayName) AND ((Status = 0 AND length(CredentialHash) = 64 AND CredentialHash NOT GLOB '*[^0-9a-f]*') OR (Status = 1 AND CredentialHash IS NULL))),0)),
            CONSTRAINT "FK_WebhookEvents_TenantUsers_TenantId_CreatedByPrincipalId" FOREIGN KEY ("TenantId", "CreatedByPrincipalId") REFERENCES "TenantUsers" ("TenantId", "PrincipalId") ON DELETE RESTRICT,
            CONSTRAINT "FK_WebhookEvents_Tenants_TenantId" FOREIGN KEY ("TenantId") REFERENCES "Tenants" ("TenantId") ON DELETE RESTRICT
        );

        INSERT INTO "ef_temp_WebhookEvents" ("ResourceId", "CreatedAtUtc", "CreatedByPrincipalId", "CredentialHash", "DisplayName", "EventKey", "Kind", "Revision", "Status", "TenantId", "UpdatedAtUtc")
        SELECT "ResourceId", "CreatedAtUtc", "CreatedByPrincipalId", "CredentialHash", "DisplayName", "EventKey", "Kind", "Revision", "Status", "TenantId", "UpdatedAtUtc"
        FROM "WebhookEvents";

        CREATE TABLE "ef_temp_ExternalEvents" (
            "EventId" TEXT NOT NULL CONSTRAINT "PK_ExternalEvents" PRIMARY KEY,
            "AdmittedAtUtc" INTEGER NOT NULL,
            "EvidenceJson" TEXT NOT NULL,
            "OccurredAtUtc" INTEGER NOT NULL,
            "ResourceId" TEXT NOT NULL,
            "SourceEventId" TEXT NOT NULL,
            "SourceRevision" INTEGER NULL,
            "TenantId" TEXT NULL,
            CONSTRAINT "CK_ExternalEvents_TenantReceipt" CHECK ((TenantId IS NULL AND SourceRevision IS NULL) OR COALESCE((TenantId IS NOT NULL AND SourceRevision >= 1 AND length(SourceEventId) BETWEEN 1 AND 64 AND length(CAST(EvidenceJson AS BLOB)) <= 4096 AND json_valid(EvidenceJson) AND json_type(EvidenceJson) IS 'object' AND json_extract(EvidenceJson, '$.sourceEventId') IS SourceEventId AND json_type(EvidenceJson, '$.data') IS 'object' AND json_extract(EvidenceJson, '$.rootAgentRunId') IS NULL AND json_extract(EvidenceJson, '$.triggerDepth') = 0),0)),
            CONSTRAINT "FK_ExternalEvents_Tenants_TenantId" FOREIGN KEY ("TenantId") REFERENCES "Tenants" ("TenantId") ON DELETE RESTRICT
        );

        INSERT INTO "ef_temp_ExternalEvents" ("EventId", "AdmittedAtUtc", "EvidenceJson", "OccurredAtUtc", "ResourceId", "SourceEventId", "SourceRevision", "TenantId")
        SELECT "EventId", "AdmittedAtUtc", "EvidenceJson", "OccurredAtUtc", "ResourceId", "SourceEventId", "SourceRevision", "TenantId"
        FROM "ExternalEvents";

        DROP TABLE "WebhookEvents";

        ALTER TABLE "ef_temp_WebhookEvents" RENAME TO "WebhookEvents";

        DROP TABLE "ExternalEvents";

        ALTER TABLE "ef_temp_ExternalEvents" RENAME TO "ExternalEvents";

        CREATE UNIQUE INDEX "IX_WebhookEvents_EventKey" ON "WebhookEvents" ("EventKey") WHERE TenantId IS NULL;

        CREATE INDEX "IX_WebhookEvents_TenantId_CreatedByPrincipalId" ON "WebhookEvents" ("TenantId", "CreatedByPrincipalId");

        CREATE UNIQUE INDEX "IX_WebhookEvents_TenantId_EventKey" ON "WebhookEvents" ("TenantId", "EventKey") WHERE TenantId IS NOT NULL;

        CREATE INDEX "IX_WebhookEvents_TenantId_ResourceId" ON "WebhookEvents" ("TenantId", "ResourceId");

        CREATE UNIQUE INDEX "IX_ExternalEvents_ResourceId_SourceEventId" ON "ExternalEvents" ("ResourceId", "SourceEventId");

        CREATE INDEX "IX_ExternalEvents_TenantId_ResourceId_AdmittedAtUtc_EventId" ON "ExternalEvents" ("TenantId", "ResourceId", "AdmittedAtUtc", "EventId");
        CREATE TRIGGER TR_TenantWebhook_Source_Insert BEFORE INSERT ON WebhookEvents
                WHEN NEW.TenantId IS NOT NULL
                BEGIN
                    SELECT RAISE(ABORT, 'tenant webhook requires its actual human creator') WHERE NOT (
                        NEW.Revision = 1 AND NEW.CreatedAtUtc = NEW.UpdatedAtUtc AND EXISTS (
                            SELECT 1 FROM TenantUsers u JOIN Principals p ON p.PrincipalId = u.PrincipalId AND p.TenantId = u.TenantId
                            JOIN Tenants t ON t.TenantId = u.TenantId
                            WHERE u.TenantId = NEW.TenantId AND u.PrincipalId = NEW.CreatedByPrincipalId AND u.Status = 0 AND p.Status = 0 AND p.Kind = 0 AND t.Status = 0));
                END;
                CREATE TRIGGER TR_TenantWebhook_Source_Update BEFORE UPDATE ON WebhookEvents
                WHEN OLD.TenantId IS NOT NULL OR NEW.TenantId IS NOT NULL
                BEGIN SELECT RAISE(ABORT, 'tenant webhook identity is immutable and requires exact revision') WHERE
                    OLD.TenantId IS NOT NEW.TenantId OR OLD.CreatedByPrincipalId IS NOT NEW.CreatedByPrincipalId OR OLD.ResourceId IS NOT NEW.ResourceId
                    OR OLD.EventKey IS NOT NEW.EventKey OR OLD.Kind IS NOT NEW.Kind OR OLD.CreatedAtUtc IS NOT NEW.CreatedAtUtc
                    OR NEW.Revision <> OLD.Revision + 1 OR NEW.UpdatedAtUtc < OLD.UpdatedAtUtc;
                END;
                CREATE TRIGGER TR_TenantWebhook_Source_Delete BEFORE DELETE ON WebhookEvents
                WHEN OLD.TenantId IS NOT NULL
                BEGIN SELECT RAISE(ABORT, 'tenant webhook sources are retained; revoke instead'); END;
                CREATE TRIGGER TR_TenantWebhook_Receipt_Insert BEFORE INSERT ON ExternalEvents
                WHEN NEW.TenantId IS NOT NULL OR EXISTS (SELECT 1 FROM WebhookEvents s WHERE s.ResourceId = NEW.ResourceId AND s.TenantId IS NOT NULL)
                BEGIN
                    SELECT RAISE(ABORT, 'tenant receipt requires its exact active source and bounded evidence') WHERE NOT COALESCE((
                        EXISTS (SELECT 1 FROM WebhookEvents s JOIN Tenants t ON t.TenantId = s.TenantId
                            WHERE s.ResourceId = NEW.ResourceId AND s.TenantId = NEW.TenantId AND s.Revision = NEW.SourceRevision AND s.Status = 0 AND t.Status = 0)
                        AND NEW.SourceEventId NOT GLOB '*[^!-~]*'
                        AND (SELECT count(*) FROM json_each(NEW.EvidenceJson)) = 5
                        AND (SELECT count(DISTINCT key) FROM json_each(NEW.EvidenceJson) WHERE key IN ('sourceEventId','data','occurredAtUtc','rootAgentRunId','triggerDepth')) = 5
                        AND json_type(NEW.EvidenceJson, '$.rootAgentRunId') IS 'null'
                        AND json_type(NEW.EvidenceJson, '$.triggerDepth') IS 'integer'
                        AND (json_type(NEW.EvidenceJson, '$.occurredAtUtc') IS 'null' OR json_type(NEW.EvidenceJson, '$.occurredAtUtc') IS 'text')),0);
                END;
                CREATE TRIGGER TR_TenantWebhook_Receipt_Update BEFORE UPDATE ON ExternalEvents
                WHEN OLD.TenantId IS NOT NULL OR NEW.TenantId IS NOT NULL
                BEGIN SELECT RAISE(ABORT, 'tenant webhook receipts are immutable'); END;
                CREATE TRIGGER TR_TenantWebhook_Receipt_Delete BEFORE DELETE ON ExternalEvents
                WHEN OLD.TenantId IS NOT NULL
                BEGIN SELECT RAISE(ABORT, 'tenant webhook receipts are retained'); END;
                CREATE TRIGGER TR_TenantWebhook_Delivery_Insert BEFORE INSERT ON ExternalEventDeliveries
                WHEN EXISTS (SELECT 1 FROM ExternalEvents e WHERE e.EventId = NEW.EventId AND e.TenantId IS NOT NULL)
                BEGIN SELECT RAISE(ABORT, 'tenant event subscriptions require their canonical owner'); END;
                CREATE TRIGGER TR_TenantWebhook_Delivery_Update BEFORE UPDATE ON ExternalEventDeliveries
                WHEN EXISTS (SELECT 1 FROM ExternalEvents e WHERE e.EventId IN (NEW.EventId,OLD.EventId) AND e.TenantId IS NOT NULL)
                BEGIN SELECT RAISE(ABORT, 'tenant event subscriptions require their canonical owner'); END;
        """;
    private const string DowngradeSql = """
        DROP TRIGGER TR_TenantWebhook_Source_Insert;
        DROP TRIGGER TR_TenantWebhook_Source_Update;
        DROP TRIGGER TR_TenantWebhook_Source_Delete;
        DROP TRIGGER TR_TenantWebhook_Receipt_Insert;
        DROP TRIGGER TR_TenantWebhook_Receipt_Update;
        DROP TRIGGER TR_TenantWebhook_Receipt_Delete;
        DROP TRIGGER TR_TenantWebhook_Delivery_Insert;
        DROP TRIGGER TR_TenantWebhook_Delivery_Update;

        DROP INDEX "IX_WebhookEvents_EventKey";

        DROP INDEX "IX_WebhookEvents_TenantId_CreatedByPrincipalId";

        DROP INDEX "IX_WebhookEvents_TenantId_EventKey";

        DROP INDEX "IX_WebhookEvents_TenantId_ResourceId";

        DROP INDEX "IX_ExternalEvents_TenantId_ResourceId_AdmittedAtUtc_EventId";

        CREATE UNIQUE INDEX "IX_WebhookEvents_EventKey" ON "WebhookEvents" ("EventKey");

        CREATE TABLE "ef_temp_ExternalEvents" (
            "EventId" TEXT NOT NULL CONSTRAINT "PK_ExternalEvents" PRIMARY KEY,
            "AdmittedAtUtc" INTEGER NOT NULL,
            "EvidenceJson" TEXT NOT NULL,
            "OccurredAtUtc" INTEGER NOT NULL,
            "ResourceId" TEXT NOT NULL,
            "SourceEventId" TEXT NOT NULL
        );

        INSERT INTO "ef_temp_ExternalEvents" ("EventId", "AdmittedAtUtc", "EvidenceJson", "OccurredAtUtc", "ResourceId", "SourceEventId")
        SELECT "EventId", "AdmittedAtUtc", "EvidenceJson", "OccurredAtUtc", "ResourceId", "SourceEventId"
        FROM "ExternalEvents";

        CREATE TABLE "ef_temp_WebhookEvents" (
            "ResourceId" TEXT NOT NULL CONSTRAINT "PK_WebhookEvents" PRIMARY KEY,
            "CreatedAtUtc" INTEGER NOT NULL,
            "CredentialHash" TEXT NULL,
            "DisplayName" TEXT NOT NULL,
            "EventKey" TEXT NOT NULL,
            "Kind" INTEGER NOT NULL,
            "Revision" INTEGER NOT NULL,
            "Status" INTEGER NOT NULL,
            "UpdatedAtUtc" INTEGER NOT NULL
        );

        INSERT INTO "ef_temp_WebhookEvents" ("ResourceId", "CreatedAtUtc", "CredentialHash", "DisplayName", "EventKey", "Kind", "Revision", "Status", "UpdatedAtUtc")
        SELECT "ResourceId", "CreatedAtUtc", "CredentialHash", "DisplayName", "EventKey", "Kind", "Revision", "Status", "UpdatedAtUtc"
        FROM "WebhookEvents";

        DROP TABLE "ExternalEvents";

        ALTER TABLE "ef_temp_ExternalEvents" RENAME TO "ExternalEvents";

        DROP TABLE "WebhookEvents";

        ALTER TABLE "ef_temp_WebhookEvents" RENAME TO "WebhookEvents";

        CREATE UNIQUE INDEX "IX_ExternalEvents_ResourceId_SourceEventId" ON "ExternalEvents" ("ResourceId", "SourceEventId");

        CREATE UNIQUE INDEX "IX_WebhookEvents_EventKey" ON "WebhookEvents" ("EventKey");
        """;
}
