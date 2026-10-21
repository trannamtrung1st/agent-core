namespace AgentCore.Application.Ports;

// Server-derived physical ownership, not permission or model-supplied arguments.
public sealed record TenantBrowserOwner(Guid TenantId, Guid AgentInstanceId, Guid ExecutorPrincipalId, Guid SessionId)
{
    [System.Text.Json.Serialization.JsonIgnore]
    public TenantBrowserAgentOwner Agent => new(TenantId, AgentInstanceId, ExecutorPrincipalId);
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
    // Cold/same-mode registration only; never close or transition a live view.
    ValueTask<string?> EnsureTenantIsolationAsync(TenantBrowserOwner owner, BrowserProfileMode mode, CancellationToken ct = default);
    ValueTask ReleaseTenantAsync(TenantBrowserOwner owner, CancellationToken ct = default);
}

public interface ITenantBrowserStateReader
{
    // Existing handles only; no binding, native launch, page inspection or filesystem read.
    ValueTask<ResourceStateObservation> ObserveTenantAgentAsync(TenantBrowserAgentOwner owner, IReadOnlyList<Guid>? readableSessions, CancellationToken ct = default) => throw new NotSupportedException();
    ValueTask<ResourceStateObservation> ObserveTenantSessionAsync(TenantBrowserOwner owner, CancellationToken ct = default);
}

// Physical target CAS only. This is neither human consent nor an operation grant.
public sealed record TenantBrowserCommandWitness(TenantBrowserOwner Owner, Guid ProviderEpoch, long SelectionRevision,
    long CommandRevision, BrowserProfileMode ProfileMode, Guid? ContextId, string? TabRef, int? PageGeneration,
    Guid? DialogId, string? LocationHash, string CommandHash, string? CredentialOrigin = null, string? FileInputOrigin = null);
public sealed record TenantBrowserCommandPreparation(string? ErrorCode, TenantBrowserCommandWitness? Witness = null, string? Origin = null);
public interface ITenantBrowserCommandTarget
{
    // Inspect existing handles only; never create a context, navigate or read page content.
    ValueTask<TenantBrowserCommandPreparation> PrepareTenantCommandAsync(TenantBrowserOwner owner, BrowserCommand command, CancellationToken ct = default);
}

// Technical retirement of already owned native resources. No binding, launch, reset, deletion or user-command authority.
public sealed record TenantBrowserRetirementPage(IReadOnlyList<TenantBrowserOwner> Items, Guid? NextCursor);
public interface ITenantBrowserRetirement
{
    ValueTask<TenantBrowserRetirementPage> ListTenantRetirementCandidatesAsync(Guid? afterSession, int limit, CancellationToken ct = default);
    // Recheck canonical lifecycle under native gates. An already started close can finish only its original context.
    ValueTask<string> RetireTenantAsync(TenantBrowserOwner owner, Func<CancellationToken, ValueTask<bool>> stillRetired, CancellationToken ct = default);
}
