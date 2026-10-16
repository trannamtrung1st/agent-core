using AgentCore.Application.Triggers;

namespace AgentCore.Infrastructure.Providers.Synthetic;

/// <summary>Offline semantic-verifier fixture, never registered by the Real profile.
/// Negative and failure scenarios inject explicit typed verdict fixtures.</summary>
public sealed class SyntheticTriggerCommandAuthorizer(TriggerCommandAuthorizationDecision decision = TriggerCommandAuthorizationDecision.Allow) : ITriggerCommandAuthorizer
{
    public ValueTask<TriggerCommandAuthorizationVerdict> AuthorizeAsync(AutomationAuthorizationRequest proposed, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(new TriggerCommandAuthorizationVerdict(decision));
    }
}
