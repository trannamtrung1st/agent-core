using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Domain.Definitions;

namespace AgentCore.Application.Credentials;

public sealed class AgentBrowserProfileService(IAgentInstanceStore instances, IBrowserSession browser)
{
    public async ValueTask ResetAsync(Guid id, long expectedRevision, CancellationToken ct = default)
    {
        var instance = await instances.FindAsync(id, ct) ?? throw AgentCoreErrors.NotFound("Agent instance was not found.");
        if (instance.Lifecycle != AgentInstanceLifecycle.Active) throw AgentCoreErrors.Forbidden("Archived instance is read-only.");
        if (instance.Revision != expectedRevision) throw AgentCoreErrors.Conflict("Agent instance revision is stale.");
        try { await browser.ResetPersistentProfileAsync(id, ct); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { throw new AgentCoreException("profile_unavailable", "Browser profile could not be reset.", 503); }
    }
}
