using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentCore.Application.Ports;
using AgentCore.Application.Tools;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Definitions;
using AgentCore.Domain.Security;

namespace AgentCore.Application.Security;

public static partial class TenantApprovalRules
{
    public static PreparedTenantOperation Prepare(Guid operationId, Guid instance, AgentIdentity persona, long instanceRevision, long personaRevision)
    {
        AgentDefinitionValidator.ValidateIdentity(persona);
        if (operationId == Guid.Empty || instance == Guid.Empty || instanceRevision < 1 || personaRevision < 1) throw new ArgumentException("Invalid prepared persona operation.");
        var json = JsonSerializer.Serialize(new { operation = "agents.persona.update", permission = "agents.update",
            resourceType = "agent", resourceId = instance.ToString("D"), expectedInstanceRevision = instanceRevision,
            expectedPersonaRevision = personaRevision, persona = new { persona.Name, persona.Role, persona.Description, persona.Tone } });
        if (Encoding.UTF8.GetByteCount(json) > 32768) throw new ArgumentException("Prepared operation exceeds its content bound.");
        return new(operationId, instance, persona, instanceRevision, personaRevision, json, Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(json))));
    }

    public static PreparedTenantOperation PreparePreferences(Guid operationId, Guid instance, PreparedConversationPreferences preferences, long instanceRevision, long personaRevision)
    {
        var normalized = TenantPreferenceToolContract.Parameters(TenantPreferenceToolContract.Patch(preferences));
        if (operationId == Guid.Empty || instance == Guid.Empty || instanceRevision < 1 || personaRevision < 1 || normalized != preferences)
            throw new ArgumentException("Invalid prepared preference operation.");
        var json = JsonSerializer.Serialize(new { operation = "agents.preferences.update", permission = "agents.update",
            resourceType = "agent", resourceId = instance.ToString("D"), expectedInstanceRevision = instanceRevision,
            expectedPersonaRevision = personaRevision, preferences = normalized });
        return new(operationId, instance, null, instanceRevision, personaRevision, json, Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(json))), normalized);
    }

    public static PreparedTenantOperation PrepareCredentialCheck(Guid operationId, Guid instance, PreparedCredentialConnectionCheck check, long instanceRevision, long personaRevision)
    {
        if (operationId == Guid.Empty || instance == Guid.Empty || check.CredentialId == Guid.Empty || check.BindingId == Guid.Empty
            || check.Reference != AgentCore.Domain.Credentials.CredentialRules.Reference(check.Reference) || check.BindingRevision < 1
            || check.CredentialRevision < 1 || check.SecretRevision < 1 || check.AuthEpoch < 1 || instanceRevision < 1 || personaRevision < 1)
            throw new ArgumentException("Invalid exact credential check.");
        var json = JsonSerializer.Serialize(new { operation = "credentials.connection.check", permission = "credentials.use", resourceType = "credential",
            resourceId = check.CredentialId.ToString("D"), expectedInstanceRevision = instanceRevision, expectedPersonaRevision = personaRevision, credentialCheck = check });
        return new(operationId, instance, null, instanceRevision, personaRevision, json, Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(json))), CredentialCheck: check);
    }

    public static PreparedTenantOperation Rebuild(PreparedTenantOperation op) => BindSpace(RebuildCore(op), op.SpaceContext);

    private static PreparedTenantOperation RebuildCore(PreparedTenantOperation op) => op.Kind switch
    {
        TenantOperationKind.AutomationMutation => PrepareAutomationMutation(op.OperationId, op.InstanceId, op.AutomationMutation!, op.ExpectedInstanceRevision, op.ExpectedPersonaRevision),
        TenantOperationKind.HttpRequest => PrepareHttpRequest(op.OperationId, op.InstanceId, op.HttpRequest!, op.ExpectedInstanceRevision, op.ExpectedPersonaRevision),
        TenantOperationKind.ManagementMutation => PrepareManagementMutation(op.OperationId, op.InstanceId, op.ManagementMutation!, op.ExpectedInstanceRevision, op.ExpectedPersonaRevision),
        TenantOperationKind.ManagementRead => PrepareManagementRead(op.OperationId, op.InstanceId, op.ManagementRead!, op.ExpectedInstanceRevision, op.ExpectedPersonaRevision),
        TenantOperationKind.AutomationRead => PrepareAutomationRead(op.OperationId, op.InstanceId, op.AutomationRead!, op.ExpectedInstanceRevision, op.ExpectedPersonaRevision),
        TenantOperationKind.ExperienceOperation => PrepareExperience(op.OperationId, op.InstanceId, op.ExperienceOperation!, op.ExpectedInstanceRevision, op.ExpectedPersonaRevision),
        TenantOperationKind.ContinuityRead => PrepareContinuity(op.OperationId, op.InstanceId, op.ContinuityRead!, op.ExpectedInstanceRevision, op.ExpectedPersonaRevision),
        TenantOperationKind.MemoryOperation => PrepareMemory(op.OperationId, op.InstanceId, op.MemoryOperation!, op.ExpectedInstanceRevision, op.ExpectedPersonaRevision),
        TenantOperationKind.AutomationAdmission => PrepareAutomationAdmission(op.OperationId, op.InstanceId, op.AutomationAdmission!, op.ExpectedInstanceRevision, op.ExpectedPersonaRevision),
        TenantOperationKind.AccountAction => PrepareAccountAction(op.OperationId, op.InstanceId, op.AccountAction!, op.ExpectedInstanceRevision, op.ExpectedPersonaRevision),
        TenantOperationKind.AccountRead => PrepareAccountRead(op.OperationId, op.InstanceId, op.AccountRead!, op.ExpectedInstanceRevision, op.ExpectedPersonaRevision),
        TenantOperationKind.ReportSourceRead => PrepareReportSourceRead(op.OperationId, op.InstanceId, op.ReportSourceRead!, op.ExpectedInstanceRevision, op.ExpectedPersonaRevision),
        TenantOperationKind.CompletionRead => PrepareCompletionRead(op.OperationId, op.InstanceId, op.CompletionRead!, op.ExpectedInstanceRevision, op.ExpectedPersonaRevision),
        TenantOperationKind.BackgroundReport => PrepareBackgroundReport(op.OperationId, op.InstanceId, op.BackgroundReport!, op.ExpectedInstanceRevision, op.ExpectedPersonaRevision),
        TenantOperationKind.BackgroundStart => PrepareBackgroundStart(op.OperationId, op.InstanceId, op.BackgroundStart!, op.ExpectedInstanceRevision, op.ExpectedPersonaRevision),
        TenantOperationKind.WorkspaceMutation => PrepareWorkspaceMutation(op.OperationId, op.InstanceId, op.WorkspaceMutation!, op.ExpectedInstanceRevision, op.ExpectedPersonaRevision),
        TenantOperationKind.WorkspaceRead => PrepareWorkspaceRead(op.OperationId, op.InstanceId, op.WorkspaceRead!, op.ExpectedInstanceRevision, op.ExpectedPersonaRevision),
        TenantOperationKind.AttachmentRead => PrepareAttachmentRead(op.OperationId, op.InstanceId, op.AttachmentRead!, op.ExpectedInstanceRevision, op.ExpectedPersonaRevision),
        TenantOperationKind.ArtifactVerification => PrepareArtifactVerification(op.OperationId, op.InstanceId, op.ArtifactVerification!, op.ExpectedInstanceRevision, op.ExpectedPersonaRevision),
        TenantOperationKind.ArtifactPublication => PrepareArtifact(op.OperationId, op.InstanceId, op.ArtifactPublication!, op.ExpectedInstanceRevision, op.ExpectedPersonaRevision),
        TenantOperationKind.Persona => Prepare(op.OperationId, op.InstanceId, op.Persona!, op.ExpectedInstanceRevision, op.ExpectedPersonaRevision),
        TenantOperationKind.ConversationPreferences => PreparePreferences(op.OperationId, op.InstanceId, op.Preferences!, op.ExpectedInstanceRevision, op.ExpectedPersonaRevision),
        TenantOperationKind.CredentialConnectionCheck => PrepareCredentialCheck(op.OperationId, op.InstanceId, op.CredentialCheck!, op.ExpectedInstanceRevision, op.ExpectedPersonaRevision),
        TenantOperationKind.SandboxCommand => PrepareSandbox(op.OperationId, op.InstanceId, op.SandboxCommand!, op.ExpectedInstanceRevision, op.ExpectedPersonaRevision),
        TenantOperationKind.SandboxControl => PrepareSandboxControl(op.OperationId, op.InstanceId, op.SandboxControl!, op.ExpectedInstanceRevision, op.ExpectedPersonaRevision),
        TenantOperationKind.SandboxRead => PrepareSandboxRead(op.OperationId, op.InstanceId, op.SandboxRead!, op.ExpectedInstanceRevision, op.ExpectedPersonaRevision),
        TenantOperationKind.CredentialRead => PrepareCredentialRead(op.OperationId, op.InstanceId, op.CredentialRead!, op.ExpectedInstanceRevision, op.ExpectedPersonaRevision),
        TenantOperationKind.BrowserCommand => PrepareBrowser(op.OperationId, op.InstanceId, op.BrowserCommand!, op.ExpectedInstanceRevision, op.ExpectedPersonaRevision),
        _ => throw new ArgumentException("Exactly one supported operation payload is required.")
    };

    public static void Validate(TenantApprovalRequest request, AgentRun run, TenantAgent target, bool requireCurrentWitness = true, AgentRun? source = null)
    {
        if (request.Operation.AutomationAdmission is not null) throw new ArgumentException("Admission consent requires its reserved occurrence owner.");
        var context = run.Admission.ExecutionContext ?? throw new AccessDeniedException("tenant_context_required");
        var op = request.Operation;
        var space = run.Admission.Configuration?.SpaceContext ?? throw new AccessDeniedException("space_context_required");
        if (op.SpaceContext != space.Witness()) throw new AccessDeniedException("operation_context_changed");
        if (op.ReportSourceRead is not null) TenantReportSourceReadRules.Source(request, run);
        else if (op.BackgroundReport is not null) TenantBackgroundReportRules.Source(request, run);
        else TenantApprovalContinuationRules.Require(request, run, source);
        if (op.HttpRequest is { } http && http.SessionId != request.SessionId) throw new ArgumentException("Use the actual HTTP request Session.");
        if (op.ManagementMutation is { } managementMutation && managementMutation.SessionId != request.SessionId) throw new ArgumentException("Use the actual management change Session.");
        if (op.AutomationMutation is { } automationMutation && automationMutation.SessionId != request.SessionId) throw new ArgumentException("Use the actual Automation authoring Session.");
        if (op.ManagementRead is { } managementRead && managementRead.SessionId != request.SessionId) throw new ArgumentException("Use the actual management read Session.");
        if (op.AutomationRead is { } automationRead && automationRead.SessionId != request.SessionId) throw new ArgumentException("Use the actual Automation read Session.");
        if (op.ExperienceOperation is { } experience && experience.SessionId != request.SessionId
            || op.ContinuityRead is { } continuity && continuity.SessionId != request.SessionId
            || op.MemoryOperation is { } memory && memory.SessionId != request.SessionId
            || op.AccountAction is { } accountAction && accountAction.SessionId != request.SessionId
            || op.AccountRead is { } accountRead && accountRead.SessionId != request.SessionId
            || op.CompletionRead is { } completion && completion.ParentSessionId != request.SessionId
            || op.BackgroundStart is { } background && (background.SessionId != request.SessionId || background.TenantId != request.TenantId)
            || op.AttachmentRead is { } attachment && attachment.SessionId != request.SessionId
            || op.WorkspaceMutation is { } mutation && mutation.SessionId != request.SessionId
            || op.WorkspaceRead is { } workspace && workspace.SessionId != request.SessionId
            || op.ArtifactVerification is { } verification && verification.SessionId != request.SessionId
            || request.RequestId == Guid.Empty || request.TenantId != context.TenantId || request.SessionId != run.SessionId
            || request.RootActivationId != context.RootActivationId || request.ExecutorPrincipalId != context.AgentPrincipalId
            || request.OriginalInitiatorPrincipalId != context.OriginalInitiatorPrincipalId || op.InstanceId != run.AgentInstanceId
            || target.TenantId != request.TenantId || target.Instance.InstanceId != op.InstanceId || target.PrincipalId != context.AgentPrincipalId
            || !Enum.IsDefined(request.Kind) || (request.Operation.ManagementRead is not null || request.Operation.AutomationRead is not null || request.Operation.ExperienceOperation is { ToolName: "experience.source" } || request.Operation.ContinuityRead is not null || (request.Operation.MemoryOperation is { } memoryOperation && AgentCore.Application.Tools.TenantMemoryToolContract.ReadOnly(memoryOperation.ToolName)) || request.Operation.ReportSourceRead is not null || request.Operation.CompletionRead is not null || request.Operation.AttachmentRead is not null || request.Operation.WorkspaceRead is not null || request.Operation.ArtifactVerification is not null || request.Operation.SandboxCommand is not null || request.Operation.SandboxControl is not null || request.Operation.SandboxRead is not null || request.Operation.CredentialRead is not null || request.Operation.BrowserCommand is { Credential: null }) && request.Kind != ApprovalKind.Review || !Enum.IsDefined(request.Status) || request.Revision < 1
            || request.CreatedAt.Offset != TimeSpan.Zero || request.ExpiresAt.Offset != TimeSpan.Zero
            || request.ExpiresAt <= request.CreatedAt || request.ExpiresAt - request.CreatedAt > TimeSpan.FromDays(1)
            || Rebuild(op) != op)
            throw new ArgumentException("Approval does not match its exact prepared operation and owning Run.");
        if (requireCurrentWitness && (target.Instance.Revision != op.ExpectedInstanceRevision || target.Instance.PersonaRevision != op.ExpectedPersonaRevision))
            throw new SecurityConflictException("operation_witness_changed");
        if (requireCurrentWitness && target.Instance.Lifecycle != AgentInstanceLifecycle.Active) throw new AccessDeniedException("executor_unavailable");
        TenantApprovalSnapshotRules.Shape(request);
        if (requireCurrentWitness) TenantApprovalSnapshotRules.RequireCurrent(request, target);
    }

    public static string DuplicateReason(TenantApprovalRequest request) => request.Status switch
    { TenantApprovalStatus.Pending => "already_pending", TenantApprovalStatus.Rejected => "previously_rejected", TenantApprovalStatus.Expired => "previously_expired",
        TenantApprovalStatus.Cancelled => "previously_cancelled", _ => "already_approved" };

    public static void ValidateDecision(TenantApprovalRequest before, TenantApprovalRequest next, long expectedRevision,
        TenantApprovalDecision decision, TenantOperationGrant? grant, ApprovalProcessedSignal signal, TenantSessionInitiator human, AuthorizationAudit audit)
    {
        if (before.Decide(decision.Status, expectedRevision, decision.DecidedAt) != next || decision.DecisionId == Guid.Empty
            || decision.RequestId != before.RequestId || decision.TenantId != before.TenantId || decision.HumanPrincipalId != human.PrincipalId
            || decision.Status is not (TenantApprovalStatus.Approved or TenantApprovalStatus.Rejected or TenantApprovalStatus.Cancelled) || decision.ActionHash != before.Operation.ActionHash
            || decision.RequestRevision != next.Revision || signal.SignalId == Guid.Empty || signal.TenantId != before.TenantId
            || signal.RequestId != before.RequestId || signal.RunId != before.RunId || signal.DecisionRevision != next.Revision
            || signal.Revision != 1 || signal.ProcessedAt is not null || signal.CreatedAt != decision.DecidedAt
            || audit.ActorPrincipalId != human.PrincipalId || audit.TenantId != before.TenantId || audit.Action != "approvals.decide" || audit.Target != before.RequestId.ToString("D"))
            throw new ArgumentException("Decision, exact operation, human and continuation signal must agree.");
        var needsGrant = next.Status == TenantApprovalStatus.Approved && before.Kind == ApprovalKind.ExceptionalGrant;
        if (needsGrant != (grant is not null) || grant is not null && (grant.GrantId == Guid.Empty || grant.RequestId != before.RequestId
            || grant.TenantId != before.TenantId || grant.ExecutorPrincipalId != before.ExecutorPrincipalId || grant.OperationId != before.Operation.OperationId
            || grant.ActionHash != before.Operation.ActionHash || grant.EffectIdempotencyKey != "operation:" + before.Operation.OperationId.ToString("D")
            || grant.Status != OperationGrantStatus.Available || grant.Revision != 1 || grant.ExpiresAt != before.ExpiresAt))
            throw new ArgumentException("Only an approved exact exceptional operation may receive its bound grant.");
    }

    public static bool TryApprovalId(AccessScope scope, out Guid id)
    {
        id = Guid.Empty;
        return scope.Kind == AccessScopeKind.Resource && scope.TenantId is { } tenant && tenant != Guid.Empty && scope.ResourceType == "approval"
            && Guid.TryParseExact(scope.ResourceId, "D", out id) && id != Guid.Empty && scope.ResourceId == id.ToString("D");
    }

    public static TenantApprovalRequest Expire(TenantApprovalRequest request, long expectedRevision, ApprovalProcessedSignal signal, DateTimeOffset at)
    {
        if (signal.SignalId == Guid.Empty || signal.TenantId != request.TenantId || signal.RequestId != request.RequestId || signal.RunId != request.RunId
            || signal.DecisionRevision != checked(expectedRevision + 1) || signal.Revision != 1 || signal.ProcessedAt is not null || signal.CreatedAt != at)
            throw new ArgumentException("Expiry continuation must name the exact request and Run.");
        return request.Decide(TenantApprovalStatus.Expired, expectedRevision, at);
    }
}
