using AgentCore.Application.Events;
using AgentCore.Application.Sessions;

namespace AgentCore.Api.Tests;

/// <summary>Explicit rejection fixture for verifier-failure journeys; no implicit authorization.</summary>
internal sealed class RejectAutomationApprovalOutput : ISessionOutput
{
    public SessionRuntime? Runtime { get; set; }
    public List<ApprovalRequestedOutput> Requests { get; } = [];
    public async ValueTask PublishAsync(SessionOutput output, CancellationToken ct = default)
    {
        if (output.Payload is not ApprovalRequestedOutput approval) return;
        Requests.Add(approval);
        if (Runtime is not null) await Runtime.RespondApprovalAsync(output.ResponseId!.Value, approval.ApprovalId, ToolApprovalDecision.Reject, ct);
    }
}
