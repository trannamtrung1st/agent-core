using System.Text.Json;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Application.Tools;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Definitions;
using AgentCore.Domain.Security;

namespace AgentCore.Infrastructure.Security;

public sealed partial class InMemoryIamStore
{
    public ValueTask<TenantResourcePinRetention?> ReadRetainedResourcePinAsync(TenantRunReference reference, EffectiveAgentResource resource, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested(); return ValueTask.FromResult(WithTenantEvents(() => {
            var run = sessionBacking.ReadRetainedTenantOperationRun(reference.TenantId, reference.RunId) ?? throw new AccessDeniedException("resource_source_changed");
            if (!HasResourcePinChanges(run)) return null;
            var reader = ManagementReader(run, null, ct);
            return RetainedResourcePin(ResourcePinInputs(reader, ct), resource);
        }));
    }
    private sealed record ResourcePinSource(TenantManagementReader Reader, TenantAgentConfigurationSnapshot Snapshot,
        IReadOnlyList<AgentDefinitionPublicationResource> Published, IReadOnlyList<TenantOperationReceipt> Receipts, IReadOnlyDictionary<Guid, TenantApprovalRequest> Requests);
    private bool HasResourcePinChanges(AgentRun run) => _operationReceipts.Values.Any(r => r.ManagementResource is not null && r.Outcome == TenantOperationOutcome.Completed
        && r.TenantId == run.Owner.TenantId && r.InstanceId == run.AgentInstanceId && r.SessionId == run.SessionId
        && r.RootActivationId == run.Admission.Activation.RootActivationId && r.InstanceRevision > run.Admission.Configuration!.InstanceRevision);
    private void RequireResourcePin(string key, long? revision, AgentRun run, TenantAgent agent, CancellationToken ct)
    {
        var inputs = HasResourcePinChanges(run) ? ResourcePinInputs(ManagementReader(run, null, ct), ct) : null;
        var pinned = run.Admission.Configuration!.Resources.SingleOrDefault(r => r.Key == key);
        TenantResourceRevisionRules.Require(key, revision, run, agent, inputs?.Snapshot.Resources ?? instances.ReadTenantResourcesAsync(agent.Instance.InstanceId, ct).Result,
            inputs is null || pinned is null ? null : RetainedResourcePin(inputs, pinned));
    }
    private ResourcePinSource ResourcePinInputs(TenantManagementReader reader, CancellationToken ct) => new(reader,
        ReadTenantAgentConfigurationAsync(reader.Security.Tenant.TenantId, reader.Run.AgentInstanceId, ct).Result ?? throw new AccessDeniedException("resource_source_changed"),
        ManagementPublishedResources(reader, ct), _operationReceipts.Values.Where(r => r.TenantId == reader.Security.Tenant.TenantId
            && r.InstanceId == reader.Run.AgentInstanceId && r.SessionId == reader.Run.SessionId
            && r.RootActivationId == reader.Root.Admission.Activation.RootActivationId).ToArray(), _approvals);
    private static TenantResourcePinRetention? RetainedResourcePin(ResourcePinSource source, EffectiveAgentResource resource) => TenantManagementReadRules.RetainedResourcePin(
        source.Reader, resource, source.Snapshot, source.Published, source.Receipts, id => source.Requests.GetValueOrDefault(id));
    private bool ManagementResourceVisible(TenantApprovalRequest request, TenantManagementApprovalVisibility? visibility,
        TenantWorkspaceReadApprovalVisibility? workspace, TenantAttachmentReadApprovalVisibility? attachment, TenantBrowserApprovalVisibility? browser, CancellationToken ct)
    {
        var material = request.Operation.ManagementMutation!.Resource!.Source;
        if (material is { Name: not "conversation:user" })
        {
            if (sessionBacking.ReadTenantSession(request.TenantId, request.SessionId) is not { Snapshot.DurablyDeletedAt: null }) return false;
            if (material.Workspace?.ResourceKey is { } key && !ChildCatalog(request.TenantId, request.Operation.InstanceId, null, ct)
                .Contains(TenantAgentChildScopes.Scope(request.TenantId, request.Operation.InstanceId, "resource", key))) return false;
            if (material.Attachment is { } input && attachmentBacking?.RetainedTenant(request.TenantId, input.SessionId, input.AttachmentId) is not { State: AttachmentState.Bound }) return false;
        }
        return TenantManagementResourceRules.Visible(request, visibility, instances.FindTenantBackingAsync(request.Operation.InstanceId, ct).Result?.Revision,
            _operationReceipts.GetValueOrDefault(request.Operation.OperationId), workspace, attachment, browser);
    }
    public ValueTask<TenantManagementResourceContext> ReadTenantManagementResourceContextAsync(TenantRunReference reference, Guid? generation,
        string tool, string arguments, Guid? inspectionHuman = null, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested(); return ValueTask.FromResult(WithTenantEvents(() => {
            var run = sessionBacking.ReadRetainedTenantOperationRun(reference.TenantId, reference.RunId) ?? throw new AccessDeniedException("management_unavailable");
            if (generation is { } claim) _ = RequireMemoryFrame(new(run.Owner, run.SessionId, run.AgentRunId, claim));
            var reader = ManagementReader(run, inspectionHuman, ct);
            if (inspectionHuman is { } human) ValidateAgentActor(reader.Security, human);
            return ManagementResourceContext(reader, TenantManagementMutationContract.Parse(tool, arguments), inspectionHuman, ct);
        }));
    }
    private IReadOnlyList<AgentDefinitionPublicationResource> ManagementPublishedResources(TenantManagementReader reader, CancellationToken ct)
    {
        var owner = reader.Agent.Instance;
        return Guid.TryParse(owner.DefinitionId, out var id) ? definitions.TenantPublicationResources(reader.Security.Tenant.TenantId, id, owner.ActiveVersion)
            : inheritedResources?.ListAsync(reader.Baseline, ct).GetAwaiter().GetResult() ?? [];
    }
    private TenantManagementResourceContext ManagementResourceContext(TenantManagementReader reader, TenantManagementMutationArguments args, Guid? human, CancellationToken ct)
    {
        TenantManagementMutationRules.Eligible(reader, args.Tool); var iam = new IamAuthorizer(this); var actor = human ?? reader.Agent.PrincipalId;
        TenantManagementResourceRules.RawAsync(reader, args, iam, actor, ct).GetAwaiter().GetResult();
        var snapshot = ReadTenantAgentConfigurationAsync(reader.Security.Tenant.TenantId, reader.Run.AgentInstanceId, ct).Result ?? throw new AccessDeniedException("resource_unavailable");
        var entries = args.Tool == "resources.upsert" && args.Value.GetProperty("source").GetString() == "conversation:user"
            ? reader.Root.Admission.Activation.SourceEntryIds.Select(id => sessionBacking.FindEntry(reader.Root.SessionId, id)
                ?? throw new AccessDeniedException("resource_source_unavailable")).ToArray() : [];
        var material = TenantManagementResourceSources.Material(reader, args, entries, id => _approvals.GetValueOrDefault(id));
        TenantManagementResourceSources.RawAsync(reader, material, actor, iam, ct).GetAwaiter().GetResult();
        if (material is { Name: not "conversation:user" })
        {
            TenantManagementResourceSources.RequireVirtualSource(reader, material);
            var request = material.ReadRequestId is { } id ? _approvals.GetValueOrDefault(id) : null;
            if (material.Workspace is { } workspace) RequireWorkspaceReadSource(reader.Security.Tenant.TenantId, workspace, reader.Run, reader.Agent);
            else if (material.Attachment is { } attachment) RequireAttachmentReadSource(reader.Security.Tenant.TenantId, attachment, reader.Run, reader.Agent);
            else if (request?.Operation.BrowserCommand is not null)
                RequireBrowserOperation(request ?? throw new AccessDeniedException("resource_source_unavailable"), reader.Run, reader.Agent, retainedObservation: true);
            else
            {
                var session = sessionBacking.ReadTenantSession(reader.Security.Tenant.TenantId, reader.Run.SessionId) ?? throw new AccessDeniedException("resource_unavailable");
                TenantBrowserOperationRules.RequireSource(reader.Run.SessionId, material.ToolName!, reader.Run, reader.Agent, reader.Baseline, session);
            }
            TenantManagementResourceSources.ConsentAsync(reader, material, request,
                request is null ? null : _approvalDecisions.GetValueOrDefault(request.RequestId),
                request is null ? null : _operationReceipts.GetValueOrDefault(request.Operation.OperationId),
                request is null ? null : _operationGrants.GetValueOrDefault(request.RequestId), iam, ct).GetAwaiter().GetResult();
        }
        if (human is null) RequireResourceTargetReadConsent(reader, args, iam, ct);
        return new(new(reader, snapshot), ManagementPublishedResources(reader, ct), material);
    }
    private void RequireResourceTargetReadConsent(TenantManagementReader reader, TenantManagementMutationArguments args, IamAuthorizer iam, CancellationToken ct)
    {
        var key = args.Value.TryGetProperty("key", out var target) ? target.GetString() : null;
        var need = TenantManagementReadRules.NeedsReviewOwnedAsync(reader, "agents.read", TenantManagementReadRules.Scope(reader), iam, ct).AsTask().GetAwaiter().GetResult();
        if (key is not null) need |= TenantManagementReadRules.NeedsReviewOwnedAsync(reader, "resources.read",
            TenantAgentChildScopes.Scope(reader.Security.Tenant.TenantId, reader.Run.AgentInstanceId, "resource", key), iam, ct).AsTask().GetAwaiter().GetResult();
        if (!need) return;
        var tool = key is null ? "management.inspect" : "resources.inspect"; var arguments = key is null ? "{}" : JsonSerializer.Serialize(new { key });
        if (!TenantManagementReadReferences.Read(reader.Run.Checkpoint).Any(source => source.ToolName == tool && source.ArgumentsJson == arguments && ManagementReadConsented(reader, source, ct)))
            throw new AccessDeniedException("resource_source_read_review_required");
    }
}
