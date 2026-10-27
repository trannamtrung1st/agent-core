using System.Text.Json;
using AgentCore.Domain.Tasks;
using AgentCore.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;

namespace AgentCore.Infrastructure.Persistence;

public sealed class DelegatedTaskRow
{
    public Guid TenantId { get; set; }
    public Guid TaskId { get; set; }
    public string SourceSessionId { get; set; } = "";
    public string OperationScope { get; set; } = "";
    public string RequestKey { get; set; } = "";
    public string RequestHash { get; set; } = "";
    public string WorkerInstanceId { get; set; } = "";
    public string? WorkerSessionId { get; set; }
    public Guid RootActivationId { get; set; }
    public Guid? ParentTaskId { get; set; }
    public int State { get; set; }
    public long Revision { get; set; }
    public long UpdatedAt { get; set; }
    public string Json { get; set; } = "";
}
public sealed class TaskDependencyRow
{
    public Guid TenantId { get; set; }
    public Guid TaskId { get; set; }
    public Guid PrerequisiteId { get; set; }
}
public sealed class AgentThreadRow
{
    public Guid TenantId { get; set; }
    public Guid ThreadId { get; set; }
    public Guid FirstInstanceId { get; set; }
    public Guid SecondInstanceId { get; set; }
    public Guid? TaskId { get; set; }
    public long Sequence { get; set; }
    public long? RetiredAt { get; set; }
    public string Json { get; set; } = "";
}
public sealed class AgentMessageRow
{
    public Guid TenantId { get; set; }
    public Guid ThreadId { get; set; }
    public Guid MessageId { get; set; }
    public Guid SenderPrincipalId { get; set; }
    public string SourceScope { get; set; } = "";
    public Guid RecipientInstanceId { get; set; }
    public string RequestKey { get; set; } = "";
    public string RequestHash { get; set; } = "";
    public long Sequence { get; set; }
    public string Json { get; set; } = "";
    public string ReceiptJson { get; set; } = "";
}
public sealed class TaskNotificationRow
{
    public Guid TenantId { get; set; }
    public Guid TaskId { get; set; }
    public Guid NotificationId { get; set; }
    public Guid? TerminalRunId { get; set; }
    public string RecipientSessionId { get; set; } = "";
    public long Revision { get; set; }
    public string Json { get; set; } = "";
}
public sealed class TaskCommandRow
{
    public Guid TenantId { get; set; }
    public Guid TaskId { get; set; }
    public Guid ActorId { get; set; }
    public string RequestKey { get; set; } = "";
    public string Hash { get; set; } = "";
    public string Json { get; set; } = "";
}
internal static class TaskPersistence
{
    internal static T Value<T>(string json) => JsonSerializer.Deserialize<T>(json) ?? throw new InvalidOperationException("Task state is missing.");
    internal static DelegatedTask Value(DelegatedTaskRow row)
    {
        var value = Value<DelegatedTask>(row.Json);
        if (value.TaskId != row.TaskId || value.Source.TenantId != row.TenantId || value.Revision != row.Revision || (int)value.State != row.State
            || value.Source.SessionId.ToString("D") != row.SourceSessionId || value.Assignment.InstanceId.ToString("D") != row.WorkerInstanceId
            || value.WorkerSessionId?.ToString("D") != row.WorkerSessionId || value.Request.Fingerprint() != row.RequestHash)
            throw new InvalidOperationException("Task indexed identity does not match saved facts.");
        return value;
    }
    internal static DelegatedTaskRow Row(DelegatedTask value) => new() { TenantId = value.Source.TenantId, TaskId = value.TaskId,
        SourceSessionId = value.Source.SessionId.ToString("D"), OperationScope = value.Source.OperationScope,
        RequestKey = value.Request.RequestKey, RequestHash = value.Request.Fingerprint(), WorkerInstanceId = value.Assignment.InstanceId.ToString("D"),
        WorkerSessionId = value.WorkerSessionId?.ToString("D"), RootActivationId = value.Source.RootActivationId, ParentTaskId = value.Source.ParentTaskId,
        State = (int)value.State, Revision = value.Revision, UpdatedAt = value.UpdatedAt.ToUnixTimeMilliseconds(), Json = JsonSerializer.Serialize(value) };
    internal static void Configure(ModelBuilder model)
    {
        model.Entity<DelegatedTaskRow>(e => {
            e.ToTable("TenantDelegatedTasks", t => t.HasCheckConstraint("CK_Task_State", "State BETWEEN 0 AND 8 AND Revision >= 1"));
            e.HasKey(r => new { r.TenantId, r.TaskId }); e.Property(r => r.Revision).IsConcurrencyToken();
            e.HasIndex(r => new { r.TenantId, r.SourceSessionId, r.OperationScope, r.RequestKey }).IsUnique();
            e.HasIndex(r => new { r.TenantId, r.WorkerInstanceId, r.State, r.TaskId }); e.HasIndex(r => new { r.State, r.UpdatedAt, r.TaskId });
            e.HasIndex(r => new { r.TenantId, r.RootActivationId, r.TaskId });
            e.HasOne<TenantSessionRecord>().WithMany().HasForeignKey(r => new { r.TenantId, r.SourceSessionId }).HasPrincipalKey(r => new { r.TenantId, r.SessionId }).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<TenantSessionRecord>().WithMany().HasForeignKey(r => new { r.TenantId, r.WorkerSessionId }).HasPrincipalKey(r => new { r.TenantId, r.SessionId }).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<DelegatedTaskRow>().WithMany().HasForeignKey(r => new { r.TenantId, r.ParentTaskId }).OnDelete(DeleteBehavior.Restrict);
        });
        model.Entity<TaskDependencyRow>(e => {
            e.ToTable("TenantTaskDependencies"); e.HasKey(r => new { r.TenantId, r.TaskId, r.PrerequisiteId });
            e.HasOne<DelegatedTaskRow>().WithMany().HasForeignKey(r => new { r.TenantId, r.TaskId }).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<DelegatedTaskRow>().WithMany().HasForeignKey(r => new { r.TenantId, r.PrerequisiteId }).OnDelete(DeleteBehavior.Restrict);
        });
        model.Entity<AgentThreadRow>(e => {
            e.ToTable("TenantAgentThreads", t => t.HasCheckConstraint("CK_Thread_Parties", "TaskId IS NOT NULL OR FirstInstanceId < SecondInstanceId"));
            e.HasKey(r => new { r.TenantId, r.ThreadId }); e.Property(r => r.Sequence).IsConcurrencyToken();
            e.HasIndex(r => new { r.TenantId, r.FirstInstanceId, r.SecondInstanceId }).IsUnique().HasFilter("TaskId IS NULL AND RetiredAt IS NULL");
            e.HasIndex(r => new { r.TenantId, r.TaskId }).IsUnique().HasFilter("TaskId IS NOT NULL");
            e.HasOne<DelegatedTaskRow>().WithMany().HasForeignKey(r => new { r.TenantId, r.TaskId }).OnDelete(DeleteBehavior.Cascade);
        });
        model.Entity<AgentMessageRow>(e => {
            e.ToTable("TenantAgentMessages"); e.HasKey(r => new { r.TenantId, r.MessageId });
            e.HasIndex(r => new { r.TenantId, r.ThreadId, r.Sequence }).IsUnique();
            e.HasIndex(r => new { r.TenantId, r.SenderPrincipalId, r.SourceScope, r.RequestKey }).IsUnique();
            e.HasIndex(r => new { r.TenantId, r.RecipientInstanceId, r.MessageId });
            e.HasOne<AgentThreadRow>().WithMany().HasForeignKey(r => new { r.TenantId, r.ThreadId }).OnDelete(DeleteBehavior.Cascade);
        });
        model.Entity<TaskNotificationRow>(e => {
            e.ToTable("TenantTaskNotifications"); e.HasKey(r => new { r.TenantId, r.NotificationId }); e.Property(r => r.Revision).IsConcurrencyToken();
            e.HasIndex(r => new { r.TenantId, r.TaskId }).IsUnique();
            e.HasOne<DelegatedTaskRow>().WithMany().HasForeignKey(r => new { r.TenantId, r.TaskId }).OnDelete(DeleteBehavior.Cascade);
        });
        model.Entity<TaskCommandRow>(e => {
            e.ToTable("TenantTaskCommands"); e.HasKey(r => new { r.TenantId, r.TaskId, r.ActorId, r.RequestKey });
            e.HasOne<DelegatedTaskRow>().WithMany().HasForeignKey(r => new { r.TenantId, r.TaskId }).OnDelete(DeleteBehavior.Cascade);
        });
    }
}
