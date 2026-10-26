using AgentCore.Application.Decisions;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Models;

namespace AgentCore.Application.Models;

// One routing policy for new foreground and unattended admissions. This service never dispatches work.
public sealed class AutoModelRouter(IDecisionEngine engine)
{
    public const string PolicyRevision = "routing-v2";
    public async ValueTask<SessionModelSelection> ResolveAsync(IModelCatalog catalog, ModelPreference preference,
        ModelPreference? definitionDefault, bool requiresVision, bool requiresTools, string source,
        IReadOnlyDictionary<string, string> safeSignals, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var execution = engine.Policy;
        if (execution.AdmissionTimeoutMs is < 50 or > 10000) throw Failure("invalidBudget");
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(execution.AdmissionTimeoutMs);
        return await ResolveCoreAsync(catalog, preference, definitionDefault, requiresVision, requiresTools, source, safeSignals, execution, budget.Token, ct).ConfigureAwait(false);
    }

    private async ValueTask<SessionModelSelection> ResolveCoreAsync(IModelCatalog catalog, ModelPreference preference,
        ModelPreference? definitionDefault, bool requiresVision, bool requiresTools, string source,
        IReadOnlyDictionary<string, string> safeSignals, DecisionExecutionPolicy execution, CancellationToken decisionToken, CancellationToken ct)
    {
        preference.Validate(); definitionDefault?.Validate();
        var effective = preference.Mode == "inherit" ? definitionDefault ?? ModelPreference.Inherit : preference;
        var origin = preference.Mode == "inherit" ? effective.Mode == "inherit" ? "tenantDefault" : "agentDefault" : source;
        var reasoningIntent = preference.Reasoning ?? effective.Reasoning;
        var effort = reasoningIntent ?? ReasoningPreference.Default;
        var policy = catalog.RoutingPolicy;
        if ((effective.Mode == "auto" || effort.Mode == "auto") && !policy.Enabled) throw Failure("autoNotEnabled");
        var candidates = catalog.Models.Where(m => m.SupportsInstructionHierarchy && Enum.IsDefined(m.Transport)
            && (!requiresTools || m.Tools) && (!requiresVision || m.Vision)
            && (effort.Mode != "fixed" || m.SupportedReasoningEfforts.Contains(effort.Effort!, StringComparer.OrdinalIgnoreCase)))
            .OrderBy(m => m.OfferingId, StringComparer.Ordinal).ToArray();
        if (candidates.Length == 0) throw Failure("noEligibleModel");
        ModelDescriptor chosen; string basis; string engineId = "deterministic"; string version = PolicyRevision; string? diagnostic = null;
        if (effective.Mode != "auto")
        {
            var id = effective.Mode == "fixed" ? effective.OfferingId! : catalog.DefaultOfferingId;
            chosen = candidates.SingleOrDefault(m => m.OfferingId == id)
                ?? (catalog.Get(id) is not null ? throw AgentCoreErrors.ModelCapabilityUnsupported()
                    : throw new AgentCoreException("ModelUnavailable", "The selected model is unavailable. Repair it or choose an eligible model for new work.", 409));
            basis = effective.Mode == "fixed" ? "fixed" : "inherited";
        }
        else
        {
            if (!policy.Enabled) throw Failure("autoNotEnabled");
            if (candidates.Length > DecisionValidation.MaxCandidates) throw Failure("tooManyCandidates");
            if (candidates.Length == 1) { chosen = candidates[0]; basis = "singleEligible"; }
            else
            {
                var request = new DecisionRequest(DecisionPurpose.ModelRouting, DecisionKind.Choice,
                    candidates.Select(m => new DecisionCandidate(m.OfferingId, m.DisplayName,
                        $"Tools={m.Tools}; Vision={m.Vision}; Reasoning={m.Reasoning}; Cost=unknown; Latency=unknown; Context=unknown")).ToArray(),
                    safeSignals, new(), PolicyRevision, policy.MinimumProbability);
                var result = await DecideWithinBudgetAsync(request, execution, decisionToken, ct).ConfigureAwait(false);
                if (result.Status == DecisionStatus.Ready && !execution.ShadowMode)
                {
                    chosen = candidates.SingleOrDefault(m => m.OfferingId == result.SelectedCandidateId) ?? throw Failure("invalidResult");
                    basis = "ranked"; engineId = result.EngineId; version = result.EngineVersion;
                }
                else
                {
                    diagnostic = execution.ShadowMode && result.Status == DecisionStatus.Ready ? "shadowMode" : result.Status.ToString();
                    if (!policy.AllowConfiguredDefault) throw Failure(diagnostic);
                    var inherited = definitionDefault?.Mode == "fixed" ? definitionDefault.OfferingId : catalog.DefaultOfferingId;
                    chosen = candidates.SingleOrDefault(m => m.OfferingId == inherited) ?? throw Failure("configuredDefaultUnavailable");
                    basis = "configuredDefault"; engineId = result.EngineId; version = result.EngineVersion;
                }
            }
        }
        string? selectedEffort = effort.Mode == "fixed" ? effort.Effort : effective.Mode == "inherit" && reasoningIntent is null ? catalog.DefaultReasoningEffort : chosen.DefaultReasoningEffort;
        if (effort.Mode == "auto" && chosen.SupportedReasoningEfforts.Count > 1)
        {
            var result = await DecideWithinBudgetAsync(new(DecisionPurpose.ReasoningEffort, DecisionKind.Choice,
                chosen.SupportedReasoningEfforts.Select(e => new DecisionCandidate(e, e)).ToArray(), safeSignals, new(), PolicyRevision, policy.MinimumProbability), execution, decisionToken, ct).ConfigureAwait(false);
            if (result.Status == DecisionStatus.Ready && !execution.ShadowMode)
            { selectedEffort = result.SelectedCandidateId; engineId = result.EngineId; version = result.EngineVersion; if (effective.Mode != "auto") basis = "fixedAutoReasoning"; }
            else if (policy.AllowConfiguredDefault) { selectedEffort = chosen.DefaultReasoningEffort; diagnostic ??= result.Status.ToString(); }
            else throw Failure(result.Status.ToString());
        }
        ct.ThrowIfCancellationRequested();
        return SessionModelBinder.Bind(chosen, selectedEffort, effective.Mode == "auto" ? ModelSelectionSource.Auto :
            origin == "tenantDefault" ? ModelSelectionSource.TenantDefault : origin == "agentDefault" ? ModelSelectionSource.AgentDefault : ModelSelectionSource.User)
            with { Decision = new(basis, engineId, version, catalog.RoutingRevision, origin, PolicyRevision, diagnostic) };
    }

    private async ValueTask<DecisionResult> DecideWithinBudgetAsync(DecisionRequest request, DecisionExecutionPolicy execution,
        CancellationToken budget, CancellationToken caller)
    {
        caller.ThrowIfCancellationRequested();
        DecisionResult Timeout() => DecisionResult.Failure(DecisionStatus.Timeout, execution.EngineId, execution.EngineVersion);
        if (budget.IsCancellationRequested) return Timeout();
        try
        {
            var result = await engine.DecideAsync(request, budget).ConfigureAwait(false);
            caller.ThrowIfCancellationRequested();
            return budget.IsCancellationRequested ? Timeout() : result;
        }
        catch (OperationCanceledException) when (!caller.IsCancellationRequested) { return Timeout(); }
    }

    private static AgentCoreException Failure(string reason) => new("ModelRoutingUnavailable",
        $"Model routing could not admit this Run ({reason}). Choose an available fixed model or repair organization Routing settings.", 409);
}
