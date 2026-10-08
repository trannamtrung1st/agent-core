using System.Runtime.CompilerServices;
using AgentCore.Application.Events;
using AgentCore.Application.Models;
using AgentCore.Application.Execution;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Application.Tools;
using AgentCore.Domain.Conversation;
using AgentCore.Infrastructure.Identity;
using AgentCore.Infrastructure.Persistence;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;

namespace AgentCore.Tests.Shared;

/// <summary>Constructs isolated runtime fixtures with explicit trusted ownership and canonical Run admission.</summary>
internal static class SessionRuntimeFixture
{
    private static readonly ConditionalWeakTable<IMemoryStore, IAgentRunStore> Runs = new();
    private static readonly ConditionalWeakTable<IMemoryStore, HashSet<Guid>> IssuedIds = new();
    private static readonly ConditionalWeakTable<SessionRuntime, FixtureContext> Contexts = new();
    private sealed record FixtureContext(IAgentRunStore Runs, TimeProvider Time);
    internal static void Bind(IMemoryStore sessions, IAgentRunStore runs) => Runs.Add(sessions, runs);
    internal static readonly Guid ProfileId = Guid.Parse("bbcc7700-0000-4000-8000-000000000001");
    internal static SessionRuntime Create(
        SessionSnapshot snapshot,
        ILanguageModel languageModel,
        IAgentBrain brain,
        IMemoryStore store,
        ISessionOutput output,
        IIdGenerator ids,
        TimeProvider time,
        ILogger logger,
        IInterruptionClassifier? classifier = null,
        RecognitionCapabilities? recognition = null,
        InteractionPolicy? policy = null,
        ISpeechRecognizer? recognizer = null,
        ISpeechSynthesizer? synthesizer = null,
        ISessionAudioOutput? audioOutput = null,
        IAttachmentStore? attachments = null,
        IAttachmentProcessor? processor = null,
        IArtifactReferenceAuthorizer? artifacts = null,
        SessionToolExecutor? tools = null,
        VoiceAvailability? voice = null,
        ILanguageModelResolver? modelResolver = null,
        IModelCatalog? catalog = null,
        IUserTurnCapabilityValidator? turnCapabilities = null,
        IStructuredMemoryService? structuredMemory = null,
        IAgentRunStore? agentRuns = null,
        IDiagnosticIdSource? diagnostics = null,
        IBrowserSessionLease? browserLease = null,
        IAgentRunAuthority? runAuthority = null,
        ITriggerStore? triggerOccurrences = null)
    {
        if (agentRuns is null)
        {
            ids = new UniqueFixtureIds(ids, IssuedIds.GetValue(store, _ => []));
            snapshot = snapshot with { ProfileId = snapshot.ProfileId ?? ProfileId,
            PinnedPersona = snapshot.PinnedPersona ?? snapshot.Definition.Identity,
            ModelSelection = snapshot.ModelSelection ?? (catalog is null
                ? new SessionModelSelection("synthetic-offline/scripted", "primary-llm", "scripted", ModelSelectionSource.SystemDefault, null) : null),
            PendingAgentInputIds = snapshot.PendingAgentInputIds.Count == 0
                ? TrailingUserSuffix.Of(snapshot.Entries).Select(entry => entry.EntryId).ToArray() : snapshot.PendingAgentInputIds };
        }
        agentRuns ??= Runs.GetValue(store, sessions => sessions is InMemoryMemoryStore memory
            ? new InMemoryAgentRunStore(memory, new SystemDiagnosticIdSource(), triggerOccurrences as InMemoryTriggerStore)
            : sessions is SqliteMemoryStore
                ? throw new InvalidOperationException("Bind the canonical SQLite Run store before constructing this fixture.")
                : new FaultInjectionRunStore(sessions));
        if (ids is UniqueFixtureIds fixtureIds)
        {
            var existing = agentRuns.ListPageAsync(new(snapshot.AgentInstanceId, snapshot.ProfileId!.Value), null, null, 50).AsTask().GetAwaiter().GetResult();
            fixtureIds.Retain(existing.Items.SelectMany(run => new[] { run.AgentRunId, run.ActivationId, run.ResponseId ?? Guid.Empty, run.Claim?.Generation ?? Guid.Empty }));
        }
        var runtime = new SessionRuntime(snapshot, languageModel, brain, store, output, ids, time, logger, agentRuns,
            classifier, recognition, policy, recognizer, synthesizer, audioOutput, attachments, processor,
            artifacts, tools, voice, modelResolver, catalog, turnCapabilities, structuredMemory,
            diagnostics, browserLease, runAuthority, triggerOccurrences);
        Contexts.Add(runtime, new(agentRuns, time));
        return runtime;
    }

    internal static ValueTask<IReadOnlyList<AgentRun>> RunsForAsync(SessionRuntime runtime)
    {
        var context = Contexts.GetValue(runtime, _ => throw new InvalidOperationException("Unknown fixture."));
        return context.Runs.ListForSessionAsync(new(runtime.Snapshot.AgentInstanceId, runtime.Snapshot.ProfileId!.Value), runtime.SessionId);
    }

    internal static async Task<bool> DispatchRetryAsync(SessionRuntime runtime)
    {
        await runtime.WaitUntilIdleAsync().WaitAsync(TimeSpan.FromSeconds(10));
        var context = Contexts.GetValue(runtime, _ => throw new InvalidOperationException("Unknown fixture."));
        var owner = new AgentRunOwner(runtime.Snapshot.AgentInstanceId, runtime.Snapshot.ProfileId!.Value);
        var run = (await context.Runs.ListForSessionAsync(owner, runtime.SessionId)).SingleOrDefault(run => run.Status == AgentRunStatus.WaitingToRetry);
        if (run is null) return false;
        if (context.Time is not FakeTimeProvider clock) throw new InvalidOperationException("Durable retry fixture requires controlled time.");
        clock.Advance(run.NextRetryAtUtc!.Value - clock.GetUtcNow() + TimeSpan.FromMilliseconds(1));
        run = await context.Runs.ApplyAsync(owner, run.AgentRunId,
            new AgentRunCommand.Claim(run.Revision, clock.GetUtcNow(), Guid.NewGuid(), clock.GetUtcNow().AddMinutes(5)));
        if (!await runtime.DispatchAgentRunAsync(run.AgentRunId, false)) throw new InvalidOperationException("Fixture retry dispatch was rejected.");
        return true;
    }

    internal static async Task SettleRetriesAsync(SessionRuntime runtime)
    {
        for (var attempt = 0; attempt < AgentRunLimits.DefaultMaxAttempts; attempt++)
            if (!await DispatchRetryAsync(runtime)) return;
        await runtime.WaitUntilIdleAsync().WaitAsync(TimeSpan.FromSeconds(10));
    }

    private sealed class UniqueFixtureIds(IIdGenerator source, HashSet<Guid> used) : IIdGenerator
    {
        public void Retain(IEnumerable<Guid> ids) { lock (used) used.UnionWith(ids); }
        public Guid NewSessionId() => source.NewSessionId();
        public Guid NewId()
        {
            lock (used)
            {
                Guid id;
                do { id = source.NewId(); } while (!used.Add(id));
                return id;
            }
        }
    }

    /// <summary>Pairs run transitions with fault-injected Session saves; atomic-store behavior has separate real-store suites.</summary>
    private sealed class FaultInjectionRunStore(IMemoryStore sessions) : IAgentRunStore
    {
        private readonly Dictionary<Guid, AgentRun> runs = [];
        public async ValueTask<AgentRunAdmissionResult> AdmitAsync(SessionSnapshot snapshot, long revision, AgentRun run, CancellationToken ct = default)
        {
            await sessions.SaveAsync(snapshot, revision, ct);
            lock (runs) { runs.Add(run.AgentRunId, run); return new(true, run); }
        }
        public ValueTask<AgentRun?> GetAsync(AgentRunOwner owner, Guid id, CancellationToken ct = default)
        { lock (runs) return ValueTask.FromResult(runs.TryGetValue(id, out var run) && run.Owner == owner ? run : null); }
        public ValueTask<Activation?> GetActivationAsync(AgentRunOwner owner, Guid id, CancellationToken ct = default)
        { lock (runs) return ValueTask.FromResult(runs.Values.FirstOrDefault(run => run.Owner == owner && run.ActivationId == id)?.Admission.Activation); }
        public ValueTask<AgentRun> ApplyAsync(AgentRunOwner owner, Guid id, AgentRunCommand command, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            lock (runs)
            {
                var run = runs[id];
                if (run.Owner != owner) throw new InvalidOperationException("Wrong fixture owner.");
                return ValueTask.FromResult(runs[id] = command.Apply(run, Guid.NewGuid));
            }
        }
        public ValueTask<IReadOnlyList<AgentRun>> ListForSessionAsync(AgentRunOwner owner, Guid id, CancellationToken ct = default)
        { lock (runs) return ValueTask.FromResult<IReadOnlyList<AgentRun>>(runs.Values.Where(run => run.Owner == owner && run.SessionId == id).ToArray()); }
        public ValueTask<IReadOnlyList<AgentRun>> ListRunnableAsync(DateTimeOffset now, int limit, CancellationToken ct = default) => throw new NotSupportedException();
        public ValueTask<IReadOnlyList<Guid>> ListPendingInputSessionsAsync(int limit, CancellationToken ct = default) => throw new NotSupportedException();
        public ValueTask<AgentRunPage> ListPageAsync(AgentRunOwner owner, Guid? session, Guid? before, int limit, CancellationToken ct = default)
        { lock (runs) return ValueTask.FromResult(new AgentRunPage(runs.Values.Where(run => run.Owner == owner).Take(limit).ToArray(), null, false)); }
        public ValueTask<AgentRun?> GetLatestForAutomationAsync(AgentRunOwner owner, Guid automation, CancellationToken ct = default) => throw new NotSupportedException();
        public ValueTask<AgentRunAdmissionResult> AdmitImmediateAsync(SessionSnapshot snapshot, AgentRun run, Guid generation, CancellationToken ct = default) => throw new NotSupportedException();
        public ValueTask<AgentRunAdmissionResult> AdmitOccurrenceAsync(SessionSnapshot snapshot, AgentRun run, long revision, CancellationToken ct = default) => throw new NotSupportedException();
        public ValueTask<AgentRunAdmissionResult> AdmitCompletionReportAsync(SessionSnapshot snapshot, long revision, AgentRun run, Guid child, CancellationToken ct = default) => throw new NotSupportedException();
        public ValueTask<IReadOnlyList<BackgroundCompletionCandidate>> ListUnreportedCompletionsAsync(int limit, CancellationToken ct = default) => throw new NotSupportedException();
        public ValueTask<bool> HasCompletionReceiptAsync(AgentRunOwner owner, Guid id, CancellationToken ct = default) => throw new NotSupportedException();
        public ValueTask SkipCompletionReportAsync(AgentRunOwner owner, Guid id, string reason, DateTimeOffset now, CancellationToken ct = default) => throw new NotSupportedException();
        public ValueTask<AgentRun> CommitOutcomeAsync(SessionSnapshot snapshot, long revision, AgentRunOwner owner, Guid id, AgentRunCommand.Complete completion, Guid? draft, CancellationToken ct = default) => throw new NotSupportedException();
    }
}
