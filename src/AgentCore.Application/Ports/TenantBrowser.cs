namespace AgentCore.Application.Ports;

// Server-derived physical ownership, not permission or model-supplied arguments.
public sealed record TenantBrowserOwner(Guid TenantId, Guid AgentInstanceId, Guid ExecutorPrincipalId, Guid SessionId)
{
    public void Validate()
    {
        if (TenantId == Guid.Empty || AgentInstanceId == Guid.Empty || ExecutorPrincipalId == Guid.Empty || SessionId == Guid.Empty)
            throw new ArgumentException("A complete Browser owner is required.");
    }
    public bool SameAgent(TenantBrowserOwner other) => TenantId == other.TenantId
        && AgentInstanceId == other.AgentInstanceId && ExecutorPrincipalId == other.ExecutorPrincipalId;
}

public interface ITenantBrowserBinding
{
    void BindTenantSession(TenantBrowserOwner owner);
    ValueTask<string?> BindTenantIsolationAsync(TenantBrowserOwner owner, BrowserProfileMode mode, CancellationToken ct = default);
    ValueTask ReleaseTenantAsync(TenantBrowserOwner owner, CancellationToken ct = default);
}

public interface ITenantBrowserStateReader
{
    // Existing handles only; no binding, native launch, page inspection or filesystem read.
    ValueTask<ResourceStateObservation> ObserveTenantSessionAsync(TenantBrowserOwner owner, CancellationToken ct = default);
}
