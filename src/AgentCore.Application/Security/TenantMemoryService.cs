using System.Text.Json;
using AgentCore.Application.Context;
using AgentCore.Application.Memory;
using AgentCore.Application.Ports;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;

namespace AgentCore.Application.Security;

public sealed class TenantMemoryService(ITenantLearnedMemoryStore storage, TenantOperationAuthorizer operations, IIdGenerator ids, TimeProvider time)
{
    private static TenantRuntimeFrame Frame(AgentRun run) => new(run.Owner, run.SessionId, run.AgentRunId,
        run.Claim?.Generation ?? throw new AccessDeniedException("memory_context_unavailable"));
    public async ValueTask<TenantMemoryObservation> ReadAsync(AgentRun run, CancellationToken ct = default)
    {
        var observation = await storage.ReadTenantRunMemoryAsync(Frame(run), ct).ConfigureAwait(false);
        if (!observation.ReadAllowed) return observation;
        var (current, state) = await operations.ResolveAsync(new(run.Owner.TenantId!.Value, run.AgentRunId), ct).ConfigureAwait(false);
        if (current.Owner != run.Owner || current.Claim?.Generation != run.Claim?.Generation) throw new AccessDeniedException("memory_context_unavailable");
        var decision = await operations.Check(state, current, "memory.read", AccessScope.ForResource(state.Tenant.TenantId, "agent", run.AgentInstanceId.ToString("D")), ct,
            purpose: ExecutionAuthorizationPurpose.Inspection).ConfigureAwait(false);
        if (decision.Decision != OperationDecisionKind.Allowed) return observation with { Items = [], Sources = [], ReadAllowed = false };
        await storage.RequireTenantMemoryProjectionAsync(Frame(run), observation, ct).ConfigureAwait(false); return observation;
    }
    public ValueTask RequireProjectionAsync(AgentRun run, TenantMemoryObservation? observation, CancellationToken ct = default) => observation is not { ReadAllowed: true }
        ? ValueTask.CompletedTask : storage.RequireTenantMemoryProjectionAsync(Frame(run), observation, ct);
    public ValueTask RequireAsync(AgentRun run, IReadOnlyList<TenantMemorySource>? sources, CancellationToken ct = default) => sources is not { Count: > 0 }
        ? ValueTask.CompletedTask : storage.RequireTenantMemorySourcesAsync(new(run.Owner.TenantId!.Value, run.AgentRunId), sources, run.Claim?.Generation, ct);
    public async ValueTask<TenantMemoryPlan> PrepareAsync(AgentRun run, SessionSnapshot snapshot, IReadOnlyList<MemoryProposal> proposals, CancellationToken ct = default)
    {
        var candidates = Enumerable.Range(0, TenantMemoryRules.CandidateCount).Select(_ => ids.NewId()).ToArray();
        return await storage.PrepareTenantLearningAsync(Frame(run), snapshot, proposals.Take(MemoryProposalCodec.MaxProposalsPerTurn).ToArray(), candidates,
            ids.NewId(), DateTimeOffset.FromUnixTimeMilliseconds(time.GetUtcNow().ToUnixTimeMilliseconds()), ct).ConfigureAwait(false);
    }
    public static IReadOnlyList<TenantMemorySource> Merge(IReadOnlyList<TenantMemorySource>? prior, IReadOnlyList<TenantMemorySource>? current)
    {
        var result = (prior ?? []).Concat(current ?? []).Distinct().ToArray();
        if (result.Length > MemoryLimits.MaxActiveItems) throw new AccessDeniedException("memory_context_capacity");
        return Array.AsReadOnly(result);
    }
    public static EffectiveContextSnapshot Project(EffectiveContextSnapshot context, TenantMemoryObservation observation, AgentRun run, IReadOnlyList<ConversationEntry> history)
    {
        context = context.Remove(ContextSectionId.Memory);
        context = AgentCore.Application.Agents.PromptContextBuilder.WithTenantMemoryReceipts(context, history, run.Owner.TenantId!.Value,
            run.AgentInstanceId, observation.UserPrincipalId, observation.ReadAllowed);
        if (observation.Items.Count == 0) return context;
        return context.Upsert(ContextSection.Evidence(ContextSectionId.Memory, ContextSourceKind.Memory,
            SessionMemoryPrompt.LearnedDataLabel + "\n" + SessionMemoryPrompt.TrustedPrecedence + "\n" + JsonSerializer.Serialize(observation.Items.Select(i => new {
                memoryId = i.MemoryId, scope = TenantMemoryRules.ScopeName(i.Scope), kind = i.Kind.ToString(), subject = i.Subject, content = i.Content }))));
    }
}
