using System.Text.Json;
using AgentCore.Application.Execution;
using AgentCore.Application.Models;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Definitions;

namespace AgentCore.Application.Security;

public sealed record TenantBackgroundCompletion(AgentRun Child, TenantSession ChildSession, TenantSession? Parent, AgentRun? InitialChild = null);

public static class TenantBackgroundCompletionFactory
{
    public static AgentRun Create(TenantBackgroundCompletion source, ResolvedAgentRunConfiguration resolved, SessionModelSelection model,
        Guid activationId, Guid runId, Guid responseId, DateTimeOffset now)
    {
        var child = source.Child; var parent = source.Parent ?? throw new AccessDeniedException("parent_unavailable");
        var original = child.Admission.ExecutionContext ?? throw new AccessDeniedException("tenant_context_required");
        var evidence = BackgroundCompletionProjection.Build(child, child.Admission.TaskIntent?.Text ?? source.ChildSession.Snapshot.Title, [], parent.Snapshot.DurableLastEntrySequence);
        var activation = new Activation(activationId, parent.Owner.SessionId, ActivationKind.BackgroundCompleted, [], child.AgentRunId,
            null, child.SessionId, child.AgentRunId, "completion:" + child.AgentRunId.ToString("D"), now, original.RootActivationId, child.ActivationId, evidence);
        var execution = new TrustedExecutionContext(original.TenantId, original.AgentPrincipalId, original.OriginalInitiatorPrincipalId,
            parent.Owner.SessionId, runId, activationId, original.RootActivationId, child.AgentRunId, child.ActivationId, ExecutionOrigin.CompletionReport);
        return AgentRun.Create(runId, child.Owner, new(activation, resolved.Configuration.Definition.Id, resolved.Configuration.Definition.Version,
            resolved.Persona, responseId, AgentRunOutputContract.CompletionReport,
            ExecutionBudgetResolver.Resolve(resolved.Configuration.Definition, resolved.InstanceBudgets, false), resolved.Configuration, executionContext: execution),
            new(model.CatalogKey, model.ProviderAlias, model.ModelId, model.ReasoningEffort), AgentRunLimits.DefaultMaxAttempts, now,
            resolved.Skills, resolved.Skills.Where(s => s.Projection == SkillProjection.Always).Select(s => s.Key).ToArray());
    }
    public static long? InputBoundary(AgentRun run)
    {
        if (run.Admission.OutputContract != AgentRunOutputContract.CompletionReport) return null;
        if (run.Admission.ExecutionContext?.Origin is not (ExecutionOrigin.CompletionReport or ExecutionOrigin.ApprovalContinuation)
            || run.Admission.Activation.EvidenceJson is not { } json) throw new AccessDeniedException("completion_input_unavailable");
        var signal = JsonSerializer.Deserialize<AgentRunAdmissionFactory.SignalInput>(json);
        return signal is { TriggerKind: TriggerKind.BackgroundCompleted, InputBoundarySequence: >= 0 }
            ? signal.InputBoundarySequence : throw new AccessDeniedException("completion_input_unavailable");
    }
    public static void RequireParent(TenantBackgroundCompletion source)
    {
        var child = source.Child; var initial = source.InitialChild ?? child; var parent = source.Parent; var snapshot = parent?.Snapshot;
        if (!child.IsTerminal || child.Result?.OutcomeKind == AgentRunOutcomeKind.Continued || source.ChildSession.Snapshot.DurablyDeletedAt is not null || !source.ChildSession.Snapshot.Origin.MayReportCompletion(initial.AgentRunId)
            || initial.Owner != child.Owner || initial.SessionId != child.SessionId || initial.Admission.OutputContract != child.Admission.OutputContract
            || source.ChildSession.Owner.TenantId != child.Owner.TenantId || source.ChildSession.Owner.ExecutorPrincipalId != child.Owner.ExecutorPrincipalId
            || parent is null || parent.Owner.TenantId != child.Owner.TenantId || parent.Owner.ExecutorPrincipalId != child.Owner.ExecutorPrincipalId
            || parent.Owner.AgentInstanceId != child.AgentInstanceId || initial.Admission.Activation.SourceSessionId != parent.Owner.SessionId
            || snapshot!.DurablyDeletedAt is not null || snapshot.ArchivedAt is not null || LifecycleTransition.IsTerminal(snapshot)
            || snapshot.Status is SessionStatus.Ended or SessionStatus.Ending || snapshot.LifecycleStatus != SessionLifecycleStatus.Active
            || snapshot.Status == SessionStatus.Paused && !SessionPauseSemantics.IsTransportResumable(snapshot.PauseReason))
            throw new AccessDeniedException("parent_unavailable");
    }
}

// The incumbent supervised tenant coordinator invokes this bounded admission pass.
public sealed class TenantBackgroundCompletionService(ITenantRuntimeStore storage, TenantRunConfigurationResolver configurations,
    IModelCatalog models, IIdGenerator ids, TimeProvider time)
{
    public async ValueTask AdmitPendingAsync(int limit, CancellationToken ct)
    {
        foreach (var source in await storage.ListTenantBackgroundCompletionsAsync(limit, ct).ConfigureAwait(false))
        {
            try
            {
                TenantBackgroundCompletionFactory.RequireParent(source);
                var resolved = await configurations.ResolveAsync(source.Child.Owner.TenantId!.Value, source.Child.AgentInstanceId, ct).ConfigureAwait(false);
                var model = SessionModelBinder.RefreshDefault(models, resolved.Configuration.Configuration.Definition, source.Parent!.Snapshot.ModelSelection)
                    ?? throw new AccessDeniedException("target_model_unavailable");
                var report = TenantBackgroundCompletionFactory.Create(source, resolved.Configuration, model, ids.NewId(), ids.NewId(), ids.NewId(), time.GetUtcNow());
                _ = await storage.AdmitTenantBackgroundCompletionAsync(report, source.Parent.Snapshot.Revision, ids.NewId(), ct).ConfigureAwait(false);
            }
            catch (SecurityConflictException) { } // Fresh bounded pass resolves a busy/stale parent; no replay of effects.
            catch (AccessDeniedException exception)
            {
                await storage.SkipTenantBackgroundCompletionAsync(source.Child.Owner, source.Child.AgentRunId, exception.ReasonCode, ct).ConfigureAwait(false);
            }
        }
    }
}
