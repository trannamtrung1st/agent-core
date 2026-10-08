using AgentCore.Application.Execution;
using AgentCore.Application.Ports;
using AgentCore.Domain.Conversation;
using AgentCore.Infrastructure.Identity;
using AgentCore.Infrastructure.Persistence;

namespace AgentCore.Application.Tests;

/// <summary>Fixtures query admitted runs while exercising the real atomic InMemory store.</summary>
internal sealed class RuntimeAgentRunStore : IAgentRunStore
{
    private InMemoryAgentRunStore? _store;
    private InMemoryMemoryStore? _sessions;
    private readonly Dictionary<Guid, AgentRunOwner> _owners = [];
    public void Bind(InMemoryMemoryStore sessions, InMemoryTriggerStore? triggers = null)
    {
        if (_sessions is not null && !ReferenceEquals(_sessions, sessions))
            throw new InvalidOperationException("Fixture changed its atomic Session store.");
        _sessions = sessions;
        _store ??= new(sessions, new SystemDiagnosticIdSource(), triggers);
    }
    public static SessionSnapshot WithPins(SessionSnapshot snapshot) => snapshot with
    {
        ProfileId = snapshot.ProfileId ?? Guid.Parse("bbcc7700-0000-4000-8000-000000000001"),
        PinnedPersona = snapshot.PinnedPersona ?? snapshot.Definition.Identity
    };
    private InMemoryAgentRunStore Store => _store ?? throw new InvalidOperationException("Bind fixture Session store before admission.");
    public Exception? LastAdmissionError { get; private set; }
    public Func<AgentRunOwner, Guid, AgentRunCommand, CancellationToken, ValueTask>? BeforeApply { get; set; }
    public async ValueTask<AgentRunAdmissionResult> AdmitAsync(SessionSnapshot snapshot, long revision, AgentRun run, CancellationToken ct = default)
    {
        AgentRunAdmissionResult result;
        try { result = await Store.AdmitAsync(snapshot, revision, run, ct); }
        catch (Exception exception) { LastAdmissionError = exception; throw; }
        lock (_owners) _owners[result.Run.AgentRunId] = result.Run.Owner;
        return result;
    }
    public ValueTask<CompletionInboxItem?> GetCompletionInboxAsync(AgentRunOwner o, Guid s, Guid c, DateTimeOffset now, CancellationToken ct = default) => Store.GetCompletionInboxAsync(o, s, c, now, ct);
    public ValueTask<bool> HasCompletionClaimAsync(AgentRunOwner o, Guid r, Guid g, DateTimeOffset now, CancellationToken ct = default) => Store.HasCompletionClaimAsync(o, r, g, now, ct);
    public ValueTask<IReadOnlyList<BackgroundCompletionCandidate>> ListBackgroundPageAsync(AgentRunOwner o, Guid s, Guid? c, bool p, int l, CancellationToken ct = default) => Store.ListBackgroundPageAsync(o, s, c, p, l, ct);
    public ValueTask<bool> HasCompletionAcknowledgmentAsync(AgentRunOwner owner, Guid parent, CancellationToken ct = default) => Store.HasCompletionAcknowledgmentAsync(owner, parent, ct);
    public ValueTask<IReadOnlyList<BackgroundCompletionCandidate>> ListBackgroundChildrenAsync(AgentRunOwner owner, Guid parent, int limit, CancellationToken ct = default) => Store.ListBackgroundChildrenAsync(owner, parent, limit, ct);
    public ValueTask<IReadOnlyList<CompletionInboxItem>> ListCompletionInboxAsync(AgentRunOwner owner, Guid session, int limit, DateTimeOffset now, CancellationToken ct = default) => Store.ListCompletionInboxAsync(owner, session, limit, now, ct);
    public ValueTask<CompletionInboxItem> TakeCompletionAsync(AgentRunOwner owner, Guid parent, Guid generation, Guid child, long revision, string call, Guid token, DateTimeOffset now, CancellationToken ct = default) => Store.TakeCompletionAsync(owner, parent, generation, child, revision, call, token, now, ct);
    public ValueTask<CompletionInboxItem> AcknowledgeCompletionAsync(AgentRunOwner owner, Guid parent, Guid generation, Guid child, long revision, Guid token, string usage, DateTimeOffset now, CancellationToken ct = default) => Store.AcknowledgeCompletionAsync(owner, parent, generation, child, revision, token, usage, now, ct);
    public ValueTask<AgentRunAdmissionResult> AdmitImmediateAsync(SessionSnapshot snapshot, AgentRun run, Guid generation, CancellationToken ct = default) => Store.AdmitImmediateAsync(snapshot, run, generation, ct);
    public ValueTask<IReadOnlyList<BackgroundCompletionCandidate>> ListDeliveryCandidatesAsync(int limit, DateTimeOffset now, CancellationToken ct = default) => Store.ListDeliveryCandidatesAsync(limit, now, ct);
    public ValueTask<IReadOnlyList<BackgroundCompletionCandidate>> ListUnreportedCompletionsAsync(int limit, CancellationToken ct = default) => Store.ListUnreportedCompletionsAsync(limit, ct);
    public ValueTask<CompletionDeliveryState> GetCompletionDeliveryAsync(AgentRunOwner owner, Guid id, CancellationToken ct = default) => Store.GetCompletionDeliveryAsync(owner, id, ct);
    public ValueTask<bool> HasCompletionReceiptAsync(AgentRunOwner owner, Guid id, CancellationToken ct = default) => Store.HasCompletionReceiptAsync(owner, id, ct);
    public ValueTask SkipCompletionReportAsync(AgentRunOwner owner, Guid id, string reason, DateTimeOffset now, CancellationToken ct = default) => Store.SkipCompletionReportAsync(owner, id, reason, now, ct);
    public ValueTask<AgentRunAdmissionResult> AdmitCompletionReportBatchAsync(SessionSnapshot parent, long revision, AgentRun report, IReadOnlyList<Guid> children, CancellationToken ct = default) => Store.AdmitCompletionReportBatchAsync(parent, revision, report, children, ct);
    public ValueTask<AgentRunAdmissionResult> AdmitCompletionReportAsync(SessionSnapshot parent, long revision, AgentRun report, Guid child, CancellationToken ct = default) => Store.AdmitCompletionReportAsync(parent, revision, report, child, ct);
    public ValueTask<AgentRun> CommitOutcomeAsync(SessionSnapshot snapshot, long revision, AgentRunOwner owner,
        Guid id, AgentRunCommand.Complete completion, Guid? draft, CancellationToken ct = default) =>
        Store.CommitOutcomeAsync(snapshot, revision, owner, id, completion, draft, ct);
    public ValueTask<AgentRunAdmissionResult> AdmitOccurrenceAsync(SessionSnapshot snapshot, AgentRun run, long revision, CancellationToken ct = default) => Store.AdmitOccurrenceAsync(snapshot, run, revision, ct);
    public ValueTask<AgentRun?> GetAsync(AgentRunOwner owner, Guid id, CancellationToken ct = default) => Store.GetAsync(owner, id, ct);
    public ValueTask<Activation?> GetActivationAsync(AgentRunOwner owner, Guid id, CancellationToken ct = default) => Store.GetActivationAsync(owner, id, ct);
    public ValueTask<IReadOnlyList<AgentRun>> ListForSessionAsync(AgentRunOwner owner, Guid id, CancellationToken ct = default) => Store.ListForSessionAsync(owner, id, ct);
    public ValueTask<AgentRun?> GetLatestForAutomationAsync(AgentRunOwner owner, Guid id, CancellationToken ct = default) => Store.GetLatestForAutomationAsync(owner, id, ct);
    public ValueTask<AgentRunPage> ListPageAsync(AgentRunOwner owner, Guid? session, Guid? before, int limit, CancellationToken ct = default) => Store.ListPageAsync(owner, session, before, limit, ct);
    public ValueTask<IReadOnlyList<AgentRun>> ListRunnableAsync(DateTimeOffset now, int limit, CancellationToken ct = default) => Store.ListRunnableAsync(now, limit, ct);
    public ValueTask<IReadOnlyList<Guid>> ListPendingInputSessionsAsync(int limit, CancellationToken ct = default) => Store.ListPendingInputSessionsAsync(limit, ct);
    public async ValueTask<AgentRun> ApplyAsync(AgentRunOwner owner, Guid id, AgentRunCommand command, CancellationToken ct = default)
    {
        if (BeforeApply is { } before) await before(owner, id, command, ct);
        return await Store.ApplyAsync(owner, id, command, ct);
    }
    public async ValueTask<IReadOnlyList<AgentRun>> OpenAsync(Guid sessionId)
    {
        var snapshot = await _sessions!.LoadAsync(sessionId);
        var runs = await Store.ListForSessionAsync(new(snapshot!.AgentInstanceId, snapshot.ProfileId!.Value), sessionId);
        return runs.Where(run => !run.IsTerminal).ToArray();
    }
    public async ValueTask<AgentRun?> ForSourceAsync(Guid sessionId, Guid eventId)
    {
        var snapshot = await _sessions!.LoadAsync(sessionId);
        var runs = await Store.ListForSessionAsync(new(snapshot!.AgentInstanceId, snapshot.ProfileId!.Value), sessionId);
        return runs.SingleOrDefault(run => run.Admission.Activation.SourceEventId == eventId
            || run.Admission.Activation.SourceEntryIds.Contains(eventId)
            || snapshot.Entries.Any(entry => entry.SourceEventId == eventId && run.Admission.Activation.SourceEntryIds.Contains(entry.EntryId)));
    }
}
