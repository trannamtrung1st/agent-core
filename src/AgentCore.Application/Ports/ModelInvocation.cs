using AgentCore.Application.Security;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Models;

namespace AgentCore.Application.Ports;

// Constructed by the existing Run owner, never from a model/tool or HTTP argument.
public sealed class ModelInvocation
{
    internal ModelInvocation(TrustedExecutionContext context, ModelRoutePin route, Func<CancellationToken, ValueTask> requireCurrent, ModelBinding? binding = null)
    { Context = context; Route = route; RequireCurrent = requireCurrent; Binding = binding; }
    public TrustedExecutionContext Context { get; }
    public ModelRoutePin Route { get; }
    public ModelBinding? Binding { get; }
    internal Func<CancellationToken, ValueTask> RequireCurrent { get; }
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
        }, run.PinnedModel.Binding);
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
