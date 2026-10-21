using System.Text.Json;
using AgentCore.Domain.Experience;
using AgentCore.Domain.Security;
using AgentCore.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AgentCore.Infrastructure.Security;

public sealed record TenantExperienceRecord(Guid TenantId, Guid ExperienceId, string AgentInstanceId, Guid ExecutorPrincipalId,
    string OwnerKey, Guid? UserPrincipalId, Guid? AccountId, Guid? UserId, string SessionId, string RunId, string RootRunId,
    int SourceKind, Guid SourceId, long ThroughCursor, string SourceWitnessJson, string? ContentJson, string ContentHash,
    int Visibility, long Revision, long CreatedAtUtc, long UpdatedAtUtc, string? SourceExperienceIdsJson)
{
    internal static void Configure(ModelBuilder model)
    {
        var row = model.Entity<TenantExperienceRecord>();
        row.ToTable("TenantExperiences", t => {
            t.HasCheckConstraint("CK_TenantExperience_Owner", "((UserPrincipalId IS NULL AND AccountId IS NULL AND UserId IS NULL AND OwnerKey='instance') OR (UserPrincipalId IS NOT NULL AND AccountId IS NOT NULL AND UserId IS NOT NULL AND OwnerKey='user:' || lower(UserPrincipalId)))");
            t.HasCheckConstraint("CK_TenantExperience_Value", "SourceKind IN (0,1,2) AND Visibility IN (0,1,2,3) AND Revision > 0 AND CreatedAtUtc >= 0 AND UpdatedAtUtc >= CreatedAtUtc AND length(ContentHash)=64 AND ContentHash NOT GLOB '*[^0-9a-f]*' AND length(SourceWitnessJson) BETWEEN 2 AND 8192 AND json_valid(SourceWitnessJson) AND ((Visibility=2 AND ContentJson IS NULL) OR (Visibility<>2 AND ContentJson IS NOT NULL AND length(CAST(ContentJson AS BLOB)) BETWEEN 2 AND 36000 AND json_valid(ContentJson) AND json_type(ContentJson)='object'))");
            t.HasCheckConstraint("CK_TenantExperience_Source", "((SourceKind IN (0,1) AND ThroughCursor>0 AND json_type(SourceWitnessJson)='object' AND json_extract(SourceWitnessJson,'$.Kind')=SourceKind AND json_extract(SourceWitnessJson,'$.SourceId')=lower(SourceId) AND json_extract(SourceWitnessJson,'$.ThroughCursor')=ThroughCursor AND SourceExperienceIdsJson IS NULL) OR (SourceKind=2 AND ThroughCursor=0 AND SourceId=ExperienceId AND SourceWitnessJson='[]' AND SourceExperienceIdsJson IS NOT NULL AND length(SourceExperienceIdsJson) BETWEEN 2 AND 320 AND json_valid(SourceExperienceIdsJson) AND json_type(SourceExperienceIdsJson)='array' AND json_array_length(SourceExperienceIdsJson) BETWEEN 2 AND 8))");
        });
        row.HasKey(r => new { r.TenantId, r.ExperienceId }); row.Property(r => r.Revision).IsConcurrencyToken();
        row.HasIndex(r => new { r.TenantId, r.AgentInstanceId, r.OwnerKey, r.SourceKind, r.SourceId, r.ThroughCursor }).IsUnique();
        row.HasIndex(r => new { r.TenantId, r.AgentInstanceId, r.UserPrincipalId, r.Visibility, r.UpdatedAtUtc });
        row.HasOne<TenantSessionRecord>().WithMany().HasForeignKey(r => new { r.TenantId, r.SessionId, r.AgentInstanceId, r.ExecutorPrincipalId })
            .HasPrincipalKey(r => new { r.TenantId, r.SessionId, r.AgentInstanceId, r.ExecutorPrincipalId }).OnDelete(DeleteBehavior.Restrict);
        row.HasOne<TenantUser>().WithMany().HasForeignKey(r => new { r.TenantId, PrincipalId = r.UserPrincipalId, r.AccountId })
            .HasPrincipalKey(u => new { u.TenantId, u.PrincipalId, u.AccountId }).OnDelete(DeleteBehavior.Restrict);
        row.HasOne<TenantUser>().WithMany().HasForeignKey(r => r.UserId).OnDelete(DeleteBehavior.Restrict);
        row.HasOne<AgentRunRecord>().WithMany().HasForeignKey(r => r.RunId).OnDelete(DeleteBehavior.Restrict);
        row.HasOne<AgentRunRecord>().WithMany().HasForeignKey(r => r.RootRunId).OnDelete(DeleteBehavior.Restrict);
    }
    internal static TenantExperienceRecord Row(TenantExperience x) => new(x.TenantId, x.ExperienceId, x.AgentInstanceId.ToString("D"), x.ExecutorPrincipalId, x.OwnerKey,
        x.UserPrincipalId, x.AccountId, x.UserId, x.SessionId.ToString("D"), x.RunId.ToString("D"), x.RootRunId.ToString("D"), (int)x.SourceKind, x.SourceId, x.ThroughCursor,
        x.SourceWitnessJson, x.Content is null ? null : JsonSerializer.Serialize(x.Content), x.ContentHash, (int)x.Visibility, x.Revision, x.CreatedAt.ToUnixTimeMilliseconds(), x.UpdatedAt.ToUnixTimeMilliseconds(), x.SourceExperienceIdsJson);
    internal TenantExperience Domain() => new(TenantId, ExperienceId, Guid.Parse(AgentInstanceId), ExecutorPrincipalId, UserPrincipalId, AccountId, UserId,
        Guid.Parse(SessionId), Guid.Parse(RunId), Guid.Parse(RootRunId), (ExperienceSourceKind)SourceKind, SourceId, ThroughCursor, SourceWitnessJson,
        ContentJson is null ? null : JsonSerializer.Deserialize<ExperienceContent>(ContentJson), ContentHash, (ExperienceVisibility)Visibility, Revision,
        DateTimeOffset.FromUnixTimeMilliseconds(CreatedAtUtc), DateTimeOffset.FromUnixTimeMilliseconds(UpdatedAtUtc), SourceExperienceIdsJson);
}
