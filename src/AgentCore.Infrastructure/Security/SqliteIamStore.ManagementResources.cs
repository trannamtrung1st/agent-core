using System.Text.Json;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Application.Tools;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Definitions;
using AgentCore.Domain.Security;
using AgentCore.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AgentCore.Infrastructure.Security;

public sealed partial class SqliteIamStore
{
    public async ValueTask<TenantResourcePinRetention?> ReadRetainedResourcePinAsync(TenantRunReference reference, EffectiveAgentResource resource, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct).ConfigureAwait(false);
        var row = await OwnedRuns(db, reference.TenantId).AsNoTracking().SingleOrDefaultAsync(r => r.AgentRunId == reference.RunId.ToString("D"), ct).ConfigureAwait(false)
            ?? throw new AccessDeniedException("resource_source_changed");
        var run = AgentRunStoreMapping.ToDomain(row); if (!await HasResourcePinChanges(db, run, ct).ConfigureAwait(false)) return null;
        return RetainedResourcePin(await ResourcePinInputs(db, await ManagementReader(db, run, null, ct).ConfigureAwait(false), ct).ConfigureAwait(false), resource);
    }
    private sealed record ResourcePinSource(TenantManagementReader Reader, TenantAgentConfigurationSnapshot Snapshot,
        IReadOnlyList<AgentDefinitionPublicationResource> Published, IReadOnlyList<TenantOperationReceipt> Receipts, IReadOnlyDictionary<Guid, TenantApprovalRequest> Requests);
    private static Task<bool> HasResourcePinChanges(AgentCoreDbContext db, AgentRun run, CancellationToken ct) => db.Set<TenantOperationReceiptRecord>().AsNoTracking().AnyAsync(r =>
        r.ManagementResourceJson != null && r.Outcome == (int)TenantOperationOutcome.Completed && r.TenantId == run.Owner.TenantId
        && r.InstanceId == run.AgentInstanceId.ToString("D") && r.SessionId == run.SessionId.ToString("D")
        && r.RootActivationId == run.Admission.Activation.RootActivationId.ToString("D") && r.InstanceRevision > run.Admission.Configuration!.InstanceRevision, ct);
    private async ValueTask RequireResourcePin(AgentCoreDbContext db, string key, long? revision, AgentRun run, TenantAgent agent, CancellationToken ct)
    {
        var inputs = await HasResourcePinChanges(db, run, ct).ConfigureAwait(false) ? await ResourcePinInputs(db, await ManagementReader(db, run, null, ct).ConfigureAwait(false), ct).ConfigureAwait(false) : null;
        var pinned = run.Admission.Configuration!.Resources.SingleOrDefault(r => r.Key == key);
        TenantResourceRevisionRules.Require(key, revision, run, agent, inputs?.Snapshot.Resources ?? await SqliteAgentInstanceStore.ReadResourcesAsync(db, agent.Instance.InstanceId, ct).ConfigureAwait(false),
            inputs is null || pinned is null ? null : RetainedResourcePin(inputs, pinned));
    }
    private static TenantResourcePinRetention? RetainedResourcePin(ResourcePinSource source, EffectiveAgentResource resource) => TenantManagementReadRules.RetainedResourcePin(
        source.Reader, resource, source.Snapshot, source.Published, source.Receipts, id => source.Requests.GetValueOrDefault(id));
    private async ValueTask<ResourcePinSource> ResourcePinInputs(AgentCoreDbContext db, TenantManagementReader reader, CancellationToken ct)
    {
        var tenant = reader.Security.Tenant.TenantId; var run = reader.Run; var root = run.Admission.Activation.RootActivationId.ToString("D");
        var receipts = (await db.Set<TenantOperationReceiptRecord>().AsNoTracking().Where(r => r.TenantId == tenant && r.RootActivationId == root
            && r.InstanceId == run.AgentInstanceId.ToString("D") && r.SessionId == run.SessionId.ToString("D")).ToArrayAsync(ct).ConfigureAwait(false)).Select(r => r.Domain()).ToArray();
        var requests = (await db.Set<TenantApprovalRecord>().AsNoTracking().Where(r => r.TenantId == tenant && r.RootActivationId == root
            && r.InstanceId == run.AgentInstanceId.ToString("D") && r.SessionId == run.SessionId.ToString("D")).ToArrayAsync(ct).ConfigureAwait(false)).Select(r => r.Domain()).ToDictionary(r => r.RequestId);
        var snapshot = new TenantAgentConfigurationSnapshot(reader.Agent, await SqliteAgentInstanceStore.ReadSkillsAsync(db, run.AgentInstanceId, ct).ConfigureAwait(false),
            await SqliteAgentInstanceStore.ReadResourcesAsync(db, run.AgentInstanceId, ct).ConfigureAwait(false), await ReadIsolationMetadataAsync(db, run.AgentInstanceId.ToString("D"), ct).ConfigureAwait(false));
        return new(reader, snapshot, await ManagementPublishedResources(db, reader, ct).ConfigureAwait(false), receipts, requests);
    }
    public async ValueTask<TenantManagementResourceContext> ReadTenantManagementResourceContextAsync(TenantRunReference reference, Guid? generation,
        string tool, string arguments, Guid? inspectionHuman = null, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct).ConfigureAwait(false);
        await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        var row = await OwnedRuns(db, reference.TenantId).AsNoTracking().SingleOrDefaultAsync(r => r.AgentRunId == reference.RunId.ToString("D"), ct).ConfigureAwait(false)
            ?? throw new AccessDeniedException("management_unavailable"); var run = AgentRunStoreMapping.ToDomain(row);
        if (generation is { } claim) _ = await RequireRuntimeFrame(db, new(run.Owner, run.SessionId, run.AgentRunId, claim), ct).ConfigureAwait(false);
        var reader = await ManagementReader(db, run, inspectionHuman, ct).ConfigureAwait(false);
        if (inspectionHuman is { } human) await ValidateTenantActorAsync(db, reader.Security, human, ct).ConfigureAwait(false);
        return await ManagementResourceContext(db, reader, TenantManagementMutationContract.Parse(tool, arguments), inspectionHuman, ct).ConfigureAwait(false);
    }
    private async ValueTask<IReadOnlyList<AgentDefinitionPublicationResource>> ManagementPublishedResources(AgentCoreDbContext db, TenantManagementReader reader, CancellationToken ct)
    {
        var tenant = reader.Security.Tenant.TenantId; var owner = reader.Agent.Instance;
        if (!Guid.TryParse(owner.DefinitionId, out _)) return inheritedResources is null ? [] : await inheritedResources.ListAsync(reader.Baseline, ct).ConfigureAwait(false);
        var rows = await db.AgentDefinitionPublicationResources.IgnoreQueryFilters().AsNoTracking().Where(r => r.DefinitionId == owner.DefinitionId && r.Version == owner.ActiveVersion
            && db.Set<TenantDefinitionRecord>().Any(d => d.TenantId == tenant && d.DeletedAt == null && d.DefinitionId == r.DefinitionId)).ToArrayAsync(ct).ConfigureAwait(false);
        return rows.Select(DefinitionResourcePersistence.MapPublicationResource).ToArray();
    }
    private async ValueTask<TenantManagementResourceContext> ManagementResourceContext(AgentCoreDbContext db, TenantManagementReader reader,
        TenantManagementMutationArguments args, Guid? human, CancellationToken ct)
    {
        TenantManagementMutationRules.Eligible(reader, args.Tool); var iam = new IamAuthorizer(this); var actor = human ?? reader.Agent.PrincipalId;
        await TenantManagementResourceRules.RawAsync(reader, args, iam, actor, ct).ConfigureAwait(false);
        var tenant = reader.Security.Tenant.TenantId; var run = reader.Run;
        var snapshot = new TenantAgentConfigurationSnapshot(reader.Agent, await SqliteAgentInstanceStore.ReadSkillsAsync(db, run.AgentInstanceId, ct).ConfigureAwait(false),
            await SqliteAgentInstanceStore.ReadResourcesAsync(db, run.AgentInstanceId, ct).ConfigureAwait(false), await ReadIsolationMetadataAsync(db, run.AgentInstanceId.ToString("D"), ct).ConfigureAwait(false));
        var ids = reader.Root.Admission.Activation.SourceEntryIds.Select(id => id.ToString("D")).ToArray();
        var entries = args.Tool == "resources.upsert" && args.Value.GetProperty("source").GetString() == "conversation:user"
            ? (await db.Entries.AsNoTracking().Where(e => e.SessionId == reader.Root.SessionId.ToString("D") && ids.Contains(e.EntryId)).ToArrayAsync(ct).ConfigureAwait(false)).Select(SqliteMemoryStore.ToEntry).ToArray() : [];
        var root = reader.Root.Admission.Activation.RootActivationId.ToString("D");
        var requests = (await db.Set<TenantApprovalRecord>().AsNoTracking().Where(r => r.TenantId == tenant && r.RootActivationId == root && r.SessionId == run.SessionId.ToString("D"))
            .ToArrayAsync(ct).ConfigureAwait(false)).Select(r => r.Domain()).ToDictionary(r => r.RequestId);
        var material = TenantManagementResourceSources.Material(reader, args, entries, id => requests.GetValueOrDefault(id));
        await TenantManagementResourceSources.RawAsync(reader, material, actor, iam, ct).ConfigureAwait(false);
        if (material is { Name: not "conversation:user" })
        {
            TenantManagementResourceSources.RequireVirtualSource(reader, material);
            var request = material.ReadRequestId is { } id ? requests.GetValueOrDefault(id) : null;
            if (material.Workspace is { } workspace) await RequireWorkspaceReadSource(db, tenant, workspace, run, reader.Agent, ct).ConfigureAwait(false);
            else if (material.Attachment is { } attachment) await RequireAttachmentReadSource(db, tenant, attachment, run, reader.Agent, ct).ConfigureAwait(false);
            else if (request?.Operation.BrowserCommand is not null)
                await RequireBrowserOperation(db, request ?? throw new AccessDeniedException("resource_source_unavailable"), run, reader.Agent, ct, retainedObservation: true).ConfigureAwait(false);
            else
            {
                var session = await ReadSandboxSession(db, tenant, run.SessionId, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("resource_unavailable");
                TenantBrowserOperationRules.RequireSource(run.SessionId, material.ToolName!, run, reader.Agent, reader.Baseline, session);
            }
            var decision = request is null ? null : await db.Set<TenantApprovalDecision>().AsNoTracking().SingleOrDefaultAsync(d => d.TenantId == tenant && d.RequestId == request.RequestId, ct).ConfigureAwait(false);
            var receipt = request is null ? null : (await db.Set<TenantOperationReceiptRecord>().AsNoTracking().SingleOrDefaultAsync(r => r.TenantId == tenant && r.OperationId == request.Operation.OperationId, ct).ConfigureAwait(false))?.Domain();
            var grant = request is null ? null : await db.Set<TenantOperationGrant>().AsNoTracking().SingleOrDefaultAsync(g => g.TenantId == tenant && g.RequestId == request.RequestId, ct).ConfigureAwait(false);
            await TenantManagementResourceSources.ConsentAsync(reader, material, request, decision, receipt, grant, iam, ct).ConfigureAwait(false);
        }
        if (human is null) await RequireResourceTargetReadConsent(db, reader, args, iam, ct).ConfigureAwait(false);
        return new(new(reader, snapshot), await ManagementPublishedResources(db, reader, ct).ConfigureAwait(false), material);
    }
    private async ValueTask RequireResourceTargetReadConsent(AgentCoreDbContext db, TenantManagementReader reader, TenantManagementMutationArguments args, IamAuthorizer iam, CancellationToken ct)
    {
        var key = args.Value.TryGetProperty("key", out var target) ? target.GetString() : null;
        var need = await TenantManagementReadRules.NeedsReviewOwnedAsync(reader, "agents.read", TenantManagementReadRules.Scope(reader), iam, ct).ConfigureAwait(false);
        if (key is not null) need |= await TenantManagementReadRules.NeedsReviewOwnedAsync(reader, "resources.read",
            TenantAgentChildScopes.Scope(reader.Security.Tenant.TenantId, reader.Run.AgentInstanceId, "resource", key), iam, ct).ConfigureAwait(false);
        if (!need) return;
        var tool = key is null ? "management.inspect" : "resources.inspect"; var arguments = key is null ? "{}" : JsonSerializer.Serialize(new { key });
        foreach (var source in TenantManagementReadReferences.Read(reader.Run.Checkpoint).Where(source => source.ToolName == tool && source.ArgumentsJson == arguments))
            if (await ManagementReadConsented(db, reader, source, ct).ConfigureAwait(false)) return;
        throw new AccessDeniedException("resource_source_read_review_required");
    }
}
