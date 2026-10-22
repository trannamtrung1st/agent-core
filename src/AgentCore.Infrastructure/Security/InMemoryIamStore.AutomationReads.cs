using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Application.Tools;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;
using AgentCore.Domain.Triggers;

namespace AgentCore.Infrastructure.Security;
public sealed partial class InMemoryIamStore : ITenantAutomationReadStore
{
    public ValueTask<TenantAutomationReadObservation> ObserveTenantAutomationReadAsync(TenantRunReference reference, Guid? generation, string tool, string arguments,
        PreparedAutomationRead? expected = null, Guid? inspectionHuman = null, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested(); return ValueTask.FromResult(WithTenantEvents(() => {
            var run = sessionBacking.ReadRetainedTenantOperationRun(reference.TenantId, reference.RunId) ?? throw new AccessDeniedException("automation_read_unavailable");
            if (generation is { } claim) _ = RequireMemoryFrame(new(run.Owner, run.SessionId, run.AgentRunId, claim));
            return ObserveAutomationRead(run, tool, arguments, expected, inspectionHuman, ct);
        }));
    }
    private TenantAutomationReadObservation ObserveAutomationRead(AgentRun run, string tool, string arguments, PreparedAutomationRead? expected, Guid? human, CancellationToken ct)
    {
        var reader = MemoryReader(run, ct); var args = TenantAutomationReadContract.Parse(tool, arguments); var iam = new IamAuthorizer(this);
        TenantAutomationReadRules.Eligible(reader, tool);
        if (human is not null) { if (expected is null) throw new AccessDeniedException("approval_unavailable"); ValidateAgentActor(reader.Security, human.Value); }
        if (expected is not null)
        {
            if (expected.ToolName != tool || expected.ArgumentsJson != args.Json) throw new AccessDeniedException("automation_source_changed");
            TenantAutomationReadRules.ValidateCurrentAsync(reader, expected,
                (id, _) => ValueTask.FromResult(automationBacking.ReadTenantAutomation(reader.Security.Tenant.TenantId, id)),
                (kind, id, trigger, _) => ValueTask.FromResult(TenantEvent(reader.Security.Tenant.TenantId, kind, id, trigger)), iam, ct, human).AsTask().GetAwaiter().GetResult();
            return new(reader, expected);
        }
        var allowed = TenantAutomationReadRules.VisibleAsync(reader, iam, ct).AsTask().GetAwaiter().GetResult();
        var parents = args.AutomationId is { } selected
            ? automationBacking.ReadTenantAutomation(reader.Security.Tenant.TenantId, selected) is { } item && item.Owner.AgentInstanceId == run.AgentInstanceId && (allowed is null || allowed.Contains(selected)) ? new TenantAutomationPage([item], 1, 0, 1) : throw new AccessDeniedException("automation_read_required")
            : ListTenantAutomationsAsync(reader.Security.Tenant.TenantId, run.AgentInstanceId, allowed, args.Page, args.PageSize, ct,
                args.Status is null ? null : args.Status == "suspended" ? AutomationStatus.SuspendedPolicy : Enum.Parse<AutomationStatus>(args.Status, true)).Result;
        var visibility = TenantAutomationReadRules.VisibilityAsync(reader, iam, ct).AsTask().GetAwaiter().GetResult();
        var coverage = args.AutomationId is { } id ? ListTenantEventCoverageAsync(reader.Security.Tenant.TenantId, run.AgentInstanceId, id, visibility, args.CoverageCursor, 4, ct).Result : null;
        var deliveries = (coverage?.Items ?? []).Select(i => TenantEvent(reader.Security.Tenant.TenantId,
            sessionBacking.CoreEvents.TenantReceipt(reader.Security.Tenant.TenantId, i.Source.EventId) is null ? TriggerSourceKind.ApplicationEvent : TriggerSourceKind.CoreEvent, i.Source.EventId, i.TriggerId!.Value)
            ?? throw new AccessDeniedException("automation_source_changed")).ToArray();
        var value = TenantAutomationReadRules.Build(reader, args, parents.Items, parents.Total, coverage, deliveries);
        TenantAutomationReadRules.RawAsync(reader, value, iam, ct).AsTask().GetAwaiter().GetResult(); return new(reader, value);
    }
}
