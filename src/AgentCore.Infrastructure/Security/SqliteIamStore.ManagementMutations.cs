using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;
using AgentCore.Application.Tools;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using AgentCore.Infrastructure.Persistence;
namespace AgentCore.Infrastructure.Security;
public sealed partial class SqliteIamStore
{
    public async ValueTask RequireTenantManagementSkillSourcesAsync(TenantRunReference reference, Guid? generation, string tool, string arguments, Guid? inspectionHuman = null, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        var row = await OwnedRuns(db, reference.TenantId).AsNoTracking().SingleOrDefaultAsync(r => r.AgentRunId == reference.RunId.ToString("D"), ct).ConfigureAwait(false)
            ?? throw new AccessDeniedException("management_unavailable");
        var run = AgentRunStoreMapping.ToDomain(row);
        if (generation is { } claim) _ = await RequireRuntimeFrame(db, new(run.Owner, run.SessionId, run.AgentRunId, claim), ct).ConfigureAwait(false);
        var reader = await ManagementReader(db, run, inspectionHuman, ct).ConfigureAwait(false);
        await RequireManagementSkillSources(db, reader, TenantManagementMutationContract.Parse(tool, arguments), inspectionHuman, ct).ConfigureAwait(false);
    }
    private async ValueTask RequireManagementSkillSources(AgentCoreDbContext db, TenantManagementReader reader, TenantManagementMutationArguments args, Guid? human, CancellationToken ct)
    {
        var iam = new IamAuthorizer(this);
        await TenantManagementSkillRules.RawAsync(reader, args, iam, human ?? reader.Agent.PrincipalId, ct).ConfigureAwait(false);
        if (human is not null) return;
        var key = args.Value.TryGetProperty("key", out var selection) ? selection.GetString() : null;
        var need = await TenantManagementReadRules.NeedsReviewOwnedAsync(reader,
            "agents.read", TenantManagementReadRules.Scope(reader), iam, ct).ConfigureAwait(false);
        if (key is not null) need |= await TenantManagementReadRules.NeedsReviewOwnedAsync(reader,
            "skills.read", TenantAgentChildScopes.Scope(reader.Security.Tenant.TenantId, reader.Agent.Instance.InstanceId, "skill", key), iam, ct).ConfigureAwait(false);
        if (!need) return;
        var observed = await ObserveManagementRead(db, reader.Run, key is null ? "management.inspect" : "skills.inspect", key is null ? "{}" : JsonSerializer.Serialize(new { key }), null, null, ct).ConfigureAwait(false);
        if (!await ManagementReadConsented(db, reader, TenantManagementReadService.Reference(observed.Value, reader.Security.Tenant.TenantId), ct).ConfigureAwait(false))
            throw new AccessDeniedException("skill_source_read_review_required");
    }
    private async ValueTask<(TenantManagementReader Reader, TenantAgentConfigurationChange Change)> RequireManagementMutation(AgentCoreDbContext db, TenantApprovalRequest request, AgentRun run, CancellationToken ct, bool requireStagedContent = false)
    {
        var value = request.Operation.ManagementMutation ?? throw new AccessDeniedException("management_unavailable");
        var reader = await ManagementReader(db, run, null, ct).ConfigureAwait(false); var iam = new IamAuthorizer(this);
        await TenantManagementReadRules.RawAsync(reader, iam, ct).ConfigureAwait(false);
        await TenantManagementMutationRules.SecondaryAsync(reader, value, iam, reader.Agent.PrincipalId, ct).ConfigureAwait(false);
        await TenantManagementMutationRules.PrimaryAsync(request, reader, iam, ct).ConfigureAwait(false);
        if (value.Resource is { } resource)
        {
            var context = await ManagementResourceContext(db, reader, TenantManagementMutationContract.Parse(value.Harness?.ToolName ?? value.ToolName, value.Harness?.ArgumentsJson ?? value.ArgumentsJson), null, ct).ConfigureAwait(false);
            if (JsonSerializer.Serialize(context.Material) != JsonSerializer.Serialize(resource.Source)) throw new AccessDeniedException("resource_source_changed");
            byte[]? bytes = null;
            if (resource.InstanceResource is { } binding && (value.ToolName == "resources.copy" || requireStagedContent))
            {
                try { bytes = await AgentCore.Application.Admin.DefinitionResourceStoredContent.RequireForBindingAsync(
                    definitionContent ?? throw new AccessDeniedException("resource_content_unavailable"), binding.ContentSha256, binding.ByteLength, binding.MediaType, ct).ConfigureAwait(false); }
                catch (AgentCore.Application.Sessions.AgentCoreException) { throw new AccessDeniedException("resource_content_unavailable"); }
            }
            return (reader, TenantManagementMutationRules.Change(reader, value, context.Source.Snapshot, context.Published, value.ToolName == "resources.copy" ? bytes : null));
        }
        TenantAgentConfigurationSnapshot? snapshot = null;
        if (value.Skill is not null || value.Harness is not null)
        {
            if (value.Skill is not null) await RequireManagementSkillSources(db, reader, TenantManagementMutationContract.Parse(value.Harness?.ToolName ?? value.ToolName, value.Harness?.ArgumentsJson ?? value.ArgumentsJson), null, ct).ConfigureAwait(false);
            snapshot = new(reader.Agent, await SqliteAgentInstanceStore.ReadSkillsAsync(db, run.AgentInstanceId, ct).ConfigureAwait(false),
                await SqliteAgentInstanceStore.ReadResourcesAsync(db, run.AgentInstanceId, ct).ConfigureAwait(false), await ReadIsolationMetadataAsync(db, run.AgentInstanceId.ToString("D"), ct).ConfigureAwait(false));
        }
        return (reader, TenantManagementMutationRules.Change(reader, value, snapshot));
    }
}
