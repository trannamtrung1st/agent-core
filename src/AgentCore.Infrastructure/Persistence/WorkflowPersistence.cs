using System.Text.Json;
using AgentCore.Domain.Security;
using AgentCore.Domain.Workflows;
using Microsoft.EntityFrameworkCore;

namespace AgentCore.Infrastructure.Persistence;

public sealed class WorkflowDefinitionRow
{
    public Guid TenantId { get; set; }
    public Guid WorkflowId { get; set; }
    public long Revision { get; set; }
    public string Json { get; set; } = "";
}
public sealed class WorkflowVersionRow
{
    public Guid TenantId { get; set; }
    public Guid WorkflowId { get; set; }
    public int Version { get; set; }
    public string Json { get; set; } = "";
}
public sealed class WorkflowCatalogReceiptRow
{
    public Guid TenantId { get; set; }
    public Guid ActorId { get; set; }
    public string RequestKey { get; set; } = "";
    public Guid WorkflowId { get; set; }
    public string Json { get; set; } = "";
}
public sealed class WorkflowPublicationRow
{
    public Guid TenantId { get; set; }
    public Guid ActorId { get; set; }
    public string RequestKey { get; set; } = "";
    public Guid WorkflowId { get; set; }
    public string Json { get; set; } = "";
}
public sealed class WorkflowExecutionRow
{
    public Guid TenantId { get; set; }
    public Guid ExecutionId { get; set; }
    public Guid WorkflowId { get; set; }
    public int Version { get; set; }
    public string SourceScope { get; set; } = "";
    public string RequestKey { get; set; } = "";
    public long Revision { get; set; }
    public int Status { get; set; }
    public long CreatedAt { get; set; }
    public string Json { get; set; } = "";
}
public sealed class WorkflowDecisionRow
{
    public Guid TenantId { get; set; }
    public Guid ExecutionId { get; set; }
    public string NodeId { get; set; } = "";
    public Guid ActorId { get; set; }
    public string RequestKey { get; set; } = "";
    public string Json { get; set; } = "";
}
public sealed class WorkflowStepRow
{
    public Guid TenantId { get; set; }
    public Guid ExecutionId { get; set; }
    public string NodeId { get; set; } = "";
    public Guid OperationId { get; set; }
    public long Revision { get; set; }
    public string Json { get; set; } = "";
}
internal static class WorkflowPersistence
{
    internal static T Read<T>(string json) => JsonSerializer.Deserialize<T>(json)!;
    internal static string Write<T>(T value) => JsonSerializer.Serialize(value);
    internal static void Configure(ModelBuilder model)
    {
        model.Entity<WorkflowDefinitionRow>(e => {
            e.ToTable("TenantWorkflows"); e.HasKey(r => new { r.TenantId, r.WorkflowId });
            e.Property(r => r.Revision).IsConcurrencyToken();
            e.HasOne<Tenant>().WithMany().HasForeignKey(r => r.TenantId).OnDelete(DeleteBehavior.Restrict);
        });
        model.Entity<WorkflowVersionRow>(e => {
            e.ToTable("TenantWorkflowVersions"); e.HasKey(r => new { r.TenantId, r.WorkflowId, r.Version });
            e.HasOne<WorkflowDefinitionRow>().WithMany().HasForeignKey(r => new { r.TenantId, r.WorkflowId }).OnDelete(DeleteBehavior.Restrict);
        });
        model.Entity<WorkflowCatalogReceiptRow>(e => {
            e.ToTable("TenantWorkflowCatalogReceipts"); e.HasKey(r => new { r.TenantId, r.ActorId, r.RequestKey });
            e.HasIndex(r => new { r.TenantId, r.WorkflowId });
            e.HasOne<WorkflowDefinitionRow>().WithMany().HasForeignKey(r => new { r.TenantId, r.WorkflowId }).OnDelete(DeleteBehavior.Restrict);
        });
        model.Entity<WorkflowPublicationRow>(e => {
            e.ToTable("TenantWorkflowPublicationIntents"); e.HasKey(r => new { r.TenantId, r.ActorId, r.RequestKey });
            e.HasOne<WorkflowDefinitionRow>().WithMany().HasForeignKey(r => new { r.TenantId, r.WorkflowId }).OnDelete(DeleteBehavior.Restrict);
        });
        model.Entity<WorkflowExecutionRow>(e => {
            e.ToTable("TenantWorkflowExecutions"); e.HasKey(r => new { r.TenantId, r.ExecutionId });
            e.Property(r => r.Revision).IsConcurrencyToken();
            e.HasIndex(r => new { r.TenantId, r.SourceScope, r.RequestKey }).IsUnique();
            e.HasIndex(r => new { r.Status, r.CreatedAt, r.ExecutionId });
            e.HasIndex(r => new { r.TenantId, r.WorkflowId, r.ExecutionId });
            e.HasOne<WorkflowVersionRow>().WithMany().HasForeignKey(r => new { r.TenantId, r.WorkflowId, r.Version }).OnDelete(DeleteBehavior.Restrict);
        });
        model.Entity<WorkflowDecisionRow>(e => {
            e.ToTable("TenantWorkflowDecisions"); e.HasKey(r => new { r.TenantId, r.ExecutionId, r.NodeId });
            e.HasIndex(r => new { r.TenantId, r.ActorId, r.RequestKey }).IsUnique();
            e.HasOne<WorkflowStepRow>().WithMany().HasForeignKey(r => new { r.TenantId, r.ExecutionId, r.NodeId }).OnDelete(DeleteBehavior.Restrict);
        });
        model.Entity<WorkflowStepRow>(e => {
            e.ToTable("TenantWorkflowSteps"); e.HasKey(r => new { r.TenantId, r.ExecutionId, r.NodeId });
            e.Property(r => r.Revision).IsConcurrencyToken(); e.HasIndex(r => new { r.TenantId, r.OperationId }).IsUnique();
            e.HasOne<WorkflowExecutionRow>().WithMany().HasForeignKey(r => new { r.TenantId, r.ExecutionId }).OnDelete(DeleteBehavior.Restrict);
        });
    }
    internal static WorkflowExecutionRow Row(WorkflowExecution value) => new() {
        TenantId = value.TenantId, ExecutionId = value.ExecutionId, WorkflowId = value.WorkflowId, Version = value.Version,
        SourceScope = value.Source.RequestScope, RequestKey = value.RequestKey, Revision = value.Revision,
        Status = (int)value.Status, CreatedAt = value.CreatedAt.ToUnixTimeMilliseconds(), Json = Write(value)
    };
    internal static WorkflowStepRow Row(WorkflowStepOperation value) => new() { TenantId = value.TenantId, ExecutionId = value.ExecutionId,
        NodeId = value.NodeId, OperationId = value.OperationId, Revision = value.Revision, Json = Write(value) };
}
