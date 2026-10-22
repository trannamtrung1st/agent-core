using AgentCore.Application.Ports;
using AgentCore.Domain.Security;
using AgentCore.Domain.Triggers;

namespace AgentCore.Application.Security;
public static class TenantAutomationMutationCommitRules
{
    public static PreparedTenantOperation Require(TenantAutomationMutationObservation source, TenantAutomationMutationCommit commit, DateTimeOffset now)
    {
        TenantLocalEffectRules.RequireDirectRun(source.Reader.Run, commit.Generation, now);
        TenantAutomationMutationRules.Validate(source, commit.Prepared);
        var op = TenantApprovalRules.PrepareAutomationMutation(commit.OperationId, source.Reader.Run.AgentInstanceId, commit.Prepared, source.Reader.Agent.Instance.Revision, source.Reader.Agent.Instance.PersonaRevision);
        if (commit.Prepared.SemanticReviewRequired || commit.ExpectedRunRevision != source.Reader.Run.Revision || commit.RunId != source.Reader.Run.AgentRunId
            || commit.ExpectedInstanceRevision != source.Reader.Agent.Instance.Revision || commit.ActionHash != op.ActionHash || commit.OperationId == Guid.Empty
            || string.IsNullOrWhiteSpace(commit.SourceCallId) || commit.SourceCallId.Length > 200 || commit.ParametersHash != TenantAutomationMutationRules.ParametersHash(commit.Prepared.ToolName, commit.Prepared.ArgumentsJson))
            throw new SecurityConflictException("operation_mismatch");
        return op;
    }
    public static async ValueTask AuthorityAsync(TenantAutomationMutationObservation source, PreparedAutomationMutation value, IamAuthorizer iam,
        CancellationToken ct, bool primaryRequired = true, bool noReview = false, DateTimeOffset? at = null)
    {
        var reader = source.Reader; var tenant = reader.Security.Tenant.TenantId; var permission = TenantAutomationMutationRules.Permission(value.ToolName);
        var primary = TenantAutomationMutationRules.Primary(tenant, value.ToolName, value.Proposed);
        await TenantAutomationMutationRules.RawAsync(source, value, iam, ct).ConfigureAwait(false);
        if (primaryRequired && !(await iam.CheckOwnedTenantAsync(reader.Security, reader.Agent.PrincipalId, PermissionCatalog.Get(permission), primary, ct).ConfigureAwait(false)).Allowed)
            throw new AccessDeniedException("automation_executor_required");
        if (noReview)
            foreach (var (key, scope) in TenantAutomationMutationRules.Targets(source, value).Append((permission, primary)).Distinct())
                if (await TenantSandboxReadOperationRules.NeedsReviewAsync(reader.Security, reader.Run.Admission.ExecutionContext!.OriginalInitiatorPrincipalId, key, scope, iam, ct).ConfigureAwait(false)) throw new AccessDeniedException("automation_mutation_review_required");
        if (value.ToolName == AgentCore.Application.Tools.ToolCatalog.AutomationRun)
        {
            await TenantAutomationInvocationRules.RequesterAsync(source, iam, ct).ConfigureAwait(false);
            return;
        }
        if (value.ToolName is not (AgentCore.Application.Tools.ToolCatalog.AutomationDelete or AgentCore.Application.Tools.ToolCatalog.AutomationDisable))
        {
            try { TenantAutomationRules.Configuration(value.Proposed, reader.Current, at ?? value.Proposed.Provenance.UpdatedAt, current: source.Current); }
            catch (ArgumentException) { throw new AccessDeniedException("automation_timing_or_policy_changed"); }
            await TenantCompletionDestinationRules.AuthoringAsync(reader.Security, reader.Agent.PrincipalId, value.Proposed, source.ReportDestination, iam, ct).ConfigureAwait(false);
            await TenantAutomationConversationRules.AuthoringAsync(reader.Security, reader.Agent.PrincipalId, value.Proposed, source.Conversation, iam, ct).ConfigureAwait(false);
        }
    }
    public static void Transaction(TenantSecurityState next, long expected, TenantAutomationMutationObservation source, PreparedAutomationMutation value, AuthorizationAudit audit)
    {
        if (next.Tenant.TenantId != source.Reader.Run.Owner.TenantId || next.Tenant.Revision != checked(expected + 1)
            || audit.TenantId != next.Tenant.TenantId || audit.ActorPrincipalId != source.Reader.Agent.PrincipalId || audit.Action != "operations.automations.mutate"
            || audit.Target != value.Proposed.AutomationId.ToString("D") || audit.Decision != "allowed" || audit.ReasonCode != TenantAutomationMutationRules.Completed)
            throw new ArgumentException("Invalid Automation authoring transaction.");
    }
    public static TenantAutomationMutationReceipt Evidence(PreparedAutomationMutation value) => new(value.Proposed.AutomationId, value.ToolName,
        value.Current?.Revision ?? 0, value.Proposed.Revision, value.Current is null ? null : TenantAutomationReadRules.ConfigurationHash(value.Current), TenantAutomationReadRules.ConfigurationHash(value.Proposed),
        value.Invocation is { } plan ? AgentCore.Application.Triggers.AutomationRules.ManualOccurrence(value.Current!, plan.Model, value.Proposed.Provenance.UpdatedAt).OccurrenceId : null,
        value.Invocation?.SessionId, value.Invocation?.RunId);
}
