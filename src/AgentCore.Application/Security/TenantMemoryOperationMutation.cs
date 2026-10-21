using System.Text.Json;
using AgentCore.Application.Ports;
using AgentCore.Application.Tools;
using AgentCore.Domain.Memory;

namespace AgentCore.Application.Security;

public static class TenantMemoryOperationMutation
{
    public static IReadOnlyList<TenantLearnedMemory> Values(TenantMemoryOperationObservation observed, DateTimeOffset at)
    {
        at = DateTimeOffset.FromUnixTimeMilliseconds(at.ToUnixTimeMilliseconds());
        var args = observed.Arguments; var reader = observed.Reader;
        if (args.ReadOnly) return [];
        if (observed.ExistingResult is not null) throw new SecurityConflictException("memory_source_changed");
        if (args.Tool == ToolCatalog.MemoryForget) return [TenantMemoryManagementRules.Tombstone(observed.Items.Single(), at)];
        var first = observed.Items[0]; var subject = StructuredMemoryItem.CollapseSubject(args.Subject!); var content = args.Content!.Trim();
        var result = new TenantLearnedMemory(reader.Security.Tenant.TenantId, observed.Value.ResultMemoryId!.Value, first.Scope,
            reader.User!.PrincipalId, reader.User.AccountId, reader.User.UserId, reader.Run.AgentInstanceId, reader.Agent.PrincipalId,
            reader.Run.SessionId, reader.Run.AgentRunId, reader.Root.AgentRunId, reader.Root.Admission.Activation.SourceEntryIds.First(),
            first.Kind, MemoryItemStatus.Active, subject, content, StructuredMemoryItem.SubjectKeyFor(subject),
            observed.Items.Any(i => i.Source == "userExplicit") ? "userExplicit" : "agentInferred", TenantMemoryRules.Hash(first.Kind, subject, content), 1, at, at,
            SourceMemoryIdsJson: JsonSerializer.Serialize(observed.Items.Select(i => i.MemoryId).Order().ToArray()));
        return Array.AsReadOnly(observed.Items.Select(i => i with { Status = MemoryItemStatus.Superseded, Revision = checked(i.Revision + 1), UpdatedAt = at > i.UpdatedAt ? at : i.UpdatedAt }).Append(result).ToArray());
    }
    public static IReadOnlyList<Guid> Sources(TenantLearnedMemory item) => item.SourceMemoryIdsJson is null ? item.SupersedesMemoryId is { } source ? [source] : []
        : JsonSerializer.Deserialize<Guid[]>(item.SourceMemoryIdsJson) ?? throw new InvalidOperationException("Invalid retained Memory lineage.");
}
