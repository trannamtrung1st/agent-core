using System.Text.Json;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Application.Tools;
using AgentCore.Domain.Conversation;

namespace AgentCore.Application.Sessions;

public sealed partial class SessionRuntime
{
    private async Task<string> ExecuteTenantCompletionToolAsync(AgentRun parent, ModelToolCall call, JsonElement args, CancellationToken ct)
    {
        if (_agentRuns is not ITenantCompletionRuntimeStore completions || parent.Claim is null)
            throw new AccessDeniedException("completion_unavailable");
        var permitted = call.Name switch {
            ToolCatalog.BackgroundList => new[] { "limit", "cursor", "scope" },
            ToolCatalog.BackgroundInspect => new[] { "backgroundSessionId" },
            ToolCatalog.BackgroundTake => new[] { "backgroundSessionId", "revision" },
            ToolCatalog.BackgroundAcknowledge => new[] { "backgroundSessionId", "revision", "token", "usage" },
            _ => throw new ArgumentException("Unsupported completion operation.") };
        var names = args.EnumerateObject().Select(p => p.Name).ToArray();
        if (names.Distinct(StringComparer.Ordinal).Count() != names.Length || names.Any(n => !permitted.Contains(n)))
            throw new ArgumentException("Unexpected completion argument.");
        var frame = new TenantRuntimeFrame(parent.Owner, SessionId, parent.AgentRunId, parent.Claim.Generation);
        if (call.Name == ToolCatalog.BackgroundList)
        {
            var limit = args.TryGetProperty("limit", out var size) ? size.GetInt32() : 20;
            Guid? cursor = args.TryGetProperty("cursor", out var before) ? before.GetGuid() : null;
            var scope = args.TryGetProperty("scope", out var value) ? value.GetString() : "pending_results";
            if (scope is not ("from_current_session" or "pending_results")) throw new ArgumentException("Unsupported completion scope.");
            var page = await completions.ReadTenantCompletionsAsync(frame, call.Name, null, cursor, scope == "pending_results", limit, ct).ConfigureAwait(false);
            return JsonSerializer.Serialize(new { untrusted = true, completions = page.Items.Select(view => {
                var run = view.Source.CurrentTaskRun ?? view.Source.Run;
                return new { backgroundSessionId = run.SessionId, agentRunId = view.Source.Run.AgentRunId, sourceAgentRunId = run.AgentRunId, readRequestId = view.Source.CompletionReadRequestId,
                    parentSessionId = SessionId, status = run.Status.ToString(), updatedAtUtc = run.UpdatedAtUtc,
                    progress = run.Progress?.Summary is { } progress ? ToolJsonResults.ClipUtf8Prefix(progress, 320) : null, inbox = InboxProjection(view.Inbox) };
            }), hasMore = page.HasMore, nextCursor = page.NextCursor, pendingCount = page.PendingCount, pendingCountHasMore = page.PendingCountHasMore });
        }
        var session = args.GetProperty("backgroundSessionId").GetGuid();
        TenantCompletionView view;
        if (call.Name is ToolCatalog.BackgroundTake or ToolCatalog.BackgroundAcknowledge)
        {
            var revision = args.GetProperty("revision").GetInt64();
            var usage = call.Name == ToolCatalog.BackgroundAcknowledge ? args.GetProperty("usage").GetString() ?? throw new ArgumentException("Usage is required.") : null;
            view = await completions.MutateTenantCompletionAsync(frame, session, revision, call.Id,
                usage is null ? _ids.NewId() : args.GetProperty("token").GetGuid(), usage, ct).ConfigureAwait(false);
        }
        else view = (await completions.ReadTenantCompletionsAsync(frame, call.Name, session, null, false, 1, ct).ConfigureAwait(false)).Items[0];
        var source = view.Source.CurrentTaskRun ?? view.Source.Run; var item = view.Inbox;
        var summary = source.Result?.Text ?? source.Failure?.Summary ?? (source.Status == AgentRunStatus.Cancelled ? "Background work was cancelled." : null);
        return JsonSerializer.Serialize(new { untrusted = true, backgroundSessionId = session, parentSessionId = SessionId,
            agentRunId = view.Source.Run.AgentRunId, sourceAgentRunId = source.AgentRunId, readRequestId = view.Source.CompletionReadRequestId, status = source.Status.ToString(),
            outcome = source.Result?.OutcomeKind.ToString(), summary = summary is null ? null : ToolJsonResults.ClipUtf8Prefix(summary, 2400),
            updatedAtUtc = source.UpdatedAtUtc, progress = source.Progress?.Summary is { } progress ? ToolJsonResults.ClipUtf8Prefix(progress, 320) : null,
            attentionRequired = source.Result?.AttentionRequired == true || source.Status == AgentRunStatus.Failed, failureCode = source.Failure?.Code,
            artifacts = view.Artifacts.Take(2).Select(file => new { artifactId = file.ArtifactId, name = ToolJsonResults.ClipUtf8Prefix(file.DisplayName, 160),
                contentType = ToolJsonResults.ClipUtf8Prefix(file.ContentType, 80), file.ByteSize }), artifactsHasMore = view.Artifacts.Count > 2,
            inbox = InboxProjection(item), token = call.Name == ToolCatalog.BackgroundTake ? item?.ClaimToken : null,
            receipt = call.Name == ToolCatalog.BackgroundTake ? new { parentAgentRunId = item?.ClaimRunId, toolCallId = item?.ClaimToolCallId,
                generation = item?.ClaimGeneration, expiresAtUtc = item?.ClaimExpiresAtUtc } : null, acknowledgmentPending = item?.Acknowledgment is not null });
    }
}
