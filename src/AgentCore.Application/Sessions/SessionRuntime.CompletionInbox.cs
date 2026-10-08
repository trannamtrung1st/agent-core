using System.Text.Json;
using AgentCore.Application.Agents;
using AgentCore.Application.Events;
using AgentCore.Application.Execution;
using AgentCore.Application.Ports;
using AgentCore.Application.Tools;
using AgentCore.Domain.Conversation;

namespace AgentCore.Application.Sessions;

public sealed partial class SessionRuntime
{
    private sealed record CompletionToolResult(string Json, bool Suspended = false);
    private sealed record CompletionToolReceived(EventContext Context, Guid ResponseId, ModelToolCall Call,
        TaskCompletionSource<CompletionToolResult> Completed) : SessionInput(Context);

    private async Task<CompletionToolResult> RequestCompletionToolAsync(EventContext cause, Guid responseId, ModelToolCall call, CancellationToken ct)
    {
        var result = new TaskCompletionSource<CompletionToolResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        BeginWork();
        if (!Enqueue(new CompletionToolReceived(WorkerContext(cause), responseId, call, result), urgent: true))
            return new(SkillLoadAdmission.Error("stale", "Execution no longer owns this request."));
        return await result.Task.WaitAsync(ct).ConfigureAwait(false);
    }

    private async Task HandleCompletionToolAsync(CompletionToolReceived input, CancellationToken ct)
    {
        try
        {
            if (!await OwnsWorkerAsync(input.Context, input.ResponseId, ct).ConfigureAwait(false))
            { input.Completed.TrySetResult(new(SkillLoadAdmission.Error("Conflict", "Execution generation is stale."))); return; }
            var parent = _boundAgentRun!;
            var current = _runAuthority is null ? _snapshot.Definition : await _runAuthority.CurrentDefinitionAsync(parent, ct).ConfigureAwait(false);
            if (current is null || !RolePermissions.AllowsTool(current, input.Call.Name) || !RolePermissions.AllowsTool(_snapshot.Definition, input.Call.Name))
            { input.Completed.TrySetResult(new(SkillLoadAdmission.Error("forbidden", "This capability is not authorized."))); return; }
            using var document = JsonDocument.Parse(input.Call.ArgumentsJson);
            var args = document.RootElement;
            if (args.ValueKind != JsonValueKind.Object) throw new ArgumentException("Tool arguments must be an object.");
            var now = _time.GetUtcNow();
            if (input.Call.Name == ToolCatalog.ExecutionWait)
            {
                var mode = args.GetProperty("mode").GetString();
                var duration = mode == "duration";
                var allowed = duration ? new[] { "mode", "seconds" } : new[] { "mode", "backgroundSessionIds", "until", "timeoutSeconds" };
                if (mode is not ("duration" or "background") || args.EnumerateObject().Any(p => !allowed.Contains(p.Name))) throw new ArgumentException("Wait condition is unsupported.");
                var seconds = args.GetProperty(duration ? "seconds" : "timeoutSeconds").GetDouble();
                if (!double.IsFinite(seconds) || seconds <= 0 || seconds > AgentRunLimits.MaxWaitSeconds) throw new ArgumentException("Wait seconds must be greater than zero and at most 300.");
                var ids = duration ? [] : args.GetProperty("backgroundSessionIds").EnumerateArray().Select(v => v.GetGuid()).ToArray();
                var until = duration ? "all" : args.GetProperty("until").GetString();
                if (until is not ("all" or "any")) throw new ArgumentException("Wait until must be all or any.");
                var wait = new AgentRunWait(input.Call.Id, duration ? AgentRunWaitMode.Duration : AgentRunWaitMode.Background,
                    ids, until == "all" ? AgentRunWaitUntil.All : AgentRunWaitUntil.Any, now, now.AddSeconds(seconds));
                _boundAgentRun = await _agentRuns.ApplyAsync(parent.Owner, parent.AgentRunId,
                    new AgentRunCommand.SuspendWait(parent.Revision, now, parent.Claim!.Generation,
                        parent.Checkpoint ?? throw new ArgumentException("Pending wait checkpoint is missing."), wait), ct).ConfigureAwait(false);
                await PublishProgressAsync(input.Context, input.ResponseId, ResponseProgressKind.WaitingExternal,
                    ResponseProgressState.Started, _ids.NewId(), duration ? "Waiting for the requested duration…" : "Waiting for background results…", ct).ConfigureAwait(false);
                input.Completed.TrySetResult(new("", true));
                return;
            }
            var permitted = input.Call.Name switch {
                ToolCatalog.BackgroundList => new[] { "limit", "cursor", "scope" }, ToolCatalog.BackgroundInspect => new[] { "backgroundSessionId" },
                ToolCatalog.BackgroundTake => new[] { "backgroundSessionId", "revision" },
                _ => new[] { "backgroundSessionId", "revision", "token", "usage" } };
            if (args.EnumerateObject().Any(p => !permitted.Contains(p.Name))) throw new ArgumentException("Unexpected completion argument.");
            var limit = args.TryGetProperty("limit", out var limitValue) ? limitValue.GetInt32() : 20;
            if (limit is < 1 or > 20) throw new ArgumentException("Background list limit must be 1–20.");
            var inbox = await _agentRuns.ListCompletionInboxAsync(parent.Owner, SessionId, AgentRunLimits.MaxListLimit, now, ct).ConfigureAwait(false);
            if (input.Call.Name == ToolCatalog.BackgroundList)
            {
                var scope = args.TryGetProperty("scope", out var scopeValue) ? scopeValue.GetString() : "pending_results";
                if (scope is not ("from_current_session" or "pending_results")) throw new ArgumentException("Unsupported background scope.");
                Guid? cursor = args.TryGetProperty("cursor", out var cursorValue) ? cursorValue.GetGuid() : null;
                var children = await _agentRuns.ListBackgroundPageAsync(parent.Owner, SessionId, cursor, scope == "pending_results", limit + 1, ct).ConfigureAwait(false);
                var page = children.Take(limit).ToArray();
                var projections = new List<object>();
                foreach (var child in page)
                {
                    var accounting = await _agentRuns.GetCompletionInboxAsync(parent.Owner, SessionId, child.Run.AgentRunId, now, ct).ConfigureAwait(false);
                    projections.Add(new { backgroundSessionId = child.Run.SessionId, agentRunId = child.Run.AgentRunId,
                        parentSessionId = SessionId, status = child.Run.Status.ToString(), updatedAtUtc = child.Run.UpdatedAtUtc, progress = child.Run.Progress?.Summary,
                        inbox = InboxProjection(accounting) });
                }
                input.Completed.TrySetResult(new(JsonSerializer.Serialize(new { untrusted = true, completions = projections,
                    hasMore = children.Count > limit, nextCursor = children.Count > limit ? (Guid?)page[^1].Run.AgentRunId : null,
                    pendingCount = inbox.Count(i => i.Status == CompletionInboxStatus.Pending), pendingCountHasMore = inbox.Count == AgentRunLimits.MaxListLimit })));
                return;
            }
            var sessionId = args.GetProperty("backgroundSessionId").GetGuid();
            var childSession = await _store.LoadMetadataAsync(sessionId, ct).ConfigureAwait(false);
            var childRun = childSession?.Origin.InitialBackgroundAgentRunId is { } initialId
                && childSession.AgentInstanceId == parent.AgentInstanceId && childSession.ProfileId == parent.ProfileId
                && childSession.Origin.OriginatingSessionId == SessionId ? await _agentRuns.GetAsync(parent.Owner, initialId, ct).ConfigureAwait(false) : null;
            if (childRun is null) throw AgentCoreErrors.NotFound("Owned initial background child was not found.");
            var item = await _agentRuns.GetCompletionInboxAsync(parent.Owner, SessionId, childRun.AgentRunId, now, ct).ConfigureAwait(false);
            if (input.Call.Name == ToolCatalog.BackgroundTake || input.Call.Name == ToolCatalog.BackgroundAcknowledge)
            {
                if (item is null) throw AgentCoreErrors.NotFound("Reportable terminal completion was not found.");
                var revision = args.GetProperty("revision").GetInt64();
                item = input.Call.Name == ToolCatalog.BackgroundTake
                    ? await _agentRuns.TakeCompletionAsync(parent.Owner, parent.AgentRunId, parent.Claim!.Generation, childRun.AgentRunId,
                        revision, input.Call.Id, _ids.NewId(), now, ct).ConfigureAwait(false)
                    : await _agentRuns.AcknowledgeCompletionAsync(parent.Owner, parent.AgentRunId, parent.Claim!.Generation, childRun.AgentRunId,
                        revision, args.GetProperty("token").GetGuid(), args.GetProperty("usage").GetString()!, now, ct).ConfigureAwait(false);
            }
            var summary = childRun.Result?.Text ?? childRun.Failure?.Summary ?? (childRun.Status == AgentRunStatus.Cancelled ? "Background work was cancelled." : null);
            var artifacts = await _tools.CompletionArtifactsAsync(sessionId, ct).ConfigureAwait(false);
            input.Completed.TrySetResult(new(JsonSerializer.Serialize(new { untrusted = true, backgroundSessionId = sessionId,
                parentSessionId = SessionId, updatedAtUtc = childRun.UpdatedAtUtc, progress = childRun.Progress?.Summary,
                attentionRequired = childRun.Result?.AttentionRequired == true || childRun.Status == AgentRunStatus.Failed,
                failureCode = childRun.Failure?.Code,
                artifacts = artifacts.Take(2).Select(file => new { artifactId = file.ArtifactId, name = ToolJsonResults.ClipUtf8Prefix(file.DisplayName, 160), contentType = ToolJsonResults.ClipUtf8Prefix(file.ContentType, 80), file.ByteSize }),
                artifactsHasMore = artifacts.Count > 2,
                agentRunId = childRun.AgentRunId, status = childRun.Status.ToString(), outcome = childRun.Result?.OutcomeKind.ToString(),
                summary = summary is null ? null : ToolJsonResults.ClipUtf8Prefix(summary, 2400), inbox = InboxProjection(item),
                token = input.Call.Name == ToolCatalog.BackgroundTake ? item?.ClaimToken : null,
                receipt = input.Call.Name == ToolCatalog.BackgroundTake ? new { parentAgentRunId = item?.ClaimRunId, toolCallId = item?.ClaimToolCallId, generation = item?.ClaimGeneration, expiresAtUtc = item?.ClaimExpiresAtUtc } : null,
                acknowledgmentPending = item?.Acknowledgment is not null })));
        }
        catch (Exception e) when (e is AgentCoreException or ArgumentException or JsonException or InvalidOperationException or KeyNotFoundException or FormatException)
        { input.Completed.TrySetResult(new(SkillLoadAdmission.Error(e is AgentCoreException core ? core.Code : "ValidationError", e is AgentCoreException ? e.Message : "Provide a supported bounded completion or wait request."))); }
        finally { input.Completed.TrySetResult(new(SkillLoadAdmission.Error("failed", "Completion operation did not finish."))); }
    }

    private static object? InboxProjection(CompletionInboxItem? item) => item is null ? null : new {
        status = item.Status.ToString(), revision = item.Revision, handledByRunId = item.HandledByRunId, reportActivationId = item.ReportActivationId,
        acknowledgmentPending = item.Acknowledgment is not null };
}
