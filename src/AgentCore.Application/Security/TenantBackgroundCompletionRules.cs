using System.Text.Json;
using AgentCore.Application.Agents;
using AgentCore.Application.Execution;
using AgentCore.Application.Ports;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Definitions;
using AgentCore.Domain.Security;

namespace AgentCore.Application.Security;

public static class TenantBackgroundCompletionRules
{
    public static void Shape(TenantBackgroundCompletion source, AgentRun report, TenantAgent agent, long expectedRevision, DateTimeOffset now)
    {
        TenantBackgroundCompletionFactory.RequireParent(source);
        var parent = source.Parent!; var configuration = report.Admission.Configuration;
        if (parent.Snapshot.Revision != expectedRevision) throw new SecurityConflictException("stale_revision");
        if (!report.IsInitialQueued || report.ResponseId is null || report.Owner != source.Child.Owner || report.CreatedAtUtc > now
            || report.CreatedAtUtc < source.Child.UpdatedAtUtc || configuration is null || configuration.InstanceRevision != agent.Instance.Revision
            || configuration.SpaceContext is not { } context || !context.MatchesSelection(parent.Snapshot.ContextSelection)
            || configuration.PersonaRevision != agent.Instance.PersonaRevision || report.PinnedPersona != agent.Instance.Persona
            || agent.Instance.Lifecycle != AgentInstanceLifecycle.Active || report.DefinitionId != agent.Instance.DefinitionId || report.DefinitionVersion != agent.Instance.ActiveVersion
            || report.Checkpoint is not null || report.ApprovalRequestId is not null)
            throw new AccessDeniedException("completion_admission_mismatch");
        var resolved = new ResolvedAgentRunConfiguration(configuration, report.PinnedPersona, report.PinnedSkillCatalog, agent.Instance.ExecutionBudgets);
        var model = report.PinnedModel;
        var expected = TenantBackgroundCompletionFactory.Create(source, resolved,
            new(model.OfferingId, model.ConnectionId, model.ExternalModelId, ModelSelectionSource.Host, model.ReasoningEffort, Binding: model.Binding),
            report.ActivationId, report.AgentRunId, report.ResponseId.Value, report.CreatedAtUtc);
        if (JsonSerializer.Serialize(expected.Admission) != JsonSerializer.Serialize(report.Admission))
            throw new AccessDeniedException("completion_admission_mismatch");
    }
    public static CompletionInboxItem Settle(CompletionInboxItem item, AgentRun run, Guid? generation)
    {
        var next = item.Settle(run, generation);
        if (next.Status == CompletionInboxStatus.DeliveryQueued && next.ReportActivationId == run.ActivationId && run.IsTerminal
            && run.Result?.OutcomeKind != AgentRunOutcomeKind.Continued)
            next = next with { Revision = next.Revision + 1, Status = CompletionInboxStatus.Skipped,
                SkipReason = run.Result?.OutcomeKind == AgentRunOutcomeKind.NoAction ? "quiet-report-outcome" : "report-unavailable" };
        return next;
    }
}
