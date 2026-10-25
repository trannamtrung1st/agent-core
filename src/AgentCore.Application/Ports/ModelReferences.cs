namespace AgentCore.Application.Ports;

public enum ModelReferenceKind { Default, Definitions, Instances, Automations, Sessions, Runs }
public sealed record ModelReferenceItem(Guid ResourceId, string ReferenceId, Guid OfferingId, string Label, string Usage, long Revision, string State);
public sealed record ModelReferencePage(IReadOnlyList<ModelReferenceItem> Items, int Total, int Page, int PageSize, long SecurityRevision);
// Null permits the whole tenant; an empty set permits no resources. Filters precede counts/paging.
public sealed record ModelReferenceVisibility(IReadOnlyList<Guid>? Resources, IReadOnlyList<Guid>? Agents, IReadOnlyList<Guid>? Sessions,
    IReadOnlyList<Guid>? Offerings);
public sealed record ModelReferenceQuery(Guid TenantId, Guid? OfferingId, Guid? ConnectionId, ModelReferenceKind Kind,
    int Page, int PageSize, ModelReferenceVisibility Visible);
