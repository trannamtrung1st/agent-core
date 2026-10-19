using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;

namespace AgentCore.Infrastructure.Browser;

public sealed partial class NativePlaywrightBrowser : ITenantBrowserProfileControl
{
    private readonly ConcurrentDictionary<Guid, long> _tenantProfileResets = new();
    private readonly ConcurrentDictionary<Guid, Guid> _tenantProfileInFlight = new();
    private readonly ConcurrentDictionary<Guid, TenantBrowserProfileResult> _tenantProfileResults = new();
    internal Func<Task>? TenantProfileBeforeDeleteProbe { get; set; }
    internal Action<string>? TenantProfileDeleteProbe { get; set; }

    private void RequireTenantProfileOwner(TenantBrowserAgentOwner owner, bool bind)
    {
        owner.Validate();
        lock (_bindingGate)
        {
            if (_tenantAgents.TryGetValue(owner.AgentInstanceId, out var previous) && previous != owner
                || _legacyAgents.Contains(owner.AgentInstanceId)
                || !_tenantAgents.ContainsKey(owner.AgentInstanceId) && (_persistent.ContainsKey(owner.AgentInstanceId)
                    || _sessionOwners.Any(p => p.Value == owner.AgentInstanceId))) throw new AccessDeniedException("browser_owner_mismatch");
            if (bind) _tenantAgents.TryAdd(owner.AgentInstanceId, owner);
        }
    }
    private static void NoProfileLink(string path)
    {
        try { if (File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint)) throw new IOException("Browser profile path is unavailable."); }
        catch (FileNotFoundException) { }
        catch (DirectoryNotFoundException) { }
    }
    private string TenantProfileDirectory(TenantBrowserAgentOwner owner)
    {
        var part = Path.GetFullPath(string.IsNullOrWhiteSpace(_options.ProfileRoot) ? "data/browser-profiles" : _options.ProfileRoot);
        NoProfileLink(part);
        foreach (var segment in new[] { "tenants", owner.TenantId.ToString("D"), "agents", owner.AgentInstanceId.ToString("D") })
        { part = Path.Combine(part, segment); NoProfileLink(part); }
        return part;
    }
    private TenantBrowserProfileObservation ProfileObservation(TenantBrowserAgentOwner owner, bool busy = false)
    {
        var directory = TenantProfileDirectory(owner); var present = Directory.Exists(directory);
        if (File.Exists(directory)) throw new IOException("Browser profile path is unavailable.");
        var context = _persistent.GetValueOrDefault(owner.AgentInstanceId);
        var views = _sessions.Count(p => p.Value.Persistent && p.Value.AgentInstanceId == owner.AgentInstanceId);
        var identity = context is null ? Guid.Empty : _tenantContexts.GetValue(context.Context, static _ => new()).Id;
        var source = $"{_tenantProviderEpoch:D}|{owner.TenantId:D}|{owner.AgentInstanceId:D}|{owner.ExecutorPrincipalId:D}|{identity:D}|{_tenantCommands.GetValueOrDefault((BrowserProfileMode.PersistentAgent, owner.AgentInstanceId))}|{_tenantProfileResets.GetValueOrDefault(owner.AgentInstanceId)}|{present}|{(present ? Directory.GetCreationTimeUtc(directory).Ticks : 0)}|{(present ? Directory.GetLastWriteTimeUtc(directory).Ticks : 0)}";
        return new(_tenantProviderEpoch, busy ? "" : Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(source))), present, views,
            busy || context?.PendingContextMutation is { Completion.IsCompletedSuccessfully: false },
            _tenantProfileInFlight.TryGetValue(owner.AgentInstanceId, out var operation) ? operation : null, _tenantProfileResults.GetValueOrDefault(owner.AgentInstanceId));
    }
    public async ValueTask<TenantBrowserProfileObservation> ReadTenantProfileAsync(TenantBrowserAgentOwner owner, CancellationToken ct = default)
    {
        RequireTenantProfileOwner(owner, false);
        var gate = _contextUse.GetOrAdd(owner.AgentInstanceId, static _ => new SemaphoreSlim(1, 1));
        if (!await gate.WaitAsync(0, ct)) return ProfileObservation(owner, true);
        try
        {
            var profile = _profileGates.GetOrAdd(owner.AgentInstanceId, static _ => new SemaphoreSlim(1, 1));
            if (!await profile.WaitAsync(0, ct)) return ProfileObservation(owner, true);
            try { return ProfileObservation(owner); }
            finally { profile.Release(); }
        }
        finally { gate.Release(); }
    }
    public async ValueTask<TenantBrowserProfileResult> ResetTenantProfileAsync(TenantBrowserAgentOwner owner, Guid operationId, string witness,
        Func<Guid, CancellationToken, ValueTask<bool>> begin, Func<CancellationToken, ValueTask<bool>> authorizeDelete, CancellationToken ct = default)
    {
        if (operationId == Guid.Empty) throw new ArgumentException("A reset operation is required.");
        RequireTenantProfileOwner(owner, true);
        var gate = _contextUse.GetOrAdd(owner.AgentInstanceId, static _ => new SemaphoreSlim(1, 1));
        if (!await gate.WaitAsync(0, ct)) return new(operationId, "not_started", "browser_profile_busy");
        var begun = false;
        try
        {
            var profile = _profileGates.GetOrAdd(owner.AgentInstanceId, static _ => new SemaphoreSlim(1, 1));
            if (!await profile.WaitAsync(0, ct)) return new(operationId, "not_started", "browser_profile_busy");
            try
            {
                var observed = ProfileObservation(owner);
                if (observed.Busy || observed.InFlightOperation is not null) return new(operationId, "not_started", "browser_profile_busy");
                if (observed.Witness != witness) return new(operationId, "not_started", "browser_profile_changed");
                var directory = TenantProfileDirectory(owner);
                var created = Directory.Exists(directory) ? Directory.GetCreationTimeUtc(directory) : (DateTime?)null;
                if (!await begin(_tenantProviderEpoch, ct)) return new(operationId, "not_started", "browser_profile_authority_changed");
                begun = true; _tenantProfileInFlight[owner.AgentInstanceId] = operationId;
                _tenantProfileResets.AddOrUpdate(owner.AgentInstanceId, 1, static (_, revision) => checked(revision + 1));
                if (_persistent.TryGetValue(owner.AgentInstanceId, out var context))
                {
                    if (!await context.Gate.WaitAsync(0, ct)) return Unknown();
                    try
                    {
                        if (context.PendingContextMutation is { Completion.IsCompletedSuccessfully: false }) return Unknown();
                        var closed = await CloseExplicitlyAsync(context, ct);
                        if (closed.Status is not ("closed" or "already_closed")) return Unknown();
                    }
                    finally { context.Gate.Release(); }
                }
                if (TenantProfileBeforeDeleteProbe is { } probe) await probe().WaitAsync(ct);
                directory = TenantProfileDirectory(owner); NoProfileLink(directory + ".lock");
                if (Directory.Exists(directory) && (created is null || Directory.GetCreationTimeUtc(directory) != created.Value)) return Unknown();
                // Keep the reusable lock inode; unlinking a held lock would let a second process bypass it.
                Directory.CreateDirectory(Path.GetDirectoryName(directory)!);
                using var lease = new FileStream(directory + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                if (!await authorizeDelete(ct)) return Unknown();
                directory = TenantProfileDirectory(owner);
                if (Directory.Exists(directory))
                {
                    if (created is null || Directory.GetCreationTimeUtc(directory) != created.Value) return Unknown();
                    if (TenantProfileDeleteProbe is { } delete) delete(directory); else Directory.Delete(directory, true);
                }
                if (Directory.Exists(directory) || File.Exists(directory)) return Unknown();
                return Complete("reset", "browser_profile_reset_completed");
            }
            finally { profile.Release(); }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or Microsoft.Playwright.PlaywrightException or OperationCanceledException)
        { if (begun) return Unknown(); throw; }
        finally { _tenantProfileInFlight.TryRemove(owner.AgentInstanceId, out _); gate.Release(); }
        TenantBrowserProfileResult Complete(string status, string reason)
        { var result = new TenantBrowserProfileResult(operationId, status, reason); _tenantProfileResults[owner.AgentInstanceId] = result; return result; }
        TenantBrowserProfileResult Unknown() => Complete("unknown", "browser_profile_reset_unknown");
    }
}
