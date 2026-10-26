using AgentCore.Application.Security;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Models;

namespace AgentCore.Application.Ports;

// Constructed by the existing Run owner, never from a model/tool or HTTP argument.
public sealed record EvaluationModelAdmission(Guid AttemptId, Guid TenantId, Guid ActorId, Guid SubjectId, long SubjectRevision, string SourceFingerprint);

public sealed class ModelInvocation
{
    internal ModelInvocation(TrustedExecutionContext context, ModelRoutePin route, Func<CancellationToken, ValueTask> requireCurrent, ModelBinding? binding = null, string? reasoningEffort = null)
    { Context = context; Route = route; RequireCurrent = requireCurrent; Binding = binding; ReasoningEffort = reasoningEffort; }
    private ModelInvocation(EvaluationModelAdmission admission, ModelBinding binding, Func<CancellationToken, ValueTask> requireCurrent)
    { Evaluation = admission; Route = binding.Route; Binding = binding; RequireCurrent = requireCurrent; }
    public Guid InvocationId => Evaluation?.AttemptId ?? Context!.RunId;
    public Guid TenantId => Evaluation?.TenantId ?? Context!.TenantId;
    public EvaluationModelAdmission? Evaluation { get; }
    public TrustedExecutionContext? Context { get; }
    internal static ModelInvocation ForEvaluation(EvaluationModelAdmission admission, ModelBinding binding, Func<CancellationToken, ValueTask> requireCurrent)
    {
        if (admission.AttemptId == Guid.Empty || admission.ActorId == Guid.Empty || admission.TenantId != binding.Route.TenantId || admission.SubjectId == Guid.Empty || admission.SubjectRevision < 1)
            throw new AccessDeniedException("evaluation_model_admission_invalid");
        return new(admission, binding, requireCurrent);
    }
    public ModelRoutePin Route { get; }
    public ModelBinding? Binding { get; }
    public string? ReasoningEffort { get; }
    internal Func<CancellationToken, ValueTask> RequireCurrent { get; }
    public void RequireEvaluationRequest(ModelRequest request, ModelPurpose purpose)
    {
        if (Evaluation is not null && (purpose != ModelPurpose.CompletionEvaluation || request.ResponseId != Evaluation.AttemptId
            || request.MaxOutputTokens is < 1 or > AgentCore.Domain.Evaluations.EvaluationRules.ModelTokenLimit
            || request.Tools is { Count: > 0 } || request.ToolChoice != ModelToolChoice.Auto || request.Messages.Any(m => m.Parts is { Count: > 0 })))
            throw new AccessDeniedException("evaluation_model_budget_invalid");
    }
    public ValueTask RequireCurrentAsync(CancellationToken ct = default) => RequireCurrent(ct);
    public static ModelInvocation ForRun(AgentRun run, ModelRoutePin route, IAgentRunAuthority authority)
    {
        var context = run.Admission.ExecutionContext ?? throw new AccessDeniedException("tenant_context_required");
        if (run.Owner.TenantId != context.TenantId || route.TenantId != context.TenantId
            || run.PinnedModel.Binding?.Route != route
            || run.PinnedModel.OfferingId != route.OfferingId.ToString("D") || run.PinnedModel.ConnectionId != route.ConnectionId.ToString("D")
            || run.PinnedModel.ExternalModelId != route.ExternalModelId)
            throw new AccessDeniedException("model_route_scope_mismatch");
        return new(context, route, async ct =>
        {
            if (await authority.CurrentDefinitionAsync(run, ct).ConfigureAwait(false) is null)
                throw new AccessDeniedException("tenant_runtime_unavailable");
        }, run.PinnedModel.Binding, run.PinnedModel.ReasoningEffort);
    }
}

public interface ITenantModelRouteAuthority
{
    ValueTask<ModelRouteSnapshot> RequireAsync(ModelRoutePin route, ModelInvocation invocation, ModelPurpose purpose, CancellationToken ct = default);
}

public interface IModelProviderFactory
{
    ILanguageModel Create(ModelRoutePin route, ModelCapabilityProfile capabilities, ModelInvocation invocation, ModelPurpose purpose);
}
