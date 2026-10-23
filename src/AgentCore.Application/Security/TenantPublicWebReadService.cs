using System.Text;
using System.Text.Json;
using AgentCore.Application.Execution;
using AgentCore.Application.Ports;
using AgentCore.Application.Tools;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Definitions;
using AgentCore.Domain.Security;

namespace AgentCore.Application.Security;

public sealed class TenantPublicWebReadService(TenantPublicWebOperationTarget targets,IWebSearchProvider search,IPublicWebFetcher fetch,TimeProvider time)
{
    public async ValueTask<string> InspectAsync(TenantRuntimeFrame frame, ModelToolCall call, int remainingBytes, CancellationToken ct = default)
    {
        var args = TenantPublicWebReadContract.Parse(call.Name, call.ArgumentsJson);
        var started = time.GetTimestamp();
        try
        {
            var before = await targets.RequireAsync(frame, args.Tool, ct, audit:true).ConfigureAwait(false);
            if (args.Tool == ToolCatalog.WebSearch && !search.IsAvailable) throw new AccessDeniedException("provider_unavailable");
            var duration = before.Run.Claim!.LeaseExpiresAtUtc - time.GetUtcNow() - TimeSpan.FromSeconds(1);
            var perTool = TimeSpan.FromSeconds(Math.Min(30, before.Run.Admission.ExecutionBudget?.Profile.PerToolSeconds ?? 30)) - time.GetElapsedTime(started);
            if (duration > perTool) duration = perTool;
            if (before.Run.Checkpoint?.PauseBudgetClock(time.GetUtcNow()).RemainingOverallBudgetMs is { } overall && duration.TotalMilliseconds > overall) duration = TimeSpan.FromMilliseconds(overall);
            if (duration <= TimeSpan.Zero) throw new AccessDeniedException("execution_budget_exhausted");
            using var deadline = new CancellationTokenSource(duration, time); using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, deadline.Token);
            Task<string>? pending = null; string result;
            try
            {
                pending = ReadAsync(args, remainingBytes, linked.Token);
                result = await pending.WaitAsync(linked.Token).ConfigureAwait(false);
                linked.Token.ThrowIfCancellationRequested();
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { if (pending is not null) _ = ObserveAsync(pending); result = Error("timeout"); }
            catch (OperationCanceledException) { if (pending is not null) _ = ObserveAsync(pending); throw; }
            catch (Exception) { if (pending is not null) _ = ObserveAsync(pending); result = Error("public_web_unavailable"); }
            ct.ThrowIfCancellationRequested();
            await targets.FenceAsync(frame, args.Tool, before, ct).ConfigureAwait(false);
            return result;
        }
        catch (Exception e) when (e is AccessDeniedException or SecurityConflictException)
        {
            await targets.RecordFailureAsync(frame, "sessions.read", e is AccessDeniedException denied ? denied.ReasonCode : ((SecurityConflictException)e).ReasonCode, ct).ConfigureAwait(false);
            throw;
        }
    }
    private async Task<string> ReadAsync(TenantPublicWebReadArguments args, int remaining, CancellationToken ct)
    {
        if (args.Tool == ToolCatalog.WebFetch)
        {
            var result = await fetch.FetchAsync(new(args.Url!), ct).ConfigureAwait(false); ct.ThrowIfCancellationRequested();
            if (result.ErrorCode is { } error) return Error(Safe(error));
            var finalUrl = TenantPublicWebReadContract.PublicUrl(result.FinalUrl).AbsoluteUri;
            var content = ToolJsonResults.ClipUtf8Prefix(result.Text, 256 * 1024);
            return ToolJsonResults.FitJsonWithContentField(remaining, content, (text, clipped) => JsonSerializer.Serialize(new {
                tenantPublicWebRead = true, readCommandHash = args.Hash, finalUrl, contentType = result.ContentType is { Length: <= 200 } type ? type : null,
                text, truncated = result.Truncated || clipped || content.Length != result.Text.Length, untrusted = true }));
        }
        var found = await search.SearchAsync(new(args.Query!, args.Limit), ct).ConfigureAwait(false); ct.ThrowIfCancellationRequested();
        var items = new List<WebSearchItem>(); var truncated = found.Truncated || found.Results.Count > args.Limit;
        foreach (var item in found.Results.Take(args.Limit))
        {
            string url; try { url = TenantPublicWebReadContract.PublicUrl(item.Url).AbsoluteUri; } catch (ArgumentException) { truncated = true; continue; }
            var title = ToolJsonResults.ClipUtf8Prefix(item.Title, 512); var snippet = ToolJsonResults.ClipUtf8Prefix(item.Snippet, 4096);
            truncated |= title != item.Title || snippet != item.Snippet; items.Add(new(title, url, snippet));
        }
        while (true)
        {
            var json = JsonSerializer.Serialize(new { tenantPublicWebRead = true, readCommandHash = args.Hash,
                results = items.Select(i => new { title=i.Title, url=i.Url, snippet=i.Snippet }), truncated, untrusted=true });
            if (Encoding.UTF8.GetByteCount(json) <= remaining || items.Count == 0) return ToolJsonResults.FitToBudget(remaining, json);
            items.RemoveAt(items.Count - 1); truncated = true;
        }
    }
    public static bool HasStored(AgentRun run) => AgentRunToolCallCheckpoint.TryRead(run.Checkpoint, out var messages)
        && messages!.Any(m => m.Role == ModelRole.Tool && TenantPublicWebReadContract.IsRead(m.Name ?? ""));
    public async ValueTask RequireStoredAsync(AgentRun run, CancellationToken ct = default)
    {
        if (!AgentRunToolCallCheckpoint.TryRead(run.Checkpoint, out var messages)) return;
        var reads = TenantPublicWebReadRules.StoredTools(run);
        foreach (var tool in reads) _ = await targets.RequireAsync(new(run.Owner, run.SessionId, run.AgentRunId, run.Claim!.Generation), tool, ct).ConfigureAwait(false);
    }
    private static readonly HashSet<string> Codes = new(StringComparer.Ordinal) { "forbidden_host", "credentials_in_url", "invalid_url", "http_error", "body_too_large", "unsupported_content_type", "too_many_redirects", "transport_error", "timeout", "unavailable" };
    private static string Safe(string error) => Codes.Contains(error) ? error : "public_web_unavailable";
    private static string Error(string error) => JsonSerializer.Serialize(new { error, untrusted = true });
    private static async Task ObserveAsync(Task pending) { try { await pending.ConfigureAwait(false); } catch (Exception) { } }
}
