using System.Text.Json;
using AgentCore.Application.Agents;
using AgentCore.Application.Execution;
using AgentCore.Application.Ports;
using AgentCore.Application.Tools;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Definitions;
using AgentCore.Domain.Security;

namespace AgentCore.Application.Security;

public static class TenantReportSourceReadRules
{
    public const string Reason = "report_source_read_authorized";
    public const string AuditAction = "operations.background.report-source.read";
    public static PreparedReportSourceRead Witness(TenantBackgroundCompletion source)
    {
        var initial = source.InitialChild ?? source.Child;
        var objective = source.Child.Admission.TaskIntent?.Text ?? source.ChildSession.Snapshot.Title;
        var json = BackgroundCompletionProjection.Build(source.Child, objective, []);
        return new(source.Parent!.Owner.SessionId, source.Child.SessionId, initial.AgentRunId, source.Child.AgentRunId,
            source.Child.Revision, ToolJsonResults.ClipUtf8Prefix(objective, 4000), json, TenantCompletionReadRules.Hash(json));
    }
    public static PreparedTenantOperation Lookup(TenantBackgroundCompletion source) => new(source.Child.AgentRunId,
        source.Child.AgentInstanceId, null, 1, 1, "", "", ReportSourceRead: Witness(source));
    public static PreparedCompletionRead Targets(PreparedReportSourceRead p) => new(p.ParentSessionId, p.ChildSessionId,
        p.InitialRunId, p.SourceRunId, p.SourceRevision, p.Objective, p.ObservationJson, p.ObservationHash, "[]");
    public static void Source(TenantApprovalRequest request, AgentRun source)
    {
        var p = request.Operation.ReportSourceRead ?? throw new SecurityConflictException("operation_mismatch");
        if (!source.IsTerminal || source.Result?.OutcomeKind == AgentRunOutcomeKind.Continued || source.Admission.OutputContract != AgentRunOutputContract.BackgroundOutcome
            || source.AgentRunId != request.RunId || source.AgentRunId != p.SourceRunId || source.SessionId != request.SessionId
            || source.SessionId != p.ChildSessionId || source.Revision != p.SourceRevision) throw new AccessDeniedException("completion_unavailable");
    }
    public static void Require(TenantApprovalRequest request, TenantBackgroundCompletion source, TenantAgent agent, AgentDefinition current, AgentRun root, bool pending)
    {
        Source(request, source.Child); TenantBackgroundCompletionFactory.RequireParent(source);
        TenantApprovalRules.Validate(request, source.Child, agent);
        if (request.Operation.ReportSourceRead != Witness(source) || agent.PrincipalId != source.Child.Owner.ExecutorPrincipalId
            || !RolePermissions.AllowsTool(root.Admission.Configuration!.Definition, ToolCatalog.BackgroundStart)
            || !RolePermissions.AllowsTool(current, ToolCatalog.BackgroundStart)
            || pending && source.Item?.Status != CompletionInboxStatus.Pending) throw new AccessDeniedException("completion_source_changed");
    }
    public static async ValueTask ReadAsync(TenantSecurityState state, TenantApprovalRequest request, Guid human, IamAuthorizer iam, CancellationToken ct)
    {
        await TenantCompletionReadRules.PermissionsAsync(state, Targets(request.Operation.ReportSourceRead!), human, iam, ct).ConfigureAwait(false);
        if (!(await iam.CheckOwnedTenantAsync(state, human, PermissionCatalog.Get("sessions.read"),
            TenantSessionRules.Scope(request.TenantId, request.Operation.ReportSourceRead!.ParentSessionId), ct).ConfigureAwait(false)).Allowed)
            throw new AccessDeniedException("approval_unavailable");
    }
    public static async ValueTask ReviewerAsync(TenantSecurityState state, TenantApprovalRequest request, Guid human, IamAuthorizer iam, CancellationToken ct)
    {
        await ReadAsync(state, request, human, iam, ct).ConfigureAwait(false);
        await TenantCompletionReadRules.ReviewerAsync(state, Targets(request.Operation.ReportSourceRead!), request.OriginalInitiatorPrincipalId, human, iam, ct).ConfigureAwait(false);
    }
    public static void Receipt(TenantApprovalRequest request, TenantOperationReceipt receipt, TenantApprovalDecision decision, TenantOperationGrant? grant)
    {
        if (request.Operation.ReportSourceRead is null || request.Status != TenantApprovalStatus.Approved || request.Kind != ApprovalKind.Review || grant is not null
            || receipt.Outcome != TenantOperationOutcome.Completed || receipt.ReasonCode != Reason || receipt.OperationId != request.Operation.OperationId
            || receipt.RequestId != request.RequestId || receipt.TenantId != request.TenantId || receipt.RunId != request.RunId || receipt.SessionId != request.SessionId
            || receipt.RootActivationId != request.RootActivationId || receipt.InstanceId != request.Operation.InstanceId || receipt.ExecutorPrincipalId != request.ExecutorPrincipalId
            || receipt.ActionHash != request.Operation.ActionHash || decision.RequestId != request.RequestId || decision.TenantId != request.TenantId
            || decision.Status != TenantApprovalStatus.Approved || decision.RequestRevision != request.Revision || decision.ActionHash != request.Operation.ActionHash)
            throw new AccessDeniedException("completion_read_review_required");
    }
}

public static partial class TenantApprovalRules
{
    public static PreparedTenantOperation PrepareReportSourceRead(Guid id, Guid instance, PreparedReportSourceRead p, long revision, long persona)
    {
        if (id == Guid.Empty || instance == Guid.Empty || revision < 1 || persona < 1 || p.ParentSessionId == Guid.Empty || p.ChildSessionId == Guid.Empty
            || p.ParentSessionId == p.ChildSessionId || p.InitialRunId == Guid.Empty || p.SourceRunId == Guid.Empty || p.SourceRevision < 1
            || string.IsNullOrWhiteSpace(p.Objective) || p.Objective.Length > 4000 || p.ObservationJson.Length is < 2 or > AgentRunLimits.MaxEvidenceBytes
            || p.ObservationHash != TenantCompletionReadRules.Hash(p.ObservationJson)) throw new ArgumentException("Invalid exact report source read.");
        var evidence = JsonSerializer.Deserialize<AgentRunAdmissionFactory.SignalInput>(p.ObservationJson);
        if (evidence?.TriggerKind != TriggerKind.BackgroundCompleted || evidence.InputBoundarySequence is not null || evidence.Text is null) throw new ArgumentException("Invalid report source evidence.");
        using var json = JsonDocument.Parse(evidence.Text);
        if (json.RootElement.GetProperty("childSessionId").GetGuid() != p.ChildSessionId || json.RootElement.GetProperty("initialAgentRunId").GetGuid() != p.SourceRunId
            || json.RootElement.GetProperty("status").GetString() is not ("Completed" or "Failed" or "Cancelled")
            || json.RootElement.GetProperty("artifacts").GetArrayLength() != 0) throw new ArgumentException("Invalid report source identity.");
        var action = JsonSerializer.Serialize(new { operation = "background.report.source.read", source = p, expectedInstanceRevision = revision, expectedPersonaRevision = persona });
        return new(id, instance, null, revision, persona, action, TenantCompletionReadRules.Hash(action), ReportSourceRead: p);
    }
}
