using AgentCore.Domain.Memory;
using AgentCore.Domain.Security;
using AgentCore.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AgentCore.Infrastructure.Security;

public sealed record TenantLearnedMemoryRecord(Guid TenantId, Guid MemoryId, int Scope, string OwnerKey,
    Guid? UserPrincipalId, Guid? AccountId, Guid? UserId, string AgentInstanceId, Guid ExecutorPrincipalId,
    string SessionId, string RunId, string RootRunId, string? SourceEntryId, int Kind, int Status,
    string Subject, string Content, string SubjectKey, string Source, string ContentHash, long Revision,
    long CreatedAtUtc, long UpdatedAtUtc, Guid? SupersedesMemoryId, string? SourceMemoryIdsJson)
{
    internal static void Configure(ModelBuilder model)
    {
        var row = model.Entity<TenantLearnedMemoryRecord>();
        row.ToTable("TenantLearnedMemories", t => {
            t.HasCheckConstraint("CK_TenantLearnedMemory_Owner", "Scope IN (0,1,2) AND ((UserPrincipalId IS NULL AND AccountId IS NULL AND UserId IS NULL AND Scope = 0) OR (UserPrincipalId IS NOT NULL AND AccountId IS NOT NULL AND UserId IS NOT NULL))");
            t.HasCheckConstraint("CK_TenantLearnedMemory_Value", "Kind BETWEEN 0 AND 4 AND Status BETWEEN 0 AND 3 AND Revision > 0 AND CreatedAtUtc >= 0 AND UpdatedAtUtc >= CreatedAtUtc AND length(ContentHash) = 64 AND Source IN ('userExplicit','agentInferred') AND length(OwnerKey) BETWEEN 1 AND 160 AND length(Subject) <= 128 AND length(Content) <= 2000 AND (Status = 2 OR (length(Subject) > 0 AND length(Content) > 0))");
        });
        row.Property(x => x.SourceMemoryIdsJson).HasMaxLength(320);
        row.ToTable("TenantLearnedMemories", t => t.HasCheckConstraint("CK_TenantLearnedMemory_Lineage",
            "SourceMemoryIdsJson IS NULL OR (length(SourceMemoryIdsJson) BETWEEN 2 AND 320 AND json_valid(SourceMemoryIdsJson) AND json_type(SourceMemoryIdsJson) = 'array' AND json_array_length(SourceMemoryIdsJson) BETWEEN 2 AND 8 AND SupersedesMemoryId IS NULL)"));
        row.HasKey(x => new { x.TenantId, x.MemoryId }); row.Property(x => x.Revision).IsConcurrencyToken();
        row.HasIndex(x => new { x.TenantId, x.OwnerKey, x.Kind, x.SubjectKey }).IsUnique().HasFilter("Status = 0");
        row.HasIndex(x => new { x.TenantId, x.UserPrincipalId, x.Scope, x.AgentInstanceId, x.SessionId, x.Status });
        row.HasIndex(x => new { x.TenantId, x.OwnerKey, x.Kind, x.SubjectKey, x.Status, x.UpdatedAtUtc });
        row.HasOne<TenantUser>().WithMany().HasForeignKey(x => new { x.TenantId, PrincipalId = x.UserPrincipalId, x.AccountId })
            .HasPrincipalKey(u => new { u.TenantId, u.PrincipalId, u.AccountId }).OnDelete(DeleteBehavior.Restrict);
        row.HasOne<TenantUser>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Restrict);
        row.HasOne<TenantSessionRecord>().WithMany().HasForeignKey(x => new { x.TenantId, x.SessionId, x.AgentInstanceId, x.ExecutorPrincipalId })
            .HasPrincipalKey(s => new { s.TenantId, s.SessionId, s.AgentInstanceId, s.ExecutorPrincipalId }).OnDelete(DeleteBehavior.Restrict);
        row.HasOne<AgentRunRecord>().WithMany().HasForeignKey(x => x.RunId).OnDelete(DeleteBehavior.Restrict);
        row.HasOne<AgentRunRecord>().WithMany().HasForeignKey(x => x.RootRunId).OnDelete(DeleteBehavior.Restrict);
        row.HasOne<TenantLearnedMemoryRecord>().WithMany().HasForeignKey(x => new { x.TenantId, x.SupersedesMemoryId })
            .HasPrincipalKey(x => new { x.TenantId, x.MemoryId }).OnDelete(DeleteBehavior.Restrict);
    }
    internal static TenantLearnedMemoryRecord Row(TenantLearnedMemory v) => new(v.TenantId, v.MemoryId, (int)v.Scope, v.OwnerKey,
        v.UserPrincipalId, v.AccountId, v.UserId, v.AgentInstanceId.ToString("D"), v.ExecutorPrincipalId, v.SessionId.ToString("D"), v.RunId.ToString("D"),
        v.RootRunId.ToString("D"), v.SourceEntryId?.ToString("D"), (int)v.Kind, (int)v.Status, v.Subject, v.Content, v.SubjectKey, v.Source, v.ContentHash,
        v.Revision, v.CreatedAt.ToUnixTimeMilliseconds(), v.UpdatedAt.ToUnixTimeMilliseconds(), v.SupersedesMemoryId, v.SourceMemoryIdsJson);
    internal TenantLearnedMemory Domain() => new(TenantId, MemoryId, (MemoryScope)Scope, UserPrincipalId, AccountId, UserId, Guid.Parse(AgentInstanceId),
        ExecutorPrincipalId, Guid.Parse(SessionId), Guid.Parse(RunId), Guid.Parse(RootRunId), SourceEntryId is null ? null : Guid.Parse(SourceEntryId),
        (MemoryKind)Kind, (MemoryItemStatus)Status, Subject, Content, SubjectKey, Source, ContentHash, Revision,
        DateTimeOffset.FromUnixTimeMilliseconds(CreatedAtUtc), DateTimeOffset.FromUnixTimeMilliseconds(UpdatedAtUtc), SupersedesMemoryId, SourceMemoryIdsJson);
}
