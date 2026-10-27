using System.Text.RegularExpressions;

namespace AgentCore.Infrastructure.Persistence.Migrations;

// Forward-only extension of the frozen predecessor. Legacy guard bodies are preserved; only this exact typed purpose bypasses live-Run Space admission.
internal static class EvaluationSandboxApprovalSchema
{
    internal static string Values => "((" + DecisionIntelligencePreferences.ApprovalValuesConstraint + ") AND json_extract(PayloadJson,'$.Operation.SandboxCommand.Evaluation') IS NULL) OR COALESCE((" + NativeValues + "),0)";
    private const string NativeValues = """
        Kind=0 AND Status IN (0,1,2,3,4) AND Revision>=1 AND ExpiresAt>CreatedAt AND ExpiresAt-CreatedAt<=3600000
        AND length(Fingerprint)=64 AND Permission='sandbox.execute' AND ResourceType='agent' AND ResourceId=InstanceId
        AND RunId IS NOT NULL AND SessionId IS NOT NULL AND AutomationId IS NULL AND CredentialId IS NULL
        AND SandboxProfileId IS NOT NULL AND SandboxProfileRevision>0 AND length(PayloadJson)<=131072
        AND json_type(PayloadJson,'$.Operation.SandboxCommand.Evaluation')='object'
        AND json_extract(PayloadJson,'$.Operation.SpaceContext') IS NULL
        AND json_extract(PayloadJson,'$.Operation.SandboxCommand.WorkspaceAccess') IS NULL
        AND json_extract(PayloadJson,'$.Operation.SandboxCommand.HomeAccess')='ReadOnly'
        AND json_extract(PayloadJson,'$.Operation.SandboxCommand.ExpectedGeneration') IS NULL
        AND json_extract(PayloadJson,'$.Operation.SandboxCommand.ExpectedLeaseRevision')=0
        AND json_extract(PayloadJson,'$.Operation.SandboxCommand.ProfileId')=SandboxProfileId
        AND json_extract(PayloadJson,'$.Operation.SandboxCommand.ProfileRevision')=SandboxProfileRevision
        AND length(json_extract(PayloadJson,'$.Operation.SandboxCommand.Evaluation.AttemptId'))=36
        AND length(json_extract(PayloadJson,'$.Operation.SandboxCommand.Evaluation.ExecutionId'))=36
        AND length(json_extract(PayloadJson,'$.Operation.SandboxCommand.Evaluation.Script.ScriptId'))=36
        AND json_extract(PayloadJson,'$.Operation.SandboxCommand.Evaluation.Script.Version') BETWEEN 1 AND 1000
        AND length(json_extract(PayloadJson,'$.Operation.SandboxCommand.Evaluation.Script.ContentHash'))=64
        AND length(json_extract(PayloadJson,'$.Operation.SandboxCommand.Evaluation.InputHash'))=64
        AND length(json_extract(PayloadJson,'$.Operation.SandboxCommand.Evaluation.CheckId')) BETWEEN 1 AND 64
        AND json_extract(json_extract(PayloadJson,'$.Operation.SandboxCommand.CommandJson'),'$.cwd')='/working'
        AND json_extract(json_extract(PayloadJson,'$.Operation.SandboxCommand.CommandJson'),'$.background')=0
        AND json_extract(json_extract(PayloadJson,'$.Operation.SandboxCommand.CommandJson'),'$.timeoutSeconds') BETWEEN 1 AND 30
        AND json_type(json_extract(PayloadJson,'$.Operation.SandboxCommand.CommandJson'),'$.command')='text'
        """;

    internal static string Rebuild(bool native = true)
    {
        var frozen = FrozenMigrationMetadata.Read("ac6d98ef7ad68f459fff90ec8350c71d537cfff42abdd03f7cf9932268dae03d");
        const string start = "CREATE TABLE \"decision_approval_rebuild\"";
        var begin = frozen.IndexOf(start, StringComparison.Ordinal);
        var end = frozen.IndexOf("CREATE UNIQUE INDEX \"IX_TenantApprovalRequests_OperationId\"", begin, StringComparison.Ordinal);
        var schema = frozen[begin..end];
        if (!schema.Contains(DecisionIntelligencePreferences.ApprovalValuesConstraint, StringComparison.Ordinal)) throw new InvalidOperationException("Frozen approval predecessor differs.");
        schema = schema.Replace(DecisionIntelligencePreferences.ApprovalValuesConstraint, native ? Values : DecisionIntelligencePreferences.ApprovalValuesConstraint, StringComparison.Ordinal)
            .Replace("decision_approval_rebuild", "evaluation_approval_rebuild", StringComparison.Ordinal);
        var indexes = Regex.Matches(frozen, @"(?m)^CREATE (?:UNIQUE )?INDEX [^\n]+ ON ""TenantApprovalRequests""[^\n]+;")
            .Select(m => m.Value);
        var triggers = Regex.Matches(frozen, @"(?ms)^CREATE TRIGGER \S+ [^\n]*ON TenantApprovalRequests\b.*?(?=^CREATE TRIGGER |\z)")
            .Select(m => native ? NativeExemption(m.Value.TrimEnd()) : m.Value.TrimEnd());
        return "PRAGMA legacy_alter_table=ON;\n" + schema + "\n" + string.Join("\n", indexes) + "\n" + string.Join("\n", triggers)
            + "\n" + (native ? NativeGuards : "") + "\nPRAGMA legacy_alter_table=OFF;";
    }
    private static string NativeExemption(string trigger)
    {
        if (!trigger.EndsWith("END;", StringComparison.Ordinal)) throw new InvalidOperationException("Frozen approval trigger boundary differs.");
        const string guard = "json_type(NEW.PayloadJson,'$.Operation.SandboxCommand.Evaluation') IS NOT 'object'";
        if (trigger.StartsWith("CREATE TRIGGER TR_TenantApprovals_IndependentTask_", StringComparison.Ordinal))
            return trigger.Replace("WHEN NEW.OriginalInitiatorPrincipalId IS NULL", "WHEN " + guard + " AND NEW.OriginalInitiatorPrincipalId IS NULL", StringComparison.Ordinal);
        if (trigger.StartsWith("CREATE TRIGGER TR_TenantApprovals_Space_INSERT ", StringComparison.Ordinal)
            || trigger.StartsWith("CREATE TRIGGER TR_TenantApprovals_Space_Workspace_INSERT ", StringComparison.Ordinal))
            return trigger.Replace("\nBEGIN", "\nWHEN " + guard + "\nBEGIN", StringComparison.Ordinal);
        return trigger;
    }
    private const string NativeGuards = """
        CREATE TRIGGER TR_EvaluationSandbox_Approval_Insert BEFORE INSERT ON TenantApprovalRequests
        WHEN json_type(NEW.PayloadJson,'$.Operation.SandboxCommand.Evaluation')='object'
        BEGIN
            SELECT RAISE(ABORT,'Native evaluation has exactly one operation payload') WHERE EXISTS (
                SELECT 1 FROM json_each(NEW.PayloadJson,'$.Operation') p WHERE p.key NOT IN
                ('OperationId','InstanceId','ExpectedInstanceRevision','ExpectedPersonaRevision','CanonicalActionJson','ActionHash','SandboxCommand') AND p.type<>'null');
            SELECT RAISE(ABORT,'Native evaluation requires its exact terminal source, Suite and Script') WHERE NOT EXISTS (
                SELECT 1 FROM EvaluationAttempts a
                JOIN AgentRuns r ON r.TenantId=a.TenantId AND r.AgentRunId=lower(a.SubjectId)
                JOIN EvaluationSuites s ON s.TenantId=a.TenantId AND s.SuiteId=a.SuiteId AND s.Version=a.SuiteVersion
                JOIN json_each(s.Json,'$.Checks') c ON json_extract(c.value,'$.CheckId')=json_extract(NEW.PayloadJson,'$.Operation.SandboxCommand.Evaluation.CheckId')
                JOIN EvaluationScripts script ON script.TenantId=a.TenantId AND lower(script.ScriptId)=json_extract(c.value,'$.Script.ScriptId') AND script.Version=json_extract(c.value,'$.Script.Version')
                WHERE a.TenantId=NEW.TenantId AND lower(a.AttemptId)=json_extract(NEW.PayloadJson,'$.Operation.SandboxCommand.Evaluation.AttemptId')
                    AND a.SubjectKind=1 AND a.Status=1 AND json_extract(a.Json,'$.SubjectRevision')=r.Revision AND r.Status IN (4,5,6)
                    AND r.AgentRunId=NEW.RunId AND r.SessionId=NEW.SessionId AND r.AgentInstanceId=NEW.InstanceId AND r.ExecutorPrincipalId=NEW.ExecutorPrincipalId
                    AND json_extract(a.Json,'$.Hook') IN (1,2,3)
                    AND json_extract(script.Json,'$.Kind')=1
                    AND json_extract(script.Json,'$.ContentHash')=json_extract(c.value,'$.Script.ContentHash')
                    AND json_extract(c.value,'$.Script')=json_extract(NEW.PayloadJson,'$.Operation.SandboxCommand.Evaluation.Script')
                    AND json_extract(script.Json,'$.SandboxProfileId')=NEW.SandboxProfileId
                    AND json_extract(script.Json,'$.Body')=json_extract(json_extract(NEW.PayloadJson,'$.Operation.SandboxCommand.CommandJson'),'$.command')
                    AND json_extract(script.Json,'$.SandboxTimeoutSeconds')=json_extract(json_extract(NEW.PayloadJson,'$.Operation.SandboxCommand.CommandJson'),'$.timeoutSeconds'));
        END;
        CREATE TRIGGER TR_EvaluationSandbox_Approval_Immutable BEFORE UPDATE ON TenantApprovalRequests
        WHEN json_type(NEW.PayloadJson,'$.Operation.SandboxCommand.Evaluation')='object' OR json_type(OLD.PayloadJson,'$.Operation.SandboxCommand.Evaluation')='object'
        BEGIN
            SELECT RAISE(ABORT,'Native evaluation approval identity is immutable')
                WHERE json_remove(NEW.PayloadJson,'$.Status','$.Revision') IS NOT json_remove(OLD.PayloadJson,'$.Status','$.Revision');
        END;
        """;
}
