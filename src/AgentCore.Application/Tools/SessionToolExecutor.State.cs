using System.Text.Json;
using AgentCore.Application.Admin;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;

namespace AgentCore.Application.Tools;

public sealed partial class SessionToolExecutor
{
    private static readonly JsonSerializerOptions StateJson = new(JsonSerializerDefaults.Web);

    internal async ValueTask<RuntimeStateObservation?> RuntimeStateFactsAsync(Guid sessionId, Guid? runId, Guid? generation, CancellationToken ct)
    {
        try { return tenantAuthorization is null ? null : await tenantAuthorization.RuntimeStateFactsAsync(sessionId, runId, generation, ct).ConfigureAwait(false); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception) { return null; }
    }

    internal static string RenderRuntimeStateFacts(RuntimeStateObservation observation) => JsonSerializer.Serialize(new {
        observation.Scope, observation.ObservedAtUtc, observation.FreshThroughUtc, observation.Availability,
        observation.Sessions, observation.HostedSessions, observation.Runs, observation.Resources, observation.Issues
    }, StateJson);

    private Task<ToolExecutionResult> ExecuteStateInspectAsync(Guid sessionId, JsonElement args,
        ToolExecutionAdmission? admission, int budget, CancellationToken ct) => Task.FromResult(FitResult(Math.Min(budget, 4096),
            Error("forbidden", "State inspection requires canonical tenant Run authority.")));
}
