using System.Text.Json;
using AgentCore.Application.Sessions;
using AgentCore.Domain.Conversation;
using AgentCore.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;

namespace AgentCore.Infrastructure.Persistence;

internal static class SandboxRuntimePersistence
{
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private const string LeaseScope = "(TenantId IS NULL AND ExecutorPrincipalId IS NULL AND ProfileId IS NULL AND ProfileRevision IS NULL AND json_extract(Json, '$.tenantId') IS NULL AND json_extract(Json, '$.executorPrincipalId') IS NULL AND json_extract(Json, '$.profile.tenantId') IS NULL) OR (TenantId IS NOT NULL AND ExecutorPrincipalId IS NOT NULL AND ProfileId IS NOT NULL AND ProfileRevision IS NOT NULL AND ProfileRevision > 0 AND json_type(Json, '$.tenantId') IS 'text' AND lower(json_extract(Json, '$.tenantId')) = lower(TenantId) AND json_type(Json, '$.executorPrincipalId') IS 'text' AND lower(json_extract(Json, '$.executorPrincipalId')) = lower(ExecutorPrincipalId) AND json_type(Json, '$.profile.tenantId') IS 'text' AND lower(json_extract(Json, '$.profile.tenantId')) = lower(TenantId) AND json_extract(Json, '$.profile.id') IS ProfileId AND json_extract(Json, '$.profile.revision') IS ProfileRevision)";
    private const string ExecutionScope = "(TenantId IS NULL AND ExecutorPrincipalId IS NULL AND AgentInstanceId IS NULL AND OwnedRunId IS NULL AND LeaseSessionId IS NULL AND Revision = 0 AND json_extract(Json, '$.tenantId') IS NULL AND json_extract(Json, '$.executorPrincipalId') IS NULL) OR (TenantId IS NOT NULL AND ExecutorPrincipalId IS NOT NULL AND AgentInstanceId IS NOT NULL AND OwnedRunId IS NOT NULL AND OwnedRunId = AgentRunId AND LeaseSessionId IS NOT NULL AND LeaseSessionId = SessionId AND Revision > 0 AND json_type(Json, '$.tenantId') IS 'text' AND lower(json_extract(Json, '$.tenantId')) = lower(TenantId) AND json_type(Json, '$.executorPrincipalId') IS 'text' AND lower(json_extract(Json, '$.executorPrincipalId')) = lower(ExecutorPrincipalId) AND json_type(Json, '$.agentInstanceId') IS 'text' AND lower(json_extract(Json, '$.agentInstanceId')) = lower(AgentInstanceId) AND json_extract(Json, '$.revision') IS Revision)";

    internal static void Configure(ModelBuilder model)
    {
        var lease = model.Entity<SandboxLeaseRow>(); lease.ToTable("SandboxSessionLeases", t => t.HasCheckConstraint("CK_SandboxLease_TenantJson", LeaseScope));
        lease.HasIndex(x => new { x.TenantId, x.AgentInstanceId, x.ExpiresAt });
        lease.HasOne<TenantSessionRecord>().WithMany().HasForeignKey(x => new { x.TenantId, x.SessionId, x.AgentInstanceId, x.ExecutorPrincipalId })
            .HasPrincipalKey(x => new { x.TenantId, x.SessionId, x.AgentInstanceId, x.ExecutorPrincipalId }).OnDelete(DeleteBehavior.Restrict);
        lease.HasOne<TenantSandboxProfileRecord>().WithMany().HasForeignKey(x => new { x.TenantId, x.ProfileId })
            .HasPrincipalKey(x => new { x.TenantId, x.ProfileId }).OnDelete(DeleteBehavior.Restrict);
        lease.HasOne<SandboxProfileRow>().WithMany().HasForeignKey(x => new { x.ProfileId, x.ProfileRevision })
            .HasPrincipalKey(x => new { x.Id, x.Revision }).OnDelete(DeleteBehavior.Restrict);
        var execution = model.Entity<SandboxExecutionRow>(); execution.ToTable("SandboxExecutions", t => t.HasCheckConstraint("CK_SandboxExecution_TenantJson", ExecutionScope));
        execution.Property(x => x.Revision).IsConcurrencyToken(); execution.HasIndex(x => new { x.TenantId, x.SessionId, x.AgentRunId, x.ToolCallId });
        execution.HasOne<TenantSessionRecord>().WithMany().HasForeignKey(x => new { x.TenantId, x.SessionId, x.AgentInstanceId, x.ExecutorPrincipalId })
            .HasPrincipalKey(x => new { x.TenantId, x.SessionId, x.AgentInstanceId, x.ExecutorPrincipalId }).OnDelete(DeleteBehavior.Restrict);
        execution.HasOne<SandboxLeaseRow>().WithMany().HasForeignKey(x => x.LeaseSessionId).OnDelete(DeleteBehavior.Restrict);
        model.Entity<AgentRunRecord>().HasAlternateKey(x => new { x.AgentRunId, x.SessionId });
        execution.HasOne<AgentRunRecord>().WithMany().HasForeignKey(x => new { x.OwnedRunId, x.SessionId })
            .HasPrincipalKey(x => new { x.AgentRunId, x.SessionId }).OnDelete(DeleteBehavior.Restrict);
    }
    internal static SandboxLeaseRow Row(SandboxLease value) => new() { TenantId = value.TenantId, ExecutorPrincipalId = value.ExecutorPrincipalId,
        ProfileId = value.TenantId is null ? null : value.Profile.Id, ProfileRevision = value.TenantId is null ? null : value.Profile.Revision,
        SessionId = value.SessionId.ToString("D"), AgentInstanceId = value.AgentInstanceId.ToString("D"), Revision = value.Revision, State = (int)value.State,
        ExpiresAt = value.ExpiresAt.ToUnixTimeMilliseconds(), Json = JsonSerializer.Serialize(value, Json) };
    internal static SandboxLease Value(SandboxLeaseRow row)
    {
        var v = JsonSerializer.Deserialize<SandboxLease>(row.Json, Json);
        if (v is null || v.TenantId != row.TenantId || v.ExecutorPrincipalId != row.ExecutorPrincipalId || v.SessionId.ToString("D") != row.SessionId
            || v.AgentInstanceId.ToString("D") != row.AgentInstanceId || v.Revision != row.Revision || (int)v.State != row.State || v.ExpiresAt.ToUnixTimeMilliseconds() != row.ExpiresAt
            || row.TenantId is not null && (v.Profile.TenantId != row.TenantId || v.Profile.Id != row.ProfileId || v.Profile.Revision != row.ProfileRevision))
            throw AgentCoreErrors.Persistence("Sandbox generation ownership is invalid.");
        if (row.TenantId is not null) try { AgentCore.Application.Security.TenantSandboxRuntimeRules.ValidateLease(v); }
            catch (ArgumentException) { throw AgentCoreErrors.Persistence("Sandbox generation evidence is invalid."); }
        return v;
    }
    internal static SandboxExecutionRow Row(SandboxExecution value) => new() { TenantId = value.TenantId, ExecutorPrincipalId = value.ExecutorPrincipalId,
        AgentInstanceId = value.TenantId is null ? null : value.AgentInstanceId.ToString("D"), OwnedRunId = value.TenantId is null ? null : value.AgentRunId.ToString("D"),
        LeaseSessionId = value.TenantId is null ? null : value.SessionId.ToString("D"), Revision = value.Revision,
        Id = value.Id.ToString("D"), SessionId = value.SessionId.ToString("D"), AgentRunId = value.AgentRunId.ToString("D"), ToolCallId = value.ToolCallId, Json = JsonSerializer.Serialize(value, Json) };
    internal static SandboxExecution Value(SandboxExecutionRow row)
    {
        var v = JsonSerializer.Deserialize<SandboxExecution>(row.Json, Json);
        if (v is null || v.TenantId != row.TenantId || v.ExecutorPrincipalId != row.ExecutorPrincipalId || v.Id.ToString("D") != row.Id || v.SessionId.ToString("D") != row.SessionId
            || v.AgentRunId.ToString("D") != row.AgentRunId || v.ToolCallId != row.ToolCallId || v.Revision != row.Revision
            || row.TenantId is not null && (v.AgentInstanceId.ToString("D") != row.AgentInstanceId || row.OwnedRunId != row.AgentRunId || row.LeaseSessionId != row.SessionId))
            throw AgentCoreErrors.Persistence("Sandbox execution ownership is invalid.");
        if (row.TenantId is not null) try { AgentCore.Application.Security.TenantSandboxRuntimeRules.ValidateExecution(v); }
            catch (ArgumentException) { throw AgentCoreErrors.Persistence("Sandbox execution evidence is invalid."); }
        return v;
    }
}
