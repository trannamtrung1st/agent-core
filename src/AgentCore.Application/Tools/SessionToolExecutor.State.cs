using System.Text.Json;
using AgentCore.Application.Admin;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Application.Security;

namespace AgentCore.Application.Tools;

public sealed partial class SessionToolExecutor
{
    private static readonly JsonSerializerOptions StateJson = new(JsonSerializerDefaults.Web);

    internal async ValueTask<RuntimeStateObservation?> RuntimeStateFactsAsync(Guid sessionId, Guid? runId, Guid? generation, CancellationToken ct)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            try { return tenantAuthorization is null ? null : await tenantAuthorization.RuntimeStateFactsAsync(sessionId, runId, generation, ct).ConfigureAwait(false); }
            catch (SecurityConflictException) when (attempt == 0) { /* Repeat the authorized metadata read once after a concurrent edit. */ }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception) { return null; }
        }
        return null;
    }

    internal static string RenderRuntimeStateFacts(RuntimeStateObservation observation) => JsonSerializer.Serialize(new {
        observation.Scope, observation.ObservedAtUtc, observation.FreshThroughUtc, observation.Availability,
        observation.Sessions, observation.HostedSessions, observation.Runs, observation.Resources, observation.Issues
    }, StateJson);

}
