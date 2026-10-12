using AgentCore.Application.Observability;
using Microsoft.Extensions.Logging;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Domain.Definitions;

namespace AgentCore.Application.Credentials;

public sealed class AgentBrowserProfileService(IAgentInstanceStore instances, IBrowser browser, ILogger<AgentBrowserProfileService>? logger = null, IDiagnosticIdSource? diagnosticIds = null)
{
    public async ValueTask ResetAsync(Guid id, long expectedRevision, CancellationToken ct = default)
    {
        var instance = await instances.FindAsync(id, ct) ?? throw AgentCoreErrors.NotFound("Agent instance was not found.");
        if (instance.Lifecycle != AgentInstanceLifecycle.Active) throw AgentCoreErrors.Forbidden("Archived instance is read-only.");
        if (instance.Revision != expectedRevision) throw AgentCoreErrors.Conflict("Agent instance revision is stale.");
        if (browser is not IBrowserProfileReset profiles) throw new AgentCoreException("unsupported_operation", "Browser profile reset is unavailable.", 503);
        try { await profiles.ResetPersistentProfileAsync(id, ct); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            var diagnosticId = HandledFailureLog.Warning(logger, ex, "browser.profile.reset", "profile_unavailable", new DiagnosticContext(AgentInstanceId: id), diagnostics: diagnosticIds);
            throw new AgentCoreException("profile_unavailable", "Browser profile could not be reset.", 503) { DiagnosticId = diagnosticId };
        }
    }
}
