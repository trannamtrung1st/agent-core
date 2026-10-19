using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using AgentCore.Application.Ports;
using AgentCore.Application.Tools;
using Microsoft.Playwright;

namespace AgentCore.Infrastructure.Browser;

public sealed partial class NativePlaywrightBrowser : ITenantBrowserCommandTarget
{
    internal Func<Task>? TenantTargetValidatedProbe { get; set; }
    private readonly Guid _tenantProviderEpoch = Guid.NewGuid();
    private readonly ConcurrentDictionary<Guid, long> _tenantSelections = new();
    private readonly ConcurrentDictionary<(BrowserProfileMode Mode, Guid Id), long> _tenantCommands = new();
    private readonly ConditionalWeakTable<IBrowserContext, TenantContextIdentity> _tenantContexts = new();
    private readonly ConditionalWeakTable<IDialog, TenantContextIdentity> _tenantDialogs = new();
    private sealed class TenantContextIdentity { public Guid Id { get; } = Guid.NewGuid(); }
    private sealed class NoInteractiveHold : IAsyncDisposable
    {
        public static NoInteractiveHold Instance { get; } = new();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private void SelectSessionMode(Guid session, BrowserProfileMode mode)
    {
        if (_tenantSessions.ContainsKey(session) && SessionMode(session) != mode)
            _tenantSelections.AddOrUpdate(session, 1, static (_, old) => checked(old + 1));
        _sessionModes[session] = mode;
    }

    public async ValueTask<TenantBrowserCommandPreparation> PrepareTenantCommandAsync(TenantBrowserOwner owner, BrowserCommand command, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (!TenantRequestAllowed(new(owner.SessionId, command) { TenantOwner = owner })) return new("forbidden");
        if (!IsAvailable) return new("provider_unavailable");
        if (!BrowserToolCatalog.TryGet(BrowserToolArguments.ToolName(command.Kind), out var metadata) || !Provider.Supports(metadata.Feature)) return new("unsupported_operation");
        if (!BrowserToolArguments.WithinOperationalLimits(command, _policy.Limits)) return new("invalid");
        command = BrowserCommandIdentity.Freeze(command);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(1));
        try
        {
            await using var held = await EnterInteractiveAsync(owner.SessionId, deadline.Token).ConfigureAwait(false);
            if (ContextTransitionBlocked(owner.SessionId)) return new("policy_transition_pending");
            if (_sessions.TryGetValue(owner.SessionId, out var view) && TenantTargetBusy(view, command)) return new("browser_target_busy");
            var witness = CaptureTenantWitness(owner, command, out var origin);
            if (command is BrowserFillCredential && witness.CredentialOrigin is null) return new("credential_origin_unavailable");
            return new(null, witness, origin);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return new("browser_target_busy"); }
    }

    private (BrowserProfileMode Mode, Guid Id) TenantCommandKey(TenantBrowserOwner owner) =>
        (SessionMode(owner.SessionId), SessionMode(owner.SessionId) == BrowserProfileMode.PersistentAgent ? owner.AgentInstanceId : owner.SessionId);
    private static bool TenantTargetBusy(SessionBrowser view, BrowserCommand command) => view.OperationActive || view.TimedOut
        || view.PendingAction is { IsCompleted: false } && command is not BrowserDialog;
    private TenantBrowserCommandWitness CaptureTenantWitness(TenantBrowserOwner owner, BrowserCommand command) => CaptureTenantWitness(owner, command, out _);
    private TenantBrowserCommandWitness CaptureTenantWitness(TenantBrowserOwner owner, BrowserCommand command, out string? origin)
    {
        var mode = SessionMode(owner.SessionId);
        _sessions.TryGetValue(owner.SessionId, out var view);
        var context = view?.Context;
        if (context is null && mode == BrowserProfileMode.PersistentAgent && _persistent.TryGetValue(owner.AgentInstanceId, out var root)) context = root.Context;
        var location = view?.Page.Url;
        origin = location == "about:blank" ? location : Uri.TryCreate(location, UriKind.Absolute, out var uri)
            && uri.Scheme is "http" or "https" && uri.UserInfo.Length == 0 ? uri.GetLeftPart(UriPartial.Authority) : null;
        return new(owner, _tenantProviderEpoch, _tenantSelections.GetValueOrDefault(owner.SessionId),
            _tenantCommands.GetValueOrDefault(TenantCommandKey(owner)), mode,
            context is null ? null : _tenantContexts.GetValue(context, static _ => new()).Id,
            view is null ? null : FindPageId(view), view?.Generation,
            view?.Dialog is { } dialog ? _tenantDialogs.GetValue(dialog, static _ => new()).Id : null,
            location is null ? null : Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(location))), BrowserCommandIdentity.Hash(command),
            command is BrowserFillCredential fill && view is not null ? CredentialTargetOrigin(view, fill.Target) : null,
            command is BrowserUploadCommand upload && view is not null ? CredentialTargetOrigin(view, upload.Target) : null);
    }

    // Cached frame handles only. The protected sink still validates the live field and exact frame before resolving a secret.
    private string? CredentialTargetOrigin(SessionBrowser view, BrowserTarget target)
    {
        var frame = target.FrameRef is null ? view.Page.MainFrame : FindPageFrame(view, target.FrameRef);
        if (frame is null || !PermittedFrame(view, frame) || !Allows(view, frame.Url, true)
            || !BrowserTargetPolicy.EvaluateAct(_policy.InteractionMode, frame.Url, _policy.EffectiveInteractionOrigins, _policy.PolicyMode).Allowed
            || !Uri.TryCreate(frame.Url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") || uri.UserInfo.Length != 0) return null;
        return uri.GetLeftPart(UriPartial.Authority);
    }

    // The caller holds the incumbent Session/owner gates until ExecuteCoreAsync returns.
    private string? ConsumeTenantWitness(BrowserRequest request)
    {
        if (request.TenantOwner is not { } owner) return null;
        if (ContextTransitionBlocked(owner.SessionId)) return "policy_transition_pending";
        if (_sessions.TryGetValue(owner.SessionId, out var view) && TenantTargetBusy(view, request.Command)) return "browser_target_busy";
        if (request.TenantWitness is null) return "browser_target_required";
        if (request.TenantWitness != CaptureTenantWitness(owner, request.Command)) return "browser_target_changed";
        _tenantCommands.AddOrUpdate(TenantCommandKey(owner), 1, static (_, old) => checked(old + 1));
        return null;
    }
}
