using AgentCore.Application.Execution;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentCore.Application.Ports;
using AgentCore.Application.Tools;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;

namespace AgentCore.Application.Security;

public sealed class TenantContinuityReadService(ITenantContinuityStore storage, ITenantLearnedMemoryStore memories,
    TenantOperationAuthorizer operations, IamAuthorizer iam, TimeProvider time)
{
    public async ValueTask<TenantContinuityObservation> PrepareAsync(TenantRunReference reference, Guid? generation, string tool, string arguments,
        CancellationToken ct = default, PreparedContinuityRead? expected = null)
    {
        var result = await storage.ObserveTenantContinuityAsync(reference, generation, TenantContinuityToolContract.Parse(tool, arguments), expected, ct: ct).ConfigureAwait(false);
        if (generation is not null && result.Reader.Run.Claim?.LeaseExpiresAtUtc <= time.GetUtcNow()) throw new SecurityConflictException("execution_claim_expired");
        return result;
    }
    public async ValueTask<OperationAuthorizationDecision> DecisionAsync(TenantContinuityObservation observed, CancellationToken ct = default)
    {
        var reader = observed.Reader; var primary = TenantSessionRules.Scope(reader.Security.Tenant.TenantId, reader.Run.SessionId); var review = false;
        foreach (var (key, scope) in TenantContinuityReadRules.Targets(observed).Distinct())
        {
            if (key == "sessions.read" && scope == primary) continue;
            var decision = await operations.Check(reader.Security, reader.Run, key, scope, ct).ConfigureAwait(false);
            if (decision.Decision == OperationDecisionKind.Denied || decision.ApprovalKind == ApprovalKind.ExceptionalGrant)
                return new(OperationDecisionKind.Denied, decision.Decision == OperationDecisionKind.Denied ? decision.ReasonCode : "continuity_read_required", "sessions.read", primary);
            review |= decision.Decision == OperationDecisionKind.RequiresApproval;
        }
        var result = await operations.Check(reader.Security, reader.Run, "sessions.read", primary, ct, review).ConfigureAwait(false);
        if (result.ApprovalKind == ApprovalKind.ExceptionalGrant) return result with { Decision = OperationDecisionKind.Denied, ReasonCode = "continuity_read_required", ApprovalKind = null };
        if (result.Decision == OperationDecisionKind.RequiresApproval && (observed.Sessions.Count > 0 || observed.Memory?.Items.Count > 0 || observed.Experience?.Items.Count > 0))
        {
            try { await RequireAsync(reader.Run, Sources(observed.Sessions, reader.Security.Tenant.TenantId), MemorySources(observed), ct, ExperienceSources(observed)).ConfigureAwait(false);
                return result with { Decision = OperationDecisionKind.Allowed, ApprovalKind = null, ReasonCode = "exact_read_consent" }; }
            catch (Exception error) when (error is AccessDeniedException or SecurityConflictException) { }
        }
        return result;
    }
    public async ValueTask<string> InspectAsync(TenantRuntimeFrame frame, ModelToolCall call, int budget, CancellationToken ct = default)
    {
        var reference = new TenantRunReference(frame.Owner.TenantId!.Value, frame.RunId);
        var before = await PrepareAsync(reference, frame.Generation, call.Name, call.ArgumentsJson, ct).ConfigureAwait(false);
        var decision = await DecisionAsync(before, ct).ConfigureAwait(false);
        if (decision.Decision != OperationDecisionKind.Allowed) return JsonSerializer.Serialize(new { decision = decision.Decision == OperationDecisionKind.Denied ? "denied" : "requiresApproval", reasonCode = decision.ReasonCode, applied = false });
        var after = await PrepareAsync(reference, frame.Generation, call.Name, call.ArgumentsJson, ct, before.Value).ConfigureAwait(false);
        if (after.Reader.Security.Tenant.Revision != before.Reader.Security.Tenant.Revision || after.Reader.Run.Revision != before.Reader.Run.Revision
            || after.Reader.Agent.Instance.Revision != before.Reader.Agent.Instance.Revision) throw new SecurityConflictException("continuity_source_changed");
        return Serialize(after, budget, time.GetUtcNow());
    }
    public ValueTask<TenantContinuityObservation> InspectionAsync(TenantApprovalRequest request, Guid human, CancellationToken ct = default) =>
        storage.ObserveTenantContinuityAsync(new(request.TenantId, request.RunId), null, TenantContinuityToolContract.Parse(request.Operation.ContinuityRead!.ToolName,
            request.Operation.ContinuityRead.ArgumentsJson), request.Operation.ContinuityRead, human, ct);
    // The typed owner reobserves the saved operation and its feature-specific
    // source/reviewer rights; the shared approval service owns lifecycle only.
    public async ValueTask<bool> ReviewAsync(TenantApprovalRequest request, Guid human, CancellationToken ct = default)
    {
        var value = request.Operation.ContinuityRead ?? throw new AccessDeniedException("approval_unavailable");
        var observed = await PrepareAsync(new(request.TenantId, request.RunId), null, value.ToolName, value.ArgumentsJson, ct, value).ConfigureAwait(false);
        var independent = await IndependentAsync(request, observed, ct).ConfigureAwait(false);
        await ReviewerAsync(request, observed, human, ct).ConfigureAwait(false);
        return independent;
    }
    public ValueTask ReviewerAsync(TenantApprovalRequest request, TenantContinuityObservation observed, Guid human, CancellationToken ct = default) => TenantContinuityReadRules.ReviewerAsync(request, observed, human, iam, ct);
    public async ValueTask<bool> IndependentAsync(TenantApprovalRequest request, TenantContinuityObservation observed, CancellationToken ct)
    {
        foreach (var (key, scope) in TenantContinuityReadRules.Targets(observed).Distinct())
            if (TenantOperationPolicyService.Effective(observed.Reader.Security, PermissionCatalog.Get(key)).IndependentReview && (key == request.Permission
                && scope == TenantApprovalRules.Scope(request.TenantId, request.Operation) || await TenantSandboxReadOperationRules.NeedsReviewAsync(observed.Reader.Security, request.OriginalInitiatorPrincipalId, key, scope, iam, ct).ConfigureAwait(false))) return true;
        return false;
    }
    public async ValueTask<string> InspectApprovedAsync(TenantRuntimeFrame frame, TenantApprovalRequest request, TenantOperationReceipt receipt, int budget, CancellationToken ct = default)
    {
        if (request.Operation.ContinuityRead is not { } value || receipt.Outcome != TenantOperationOutcome.Completed || receipt.RequestId != request.RequestId
            || receipt.OperationId != request.Operation.OperationId || receipt.RunId != frame.RunId || receipt.ActionHash != request.Operation.ActionHash) throw new AccessDeniedException("continuity_read_review_required");
        var observed = await PrepareAsync(new(request.TenantId, frame.RunId), frame.Generation, value.ToolName, value.ArgumentsJson, ct, value).ConfigureAwait(false);
        await RequireAsync(observed.Reader.Run, Sources(observed.Sessions, request.TenantId), MemorySources(observed), ct, ExperienceSources(observed)).ConfigureAwait(false); return Serialize(observed, budget, time.GetUtcNow(), receipt.OperationId);
    }
    public async ValueTask RequireAsync(AgentRun run, IReadOnlyList<TenantHistoricalSource>? history, IReadOnlyList<TenantMemorySource>? memory, CancellationToken ct, IReadOnlyList<TenantExperienceFactSource>? experience = null)
    {
        var reference = new TenantRunReference(run.Owner.TenantId!.Value, run.AgentRunId);
        if (history is { Count: > 0 }) await storage.RequireTenantHistoricalSourcesAsync(reference, history, run.Claim?.Generation, ct).ConfigureAwait(false);
        if (memory is { Count: > 0 }) await memories.RequireTenantMemorySourcesAsync(reference, memory, run.Claim?.Generation, ct).ConfigureAwait(false);
        if (experience is { Count: > 0 }) await (storage as ITenantExperienceStore ?? throw new AccessDeniedException("experience_unavailable"))
            .RequireTenantExperienceSourcesAsync(reference, [], experience, run.Claim?.Generation, ct).ConfigureAwait(false);
    }
    public async ValueTask RequireExperienceAsync(AgentRun run, CancellationToken ct)
    {
        var sources = TenantExperienceReferences.Sources(run.Checkpoint); var facts = TenantExperienceReferences.Facts(run.Checkpoint);
        if (sources.Count == 0 && facts.Count == 0) return;
        await (storage as ITenantExperienceStore ?? throw new AccessDeniedException("experience_unavailable"))
            .RequireTenantExperienceSourcesAsync(new(run.Owner.TenantId!.Value, run.AgentRunId), sources, facts, run.Claim?.Generation, ct).ConfigureAwait(false);
    }
    private static IReadOnlyList<TenantExperienceFactSource> ExperienceSources(TenantContinuityObservation observed) => observed.Experience?.Items
        .Select(i => new TenantExperienceFactSource(i.TenantId, i.ExperienceId, i.ContentHash, i.Revision)).ToArray() ?? [];
    private static IReadOnlyList<TenantMemorySource> MemorySources(TenantContinuityObservation observed) => observed.Memory?.Items.Select(m => new TenantMemorySource(m.TenantId, m.MemoryId, m.ContentHash,
        m.Status == AgentCore.Domain.Memory.MemoryItemStatus.Deleted)).ToArray() ?? [];
    public static IReadOnlyList<TenantHistoricalSource> Sources(IEnumerable<TenantHistoricalSession> sessions, Guid tenant) => sessions.Select(s => new TenantHistoricalSource(tenant,
        s.SessionId, s.Revision, s.ThroughEntrySequence, s.AfterEntrySequence, s.Limit, s.ObservationHash)).ToArray();
    public static string Serialize(TenantContinuityObservation observed, int remainingBytes, DateTimeOffset at, Guid? operation = null)
    {
        var entries = new List<(int Score, DateTimeOffset At, Guid Id, JsonNode Item, TenantHistoricalSession? Session, TenantMemorySource? Memory, TenantExperienceFactSource? Experience)>();
        foreach (var item in observed.Memory?.Items ?? []) entries.Add((AgentCore.Application.Continuity.ContinuityRelevance.Score(item.Subject + " " + item.Content, observed.Arguments.Query), item.UpdatedAt, item.MemoryId,
            JsonSerializer.SerializeToNode(new { kind = "Memory", id = item.MemoryId, memoryKind = item.Kind.ToString(), subject = item.Subject, content = item.Content, status = item.Status.ToString(),
                provenance = new { scope = TenantMemoryRules.ScopeName(item.Scope), item.Source, sourceMemoryIds = TenantMemoryOperationMutation.Sources(item) } })!, null,
            new(item.TenantId, item.MemoryId, item.ContentHash, item.Status == AgentCore.Domain.Memory.MemoryItemStatus.Deleted), null));
        foreach (var session in observed.Sessions)
        {
            var summary = TenantContinuityRules.Safe(observed.Arguments.Kind == "Session" ? session.Summary
                : session.Title + "\n" + session.Summary + "\n" + string.Join('\n', session.Entries.Select(e => e.Text)), 700, out var clipped);
            entries.Add((AgentCore.Application.Continuity.ContinuityRelevance.Score(session.Title + " " + session.Summary + " " + string.Join(' ', session.Entries.Select(e => e.Text)), observed.Arguments.Query), session.UpdatedAt, session.SessionId,
                JsonSerializer.SerializeToNode(new { kind = "Session", id = session.SessionId, title = session.Title, summary,
                    entries = observed.Arguments.Kind == "Session" ? session.Entries.Select(e => new { entryId = e.EntryId, sequence = e.Sequence, role = e.Role, status = e.Status, text = e.Text, truncated = e.Truncated }).ToArray() : null, hasMore = session.HasMore, nextAfter = session.NextAfter, truncated = clipped,
                    provenance = new { scope = "AgentInstanceSession", throughEntrySequence = session.ThroughEntrySequence, afterEntrySequence = session.AfterEntrySequence, limit = session.Limit, observedAtUtc = session.UpdatedAt } })!, session, null, null));
        }
        foreach (var item in observed.Experience?.Items ?? [])
        {
            var exact = observed.Arguments.Kind == "Experience";
            entries.Add((AgentCore.Application.Continuity.ContinuityRelevance.Score(TenantExperienceRules.SearchText(item), observed.Arguments.Query), item.UpdatedAt, item.ExperienceId,
                JsonSerializer.SerializeToNode(new { kind = "Experience", id = item.ExperienceId, goal = TenantContinuityRules.Safe(item.Content?.Goal ?? "Content removed", 160, out _),
                    content = exact ? item.Content : null, summary = exact ? null : TenantContinuityRules.Safe(string.Join(' ', item.Content?.Lessons ?? []), 300, out _),
                    visibility = item.Visibility.ToString(), boundedObservation = true, untrustedHistory = true,
                    provenance = new { scope = item.UserPrincipalId is null ? "Instance" : "AgentUser", sourceKind = item.SourceKind.ToString(), sourceId = item.SourceId,
                        throughCursor = item.ThroughCursor, sourceExperienceIds = item.Parents } })!, null, null, new(item.TenantId, item.ExperienceId, item.ContentHash, item.Revision)));
        }
        var ordered = entries.OrderByDescending(e => e.Score).ThenByDescending(e => e.At).ThenBy(e => e.Id).ToArray();
        var selected = ordered.Take(observed.Arguments.Kind is null ? observed.Arguments.Limit : 1).ToList(); var budget = Math.Min(6000, remainingBytes);
        while (true)
        {
            var proofs = selected.Concat(ordered.Skip(selected.Count).Take(1)).ToArray();
            var memoryProofs = proofs.Where(e => e.Memory is not null).Select(e => selected.Any(s => s.Id == e.Id && s.Memory is not null) ? e.Memory! : e.Memory! with { MetadataOnly = true }).ToArray();
            var data = new JsonObject { ["observedAtUtc"] = at.ToString("O"), ["observationOnly"] = true, ["liveAuthorityConfirmed"] = false, ["items"] = new JsonArray(selected.Select(e => e.Item.DeepClone()).ToArray()), ["hasMore"] = observed.HasMore || ordered.Length > selected.Count || observed.Arguments.Kind == "Session" && selected.Any(s => s.Item["hasMore"]?.GetValue<bool>() == true),
                ["tenantHistoricalRead"] = true, ["historicalSources"] = JsonSerializer.SerializeToNode(Sources(proofs.Where(e => e.Session is not null).Select(e => e.Session!), observed.Reader.Security.Tenant.TenantId)),
                ["tenantMemoryRead"] = memoryProofs.Length > 0, ["memorySources"] = JsonSerializer.SerializeToNode(memoryProofs), ["tenantExperienceRead"] = proofs.Any(e => e.Experience is not null), ["experienceSources"] = JsonSerializer.SerializeToNode(proofs.Where(e => e.Experience is not null).Select(e => e.Experience!)), ["operationId"] = operation?.ToString("D") };
            var text = data.ToJsonString(); if (Encoding.UTF8.GetByteCount(text) <= budget) return text;
            if (selected.Count == 1 && observed.Arguments.Kind == "Session" && selected[0].Item["entries"] is JsonArray page && page.Count > 1)
            {
                page.RemoveAt(page.Count - 1); selected[0].Item["hasMore"] = true; selected[0].Item["truncated"] = true;
                selected[0].Item["nextAfter"] = page[^1]!["sequence"]!.GetValue<long>(); continue;
            }
            if (selected.Count == 1 && observed.Arguments.Kind == "Experience" && selected[0].Item["content"] is JsonObject content)
            {
                var arrays = content.Where(p => p.Value is JsonArray a && a.Count > 0).OrderByDescending(p => p.Value!.ToJsonString().Length).ToArray();
                if (arrays.Length > 0) { var lines = (JsonArray)arrays[0].Value!; lines.RemoveAt(lines.Count - 1); selected[0].Item["truncated"] = true; continue; }
                if (content["Goal"]?.GetValue<string>() is { Length: > 160 } goal) { content["Goal"] = goal[..160]; selected[0].Item["truncated"] = true; continue; }
            }
            if (selected.Count <= 1) return AgentRunToolCallCheckpoint.FinishRequiredResult(budget);
            selected.RemoveAt(selected.Count - 1);
        }
    }
}
