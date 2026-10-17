using System.Text.Json;
using AgentCore.Domain.Conversation;
using AgentCore.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AgentCore.Infrastructure.Security;

public sealed class TenantSandboxVerificationRecord
{
    public Guid AttemptId { get; set; }
    public Guid TenantId { get; set; }
    public string ProfileId { get; set; } = "";
    public long ProfileRevision { get; set; }
    public string? InstanceId { get; set; }
    public long? InstanceRevision { get; set; }
    public bool AcceptedFull { get; set; }
    public long Revision { get; set; }
    public long SecurityRevision { get; set; }
    public bool Active { get; set; }
    public long LeaseExpiresAt { get; set; }
    public long? CleanupClaimExpiresAt { get; set; }
    public long StartedAt { get; set; }
    public string Json { get; set; } = "";
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);
    internal static TenantSandboxVerificationRecord Row(SandboxVerificationAttempt a) => new() { AttemptId = a.AttemptId, TenantId = a.TenantId,
        ProfileId = a.ProfileId, ProfileRevision = a.ProfileRevision, InstanceId = a.InstanceId?.ToString("D"), InstanceRevision = a.InstanceRevision,
        AcceptedFull = AgentCore.Application.Security.TenantSandboxVerificationRules.AcceptFullResult(a), Revision = a.Revision, SecurityRevision = a.SecurityRevision, Active = a.Active,
        LeaseExpiresAt = a.LeaseExpiresAt.ToUnixTimeMilliseconds(), CleanupClaimExpiresAt = a.CleanupClaimExpiresAt?.ToUnixTimeMilliseconds(),
        StartedAt = a.StartedAt.ToUnixTimeMilliseconds(), Json = JsonSerializer.Serialize(a, Options) };
    internal SandboxVerificationAttempt Domain()
    {
        var value = JsonSerializer.Deserialize<SandboxVerificationAttempt>(Json, Options);
        if (value is null || value.AttemptId != AttemptId || value.TenantId != TenantId || value.ProfileId != ProfileId || value.ProfileRevision != ProfileRevision
            || value.InstanceId?.ToString("D") != InstanceId || value.InstanceRevision != InstanceRevision || AgentCore.Application.Security.TenantSandboxVerificationRules.AcceptFullResult(value) != AcceptedFull
            || value.SecurityRevision != SecurityRevision || value.Revision != Revision || value.Active != Active || value.LeaseExpiresAt.ToUnixTimeMilliseconds() != LeaseExpiresAt
            || value.CleanupClaimExpiresAt?.ToUnixTimeMilliseconds() != CleanupClaimExpiresAt || value.StartedAt.ToUnixTimeMilliseconds() != StartedAt)
            throw AgentCore.Application.Sessions.AgentCoreErrors.Persistence("Verification ownership is invalid.");
        if (value.Result is { } result)
        {
            try
            {
                AgentCore.Application.Security.TenantSandboxVerificationRules.ValidateOutcome(value, new(result, true));
                if (AcceptedFull && (value.Status == SandboxVerificationStatus.Succeeded) != result.Passed) throw new ArgumentException("Invalid accepted result.");
            }
            catch (ArgumentException) { throw AgentCore.Application.Sessions.AgentCoreErrors.Persistence("Verification evidence ownership is invalid."); }
        }
        return value;
    }
    internal static void Configure(ModelBuilder model)
    {
        var e = model.Entity<TenantSandboxVerificationRecord>(); e.ToTable("TenantSandboxVerificationAttempts", t => t.HasCheckConstraint("CK_SandboxAttempt_InstanceJson",
            "(InstanceId IS NULL AND InstanceRevision IS NULL AND json_extract(Json, '$.instanceId') IS NULL AND json_extract(Json, '$.instanceRevision') IS NULL) OR (InstanceId IS NOT NULL AND InstanceRevision IS NOT NULL AND InstanceRevision > 0 AND json_type(Json, '$.instanceId') IS 'text' AND lower(json_extract(Json, '$.instanceId')) = lower(InstanceId) AND json_type(Json, '$.instanceRevision') IS 'integer' AND json_extract(Json, '$.instanceRevision') = InstanceRevision)")); e.HasKey(x => x.AttemptId);
        e.HasAlternateKey(x => new { x.TenantId, x.ProfileId, x.ProfileRevision, x.AttemptId }); e.Property(x => x.Revision).IsConcurrencyToken();
        e.HasIndex(x => new { x.TenantId, x.ProfileId }).IsUnique().HasFilter("Active = 1 AND InstanceId IS NULL");
        e.HasIndex(x => new { x.TenantId, x.InstanceId }).IsUnique().HasFilter("Active = 1 AND InstanceId IS NOT NULL");
        e.HasIndex(x => new { x.Active, x.LeaseExpiresAt }); e.HasIndex(x => new { x.TenantId, x.ProfileId, x.SecurityRevision });
        e.HasIndex(x => new { x.TenantId, x.InstanceId, x.ProfileId, x.SecurityRevision });
        e.HasIndex(x => new { x.TenantId, x.InstanceId, x.InstanceRevision, x.ProfileId, x.ProfileRevision, x.AcceptedFull, x.SecurityRevision });
        e.HasOne<IamSystemIdentityRow>().WithMany().HasForeignKey(x => new { x.TenantId, x.InstanceId })
            .HasPrincipalKey(x => new { x.TenantId, x.AgentInstanceId }).OnDelete(DeleteBehavior.Restrict);
        e.HasOne<TenantSandboxProfileRecord>().WithMany().HasForeignKey(x => new { x.TenantId, x.ProfileId })
            .HasPrincipalKey(x => new { x.TenantId, x.ProfileId }).OnDelete(DeleteBehavior.Restrict);
        e.HasOne<SandboxProfileRow>().WithMany().HasForeignKey(x => new { x.ProfileId, x.ProfileRevision })
            .HasPrincipalKey(x => new { x.Id, x.Revision }).OnDelete(DeleteBehavior.Restrict);
    }
}
