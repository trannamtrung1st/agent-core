using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable
namespace AgentCore.Infrastructure.Persistence.Migrations;

[DbContext(typeof(AgentCoreDbContext))]
[Migration("20261025060000_ContextSuccessor")]
public sealed class ContextSuccessor : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        var predecessor = Previous();
        const string old = "WHEN 9 THEN 'ApprovalContinuation' END";
        if (!predecessor.Contains(old, StringComparison.Ordinal)) throw new InvalidOperationException("Frozen Core event origin mapping is missing.");
        migrationBuilder.Sql("DROP TRIGGER TR_TenantCore_Fact_Insert;");
        migrationBuilder.Sql(predecessor.Replace(old, "WHEN 9 THEN 'ApprovalContinuation' WHEN 10 THEN 'ContextContinuation' END", StringComparison.Ordinal));
        migrationBuilder.Sql(Guards);
    }
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            CREATE TEMP TABLE ContextSuccessorDowngradeGuard(Rows INTEGER CHECK(Rows=0));
            INSERT INTO ContextSuccessorDowngradeGuard SELECT count(*) FROM AgentRuns WHERE json_extract(PayloadJson,'$.admission.activation.kind')=10;
            DROP TABLE ContextSuccessorDowngradeGuard;
            DROP TRIGGER TR_TenantRuns_Context_INSERT;
            DROP TRIGGER TR_TenantRuns_Context_UPDATE;
            DROP INDEX IX_AgentRuns_ContextSuccessor;
            DROP TRIGGER TR_TenantCore_Fact_Insert;
            """);
        migrationBuilder.Sql(Previous());
    }
    private static string Previous()
    {
        using var stream = typeof(ContextSuccessor).Assembly.GetManifestResourceStream("AgentCore.Infrastructure.Persistence.Migrations.Sql.20261025060000_ContextSuccessor.Previous.sql")
            ?? throw new InvalidOperationException("Frozen context successor predecessor is missing.");
        using var memory = new MemoryStream(); stream.CopyTo(memory); var bytes = memory.ToArray();
        if (Convert.ToHexStringLower(SHA256.HashData(bytes)) != "71de1999d9653efba4e075af65c5ebd94cc9890852e1a5fa1ceebe24195d339c") throw new InvalidOperationException("Frozen context successor predecessor changed.");
        return Encoding.UTF8.GetString(bytes);
    }
    private const string Guards = """
        CREATE UNIQUE INDEX IX_AgentRuns_ContextSuccessor ON AgentRuns(RootActivationId) WHERE json_extract(PayloadJson,'$.admission.activation.kind')=10;
        CREATE TRIGGER TR_TenantRuns_Context_INSERT BEFORE INSERT ON AgentRuns
        WHEN json_extract(NEW.PayloadJson,'$.admission.activation.kind')=10
            OR json_extract(NEW.PayloadJson,'$.admission.executionContext.origin')=10
        BEGIN
          SELECT RAISE(ABORT,'Context successor requires its exact completed turn and saved selection') WHERE NOT COALESCE(EXISTS(
            SELECT 1 FROM AgentRuns source JOIN AgentRuns root ON root.ActivationId=NEW.RootActivationId
            JOIN Sessions session ON session.SessionId=NEW.SessionId JOIN SessionSnapshots snapshot ON snapshot.SessionId=session.SessionId
            JOIN AgentInstances owner ON owner.InstanceId=NEW.AgentInstanceId
            WHERE NEW.TenantId IS NOT NULL AND NEW.OriginalInitiatorPrincipalId IS NOT NULL AND NEW.Status=0
              AND source.AgentRunId=json_extract(NEW.PayloadJson,'$.admission.activation.sourceAgentRunId')
              AND source.TenantId=NEW.TenantId AND source.AgentInstanceId=NEW.AgentInstanceId AND source.ExecutorPrincipalId=NEW.ExecutorPrincipalId
              AND source.SessionId=NEW.SessionId AND source.RootActivationId=NEW.RootActivationId AND source.OriginalInitiatorPrincipalId=NEW.OriginalInitiatorPrincipalId
              AND source.Status=4 AND json_extract(source.PayloadJson,'$.cancellationRequested')=0
              AND json_extract(source.PayloadJson,'$.admission.outputContract')=0 AND json_extract(source.PayloadJson,'$.admission.executionContext.origin') IN (0,9)
              AND json_extract(source.PayloadJson,'$.sideEffect.disposition') NOT IN (2,5)
              AND json_extract(NEW.PayloadJson,'$.admission.activation.kind')=10 AND json_extract(NEW.PayloadJson,'$.admission.executionContext.origin')=10
              AND json_extract(NEW.PayloadJson,'$.admission.outputContract')=0 AND json_extract(NEW.PayloadJson,'$.maxAttempts')=1
              AND json_extract(NEW.PayloadJson,'$.admission.executionContext.parentRunId')=source.AgentRunId
              AND json_extract(NEW.PayloadJson,'$.admission.executionContext.parentActivationId')=source.ActivationId
              AND json_extract(NEW.PayloadJson,'$.admission.activation.parentActivationId')=source.ActivationId
              AND json_extract(NEW.PayloadJson,'$.admission.activation.sourceSessionId')=source.SessionId
              AND json_extract(NEW.PayloadJson,'$.admission.activation.sourceEventId')=source.ActivationId
              AND json_extract(NEW.PayloadJson,'$.admission.activation.dedupeKey')='context-continuation:'||NEW.RootActivationId
              AND json_array_length(NEW.PayloadJson,'$.admission.activation.sourceEntryIds')=0
              AND json_extract(json_extract(NEW.PayloadJson,'$.admission.activation.evidenceJson'),'$.EnvironmentKind')='context-transition'
              AND json_extract(json_extract(NEW.PayloadJson,'$.admission.activation.evidenceJson'),'$.InputBoundarySequence')=snapshot.LastEntrySequence
              AND json_extract(json_extract(NEW.PayloadJson,'$.admission.activation.evidenceJson'),'$.ContextTransition.SourceRunId')=source.AgentRunId
              AND json_extract(json_extract(NEW.PayloadJson,'$.admission.activation.evidenceJson'),'$.ContextTransition.TransitionId')=json_extract(session.ContextSelectionJson,'$.transitionId')
              AND json_extract(json_extract(NEW.PayloadJson,'$.admission.activation.evidenceJson'),'$.ContextTransition.SelectionRevision')=json_extract(session.ContextSelectionJson,'$.revision')
              AND json_extract(json_extract(NEW.PayloadJson,'$.admission.activation.evidenceJson'),'$.ContextTransition.SpaceId')=json_extract(session.ContextSelectionJson,'$.spaceId')
              AND json_extract(json_extract(NEW.PayloadJson,'$.admission.activation.evidenceJson'),'$.ContextTransition.EnvironmentId') IS json_extract(session.ContextSelectionJson,'$.environmentId')
              AND json_extract(session.ContextSelectionJson,'$.effectiveAfterRunId')=source.AgentRunId
              AND json_extract(session.ContextSelectionJson,'$.revision')=json_extract(source.PayloadJson,'$.admission.configuration.spaceContext.selectionRevision')+1
              AND json_extract(NEW.PayloadJson,'$.admission.configuration.spaceContext.selectionRevision')=json_extract(session.ContextSelectionJson,'$.revision')
              AND json_extract(NEW.PayloadJson,'$.admission.configuration.spaceContext.spaceId')=json_extract(session.ContextSelectionJson,'$.spaceId')
              AND json_extract(NEW.PayloadJson,'$.admission.configuration.spaceContext.environmentId') IS json_extract(session.ContextSelectionJson,'$.environmentId')
              AND json_extract(NEW.PayloadJson,'$.admission.configuration.spaceContext.environmentSelectionEpoch')=json_extract(session.ContextSelectionJson,'$.environmentSelectionEpoch')
              AND owner.Revision=json_extract(NEW.PayloadJson,'$.admission.configuration.instanceRevision')
              AND session.ArchivedAtUtc IS NULL AND session.DurablyDeletedAtUtc IS NULL AND session.Status IN ('Created','Attached') AND (session.Surfaces & 1)=1 AND snapshot.LifecycleStatus='Active'
              AND root.TenantId=NEW.TenantId AND root.SessionId=NEW.SessionId AND json_extract(root.PayloadJson,'$.admission.executionContext.origin')=0
              AND EXISTS(SELECT 1 FROM ActivationSourceEntries input JOIN ConversationEntries entry ON entry.EntryId=input.EntryId AND entry.SessionId=input.SessionId WHERE input.ActivationId=root.ActivationId AND entry.Role='User')
              AND NOT EXISTS(SELECT 1 FROM ConversationEntries later WHERE later.SessionId=NEW.SessionId AND later.Role='User'
                AND later.EntrySequence>(SELECT max(entry.EntrySequence) FROM ActivationSourceEntries input JOIN ConversationEntries entry ON entry.EntryId=input.EntryId AND entry.SessionId=input.SessionId WHERE input.ActivationId=root.ActivationId AND entry.Role='User'))
              AND NOT EXISTS(SELECT 1 FROM AgentRuns other WHERE other.SessionId=NEW.SessionId AND other.AgentRunId<>source.AgentRunId
                AND (other.Status NOT IN (4,5,6) OR other.CreatedAtUtc>source.CreatedAtUtc OR (other.RootActivationId=NEW.RootActivationId AND json_extract(other.PayloadJson,'$.admission.activation.kind')=10)))
              AND EXISTS(SELECT 1 FROM json_each(json_extract(source.PayloadJson,'$.checkpoint.payloadJson'),'$.Messages') message
                WHERE json_extract(message.value,'$.Role')='Tool' AND json_extract(message.value,'$.Name') IN ('spaces.activate','environments.activate','environments.deactivate')
                  AND json_extract(json_extract(message.value,'$.Text'),'$.contextTransition.continueTask')=1
                  AND json_extract(json_extract(message.value,'$.Text'),'$.contextTransition.sourceRunId')=source.AgentRunId
                  AND json_extract(json_extract(message.value,'$.Text'),'$.contextTransition.transitionId')=json_extract(session.ContextSelectionJson,'$.transitionId')
                  AND json_extract(json_extract(message.value,'$.Text'),'$.contextTransition.selectionRevision')=json_extract(session.ContextSelectionJson,'$.revision')
                  AND json_extract(json_extract(message.value,'$.Text'),'$.contextTransition.spaceId')=json_extract(session.ContextSelectionJson,'$.spaceId')
                  AND json_extract(json_extract(message.value,'$.Text'),'$.contextTransition.environmentId') IS json_extract(session.ContextSelectionJson,'$.environmentId'))
          ),0);
        END;
        CREATE TRIGGER TR_TenantRuns_Context_UPDATE BEFORE UPDATE ON AgentRuns
        WHEN (json_extract(OLD.PayloadJson,'$.admission.activation.kind')=10 OR json_extract(NEW.PayloadJson,'$.admission.activation.kind')=10)
          AND (NEW.TenantId IS NOT OLD.TenantId OR NEW.AgentInstanceId IS NOT OLD.AgentInstanceId OR NEW.ExecutorPrincipalId IS NOT OLD.ExecutorPrincipalId
            OR NEW.SessionId IS NOT OLD.SessionId OR NEW.ActivationId IS NOT OLD.ActivationId OR NEW.RootActivationId IS NOT OLD.RootActivationId
            OR NEW.OriginalInitiatorPrincipalId IS NOT OLD.OriginalInitiatorPrincipalId OR json_extract(NEW.PayloadJson,'$.admission') IS NOT json_extract(OLD.PayloadJson,'$.admission'))
        BEGIN SELECT RAISE(ABORT,'Retain immutable context continuation admission'); END;
        """;
}
