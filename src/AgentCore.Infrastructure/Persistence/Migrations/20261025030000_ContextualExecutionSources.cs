using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable
namespace AgentCore.Infrastructure.Persistence.Migrations;

[DbContext(typeof(AgentCoreDbContext))]
[Migration("20261025030000_ContextualExecutionSources")]
public sealed class ContextualExecutionSources : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql(View);
        foreach (var family in new[] { "Action", "Read" })
        {
            migrationBuilder.Sql($"DROP TRIGGER TR_TenantApprovals_Account{family}_Insert;");
            migrationBuilder.Sql(AccountGuard(family, true));
        }
        migrationBuilder.Sql(RunAndOperationGuards);
    }
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            CREATE TEMP TABLE SpaceExecutionDowngradeGuard(Rows INTEGER CHECK(Rows=0));
            INSERT INTO SpaceExecutionDowngradeGuard SELECT (SELECT count(*) FROM AgentRuns WHERE TenantId IS NOT NULL)
                +(SELECT count(*) FROM TenantApprovalRequests WHERE json_type(PayloadJson,'$.Operation.SpaceContext')='object');
            DROP TABLE SpaceExecutionDowngradeGuard;
            DROP TRIGGER TR_AgentRuns_Space_INSERT;
            DROP TRIGGER TR_AgentRuns_Space_UPDATE;
            DROP TRIGGER TR_TenantApprovals_Space_INSERT;
            DROP TRIGGER TR_TenantApprovals_Space_UPDATE;
            """);
        foreach (var family in new[] { "Action", "Read" })
        {
            migrationBuilder.Sql($"DROP TRIGGER TR_TenantApprovals_Account{family}_Insert;");
            migrationBuilder.Sql(AccountGuard(family, false));
        }
        migrationBuilder.Sql("DROP VIEW CurrentSpaceCredentialBindings;");
    }
    // Compute availability from the Run pin plus current canonical membership and
    // baseline sources. This view stores no grants, aliases, secrets or copies.
    private const string View = """
        CREATE VIEW CurrentSpaceCredentialBindings AS
        SELECT r.AgentRunId AS RunId, r.TenantId, r.AgentInstanceId,
            json_extract(pin.value,'$.credentialId') AS CredentialId,
            json_extract(pin.value,'$.bindingId') AS BindingId,
            json_extract(pin.value,'$.reference') AS Reference,
            json_extract(pin.value,'$.revision') AS Revision
        FROM AgentRuns r
        JOIN Spaces s ON s.TenantId=r.TenantId AND lower(s.SpaceId)=json_extract(r.PayloadJson,'$.admission.configuration.spaceContext.spaceId') AND s.DeletedAt IS NULL
        JOIN SpaceMemberships m ON m.TenantId=s.TenantId AND m.SpaceId=s.SpaceId AND m.InstanceId=r.AgentInstanceId
        JOIN json_each(r.PayloadJson,'$.admission.configuration.credentialBindings') pin
        WHERE r.TenantId IS NOT NULL AND (s.Kind=1 OR s.OwnerInstanceId=r.AgentInstanceId)
        AND (json_extract(r.PayloadJson,'$.admission.configuration.spaceContext.environmentId') IS NULL OR
            (s.EnvironmentsEnabled=1 AND s.EnvironmentSelectionEpoch=json_extract(r.PayloadJson,'$.admission.configuration.spaceContext.environmentSelectionEpoch')
            AND EXISTS(SELECT 1 FROM SpaceEnvironments e WHERE e.TenantId=s.TenantId AND e.SpaceId=s.SpaceId AND e.DeletedAt IS NULL
                AND lower(e.EnvironmentId)=json_extract(r.PayloadJson,'$.admission.configuration.spaceContext.environmentId'))))
        AND (
            (json_extract(pin.value,'$.contextSpaceId') IS NULL AND EXISTS(SELECT 1 FROM AgentCredentialBindings b
                WHERE b.TenantId=r.TenantId AND b.AgentInstanceId=r.AgentInstanceId AND b.CredentialId=json_extract(pin.value,'$.credentialId')
                AND b.BindingId=json_extract(pin.value,'$.bindingId') AND b.Reference=json_extract(pin.value,'$.reference') AND b.Revision=json_extract(pin.value,'$.revision'))
                AND NOT EXISTS(SELECT 1 FROM SpaceMembershipDisabledBindings d WHERE d.TenantId=m.TenantId AND d.SpaceId=m.SpaceId AND d.InstanceId=m.InstanceId
                    AND d.BindingKey='Credential:'||json_extract(pin.value,'$.credentialId')))
            OR (json_extract(pin.value,'$.contextSpaceId')=lower(s.SpaceId) AND json_extract(pin.value,'$.revision')=m.Revision
                AND EXISTS(SELECT 1 FROM SpaceMembershipAddedBindings a WHERE a.TenantId=m.TenantId AND a.SpaceId=m.SpaceId AND a.InstanceId=m.InstanceId
                    AND a.Kind=3 AND a.ResourceKey=json_extract(pin.value,'$.credentialId') AND a.AccountRef=json_extract(pin.value,'$.reference')))
        );
        """;
    private static string AccountGuard(string family, bool contextual) => $"""
        CREATE TRIGGER TR_TenantApprovals_Account{family}_Insert BEFORE INSERT ON TenantApprovalRequests
        WHEN json_type(NEW.PayloadJson, '$.Operation.Account{family}') IS 'object' AND NOT EXISTS (
            SELECT 1 FROM {(contextual ? "CurrentSpaceCredentialBindings" : "AgentCredentialBindings")} b JOIN Credentials c ON c.CredentialId = b.CredentialId
            JOIN TenantCredentials owned ON owned.CredentialId = c.CredentialId AND owned.TenantId = b.TenantId AND owned.DeletedAt IS NULL
            WHERE {(contextual ? "b.RunId = NEW.RunId AND " : "")}b.TenantId = NEW.TenantId AND b.AgentInstanceId = NEW.InstanceId AND b.CredentialId = NEW.CredentialId
              AND b.BindingId = json_extract(NEW.PayloadJson, '$.Operation.Account{family}.Credential.BindingId')
              AND b.Reference = json_extract(NEW.PayloadJson, '$.Operation.Account{family}.Credential.Reference')
              AND b.Revision = json_extract(NEW.PayloadJson, '$.Operation.Account{family}.Credential.BindingRevision')
              AND c.Revision = json_extract(NEW.PayloadJson, '$.Operation.Account{family}.Credential.CredentialRevision')
              AND c.SecretRevision = json_extract(NEW.PayloadJson, '$.Operation.Account{family}.Credential.SecretRevision')
              AND c.Purpose = CASE json_extract(NEW.PayloadJson, '$.Operation.Account{family}.Purpose') WHEN 1 THEN 'EmailAccount' WHEN 2 THEN 'GoogleDriveAccount' END)
        BEGIN SELECT RAISE(ABORT, 'exact account {family.ToLowerInvariant()} binding required'); END;
        """;
    private const string RunAndOperationGuards = """
        CREATE TRIGGER TR_AgentRuns_Space_INSERT BEFORE INSERT ON AgentRuns WHEN NEW.TenantId IS NOT NULL
        BEGIN
            SELECT RAISE(ABORT,'Tenant Run requires its captured Space context') WHERE NOT COALESCE(
                json_type(NEW.PayloadJson,'$.admission.configuration.spaceContext')='object'
                AND lower(NEW.TenantId)=json_extract(NEW.PayloadJson,'$.admission.configuration.spaceContext.tenantId')
                AND json_extract(NEW.PayloadJson,'$.admission.configuration.spaceContext.spaceRevision')>=1
                AND json_extract(NEW.PayloadJson,'$.admission.configuration.spaceContext.membershipRevision')>=1
                AND json_extract(NEW.PayloadJson,'$.admission.configuration.spaceContext.selectionRevision')>=1
                AND json_extract(NEW.PayloadJson,'$.admission.configuration.spaceContext.environmentSelectionEpoch')>=0,0);
            SELECT RAISE(ABORT,'Tenant Run requires its exact current Space membership') WHERE NOT EXISTS(
                SELECT 1 FROM Spaces s JOIN SpaceMemberships m ON m.TenantId=s.TenantId AND m.SpaceId=s.SpaceId AND m.InstanceId=NEW.AgentInstanceId
                JOIN Spaces p ON p.TenantId=s.TenantId AND p.Kind=0 AND p.OwnerInstanceId=NEW.AgentInstanceId AND p.DeletedAt IS NULL
                WHERE s.TenantId=NEW.TenantId AND lower(s.SpaceId)=json_extract(NEW.PayloadJson,'$.admission.configuration.spaceContext.spaceId') AND s.DeletedAt IS NULL
                AND (s.Kind=1 OR s.OwnerInstanceId=NEW.AgentInstanceId)
                AND lower(p.SpaceId)=json_extract(NEW.PayloadJson,'$.admission.configuration.spaceContext.privateSpaceId')
                AND (json_extract(NEW.PayloadJson,'$.admission.configuration.spaceContext.environmentId') IS NULL
                    AND json_extract(NEW.PayloadJson,'$.admission.configuration.spaceContext.environmentRevision') IS NULL
                    AND json_extract(NEW.PayloadJson,'$.admission.configuration.spaceContext.environmentSelectionEpoch')=0
                    OR s.EnvironmentsEnabled=1 AND s.EnvironmentSelectionEpoch=json_extract(NEW.PayloadJson,'$.admission.configuration.spaceContext.environmentSelectionEpoch')
                    AND EXISTS(SELECT 1 FROM SpaceEnvironments e WHERE e.TenantId=s.TenantId AND e.SpaceId=s.SpaceId AND e.DeletedAt IS NULL
                        AND lower(e.EnvironmentId)=json_extract(NEW.PayloadJson,'$.admission.configuration.spaceContext.environmentId')
                        AND json_extract(NEW.PayloadJson,'$.admission.configuration.spaceContext.environmentRevision')>=1)));
        END;
        CREATE TRIGGER TR_AgentRuns_Space_UPDATE BEFORE UPDATE ON AgentRuns WHEN OLD.TenantId IS NOT NULL
            AND json_extract(NEW.PayloadJson,'$.admission.configuration.spaceContext') IS NOT json_extract(OLD.PayloadJson,'$.admission.configuration.spaceContext')
        BEGIN SELECT RAISE(ABORT,'Captured Run Space context is immutable'); END;
        CREATE TRIGGER TR_TenantApprovals_Space_INSERT BEFORE INSERT ON TenantApprovalRequests
        BEGIN
            SELECT RAISE(ABORT,'Operation requires its exact captured Space witness') WHERE NOT COALESCE(
                json_type(NEW.PayloadJson,'$.Operation.SpaceContext')='object'
                AND lower(NEW.TenantId)=json_extract(NEW.PayloadJson,'$.Operation.SpaceContext.TenantId')
                AND json_extract(NEW.PayloadJson,'$.Operation.SpaceContext')=json_extract(json_extract(NEW.PayloadJson,'$.Operation.CanonicalActionJson'),'$.spaceContext'),0);
            SELECT RAISE(ABORT,'Operation Space witness must match its owning Run') WHERE json_type(NEW.PayloadJson,'$.Operation.AutomationAdmission') IS NOT 'object'
                AND NOT EXISTS(SELECT 1 FROM AgentRuns r WHERE r.TenantId=NEW.TenantId AND r.AgentRunId=NEW.RunId AND r.SessionId=NEW.SessionId
                    AND r.AgentInstanceId=NEW.InstanceId AND r.ExecutorPrincipalId=NEW.ExecutorPrincipalId
                    AND json_extract(r.PayloadJson,'$.admission.configuration.spaceContext.spaceId')=json_extract(NEW.PayloadJson,'$.Operation.SpaceContext.SpaceId')
                    AND json_extract(r.PayloadJson,'$.admission.configuration.spaceContext.privateSpaceId')=json_extract(NEW.PayloadJson,'$.Operation.SpaceContext.PrivateSpaceId')
                    AND json_extract(r.PayloadJson,'$.admission.configuration.spaceContext.environmentId') IS json_extract(NEW.PayloadJson,'$.Operation.SpaceContext.EnvironmentId')
                    AND json_extract(r.PayloadJson,'$.admission.configuration.spaceContext.spaceRevision')=json_extract(NEW.PayloadJson,'$.Operation.SpaceContext.SpaceRevision')
                    AND json_extract(r.PayloadJson,'$.admission.configuration.spaceContext.membershipRevision')=json_extract(NEW.PayloadJson,'$.Operation.SpaceContext.MembershipRevision')
                    AND json_extract(r.PayloadJson,'$.admission.configuration.spaceContext.environmentRevision') IS json_extract(NEW.PayloadJson,'$.Operation.SpaceContext.EnvironmentRevision')
                    AND json_extract(r.PayloadJson,'$.admission.configuration.spaceContext.environmentSelectionEpoch')=json_extract(NEW.PayloadJson,'$.Operation.SpaceContext.EnvironmentSelectionEpoch')
                    AND json_extract(r.PayloadJson,'$.admission.configuration.spaceContext.selectionRevision')=json_extract(NEW.PayloadJson,'$.Operation.SpaceContext.SelectionRevision'));
            SELECT RAISE(ABORT,'Completion report requires its exact destination Session selection')
                WHERE json_type(NEW.PayloadJson,'$.Operation.BackgroundReport')='object' AND NOT EXISTS(
                    SELECT 1 FROM Sessions s JOIN TenantSessions t ON t.SessionId=s.SessionId AND t.TenantId=NEW.TenantId
                    WHERE s.SessionId=json_extract(NEW.PayloadJson,'$.Operation.BackgroundReport.ParentSessionId')
                    AND t.AgentInstanceId=NEW.InstanceId AND t.ExecutorPrincipalId=NEW.ExecutorPrincipalId
                    AND lower(t.TenantId)=json_extract(NEW.PayloadJson,'$.Operation.BackgroundReport.DestinationContext.TenantId')
                    AND json_extract(s.ContextSelectionJson,'$.spaceId')=json_extract(NEW.PayloadJson,'$.Operation.BackgroundReport.DestinationContext.SpaceId')
                    AND json_extract(s.ContextSelectionJson,'$.environmentId') IS json_extract(NEW.PayloadJson,'$.Operation.BackgroundReport.DestinationContext.EnvironmentId')
                    AND json_extract(s.ContextSelectionJson,'$.environmentSelectionEpoch')=json_extract(NEW.PayloadJson,'$.Operation.BackgroundReport.DestinationContext.EnvironmentSelectionEpoch')
                    AND json_extract(s.ContextSelectionJson,'$.revision')=json_extract(NEW.PayloadJson,'$.Operation.BackgroundReport.DestinationContext.Revision'));
        END;
        CREATE TRIGGER TR_TenantApprovals_Space_UPDATE BEFORE UPDATE ON TenantApprovalRequests
            WHEN json_extract(NEW.PayloadJson,'$.Operation.SpaceContext') IS NOT json_extract(OLD.PayloadJson,'$.Operation.SpaceContext')
        BEGIN SELECT RAISE(ABORT,'Operation Space witness is immutable'); END;
        """;
}
