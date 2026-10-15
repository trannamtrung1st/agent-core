using System.Text.Json;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Domain.Definitions;

namespace AgentCore.Application.Tools;

public sealed partial class SessionToolExecutor
{
    private static ToolExecutionResult SandboxError(string code, string message) => TextResult(Error(code,message));
    private static readonly JsonSerializerOptions SandboxJson = new(JsonSerializerDefaults.Web);
    private async Task<ToolExecutionResult> ExecuteSandboxToolAsync(AgentDefinition definition, Guid sessionId, ModelToolCall call,
        JsonElement args, ToolExecutionAdmission? admission, int remainingBytes, CancellationToken ct)
    {
        if (sandboxService is null || sandboxStore is null || _profiles is null || emailRuns is null)
            return SandboxError("unavailable", "OpenSandbox is unavailable. Check Platform → Sandboxes.");
        var session = await _profiles.LoadMetadataAsync(sessionId, ct);
        if (session is null || session.ProfileId is null || admission?.AgentInstanceId != session.AgentInstanceId || admission.AgentRunId is null)
            return SandboxError("forbidden", "A trusted active Session Run is required.");
        var run = await emailRuns.GetAsync(new(session.AgentInstanceId, session.ProfileId.Value), admission.AgentRunId.Value, ct);
        if (run?.SessionId != sessionId || run.Claim is null || run.CancellationRequested)
            return SandboxError("forbidden", "The current Run claim is unavailable.");
        if (call.Name == ToolCatalog.SandboxProfiles)
        {
            var environment = await sandboxService.InspectAsync(sessionId, ct);
            var selected = environment?.TransitionProfileId ?? environment?.SelectedProfileId;
            var profiles = new List<object>();
            foreach (var id in definition.SandboxPolicy?.AllowedProfileIds.Take(16) ?? [])
            {
                var pinned = definition.ExecutionSandboxProfiles?.FirstOrDefault(p => p.Id == id)
                    ?? (definition.ExecutionSandboxProfiles is null && definition.ExecutionSandboxProfile?.Id == id ? definition.ExecutionSandboxProfile : null);
                var current = await sandboxStore.GetProfileAsync(id, ct: ct);
                var availability = pinned is null || sandboxAuthority is null ? null : await sandboxAuthority.InspectAsync(pinned, ct);
                var eligible = pinned?.AdmittedForExecution != false && current?.Enabled == true && availability?.Eligible == true && sandboxReadiness?.State == "Ready"
                    && availability.CredentialRevision == pinned?.RegistryCredentialRevision && availability.SecretRevision == pinned?.RegistrySecretRevision;
                profiles.Add(new { id, name = pinned?.Name ?? id, description = ToolJsonResults.ClipUtf8Prefix(pinned?.Description ?? "", 160), revision = pinned?.Revision,
                    available = eligible, availability = eligible ? "Eligible" : pinned is null || pinned.AdmittedForExecution == false ? "NotAdmitted" : current?.Enabled != true ? "Disabled" : sandboxReadiness?.State != "Ready" ? "ServerUnavailable" : availability?.Status ?? "Unavailable",
                    isDefault = id == definition.SandboxPolicy?.DefaultProfileId, selected = id == (selected ?? definition.SandboxPolicy?.DefaultProfileId) });
            }
            var totalCount = profiles.Count;
            while (true)
            {
                var json = JsonSerializer.Serialize(new { profiles, totalCount, truncated = profiles.Count < totalCount,
                    defaultProfileId = definition.SandboxPolicy?.DefaultProfileId, selectedProfileId = selected ?? definition.SandboxPolicy?.DefaultProfileId,
                    descriptionsAreInformational = true }, SandboxJson);
                if (System.Text.Encoding.UTF8.GetByteCount(json) <= remainingBytes || profiles.Count == 0)
                    return TextResult(ToolJsonResults.FitToBudget(remainingBytes, json));
                profiles.RemoveAt(profiles.Count - 1);
            }
        }
        if (call.Name == ToolCatalog.SandboxStatus)
        {
            var environment = await sandboxService.InspectAsync(sessionId, ct);
            // Owner UI evidence stays out of ordinary model/conversation context.
            var executionId = environment?.ActiveExecutionId ?? environment?.LastExecutionId;
            var execution = executionId is { } id ? await sandboxService.InspectProcessAsync(sessionId, id, ct) : null;
            return TextResult(ToolJsonResults.FitToBudget(remainingBytes, JsonSerializer.Serialize(new { environment = environment is null ? null : environment with { Initialization = null },
                initializationState = environment?.Initialization?.State, selectedProfile = environment?.TransitionProfileId ?? environment?.SelectedProfileId ?? definition.SandboxPolicy?.DefaultProfileId,
                latestExecution = execution is null ? null : execution with { Stdout = "", Stderr = "", CommandText = "" } }, SandboxJson)));
        }
        if (call.Name == ToolCatalog.SandboxReset)
        {
            var observed = await sandboxService.InspectAsync(sessionId, ct);
            await sandboxService.StopSessionAsync(sessionId, ct, observed?.Generation, observed?.ActiveExecutionId ?? observed?.LastExecutionId, true);
            return TextResult("""{"ok":true,"status":"terminated","message":"Sandbox-only state was discarded. Canonical /working, /home and Artifacts are preserved; the next command provisions a new generation."}""");
        }
        SandboxCommandResult result;
        if (call.Name is ToolCatalog.SandboxProcessStatus or ToolCatalog.SandboxProcessStop)
        {
            if (!TryString(args, "executionId", out var id) || !Guid.TryParse(id, out var executionId)) return SandboxError("invalid", "A Core executionId is required.");
            result = call.Name == ToolCatalog.SandboxProcessStop ? await sandboxService.StopProcessAsync(sessionId, executionId, ct) : await sandboxService.InspectProcessAsync(sessionId, executionId, ct);
        }
        else
        {
            if (call.Name == ToolCatalog.SandboxExec && definition.SandboxPolicy is null)
                return SandboxError("unavailable", "Configure the Definition's exact allowed sandbox profiles before using sandbox.exec.");
            if (args.TryGetProperty("profileId", out var profileValue) && (profileValue.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(profileValue.GetString())))
                return SandboxError("invalid", "profileId must be an exact authorized profile ID.");
            TryString(args, "profileId", out var selectedId);
            var profile = definition.ExecutionSandboxProfile ?? definition.ExecutionSandboxProfiles?.FirstOrDefault();
            if (profile is null) return SandboxError("unavailable", "This Run has no pinned sandbox profile. Register and verify an authorized prebuilt image, then start a new Run.");
            var access = new SandboxAccess(sessionId, session.AgentInstanceId, run.AgentRunId, run.Claim.Generation, call.Id, profile, definition);
            if (args.TryGetProperty("command", out var commandValue) && commandValue.ValueKind != JsonValueKind.String
                || args.TryGetProperty("cwd", out var cwdValue) && cwdValue.ValueKind != JsonValueKind.String
                || args.TryGetProperty("timeoutSeconds", out var timeoutValue) && (timeoutValue.ValueKind != JsonValueKind.Number || !timeoutValue.TryGetInt32(out _))
                || args.TryGetProperty("background", out var backgroundValue) && backgroundValue.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                return SandboxError("invalid", "command/cwd must be strings, timeoutSeconds an integer, and background a boolean.");
            TryString(args, "command", out var command); TryString(args, "cwd", out var cwd);
            string[]? argv = null;
            if (args.TryGetProperty("argv", out var list))
            {
                if (list.ValueKind != JsonValueKind.Array || list.EnumerateArray().Any(e => e.ValueKind != JsonValueKind.String)) return SandboxError("invalid", "argv must contain strings.");
                argv = list.EnumerateArray().Select(e => e.GetString()!).ToArray();
            }
            var allowed = new[] { "command", "argv", "cwd", "timeoutSeconds", "background", "profileId" };
            if (args.EnumerateObject().Any(p => !allowed.Contains(p.Name, StringComparer.Ordinal))) return SandboxError("invalid", "Unknown sandbox argument; infrastructure and ownership settings are not tool arguments.");
            var timeout = args.TryGetProperty("timeoutSeconds", out var seconds) && seconds.TryGetInt32(out var parsed) ? parsed : profile.MaxCommandSeconds;
            var background = args.TryGetProperty("background", out var bg) && bg.ValueKind == JsonValueKind.True;
            result = await sandboxService.ExecuteAsync(access, new(args.TryGetProperty("command",out _) ? command : null, argv,
                args.TryGetProperty("cwd",out _) ? cwd : "/working", timeout, background, args.TryGetProperty("profileId", out _) ? selectedId : null), ct);
        }
        var stderr = ToolJsonResults.ClipUtf8Prefix(result.Stderr, Math.Max(0, (remainingBytes - 700) / 4));
        return TextResult(ToolJsonResults.FitJsonWithContentField(remainingBytes, result.Stdout,
            (stdout, truncated) => JsonSerializer.Serialize(new { result.ExecutionId, result.Status, result.ExitCode, stdout, stderr,
                combinedLogs = result.Background,
                truncated = truncated || result.Truncated, result.DurationMs, result.WorkspaceSyncStatus, result.DiagnosticId,
                result.ProfileId, result.ProfileRevision, result.Generation, result.EnvironmentState, result.EnvironmentGeneration, result.Phase, result.FailureCategory, result.CancellationReason, result.TerminationConfirmed,
                error = result.Error ?? (result.Status == "Indeterminate" ? "outcome_unknown" : null),
                recovery = result.Recovery ?? (result.Status == "Indeterminate" ? "Outcome unknown. Do not repeat automatically; inspect and explicitly reset." : null) }, SandboxJson))) with { OutcomeUnknown = result.Error is null && result.Status == "Indeterminate", RecoveryRequired = result.Error == "recovery_required", SafeFailureSummary = result.FailureCategory is null ? null : SandboxFailureSummary.Describe(result.FailureCategory, result.Phase, result.WorkspaceSyncStatus, result.TerminationConfirmed) };
    }
}
