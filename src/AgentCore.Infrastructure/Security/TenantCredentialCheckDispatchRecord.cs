using AgentCore.Domain.Security;
using AgentCore.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AgentCore.Infrastructure.Security;

public sealed record TenantCredentialCheckDispatchRecord(Guid OperationId, Guid TenantId, Guid RequestId, string RunId,
    string SessionId, string RootActivationId, Guid ExecutorPrincipalId, string InstanceId, string CredentialId, Guid Generation,
    string ActionHash, long StartedRunRevision, long StartedAt, long? CompletedAt)
{
    internal TenantCredentialCheckDispatch Domain() => new(OperationId, TenantId, RequestId, Guid.Parse(RunId), Guid.Parse(SessionId), Guid.Parse(RootActivationId),
        ExecutorPrincipalId, Guid.Parse(InstanceId), Guid.Parse(CredentialId), Generation, ActionHash, StartedRunRevision,
        DateTimeOffset.FromUnixTimeMilliseconds(StartedAt), CompletedAt is { } end ? DateTimeOffset.FromUnixTimeMilliseconds(end) : null);
    internal static TenantCredentialCheckDispatchRecord From(TenantCredentialCheckDispatch d) => new(d.OperationId, d.TenantId, d.RequestId,
        d.RunId.ToString("D"), d.SessionId.ToString("D"), d.RootActivationId.ToString("D"), d.ExecutorPrincipalId, d.InstanceId.ToString("D"),
        d.CredentialId.ToString("D"), d.Generation, d.ActionHash, d.StartedRunRevision, d.StartedAt.ToUnixTimeMilliseconds(), d.CompletedAt?.ToUnixTimeMilliseconds());
    internal static void Configure(ModelBuilder model)
    {
        var e = model.Entity<TenantCredentialCheckDispatchRecord>();
        e.ToTable("TenantCredentialCheckDispatches", t => t.HasCheckConstraint("CK_TenantCredentialCheckDispatch_Values",
            "StartedRunRevision >= 1 AND length(ActionHash) = 64 AND (CompletedAt IS NULL OR CompletedAt >= StartedAt)"));
        e.HasKey(r => r.OperationId); e.HasIndex(r => new { r.TenantId, r.CredentialId }).IsUnique().HasFilter("CompletedAt IS NULL");
        e.HasIndex(r => new { r.CompletedAt, r.StartedAt }); e.Property(r => r.CompletedAt).IsConcurrencyToken();
        e.HasOne<TenantApprovalRecord>().WithMany().HasForeignKey(r => new { r.TenantId, r.RequestId, r.ExecutorPrincipalId, r.OperationId, r.ActionHash })
            .HasPrincipalKey(r => new { r.TenantId, r.RequestId, r.ExecutorPrincipalId, r.OperationId, r.Fingerprint }).OnDelete(DeleteBehavior.Restrict);
        e.HasOne<AgentRunRecord>().WithMany().HasForeignKey(r => new { r.RunId, r.SessionId }).HasPrincipalKey(r => new { r.AgentRunId, r.SessionId }).OnDelete(DeleteBehavior.Restrict);
        e.HasOne<AgentRunRecord>().WithMany().HasForeignKey(r => new { r.RunId, r.RootActivationId }).HasPrincipalKey(r => new { r.AgentRunId, r.RootActivationId }).OnDelete(DeleteBehavior.Restrict);
        e.HasOne<TenantCredentialRecord>().WithMany().HasForeignKey(r => new { r.TenantId, r.CredentialId }).HasPrincipalKey(r => new { r.TenantId, r.CredentialId }).OnDelete(DeleteBehavior.Restrict);
        e.HasOne<TenantSessionRecord>().WithMany().HasForeignKey(r => new { r.TenantId, r.SessionId, r.InstanceId, r.ExecutorPrincipalId })
            .HasPrincipalKey(r => new { r.TenantId, r.SessionId, r.AgentInstanceId, r.ExecutorPrincipalId }).OnDelete(DeleteBehavior.Restrict);
    }
}
