using System.Text.Json;
using AgentCore.Domain.Tasks;
using AgentCore.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;

namespace AgentCore.Infrastructure.Persistence;

public sealed class HumanTaskApprovalOutcomeRow
{
    public Guid TenantId { get; set; }
    public Guid OperationId { get; set; }
    public Guid RequestId { get; set; }
    public Guid? TaskId { get; set; }
    public long RecordedAt { get; set; }
    public string Json { get; set; } = "";
}
internal static class HumanTaskApprovalPersistence
{
    internal static HumanTaskApprovalOutcomeRow Row(HumanTaskApprovalOutcome value)
    {
        value.Validate();
        return new() { TenantId = value.TenantId, OperationId = value.OperationId, RequestId = value.RequestId,
            TaskId = value.TaskAdmission?.TaskId, RecordedAt = value.RecordedAt.ToUnixTimeMilliseconds(), Json = JsonSerializer.Serialize(value) };
    }
    internal static HumanTaskApprovalOutcome Value(HumanTaskApprovalOutcomeRow row)
    {
        var value = JsonSerializer.Deserialize<HumanTaskApprovalOutcome>(row.Json) ?? throw new InvalidOperationException("Human Task admission evidence is missing.");
        value.Validate(); var expected = Row(value);
        if (expected.TenantId != row.TenantId || expected.OperationId != row.OperationId || expected.RequestId != row.RequestId
            || expected.TaskId != row.TaskId || expected.RecordedAt != row.RecordedAt)
            throw new InvalidOperationException("Human Task admission evidence disagrees with indexed facts.");
        return value;
    }
    internal static void Configure(ModelBuilder model) => model.Entity<HumanTaskApprovalOutcomeRow>(e => {
        e.ToTable("TenantTaskApprovalOutcomes"); e.HasKey(r => new { r.TenantId, r.OperationId });
        e.HasIndex(r => new { r.TenantId, r.RequestId }).IsUnique();
        e.HasOne<TenantApprovalRecord>().WithMany().HasForeignKey(r => new { r.TenantId, r.RequestId })
            .HasPrincipalKey(r => new { r.TenantId, r.RequestId }).OnDelete(DeleteBehavior.Restrict);
        e.HasOne<DelegatedTaskRow>().WithMany().HasForeignKey(r => new { r.TenantId, r.TaskId }).OnDelete(DeleteBehavior.Restrict);
    });
}
