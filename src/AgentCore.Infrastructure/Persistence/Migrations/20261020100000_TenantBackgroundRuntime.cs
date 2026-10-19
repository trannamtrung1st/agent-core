using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgentCore.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class TenantBackgroundRuntime : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(Rebuild(nullable: true));
            migrationBuilder.Sql("""
                CREATE TRIGGER TR_BackgroundReceipt_OwnerInsert BEFORE INSERT ON BackgroundCompletionReceipts
                WHEN NOT EXISTS(SELECT 1 FROM AgentRuns r WHERE r.AgentRunId=NEW.ChildAgentRunId AND r.AgentInstanceId=NEW.AgentInstanceId
                  AND ((r.TenantId IS NOT NULL AND NEW.ProfileId IS NULL) OR (r.TenantId IS NULL AND r.ProfileId=NEW.ProfileId)))
                  OR (NEW.ParentActivationId IS NOT NULL AND NOT EXISTS(SELECT 1 FROM Activations a JOIN AgentRuns r ON r.AgentRunId=NEW.ChildAgentRunId
                    WHERE a.ActivationId=NEW.ParentActivationId AND a.SessionId=NEW.ParentSessionId AND a.AgentInstanceId=r.AgentInstanceId
                    AND a.TenantId IS r.TenantId AND a.ExecutorPrincipalId IS r.ExecutorPrincipalId AND a.ProfileId IS r.ProfileId AND a.RootActivationId=r.RootActivationId))
                BEGIN SELECT RAISE(ABORT, 'Background receipt owner mismatch'); END;
                CREATE TRIGGER TR_BackgroundReceipt_OwnerUpdate BEFORE UPDATE ON BackgroundCompletionReceipts
                WHEN NEW.ChildAgentRunId<>OLD.ChildAgentRunId OR NEW.AgentInstanceId<>OLD.AgentInstanceId OR NEW.ProfileId IS NOT OLD.ProfileId
                  OR NEW.ParentSessionId<>OLD.ParentSessionId
                  OR (NEW.ParentActivationId IS NOT NULL AND NOT EXISTS(SELECT 1 FROM Activations a JOIN AgentRuns r ON r.AgentRunId=NEW.ChildAgentRunId
                    WHERE a.ActivationId=NEW.ParentActivationId AND a.SessionId=NEW.ParentSessionId AND a.AgentInstanceId=r.AgentInstanceId
                    AND a.TenantId IS r.TenantId AND a.ExecutorPrincipalId IS r.ExecutorPrincipalId AND a.ProfileId IS r.ProfileId AND a.RootActivationId=r.RootActivationId))
                BEGIN SELECT RAISE(ABORT, 'Background receipt owner mismatch'); END;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                CREATE TEMP TABLE TenantBackgroundDowngradeGuard (OwnedRows INTEGER CHECK(OwnedRows=0));
                INSERT INTO TenantBackgroundDowngradeGuard SELECT count(*) FROM BackgroundCompletionReceipts WHERE ProfileId IS NULL;
                DROP TABLE TenantBackgroundDowngradeGuard;
                DROP TRIGGER TR_BackgroundReceipt_OwnerInsert;
                DROP TRIGGER TR_BackgroundReceipt_OwnerUpdate;
                """);
            migrationBuilder.Sql(Rebuild(nullable: false));
        }
        // Explicit SQLite rebuild keeps owner triggers after the table replacement and
        // permits rollback past predecessor SQL-only migrations with no target model.
        private static string Rebuild(bool nullable) => """
            CREATE TABLE BackgroundCompletionReceipts_rebuild (
              ChildAgentRunId TEXT NOT NULL CONSTRAINT PK_BackgroundCompletionReceipts PRIMARY KEY,
              ParentSessionId TEXT NOT NULL, Status INTEGER NOT NULL, ClaimExpiresAtUtc INTEGER NULL,
              ClaimRunId TEXT NULL, InboxJson TEXT NULL, Revision INTEGER NOT NULL DEFAULT 1,
              AgentInstanceId TEXT NOT NULL, ProfileId TEXT __PROFILE_REQUIRED__, ParentActivationId TEXT NULL,
              SkipReason TEXT NULL, CreatedAtUtc INTEGER NOT NULL,
              CONSTRAINT FK_BackgroundCompletionReceipts_AgentRuns_ChildAgentRunId FOREIGN KEY (ChildAgentRunId) REFERENCES AgentRuns(AgentRunId) ON DELETE RESTRICT,
              CONSTRAINT FK_BackgroundCompletionReceipts_Activations_ParentActivationId FOREIGN KEY (ParentActivationId) REFERENCES Activations(ActivationId) ON DELETE RESTRICT);
            INSERT INTO BackgroundCompletionReceipts_rebuild
              (ChildAgentRunId,ParentSessionId,Status,ClaimExpiresAtUtc,ClaimRunId,InboxJson,Revision,AgentInstanceId,ProfileId,ParentActivationId,SkipReason,CreatedAtUtc)
              SELECT ChildAgentRunId,ParentSessionId,Status,ClaimExpiresAtUtc,ClaimRunId,InboxJson,Revision,AgentInstanceId,ProfileId,ParentActivationId,SkipReason,CreatedAtUtc FROM BackgroundCompletionReceipts;
            DROP TABLE BackgroundCompletionReceipts;
            ALTER TABLE BackgroundCompletionReceipts_rebuild RENAME TO BackgroundCompletionReceipts;
            CREATE INDEX IX_BackgroundCompletionReceipts_ClaimRunId ON BackgroundCompletionReceipts(ClaimRunId);
            CREATE INDEX IX_BackgroundCompletionReceipts_ParentActivationId ON BackgroundCompletionReceipts(ParentActivationId) WHERE ParentActivationId IS NOT NULL;
            CREATE INDEX IX_BackgroundCompletionReceipts_Status_CreatedAtUtc_ChildAgentRunId ON BackgroundCompletionReceipts(Status,CreatedAtUtc,ChildAgentRunId);
            CREATE INDEX IX_BackgroundCompletionReceipts_AgentInstanceId_ProfileId_ParentSessionId_CreatedAtUtc_ChildAgentRunId ON BackgroundCompletionReceipts(AgentInstanceId,ProfileId,ParentSessionId,CreatedAtUtc,ChildAgentRunId);
            """.Replace("__PROFILE_REQUIRED__", nullable ? "NULL" : "NOT NULL", StringComparison.Ordinal);

    }
}
