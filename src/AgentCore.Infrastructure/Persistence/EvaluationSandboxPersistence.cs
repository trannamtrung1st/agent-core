using System.Text.Json;
using AgentCore.Domain.Evaluations;
using Microsoft.EntityFrameworkCore;

namespace AgentCore.Infrastructure.Persistence;

public sealed class EvaluationSandboxRow
{
    public Guid TenantId { get; set; }
    public Guid ExecutionId { get; set; }
    public Guid ApprovalRequestId { get; set; }
    public Guid AttemptId { get; set; }
    public string CheckId { get; set; } = "";
    public long Revision { get; set; }
    public EvaluationSandboxPhase Phase { get; set; }
    public bool FindingPublished { get; set; }
    public bool CleanupPending { get; set; }
    public long DueAt { get; set; }
    public string Json { get; set; } = "";
}
internal static class EvaluationSandboxPersistence
{
    internal static EvaluationSandboxExecution Value(EvaluationSandboxRow row) => JsonSerializer.Deserialize<EvaluationSandboxExecution>(row.Json)!;
    internal static EvaluationSandboxRow Row(EvaluationSandboxExecution value) => new() { TenantId = value.TenantId, ExecutionId = value.ExecutionId,
        AttemptId = value.AttemptId, ApprovalRequestId = value.Approval.RequestId, CheckId = value.CheckId, Revision = value.Revision, Phase = value.Phase, CleanupPending = value.CleanupPending, FindingPublished = value.FindingPublished,
        DueAt = (value.RetryCleanupAt ?? (value.Phase is EvaluationSandboxPhase.Settled or EvaluationSandboxPhase.AwaitingReview ? value.CreatedAt : value.DispatchExpiresAt ?? value.ExpiresAt)).ToUnixTimeMilliseconds(), Json = JsonSerializer.Serialize(value) };
    internal static void Configure(ModelBuilder model)
    {
        model.Entity<EvaluationSandboxRow>(e => {
            e.ToTable("EvaluationSandboxExecutions"); e.HasKey(r => new { r.TenantId, r.ExecutionId });
            e.Property(r => r.Revision).IsConcurrencyToken();
            e.HasIndex(r => new { r.TenantId, r.AttemptId, r.CheckId }).IsUnique();
            e.HasIndex(r => new { r.Phase, r.CleanupPending, r.DueAt });
            e.HasOne<AgentCore.Infrastructure.Security.TenantApprovalRecord>().WithMany().HasForeignKey(r => r.ApprovalRequestId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<EvaluationAttemptRow>().WithMany().HasForeignKey(r => new { r.TenantId, r.AttemptId }).OnDelete(DeleteBehavior.Restrict);
        });
    }
}
