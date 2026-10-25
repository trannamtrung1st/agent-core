using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Application.Sessions;
using Microsoft.Extensions.Logging;

namespace AgentCore.Application.Triggers;

// This singleton holds no route/client. Each classification uses its actual admitted Run.
public sealed class TenantTriggerCommandAuthorizer(IModelProviderFactory providers, TimeProvider time,
    ILogger<ModelTriggerCommandAuthorizer>? logger = null) : ITriggerCommandAuthorizer
{
    public async ValueTask<TriggerCommandAuthorizationVerdict> AuthorizeAsync(AutomationAuthorizationRequest proposed, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (proposed.Invocation is not { Binding: { } binding } invocation || invocation.Route != binding.Route)
            return Unavailable();
        try
        {
            var descriptor = ModelDescriptor.FromBinding(binding);
            var effort = new[] { "none", "minimal", "low", "medium", "high", "xhigh", "max" }
                .FirstOrDefault(e => binding.Capabilities.ReasoningEfforts.Contains(e, StringComparer.Ordinal));
            var model = providers.Create(binding.Route, binding.Capabilities, invocation, ModelPurpose.TriggerAuthorization);
            return await new ModelTriggerCommandAuthorizer(model, effort, logger, descriptor, time)
                .AuthorizeAsync(proposed, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error) when (error is AccessDeniedException or SecurityConflictException or AgentCoreException or InvalidOperationException or ArgumentException)
        { return Unavailable(); }
    }
    private static TriggerCommandAuthorizationVerdict Unavailable() => new(TriggerCommandAuthorizationDecision.ClassifierUnavailable, TriggerAuthorizationFailureReason.ProviderFailure);
}
