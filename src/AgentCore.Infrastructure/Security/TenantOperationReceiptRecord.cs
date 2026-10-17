using AgentCore.Domain.Security;
using AgentCore.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AgentCore.Infrastructure.Security;

public sealed record TenantOperationReceiptRecord(Guid OperationId, Guid RequestId, Guid TenantId, string RunId, string SessionId,
    string RootActivationId, Guid ExecutorPrincipalId, string InstanceId, string ActionHash, int Outcome, string ReasonCode,
    long InstanceRevision, long PersonaRevision, long RecordedAt)
{
    internal TenantOperationReceipt Domain() => new(OperationId, RequestId, TenantId, Guid.Parse(RunId), Guid.Parse(SessionId), Guid.Parse(RootActivationId),
        ExecutorPrincipalId, Guid.Parse(InstanceId), ActionHash, (TenantOperationOutcome)Outcome, ReasonCode, InstanceRevision, PersonaRevision, DateTimeOffset.FromUnixTimeMilliseconds(RecordedAt));
    internal static TenantOperationReceiptRecord From(TenantOperationReceipt r) => new(r.OperationId, r.RequestId, r.TenantId, r.RunId.ToString("D"),
        r.SessionId.ToString("D"), r.RootActivationId.ToString("D"), r.ExecutorPrincipalId, r.InstanceId.ToString("D"), r.ActionHash, (int)r.Outcome,
        r.ReasonCode, r.InstanceRevision, r.PersonaRevision, r.RecordedAt.ToUnixTimeMilliseconds());
    internal static void Configure(ModelBuilder model)
    {
        var request = model.Entity<TenantApprovalRecord>();
        request.HasAlternateKey(r => new { r.RequestId, r.RootActivationId, r.InstanceId });
        var e = model.Entity<TenantOperationReceiptRecord>();
        e.ToTable("TenantOperationReceipts", t => t.HasCheckConstraint("CK_TenantOperationReceipt_Values",
            "Outcome IN (0,1) AND InstanceRevision >= 1 AND PersonaRevision >= 1 AND length(ActionHash) = 64 AND length(ReasonCode) BETWEEN 1 AND 128"));
        e.HasKey(r => r.OperationId); e.HasIndex(r => r.RequestId).IsUnique(); e.HasIndex(r => new { r.TenantId, r.RunId, r.RecordedAt });
        e.HasOne<TenantApprovalRecord>().WithMany().HasForeignKey(r => new { r.TenantId, r.RequestId, r.ExecutorPrincipalId, r.OperationId, r.ActionHash })
            .HasPrincipalKey(r => new { r.TenantId, r.RequestId, r.ExecutorPrincipalId, r.OperationId, r.Fingerprint }).OnDelete(DeleteBehavior.Restrict);
        e.HasOne<TenantApprovalRecord>().WithMany().HasForeignKey(r => new { r.RequestId, r.RootActivationId, r.InstanceId })
            .HasPrincipalKey(r => new { r.RequestId, r.RootActivationId, r.InstanceId }).OnDelete(DeleteBehavior.Restrict);
        e.HasOne<AgentRunRecord>().WithMany().HasForeignKey(r => new { r.RunId, r.SessionId }).HasPrincipalKey(r => new { r.AgentRunId, r.SessionId }).OnDelete(DeleteBehavior.Restrict);
        e.HasOne<AgentRunRecord>().WithMany().HasForeignKey(r => new { r.RunId, r.RootActivationId }).HasPrincipalKey(r => new { r.AgentRunId, r.RootActivationId }).OnDelete(DeleteBehavior.Restrict);
        e.HasOne<TenantSessionRecord>().WithMany().HasForeignKey(r => new { r.TenantId, r.SessionId, r.InstanceId, r.ExecutorPrincipalId })
            .HasPrincipalKey(r => new { r.TenantId, r.SessionId, r.AgentInstanceId, r.ExecutorPrincipalId }).OnDelete(DeleteBehavior.Restrict);
    }
}
