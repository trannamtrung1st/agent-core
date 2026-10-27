using AgentCore.Domain.Security;

namespace AgentCore.Domain.Tasks;

/// <summary>Admission evidence for a human command, with no invented source Run.</summary>
public sealed record HumanTaskApprovalOutcome(Guid TenantId, Guid RequestId, Guid OperationId, bool Admitted,
    string ReasonCode, DateTimeOffset RecordedAt, TenantTaskAdmissionReceipt? TaskAdmission = null)
{
    public void Validate()
    {
        foreach (var id in new[] { TenantId, RequestId, OperationId }) TaskLimits.Id(id, "Task approval outcome");
        if (RecordedAt.Offset != TimeSpan.Zero || ReasonCode.Length is < 1 or > 64
            || ReasonCode.Any(c => c is not (>= 'a' and <= 'z' or '_')) || Admitted != (TaskAdmission is not null)
            || Admitted && ReasonCode != "task_created") throw new ArgumentException("Invalid human Task admission outcome.");
        TaskAdmission?.Validate();
    }
}
