using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;
using AgentCore.Application.Tools;
using System.Text.Json;
namespace AgentCore.Infrastructure.Security;
public sealed partial class InMemoryIamStore
{
    public ValueTask RequireTenantManagementSkillSourcesAsync(TenantRunReference reference, Guid? generation, string tool, string arguments, Guid? inspectionHuman = null, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested(); WithTenantEvents(() => {
            var source = ReadTenantManagementSourceAsync(reference, generation, inspectionHuman, ct).Result;
            RequireManagementSkillSources(source.Reader, TenantManagementMutationContract.Parse(tool, arguments), inspectionHuman, ct); return true;
        }); return ValueTask.CompletedTask;
    }
    private void RequireManagementSkillSources(TenantManagementReader reader, TenantManagementMutationArguments args, Guid? human, CancellationToken ct)
    {
        var iam = new IamAuthorizer(this);
        TenantManagementSkillRules.RawAsync(reader, args, iam, human ?? reader.Agent.PrincipalId, ct).AsTask().GetAwaiter().GetResult();
        if (human is not null) return;
        var key = args.Value.TryGetProperty("key", out var selection) ? selection.GetString() : null;
        var need = TenantManagementReadRules.NeedsReviewOwnedAsync(reader,
            "agents.read", TenantManagementReadRules.Scope(reader), iam, ct).AsTask().GetAwaiter().GetResult();
        if (key is not null) need |= TenantManagementReadRules.NeedsReviewOwnedAsync(reader,
            "skills.read", TenantAgentChildScopes.Scope(reader.Security.Tenant.TenantId, reader.Agent.Instance.InstanceId, "skill", key), iam, ct).AsTask().GetAwaiter().GetResult();
        if (!need) return;
        var observed = ObserveManagementRead(reader.Run, key is null ? "management.inspect" : "skills.inspect", key is null ? "{}" : JsonSerializer.Serialize(new { key }), null, null, ct);
        if (!ManagementReadConsented(reader, TenantManagementReadService.Reference(observed.Value, reader.Security.Tenant.TenantId), ct))
            throw new AccessDeniedException("skill_source_read_review_required");
    }
    private (TenantManagementReader Reader, TenantAgentConfigurationChange Change) RequireManagementMutation(TenantApprovalRequest request, AgentRun run, CancellationToken ct, bool requireStagedContent = false)
    {
        var value = request.Operation.ManagementMutation ?? throw new AccessDeniedException("management_unavailable");
        var reader = ManagementReader(run, null, ct); var iam = new IamAuthorizer(this);
        TenantManagementReadRules.RawAsync(reader, iam, ct).AsTask().GetAwaiter().GetResult();
        TenantManagementMutationRules.SecondaryAsync(reader, value, iam, reader.Agent.PrincipalId, ct).AsTask().GetAwaiter().GetResult();
        TenantManagementMutationRules.PrimaryAsync(request, reader, iam, ct).AsTask().GetAwaiter().GetResult();
        RequireManagementRemedy(reader, value, ct);
        if (value.Binding is not null)
        {
            var context = ManagementBindingContext(new(reader, ReadTenantAgentConfigurationAsync(request.TenantId, run.AgentInstanceId, ct).Result ?? throw new AccessDeniedException("management_unavailable")),
                TenantManagementMutationContract.Parse(value.ToolName, value.ArgumentsJson), null, ct);
            return (reader, TenantManagementMutationRules.Change(reader, value, bindingContext: context));
        }
        if (value.Resource is { } resource)
        {
            var context = ManagementResourceContext(reader, TenantManagementMutationContract.Parse(value.Harness?.ToolName ?? value.ToolName, value.Harness?.ArgumentsJson ?? value.ArgumentsJson), null, ct);
            if (JsonSerializer.Serialize(context.Material) != JsonSerializer.Serialize(resource.Source)) throw new AccessDeniedException("resource_source_changed");
            byte[]? bytes = null;
            if (resource.InstanceResource is { } binding && (value.ToolName == "resources.copy" || requireStagedContent))
            {
                try { bytes = AgentCore.Application.Admin.DefinitionResourceStoredContent.RequireForBindingAsync(
                    definitions.ResourceStore?.TenantContent ?? throw new AccessDeniedException("resource_content_unavailable"),
                    binding.ContentSha256, binding.ByteLength, binding.MediaType, ct).GetAwaiter().GetResult(); }
                catch (AgentCore.Application.Sessions.AgentCoreException) { throw new AccessDeniedException("resource_content_unavailable"); }
            }
            return (reader, TenantManagementMutationRules.Change(reader, value, context.Source.Snapshot, context.Published, value.ToolName == "resources.copy" ? bytes : null));
        }
        if (value.Skill is not null) RequireManagementSkillSources(reader, TenantManagementMutationContract.Parse(value.Harness?.ToolName ?? value.ToolName, value.Harness?.ArgumentsJson ?? value.ArgumentsJson), null, ct);
        return (reader, TenantManagementMutationRules.Change(reader, value, value.Skill is null && value.Harness is null ? null : ReadTenantAgentConfigurationAsync(request.TenantId, run.AgentInstanceId, ct).Result));
    }
}
