using System.Text.Json;
using AgentCore.Domain.Goals;
using AgentCore.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;

namespace AgentCore.Infrastructure.Persistence;

public sealed class SessionGoalRow
{
    public Guid TenantId { get; set; }
    public string SessionId { get; set; } = "";
    public Guid GoalId { get; set; }
    public Guid SpaceId { get; set; }
    public long Revision { get; set; }
    public string Json { get; set; } = "";
    public List<GoalCheckpointRow> Checkpoints { get; set; } = [];
}
public sealed class GoalCheckpointRow
{
    public Guid TenantId { get; set; }
    public Guid GoalId { get; set; }
    public Guid CheckpointId { get; set; }
    public int Ordinal { get; set; }
    public string PlanJson { get; set; } = "";
    public string StateJson { get; set; } = "";
}
public sealed class GoalActivityRow
{
    public Guid TenantId { get; set; }
    public string SessionId { get; set; } = "";
    public Guid GoalId { get; set; }
    public Guid ActivityId { get; set; }
    public Guid ActorId { get; set; }
    public string RequestKey { get; set; } = "";
    public long Revision { get; set; }
    public string Fingerprint { get; set; } = "";
    public string Json { get; set; } = "";
    public string ReceiptJson { get; set; } = "";
}
internal static class GoalPersistence
{
    internal static SessionGoal Value(SessionGoalRow row)
    {
        var value = JsonSerializer.Deserialize<SessionGoal>(row.Json)!;
        var children = row.Checkpoints.OrderBy(c => c.Ordinal).ToArray();
        return value with { Plan = value.Plan with { Checkpoints = children.Select(c => JsonSerializer.Deserialize<GoalCheckpointPlan>(c.PlanJson)!).ToArray() },
            Checkpoints = children.Select(c => JsonSerializer.Deserialize<GoalCheckpoint>(c.StateJson)!).ToArray() };
    }
    internal static SessionGoalRow Row(SessionGoal goal) => new() { TenantId = goal.TenantId, SessionId = goal.SessionId.ToString("D"), GoalId = goal.GoalId,
        SpaceId = goal.SpaceId, Revision = goal.Revision, Json = JsonSerializer.Serialize(goal with { Plan = goal.Plan with { Checkpoints = [] }, Checkpoints = [] }),
        Checkpoints = goal.Plan.Checkpoints.Select((p, i) => new GoalCheckpointRow { TenantId = goal.TenantId, GoalId = goal.GoalId, CheckpointId = p.CheckpointId,
            Ordinal = i, PlanJson = JsonSerializer.Serialize(p), StateJson = JsonSerializer.Serialize(goal.Checkpoints.Single(c => c.CheckpointId == p.CheckpointId)) }).ToList() };
    internal static GoalActivityRow Row(GoalActivity activity, SessionGoal receipt) => new() { TenantId = activity.TenantId, SessionId = activity.SessionId.ToString("D"),
        GoalId = activity.GoalId, ActivityId = activity.ActivityId, ActorId = activity.ActorId, RequestKey = activity.RequestKey, Revision = activity.Revision,
        Fingerprint = activity.Fingerprint, Json = JsonSerializer.Serialize(activity), ReceiptJson = JsonSerializer.Serialize(receipt) };
    internal static void Configure(ModelBuilder model)
    {
        model.Entity<SessionGoalRow>(e => {
            e.ToTable("SessionGoals"); e.HasKey(r => new { r.TenantId, r.GoalId }); e.HasAlternateKey(r => new { r.TenantId, r.SessionId, r.GoalId });
            e.Property(r => r.Revision).IsConcurrencyToken(); e.HasIndex(r => new { r.TenantId, r.SessionId, r.GoalId });
            e.HasOne<TenantSessionRecord>().WithMany().HasForeignKey(r => new { r.TenantId, r.SessionId }).HasPrincipalKey(r => new { r.TenantId, r.SessionId }).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<SpaceRecord>().WithMany().HasForeignKey(r => new { r.TenantId, r.SpaceId }).OnDelete(DeleteBehavior.Restrict);
        });
        model.Entity<GoalCheckpointRow>(e => {
            e.ToTable("GoalCheckpoints"); e.HasKey(r => new { r.TenantId, r.GoalId, r.CheckpointId });
            e.HasIndex(r => new { r.TenantId, r.GoalId, r.Ordinal }).IsUnique();
            e.HasOne<SessionGoalRow>().WithMany(r => r.Checkpoints).HasForeignKey(r => new { r.TenantId, r.GoalId }).OnDelete(DeleteBehavior.Cascade);
        });
        model.Entity<GoalActivityRow>(e => {
            e.ToTable("GoalActivities"); e.HasKey(r => new { r.TenantId, r.ActivityId });
            e.HasIndex(r => new { r.TenantId, r.SessionId, r.ActorId, r.RequestKey }).IsUnique();
            e.HasIndex(r => new { r.TenantId, r.GoalId, r.Revision }).IsUnique();
            e.HasOne<SessionGoalRow>().WithMany().HasForeignKey(r => new { r.TenantId, r.SessionId, r.GoalId }).HasPrincipalKey(r => new { r.TenantId, r.SessionId, r.GoalId }).OnDelete(DeleteBehavior.Cascade);
        });
    }
}
