using System.Text.Json;
using AgentCore.Application.Admin;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;

namespace AgentCore.Application.Tools;

public sealed partial class SessionToolExecutor
{
    private static readonly JsonSerializerOptions StateJson = new(JsonSerializerDefaults.Web);

    internal async ValueTask<RuntimeStateObservation?> RuntimeStateFactsAsync(Guid sessionId, Guid? runId, CancellationToken ct)
    {
        try
        {
            if (runId is null || runtimeState?.Invoke() is not { } reader) return null;
            return await reader.ObserveForAgentAsync(sessionId, runId.Value, rateLimit: false, ct: ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception) { return null; } // Optional observation never changes execution authority/outcome.
    }

    internal static string RenderRuntimeStateFacts(RuntimeStateObservation observation) => JsonSerializer.Serialize(new {
        observation.Scope, observation.ObservedAtUtc, observation.FreshThroughUtc, observation.Availability,
        observation.Sessions, observation.HostedSessions, observation.Runs, observation.Resources, observation.Issues
    }, StateJson);

    private async Task<ToolExecutionResult> ExecuteStateInspectAsync(Guid sessionId, JsonElement args,
        ToolExecutionAdmission? admission, int budget, CancellationToken ct)
    {
        if (args.EnumerateObject().Any(p => p.Name != "scope") || args.EnumerateObject().Count() > 1
            || args.TryGetProperty("scope", out var supplied) && (supplied.ValueKind != JsonValueKind.String
                || supplied.GetString() is not ("currentSession" or "instanceSummary")))
            return FitResult(Math.Min(budget, 4096), Error("invalid", "Use only optional scope currentSession or instanceSummary. Resource and owner IDs are not accepted."));
        if (admission?.AgentRunId is not { } runId || admission.OwnedSessionId != sessionId || runtimeState?.Invoke() is not { } reader)
            return FitResult(Math.Min(budget, 4096), Error("forbidden", "State inspection requires a trusted active Session Run."));
        var observation = await reader.ObserveForAgentAsync(sessionId, runId,
            args.TryGetProperty("scope", out var value) ? value.GetString()! : "currentSession", ct: ct);
        return FitResult(Math.Min(budget, 4096), JsonSerializer.Serialize(observation, StateJson));
    }
}
