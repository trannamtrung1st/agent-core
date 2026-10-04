using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Domain.Connections;
using Microsoft.Extensions.Logging;

namespace AgentCore.Application.Connections;

public sealed class ApplicationConnectionService(
    IApplicationConnectionStore store,
    IIdGenerator ids,
    TimeProvider time,
    IAgentInstanceStore? instances = null,
    IBrowserSession? browser = null,
    ILogger<ApplicationConnectionService>? logger = null)
{
    public async ValueTask<ApplicationConnection> GetAsync(
        Guid agentInstanceId,
        CancellationToken cancellationToken = default)
    {
        return await TryGetAsync(agentInstanceId, cancellationToken).ConfigureAwait(false)
            ?? throw AgentCoreErrors.NotFound("Application connection was not found.");
    }

    public async ValueTask<ApplicationConnection?> TryGetAsync(
        Guid agentInstanceId,
        CancellationToken cancellationToken = default)
    {
        await RequireInstanceAsync(agentInstanceId, cancellationToken).ConfigureAwait(false);
        return await store.GetByAgentAsync(agentInstanceId, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<ApplicationConnection> ConnectAsync(
        Guid agentInstanceId,
        string displayName,
        string baseUrl,
        CancellationToken cancellationToken = default)
    {
        await RequireInstanceAsync(agentInstanceId, cancellationToken).ConfigureAwait(false);
        var origin = NormalizeOrigin(baseUrl);
        var name = NormalizeName(displayName);
        var existing = await store.GetByAgentAsync(agentInstanceId, cancellationToken).ConfigureAwait(false);
        var now = time.GetUtcNow();
        var connecting = Build(
            existing,
            agentInstanceId,
            name,
            origin,
            ApplicationConnectionStatus.Connecting,
            ApplicationConnectionDetails.SignInRequired,
            now);
        var saved = await store.SaveAsync(connecting, existing?.Revision ?? 0, cancellationToken).ConfigureAwait(false);
        return await ObserveAsync(saved, ObservePurpose.Connect, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<ApplicationConnection> ReauthenticateAsync(
        Guid agentInstanceId,
        CancellationToken cancellationToken = default)
    {
        var current = await GetAsync(agentInstanceId, cancellationToken).ConfigureAwait(false);
        if (current.Status is not (ApplicationConnectionStatus.NeedsReauthentication
            or ApplicationConnectionStatus.Connecting
            or ApplicationConnectionStatus.Unavailable))
        {
            throw AgentCoreErrors.Validation("Reauthenticate from a connection that needs sign-in.");
        }

        var now = time.GetUtcNow();
        var connecting = current with
        {
            Status = ApplicationConnectionStatus.Connecting,
            StatusDetail = ApplicationConnectionDetails.SignInRequired,
            ProfileKey = agentInstanceId,
            Revision = current.Revision + 1,
            UpdatedAtUtc = now
        };
        var saved = await store.SaveAsync(connecting, current.Revision, cancellationToken).ConfigureAwait(false);
        return await ObserveAsync(saved, ObservePurpose.Connect, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<ApplicationConnection> MarkConnectedAsync(
        Guid agentInstanceId,
        CancellationToken cancellationToken = default)
    {
        var current = await GetAsync(agentInstanceId, cancellationToken).ConfigureAwait(false);
        return await ObserveAsync(current, ObservePurpose.MarkConnected, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<ApplicationConnection> ApplyObservationAsync(
        Guid agentInstanceId,
        CancellationToken cancellationToken = default)
    {
        var current = await GetAsync(agentInstanceId, cancellationToken).ConfigureAwait(false);
        return await ObserveAsync(current, ObservePurpose.Watch, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<ApplicationConnection> RevokeAsync(
        Guid agentInstanceId,
        CancellationToken cancellationToken = default)
    {
        var current = await GetAsync(agentInstanceId, cancellationToken).ConfigureAwait(false);
        var revoked = current with
        {
            Status = ApplicationConnectionStatus.NotConnected,
            StatusDetail = null,
            ProfileKey = agentInstanceId,
            Revision = current.Revision + 1,
            UpdatedAtUtc = time.GetUtcNow()
        };
        var saved = await store.SaveAsync(revoked, current.Revision, cancellationToken).ConfigureAwait(false);
        LogStatus(saved);
        return saved;
    }

    public async ValueTask<ApplicationConnection> ResetProfileAsync(
        Guid agentInstanceId,
        CancellationToken cancellationToken = default)
    {
        var current = await GetAsync(agentInstanceId, cancellationToken).ConfigureAwait(false);
        if (browser is not null)
        {
            try
            {
                await browser.ResetPersistentProfileAsync(agentInstanceId, cancellationToken).ConfigureAwait(false);
            }
            catch (IOException)
            {
                logger?.LogWarning("Application connection profile reset failed for agent instance {AgentInstanceId}.", agentInstanceId);
                throw AgentCoreErrors.Validation("The saved sign-in could not be cleared.");
            }
            catch (UnauthorizedAccessException)
            {
                logger?.LogWarning("Application connection profile reset failed for agent instance {AgentInstanceId}.", agentInstanceId);
                throw AgentCoreErrors.Validation("The saved sign-in could not be cleared.");
            }
        }

        var reset = current with
        {
            Status = ApplicationConnectionStatus.NotConnected,
            StatusDetail = ApplicationConnectionDetails.ProfileReset,
            ProfileKey = agentInstanceId,
            Revision = current.Revision + 1,
            UpdatedAtUtc = time.GetUtcNow()
        };
        var saved = await store.SaveAsync(reset, current.Revision, cancellationToken).ConfigureAwait(false);
        LogStatus(saved);
        return saved;
    }

    public async ValueTask<ApplicationConnection> RequireActiveAsync(
        Guid agentInstanceId,
        CancellationToken cancellationToken = default)
    {
        var current = await store.GetByAgentAsync(agentInstanceId, cancellationToken).ConfigureAwait(false);
        if (current is null)
        {
            throw AgentCoreErrors.NotFound("Application connection was not found.");
        }

        if (current.Status != ApplicationConnectionStatus.Connected)
        {
            throw AgentCoreErrors.Forbidden("This application connection cannot be used.");
        }

        return current;
    }

    private async ValueTask<ApplicationConnection> ObserveAsync(
        ApplicationConnection current,
        ObservePurpose purpose,
        CancellationToken cancellationToken)
    {
        if (browser is null || !browser.IsAvailable)
        {
            if (purpose == ObservePurpose.MarkConnected)
            {
                throw AgentCoreErrors.Validation("The page could not be observed.");
            }

            return await PersistAsync(
                    current,
                    ApplicationConnectionStatus.Unavailable,
                    ApplicationConnectionDetails.BrowserUnavailable,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        if (browser is IBrowserProfileBinding binding)
        {
            binding.BindSession(current.ConnectionId, current.AgentInstanceId);
        }

        var signIn = AdminSignInUrl(current.BaseUrl);
        BrowserOperationResult navigated;
        try
        {
            navigated = await browser.NavigateAsync(
                    new BrowserNavigateRequest(current.ConnectionId, signIn),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            logger?.LogWarning(
                "Application connection observation failed for agent instance {AgentInstanceId}.",
                current.AgentInstanceId);
            if (purpose == ObservePurpose.MarkConnected)
            {
                throw AgentCoreErrors.Validation("The page could not be observed.");
            }

            return await PersistAsync(
                    current,
                    ApplicationConnectionStatus.Unavailable,
                    ApplicationConnectionDetails.BrowserUnavailable,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        var observation = navigated.Observation;
        if (observation is null)
        {
            var observed = await browser.ObserveAsync(current.ConnectionId, cancellationToken).ConfigureAwait(false);
            observation = observed.Observation;
        }

        if (observation is null)
        {
            if (purpose == ObservePurpose.MarkConnected)
            {
                throw AgentCoreErrors.Validation("The page could not be observed.");
            }

            return await PersistAsync(
                    current,
                    ApplicationConnectionStatus.Unavailable,
                    ApplicationConnectionDetails.BrowserUnavailable,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        var kind = ApplicationConnectionPageKind.Classify(observation, current.TrustedOrigins);
        var (status, detail) = Next(current.Status, purpose, kind);
        if (status == current.Status && detail == current.StatusDetail)
        {
            LogStatus(current);
            return current;
        }

        return await PersistAsync(current, status, detail, cancellationToken).ConfigureAwait(false);
    }

    private static (ApplicationConnectionStatus Status, string? Detail) Next(
        ApplicationConnectionStatus current,
        ObservePurpose purpose,
        string kind)
    {
        if (kind == ApplicationConnectionPageKind.Application)
        {
            if (purpose is ObservePurpose.Connect or ObservePurpose.MarkConnected)
            {
                return (ApplicationConnectionStatus.Connected, null);
            }

            return current == ApplicationConnectionStatus.Connected
                ? (ApplicationConnectionStatus.Connected, null)
                : (current, current == ApplicationConnectionStatus.Connecting
                    ? ApplicationConnectionDetails.SignInRequired
                    : null);
        }

        if (kind is ApplicationConnectionPageKind.Login or ApplicationConnectionPageKind.HumanVerification)
        {
            var detail = kind == ApplicationConnectionPageKind.HumanVerification
                ? ApplicationConnectionDetails.HumanVerification
                : current == ApplicationConnectionStatus.Connected || purpose == ObservePurpose.Watch
                    ? ApplicationConnectionDetails.LoginWall
                    : ApplicationConnectionDetails.SignInRequired;
            if (purpose == ObservePurpose.Connect && kind == ApplicationConnectionPageKind.Login)
            {
                return (ApplicationConnectionStatus.Connecting, ApplicationConnectionDetails.SignInRequired);
            }

            if (current == ApplicationConnectionStatus.Connected || purpose == ObservePurpose.Watch
                || kind == ApplicationConnectionPageKind.HumanVerification)
            {
                return (ApplicationConnectionStatus.NeedsReauthentication, detail);
            }

            return (ApplicationConnectionStatus.Connecting, detail);
        }

        if (purpose == ObservePurpose.MarkConnected)
        {
            return current == ApplicationConnectionStatus.Connected
                ? (ApplicationConnectionStatus.NeedsReauthentication, ApplicationConnectionDetails.PageUnknown)
                : (current, ApplicationConnectionDetails.PageUnknown);
        }

        return purpose == ObservePurpose.Connect
            ? (ApplicationConnectionStatus.Connecting, ApplicationConnectionDetails.PageUnknown)
            : (current == ApplicationConnectionStatus.Connected
                ? ApplicationConnectionStatus.Unavailable
                : current, ApplicationConnectionDetails.PageUnknown);
    }

    private async ValueTask<ApplicationConnection> PersistAsync(
        ApplicationConnection current,
        ApplicationConnectionStatus status,
        string? detail,
        CancellationToken cancellationToken)
    {
        if (!ApplicationConnectionDetails.IsAllowed(detail))
        {
            detail = ApplicationConnectionDetails.PageUnknown;
        }

        var updated = current with
        {
            Status = status,
            StatusDetail = detail,
            ProfileKey = current.AgentInstanceId,
            Revision = current.Revision + 1,
            UpdatedAtUtc = time.GetUtcNow()
        };
        var saved = await store.SaveAsync(updated, current.Revision, cancellationToken).ConfigureAwait(false);
        LogStatus(saved);
        return saved;
    }

    private ApplicationConnection Build(
        ApplicationConnection? existing,
        Guid agentInstanceId,
        string displayName,
        string origin,
        ApplicationConnectionStatus status,
        string? detail,
        DateTimeOffset now)
    {
        if (existing is null)
        {
            return new ApplicationConnection(
                ids.NewId(),
                agentInstanceId,
                ApplicationConnectionKinds.NopCommerce,
                displayName,
                origin,
                [origin],
                status,
                agentInstanceId,
                1,
                now,
                now,
                detail);
        }

        return existing with
        {
            Kind = ApplicationConnectionKinds.NopCommerce,
            DisplayName = displayName,
            BaseUrl = origin,
            TrustedOrigins = [origin],
            Status = status,
            ProfileKey = agentInstanceId,
            StatusDetail = detail,
            Revision = existing.Revision + 1,
            UpdatedAtUtc = now
        };
    }

    private async ValueTask RequireInstanceAsync(Guid agentInstanceId, CancellationToken cancellationToken)
    {
        if (agentInstanceId == Guid.Empty)
        {
            throw AgentCoreErrors.Validation("Agent instance id is required.");
        }

        if (instances is null)
        {
            return;
        }

        var instance = await instances.FindAsync(agentInstanceId, cancellationToken).ConfigureAwait(false);
        if (instance is null)
        {
            throw AgentCoreErrors.NotFound("Agent instance was not found.");
        }
    }

    private static string NormalizeName(string displayName)
    {
        var name = displayName?.Trim() ?? string.Empty;
        if (name.Length is < 1 or > 80 || name.Any(char.IsControl))
        {
            throw AgentCoreErrors.Validation("Display name must be 1 to 80 characters.");
        }

        return name;
    }

    private static string NormalizeOrigin(string baseUrl)
    {
        if (string.IsNullOrWhiteSpace(baseUrl)
            || !Uri.TryCreate(baseUrl.Trim(), UriKind.Absolute, out var uri)
            || uri.Scheme is not ("http" or "https")
            || string.IsNullOrEmpty(uri.Host)
            || !string.IsNullOrEmpty(uri.UserInfo))
        {
            throw AgentCoreErrors.Validation("Base URL must be an absolute http or https origin without credentials.");
        }

        return uri.GetLeftPart(UriPartial.Authority);
    }

    private static Uri AdminSignInUrl(string baseUrl) => new(baseUrl.TrimEnd('/') + "/admin");

    private void LogStatus(ApplicationConnection connection) =>
        logger?.LogInformation(
            "Application connection status {Status} for agent instance {AgentInstanceId}.",
            connection.Status,
            connection.AgentInstanceId);

    private enum ObservePurpose
    {
        Connect,
        MarkConnected,
        Watch
    }
}
