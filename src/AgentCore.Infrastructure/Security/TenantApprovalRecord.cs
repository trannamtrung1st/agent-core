using System.Text.Json;
using AgentCore.Domain.Security;
using AgentCore.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AgentCore.Infrastructure.Security;

public sealed class TenantApprovalRecord
{
    public Guid RequestId { get; set; }
    public Guid TenantId { get; set; }
    public string? RunId { get; set; }
    public string? SessionId { get; set; }
    public string? AutomationId { get; set; }
    public string InstanceId { get; set; } = "";
    public string RootActivationId { get; set; } = "";
    public Guid ExecutorPrincipalId { get; set; }
    public Guid? OriginalInitiatorPrincipalId { get; set; }
    public Guid OperationId { get; set; }
    public string Fingerprint { get; set; } = "";
    public string Permission { get; set; } = "";
    public string ResourceType { get; set; } = "";
    public string ResourceId { get; set; } = "";
    public string? CredentialId { get; set; }
    public string? SandboxProfileId { get; set; }
    public long? SandboxProfileRevision { get; set; }
    public int Kind { get; set; }
    public int Status { get; set; }
    public long Revision { get; set; }
    public long CreatedAt { get; set; }
    public long ExpiresAt { get; set; }
    public string PayloadJson { get; set; } = "";

    internal static TenantApprovalRecord From(TenantApprovalRequest r) => new()
    {
        RequestId = r.RequestId, TenantId = r.TenantId, RunId = r.Operation.AutomationAdmission is null ? r.RunId.ToString("D") : null, SessionId = r.Operation.AutomationAdmission is null ? r.SessionId.ToString("D") : null, AutomationId = r.Operation.AutomationAdmission?.Current.AutomationId.ToString("D"), InstanceId = r.Operation.InstanceId.ToString("D"),
        RootActivationId = r.RootActivationId.ToString("D"), ExecutorPrincipalId = r.ExecutorPrincipalId, OriginalInitiatorPrincipalId = r.OriginalInitiatorPrincipalId,
        // This legacy column is the exact grant/receipt FK witness, not semantic root correlation.
        OperationId = r.Operation.OperationId, Fingerprint = r.Operation.ActionHash, Permission = r.Permission,
        ResourceType = r.ResourceType, ResourceId = r.ResourceId, SandboxProfileId = (r.Operation.SandboxCommand?.ProfileId ?? r.Operation.SandboxControl?.ProfileId), SandboxProfileRevision = (r.Operation.SandboxCommand?.ProfileRevision ?? r.Operation.SandboxControl?.ProfileRevision), CredentialId = (r.Operation.AccountAction?.Credential.CredentialId ?? r.Operation.AccountRead?.Credential.CredentialId ?? r.Operation.CredentialCheck?.CredentialId ?? r.Operation.BrowserCommand?.Credential?.CredentialId ?? r.Operation.CredentialRead?.CredentialId)?.ToString("D"), Kind = (int)r.Kind, Status = (int)r.Status, Revision = r.Revision,
        CreatedAt = r.CreatedAt.ToUnixTimeMilliseconds(), ExpiresAt = r.ExpiresAt.ToUnixTimeMilliseconds(), PayloadJson = JsonSerializer.Serialize(r)
    };

    internal TenantApprovalRequest Domain()
    {
        var r = JsonSerializer.Deserialize<TenantApprovalRequest>(PayloadJson) ?? throw new InvalidOperationException("Approval payload is missing.");
        var op = r.Operation;
        try
        {
            if (AgentCore.Application.Security.TenantApprovalRules.Rebuild(op) != op)
                throw new InvalidOperationException("Approval prepared action hash or canonical content is inconsistent.");
            AgentCore.Application.Security.TenantApprovalSnapshotRules.Shape(r);
        }
        catch (ArgumentException ex) { throw new InvalidOperationException("Approval prepared operation shape is invalid.", ex); }
        var n = From(r);
        if (n.RequestId != RequestId || n.TenantId != TenantId || n.RunId != RunId || n.SessionId != SessionId || n.InstanceId != InstanceId
            || n.AutomationId != AutomationId || n.RootActivationId != RootActivationId || n.ExecutorPrincipalId != ExecutorPrincipalId || n.OriginalInitiatorPrincipalId != OriginalInitiatorPrincipalId
            || n.OperationId != OperationId || n.Fingerprint != Fingerprint || n.Permission != Permission || n.ResourceType != ResourceType || n.ResourceId != ResourceId
            || n.SandboxProfileId != SandboxProfileId || n.SandboxProfileRevision != SandboxProfileRevision || n.CredentialId != CredentialId || n.Kind != Kind || n.Status != Status || n.Revision != Revision || n.CreatedAt != CreatedAt || n.ExpiresAt != ExpiresAt)
            throw new InvalidOperationException("Approval indexed identity disagrees with prepared operation state.");
        return r;
    }

    internal static void Configure(ModelBuilder model)
    {
        var run = model.Entity<AgentRunRecord>();
        run.HasAlternateKey(r => new { r.AgentRunId, r.SessionId });
        run.HasAlternateKey(r => new { r.AgentRunId, r.RootActivationId });
        var e = model.Entity<TenantApprovalRecord>();
        e.ToTable("TenantApprovalRequests", t => t.HasCheckConstraint("CK_TenantApprovalRequest_Values",
            AgentCore.Infrastructure.Persistence.Migrations.TenantManagementSkillAuthoring.ValuesConstraint));
        e.HasKey(r => r.RequestId); e.HasAlternateKey(r => new { r.TenantId, r.RequestId });
        e.HasAlternateKey(r => new { r.TenantId, r.RequestId, r.ExecutorPrincipalId, r.OperationId, r.Fingerprint });
        e.HasIndex(r => new { r.TenantId, r.RootActivationId, r.ExecutorPrincipalId, r.Fingerprint }).IsUnique();
        e.HasIndex(r => r.OperationId).IsUnique();
        e.HasIndex(r => new { r.TenantId, r.AutomationId, r.Status });
        e.HasIndex(r => new { r.TenantId, r.Status, r.CreatedAt, r.RequestId });
        e.HasIndex(r => new { r.TenantId, r.ExecutorPrincipalId, r.CreatedAt });
        e.HasIndex(r => new { r.Status, r.ExpiresAt, r.RequestId });
        e.Property(r => r.Revision).IsConcurrencyToken();
        e.HasOne<AgentRunRecord>().WithMany().HasForeignKey(r => new { r.RunId, r.SessionId }).HasPrincipalKey(r => new { r.AgentRunId, r.SessionId }).OnDelete(DeleteBehavior.Restrict);
        e.HasOne<AgentRunRecord>().WithMany().HasForeignKey(r => new { r.RunId, r.RootActivationId }).HasPrincipalKey(r => new { r.AgentRunId, r.RootActivationId }).OnDelete(DeleteBehavior.Restrict);
        e.HasOne<TenantSessionRecord>().WithMany().HasForeignKey(r => new { r.TenantId, r.SessionId, r.InstanceId, r.ExecutorPrincipalId })
            .HasPrincipalKey(r => new { r.TenantId, r.SessionId, r.AgentInstanceId, r.ExecutorPrincipalId }).OnDelete(DeleteBehavior.Restrict);
        e.HasOne<TenantUser>().WithMany().HasForeignKey(r => new { r.TenantId, r.OriginalInitiatorPrincipalId }).HasPrincipalKey(r => new { r.TenantId, r.PrincipalId }).OnDelete(DeleteBehavior.Restrict);
        e.HasOne<TenantCredentialRecord>().WithMany().HasForeignKey(r => new { r.TenantId, r.CredentialId }).HasPrincipalKey(r => new { r.TenantId, r.CredentialId }).OnDelete(DeleteBehavior.Restrict);

        e.HasOne<TenantSandboxProfileRecord>().WithMany().HasForeignKey(r => new { r.TenantId, r.SandboxProfileId })
            .HasPrincipalKey(r => new { r.TenantId, r.ProfileId }).OnDelete(DeleteBehavior.Restrict);
        e.HasOne<SandboxProfileRow>().WithMany().HasForeignKey(r => new { r.SandboxProfileId, r.SandboxProfileRevision }).OnDelete(DeleteBehavior.Restrict);

        var decision = model.Entity<TenantApprovalDecision>();
        decision.ToTable("TenantApprovalDecisions"); decision.HasKey(r => r.DecisionId); decision.HasIndex(r => r.RequestId).IsUnique();
        decision.HasOne<TenantApprovalRecord>().WithMany().HasForeignKey(r => new { r.TenantId, r.RequestId }).HasPrincipalKey(r => new { r.TenantId, r.RequestId }).OnDelete(DeleteBehavior.Restrict);
        decision.HasOne<TenantUser>().WithMany().HasForeignKey(r => new { r.TenantId, r.HumanPrincipalId }).HasPrincipalKey(r => new { r.TenantId, r.PrincipalId }).OnDelete(DeleteBehavior.Restrict);
        decision.Property(r => r.DecidedAt).HasConversion(v => v.ToUnixTimeMilliseconds(), v => DateTimeOffset.FromUnixTimeMilliseconds(v));
        var grant = model.Entity<TenantOperationGrant>();
        grant.ToTable("TenantOperationGrants", t => t.HasCheckConstraint("CK_TenantOperationGrant_Values", "Status IN (0,1,2,3,4) AND Revision >= 1 AND length(ActionHash) = 64"));
        grant.HasKey(r => r.GrantId); grant.HasIndex(r => r.RequestId).IsUnique(); grant.HasIndex(r => r.EffectIdempotencyKey).IsUnique();
        grant.HasOne<TenantApprovalRecord>().WithMany().HasForeignKey(r => new { r.TenantId, r.RequestId, r.ExecutorPrincipalId, r.OperationId, r.ActionHash })
            .HasPrincipalKey(r => new { r.TenantId, r.RequestId, r.ExecutorPrincipalId, r.OperationId, r.Fingerprint }).OnDelete(DeleteBehavior.Restrict);
        grant.Property(r => r.Revision).IsConcurrencyToken(); grant.Property(r => r.ExpiresAt).HasConversion(v => v.ToUnixTimeMilliseconds(), v => DateTimeOffset.FromUnixTimeMilliseconds(v));
        var signal = model.Entity<ApprovalProcessedSignal>();
        signal.ToTable("ApprovalProcessedSignals"); signal.HasKey(r => r.SignalId); signal.HasIndex(r => r.RequestId).IsUnique(); signal.HasIndex(r => new { r.ProcessedAt, r.CreatedAt });
        signal.HasOne<TenantApprovalRecord>().WithMany().HasForeignKey(r => new { r.TenantId, r.RequestId }).HasPrincipalKey(r => new { r.TenantId, r.RequestId }).OnDelete(DeleteBehavior.Restrict);
        signal.Property(r => r.Revision).IsConcurrencyToken(); signal.Property(r => r.CreatedAt).HasConversion(v => v.ToUnixTimeMilliseconds(), v => DateTimeOffset.FromUnixTimeMilliseconds(v));
        signal.Property(r => r.ProcessedAt).HasConversion(v => v.HasValue ? (long?)v.Value.ToUnixTimeMilliseconds() : null, v => v.HasValue ? DateTimeOffset.FromUnixTimeMilliseconds(v.Value) : null);
    }
}
