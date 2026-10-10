using System.Text.Json;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;

namespace AgentCore.Application.Admin;

public sealed record BrowserPrivacyView(BrowserScreenshotPolicy Saved, BrowserScreenshotPolicy Effective,
    BrowserPrivacyAuthority Deployment, bool RestartRequired, string Activation, bool Durable, bool ConstrainedByDeployment);

/// <summary>Owner-only host policy administration. Effective revision is pinned for the host lifetime.</summary>
public sealed class BrowserPrivacyService(IBrowserPrivacyStore store, BrowserPrivacyAuthority authority, TimeProvider time, IIdGenerator ids)
{
    private BrowserScreenshotPolicy _effective = DefaultPolicy(authority);
    public BrowserScreenshotPolicy Effective => Volatile.Read(ref _effective);

    private static BrowserScreenshotPolicy DefaultPolicy(BrowserPrivacyAuthority authority) => new(
        authority.CaptureAllowed ? BrowserScreenshotPrivacyMode.Protected : BrowserScreenshotPrivacyMode.Disabled,
        [], authority.GraphicsOriginCeiling);

    public async ValueTask ActivateAtStartupAsync(CancellationToken ct = default)
    {
        var saved = await store.ReadAsync(ct) ?? DefaultPolicy(authority);
        Volatile.Write(ref _effective, Constrain(saved));
    }

    private BrowserScreenshotPolicy Constrain(BrowserScreenshotPolicy saved)
    {
        var mode = !authority.CaptureAllowed ? BrowserScreenshotPrivacyMode.Disabled
            : saved.Mode == BrowserScreenshotPrivacyMode.Unmasked && !authority.UnmaskedAllowed
                ? BrowserScreenshotPrivacyMode.Protected : saved.Mode;
        return saved with
        {
            Mode = mode,
            UnmaskedOrigins = Array.AsReadOnly(saved.UnmaskedOrigins.Intersect(authority.UnmaskedOriginCeiling, StringComparer.OrdinalIgnoreCase).ToArray()),
            TrustedGraphicsOrigins = Array.AsReadOnly(saved.TrustedGraphicsOrigins.Intersect(authority.GraphicsOriginCeiling, StringComparer.OrdinalIgnoreCase).ToArray())
        };
    }

    public async ValueTask<BrowserPrivacyView> ReadAsync(CancellationToken ct = default)
    {
        var saved = await store.ReadAsync(ct) ?? DefaultPolicy(authority);
        var effective = Effective;
        var constrained = Constrain(saved);
        var constrainedByDeployment = saved.Mode != constrained.Mode
            || !new HashSet<string>(saved.UnmaskedOrigins, StringComparer.OrdinalIgnoreCase).SetEquals(constrained.UnmaskedOrigins)
            || !new HashSet<string>(saved.TrustedGraphicsOrigins, StringComparer.OrdinalIgnoreCase).SetEquals(constrained.TrustedGraphicsOrigins);
        var activation = store.IsDurable
                ? "Saved changes activate after host restart. Until then the effective revision remains authoritative. Previously published Session artifacts remain available."
                : "This host uses InMemory persistence: saved edits are ephemeral and cannot survive a full host restart. Use SQLite persistence for durable privacy activation. The effective startup revision remains authoritative.";
        if (constrainedByDeployment)
            activation += " The saved policy is constrained by deployment restrictions. Restarting alone cannot remove those restrictions; change deployment authorization or save a permitted policy.";
        return new(saved, effective, authority, saved.Revision != effective.Revision, activation, store.IsDurable, constrainedByDeployment);
    }

    public async ValueTask<BrowserPrivacyView> SaveAsync(long expectedRevision, string mode,
        IReadOnlyList<string>? unmaskedOrigins, IReadOnlyList<string>? graphicsOrigins, bool acknowledgeExposure, CancellationToken ct = default)
    {
        if (expectedRevision < 0 || expectedRevision == long.MaxValue || !Enum.GetNames<BrowserScreenshotPrivacyMode>().Contains(mode, StringComparer.Ordinal))
            throw AgentCoreErrors.Validation("Use Protected, Unmasked or Disabled and a nonnegative expected revision.");
        var selected = Enum.Parse<BrowserScreenshotPrivacyMode>(mode);
        var unmasked = ExactOrigins(unmaskedOrigins ?? []);
        var graphics = ExactOrigins(graphicsOrigins ?? []);
        if (!authority.CaptureAllowed && selected != BrowserScreenshotPrivacyMode.Disabled)
            throw AgentCoreErrors.Forbidden("Deployment policy prohibits screenshots.");
        if (selected == BrowserScreenshotPrivacyMode.Unmasked && (!acknowledgeExposure || unmasked.Count == 0))
            throw AgentCoreErrors.Validation("Unmasked capture requires exact origins and explicit acknowledgement of exposure to model providers and Session artifacts.");
        if (selected != BrowserScreenshotPrivacyMode.Unmasked && unmasked.Count > 0)
            throw AgentCoreErrors.Validation("Unmasked origins apply only to Unmasked mode.");
        if (selected == BrowserScreenshotPrivacyMode.Unmasked && !authority.UnmaskedAllowed
            || unmasked.Except(authority.UnmaskedOriginCeiling, StringComparer.OrdinalIgnoreCase).Any()
            || graphics.Except(authority.GraphicsOriginCeiling, StringComparer.OrdinalIgnoreCase).Any())
            throw AgentCoreErrors.Forbidden("The requested capture exception exceeds deployment authority.");
        var policy = new BrowserScreenshotPolicy(selected, unmasked, graphics, checked(expectedRevision + 1));
        var audit = new AdminEventAppend(ids.NewId(), time.GetUtcNow(), AdminEventActorKind.LocalOwner,
            AdminEventOperationKind.BrowserPrivacyChanged, "browserPrivacy", "host", policy.Revision, null,
            JsonSerializer.Serialize(new { mode, revision = policy.Revision, unmaskedOriginCount = unmasked.Count,
                graphicsOriginCount = graphics.Count, exposureAcknowledged = selected == BrowserScreenshotPrivacyMode.Unmasked && acknowledgeExposure }));
        await store.SaveAsync(policy, expectedRevision, audit, ct);
        return await ReadAsync(ct);
    }

    public static IReadOnlyList<string> ExactOrigins(IReadOnlyList<string> input)
    {
        if (input.Count > 32) throw AgentCoreErrors.Validation("At most 32 exact origins are allowed.");
        var result = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var origin in input)
        {
            if (origin is null || origin.Length > 512 || origin.Contains('*') || origin.Contains('\\')
                || !Uri.TryCreate(origin, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")
                || uri.UserInfo.Length > 0 || uri.AbsolutePath != "/" || uri.Query.Length > 0 || uri.Fragment.Length > 0
                || !string.Equals(origin.TrimEnd('/'), uri.GetLeftPart(UriPartial.Authority), StringComparison.OrdinalIgnoreCase))
                throw AgentCoreErrors.Validation("Capture exceptions require exact HTTP(S) origins without paths, credentials, wildcards, queries or fragments.");
            result.Add(uri.GetLeftPart(UriPartial.Authority));
        }
        return Array.AsReadOnly(result.ToArray());
    }
}
