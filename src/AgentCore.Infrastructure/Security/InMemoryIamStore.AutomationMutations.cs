using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Application.Tools;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;

namespace AgentCore.Infrastructure.Security;
public sealed partial class InMemoryIamStore : ITenantAutomationMutationStore
{
    public ValueTask<TenantAutomationMutationObservation> ObserveTenantAutomationMutationAsync(TenantRunReference reference, Guid? generation,
        string tool, string arguments, PreparedAutomationMutation? expected = null, Guid? inspectionHuman = null, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested(); lock (_gate) lock (sessionBacking.AdmissionGate) return ValueTask.FromResult(WithTenantEvents(() => {
            var run = sessionBacking.ReadRetainedTenantOperationRun(reference.TenantId, reference.RunId) ?? throw new AccessDeniedException("automation_context_unavailable");
            if (generation is { } claim) _ = RequireMemoryFrame(new(run.Owner, run.SessionId, run.AgentRunId, claim));
            return ObserveAutomationMutation(run, tool, arguments, expected, inspectionHuman, ct);
        }));
    }
    private TenantAutomationMutationObservation ObserveAutomationMutation(AgentRun run, string tool, string arguments, PreparedAutomationMutation? expected, Guid? human, CancellationToken ct)
    {
        var reader = MemoryReader(run, ct); var args = TenantAutomationMutationContract.Parse(tool, arguments); TenantAutomationMutationRules.Eligible(reader, tool);
        if (human is { } actor) { if (expected is null) throw new AccessDeniedException("approval_unavailable"); ValidateAgentActor(reader.Security, actor); }
        var current = args.AutomationId is { } id ? automationBacking.ReadTenantAutomation(reader.Security.Tenant.TenantId, id) ?? throw new AccessDeniedException("automation_unavailable") : null;
        if (current is not null) { TenantAutomationRules.Owner(current, reader.Agent); if (current.Revision != args.ExpectedRevision) throw new SecurityConflictException("automation_source_changed"); }
        var entries = reader.Root.Admission.ExecutionContext!.Origin == ExecutionOrigin.UserMessage
            ? reader.Root.Admission.Activation.SourceEntryIds.Select(id => sessionBacking.FindEntry(reader.Root.SessionId, id) ?? throw new AccessDeniedException("automation_intent_unavailable")).ToArray() : [];
        var intent = TenantAutomationMutationRules.Intent(reader, entries); var destinations = TenantAutomationMutationRules.Destinations(run, current, args.Json);
        var source = new TenantAutomationMutationObservation(reader, current,
            destinations.Target is { } target ? sessionBacking.ReadTenantSession(reader.Security.Tenant.TenantId, target) : null,
            destinations.Report is { } report ? sessionBacking.ReadTenantSession(reader.Security.Tenant.TenantId, report) : null, intent.Text, intent.Task);
        if (expected is not null) { TenantAutomationMutationRules.Validate(source, expected); TenantAutomationMutationRules.RawAsync(source, expected, new IamAuthorizer(this), ct, human).AsTask().GetAwaiter().GetResult(); }
        else TenantAutomationMutationRules.BaseRawAsync(reader, current, new IamAuthorizer(this), ct).AsTask().GetAwaiter().GetResult();
        if (expected is null && !intent.Task)
        {
            var before = entries.Min(e => e.Sequence);
            var history = sessionBacking.ReadTenantConversation(reader.Security.Tenant.TenantId, reader.Root.SessionId, null, before, 8)?.History.Items ?? [];
            source = source with { ConversationEvidenceJson = TenantAutomationMutationRules.ConversationEvidence(reader, history, before) };
        }
        return source;
    }
}
