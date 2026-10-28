using System.Text.Json;

namespace AgentCore.Domain.Workflows;

public sealed record WorkflowPublicationIntent(Guid TenantId, Guid WorkflowId, Guid PublisherId, string RequestKey,
    string Fingerprint, long DraftRevision, WorkflowVersion Version);

public sealed record WorkflowDefinition(Guid TenantId, Guid WorkflowId, string Name, string Description,
    string DraftJson, long Revision, int PublishedVersion, bool Retired, Guid CreatedBy, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);
public sealed record WorkflowVersion(Guid TenantId, Guid WorkflowId, int Version, PublishedWorkflowGraph Graph,
    Guid PublishedBy, DateTimeOffset PublishedAt);
public sealed record WorkflowCatalogReceipt(Guid TenantId, Guid ActorId, string RequestKey, string Fingerprint,
    string Action, WorkflowDefinition Definition, WorkflowVersion? Version, long SecurityRevision);

public static class WorkflowCatalogRules
{
    public static string Draft(WorkflowGraph graph)
    {
        var json = JsonSerializer.Serialize(graph, WorkflowGraphRules.Json);
        _ = WorkflowGraphRules.Parse(json); // Closed shape and byte/depth bounds; incomplete drafts may be saved.
        return json;
    }
    public static void Validate(WorkflowDefinition value)
    {
        if (!WorkflowGraphRules.Id(value.TenantId) || !WorkflowGraphRules.Id(value.WorkflowId) || !WorkflowGraphRules.Id(value.CreatedBy)
            || !WorkflowGraphRules.Text(value.Name, 80) || value.Description is null || value.Description.Length > 1000
            || value.Description.Length != 0 && !WorkflowGraphRules.Text(value.Description, 1000)
            || value.Revision < 1 || value.PublishedVersion < 0 || value.CreatedAt.Offset != TimeSpan.Zero || value.UpdatedAt.Offset != TimeSpan.Zero
            || value.UpdatedAt < value.CreatedAt) throw new ArgumentException("Invalid workflow catalog metadata.");
        _ = WorkflowGraphRules.Parse(value.DraftJson);
    }
    public static void Change(WorkflowDefinition? before, WorkflowDefinition next, WorkflowVersion? version,
        long expectedRevision, string action, Guid actor)
    {
        Validate(next);
        if ((before?.Revision ?? 0) != expectedRevision || next.Revision != expectedRevision + 1) throw new ArgumentException("Invalid workflow revision.");
        if (before is not null && (before.TenantId != next.TenantId || before.WorkflowId != next.WorkflowId
            || before.CreatedBy != next.CreatedBy || before.CreatedAt != next.CreatedAt || next.UpdatedAt < before.UpdatedAt))
            throw new ArgumentException("Workflow identity is immutable.");
        if (action == "workflows.create")
        {
            if (before is not null || next.PublishedVersion != 0 || next.Retired || version is not null || next.CreatedBy != actor)
                throw new ArgumentException("Invalid new workflow.");
        }
        else if (action == "workflows.update")
        {
            if (before is null || next.PublishedVersion != before.PublishedVersion || version is not null)
                throw new ArgumentException("Draft editing/retirement cannot publish versions.");
        }
        else if (action == "workflows.publish")
        {
            if (before is null || before.Retired || next.Retired || version is null || next with { Revision = before.Revision, PublishedVersion = before.PublishedVersion, UpdatedAt = before.UpdatedAt } != before
                || version.TenantId != next.TenantId || version.WorkflowId != next.WorkflowId || version.Version != before.PublishedVersion + 1
                || next.PublishedVersion != version.Version || version.PublishedBy != actor || version.PublishedAt != next.UpdatedAt
                || WorkflowGraphRules.ValidateAndFreeze(WorkflowGraphRules.Parse(before.DraftJson)) != version.Graph)
                throw new ArgumentException("Publication must pin the exact saved draft and next immutable version.");
        }
        else throw new ArgumentException("Unknown workflow catalog action.");
    }
    public static string RequestKey(string value)
    {
        if (!WorkflowGraphRules.Text(value, 128)) throw new ArgumentException("Use a bounded stable request key.");
        return value;
    }
}
