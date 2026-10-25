using System.Text.Json;
using AgentCore.Application.Tools;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;

namespace AgentCore.Application.Security;

public static partial class TenantApprovalRules
{
    public static PreparedTenantOperation PrepareBackgroundReport(Guid operation, Guid instance, PreparedBackgroundReport value, long revision, long personaRevision)
    {
        var ids = new[] { value.TenantId, value.SessionId, value.ChildRunId, value.ParentSessionId, value.ActivationId, value.RunId, value.ResponseId };
        if (operation == Guid.Empty || instance == Guid.Empty || revision < 1 || personaRevision < 1 || value.SourceRevision < 1 || value.ParentRevision < 1
            || value.InputBoundarySequence < 0 || ids.Any(id => id == Guid.Empty) || ids.Distinct().Count() != ids.Length
            || value.ConfigurationHash is not { Length: 64 } || value.ConfigurationHash.Any(c => c is not (>= '0' and <= '9') and not (>= 'a' and <= 'f'))
            || value.EvidenceJson is not { Length: > 0 } || System.Text.Encoding.UTF8.GetByteCount(value.EvidenceJson) > AgentRunLimits.MaxEvidenceBytes
            || value.Model is null || value.DestinationContext is null || value.DestinationContext.TenantId != value.TenantId)
            throw new ArgumentException("Invalid exact completion report creation.");
        value.DestinationContext.Validate();
        _ = value.Model.ToPin();
        try
        {
            var signal = JsonSerializer.Deserialize<AgentCore.Application.Execution.AgentRunAdmissionFactory.SignalInput>(value.EvidenceJson);
            if (signal is not { TriggerKind: AgentCore.Application.Ports.TriggerKind.BackgroundCompleted, Text: not null }
                || signal.InputBoundarySequence != value.InputBoundarySequence) throw new ArgumentException("Invalid exact completion evidence.");
            _ = AgentCore.Application.Execution.BackgroundCompletionProjection.ReportText(signal.Text);
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or KeyNotFoundException)
        { throw new ArgumentException("Invalid exact completion evidence.", error); }
        var json = JsonSerializer.Serialize(new { operation = "background.report", report = value, expectedInstanceRevision = revision, expectedPersonaRevision = personaRevision });
        return new(operation, instance, null, revision, personaRevision, json, TenantWorkspaceMutationContract.Hash(json), BackgroundReport: value);
    }
}
