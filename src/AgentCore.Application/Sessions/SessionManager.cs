using AgentCore.Application.Events;
using AgentCore.Application.Ports;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Definitions;

namespace AgentCore.Application.Sessions;

public sealed class SessionManager
{
    private readonly IAgentDefinitionStore _definitions;
    private readonly IMemoryStore _store;
    private readonly IIdGenerator _ids;
    private readonly TimeProvider _time;
    private readonly VoiceAvailability _voice;

    public SessionManager(
        IAgentDefinitionStore definitions,
        IMemoryStore store,
        IIdGenerator ids,
        TimeProvider time,
        VoiceAvailability voice)
    {
        _definitions = definitions;
        _store = store;
        _ids = ids;
        _time = time;
        _voice = voice;
    }

    public async Task<SessionSnapshot> CreateAsync(
        string agentId,
        int? agentVersion,
        SessionMode mode,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(agentId))
        {
            throw AgentCoreErrors.Validation("agentId is required.");
        }

        var definition = await _definitions.GetAsync(agentId, agentVersion, cancellationToken).ConfigureAwait(false)
            ?? throw AgentCoreErrors.NotFound($"Agent '{agentId}' was not found.");

        var voiceAvailable = _voice.IsAvailable(definition);
        if (mode == SessionMode.Voice && !voiceAvailable)
        {
            throw AgentCoreErrors.VoiceUnavailable();
        }

        var now = _time.GetUtcNow();
        var profile = await EnsureLocalProfileAsync(now, cancellationToken).ConfigureAwait(false);
        var snapshot = new SessionSnapshot(
            SchemaVersion: 1,
            SessionId: _ids.NewSessionId(),
            Revision: 1,
            definition,
            mode,
            PendingMode: null,
            SessionStatus.Created,
            Entries: [],
            Summary: string.Empty,
            SummarizedThroughEntrySequence: 0,
            PendingTopic: null,
            ProfileId: profile.ProfileId,
            now,
            now);

        await _store.SaveAsync(snapshot, expectedRevision: 0, cancellationToken).ConfigureAwait(false);
        return snapshot;
    }

    public async Task<SessionSnapshot> GetAsync(Guid sessionId, CancellationToken cancellationToken = default) =>
        await _store.LoadAsync(sessionId, cancellationToken).ConfigureAwait(false)
            ?? throw AgentCoreErrors.NotFound("Session was not found.");

    public async Task<IReadOnlyList<ConversationEntry>> ReadHistoryAsync(
        Guid sessionId,
        long after,
        int limit,
        CancellationToken cancellationToken = default)
    {
        if (after < 0 || limit is < 1 or > 100)
        {
            throw AgentCoreErrors.Validation("History cursor is invalid.");
        }

        _ = await GetAsync(sessionId, cancellationToken).ConfigureAwait(false);
        return await _store.ReadHistoryAsync(sessionId, after, limit, cancellationToken).ConfigureAwait(false);
    }

    public async Task EndAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
        var snapshot = await GetAsync(sessionId, cancellationToken).ConfigureAwait(false);
        if (snapshot.Status == SessionStatus.Ended)
        {
            return;
        }

        var ended = snapshot with
        {
            Status = SessionStatus.Ended,
            PendingMode = null,
            Revision = snapshot.Revision + 1,
            UpdatedAt = _time.GetUtcNow()
        };
        await _store.SaveAsync(ended, snapshot.Revision, cancellationToken).ConfigureAwait(false);
    }

    private async Task<UserProfile> EnsureLocalProfileAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        var existing = await _store.LoadProfileAsync(LocalUserProfile.Id, cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            LocalUserProfile.Validate(existing.Preferences);
            return existing;
        }

        var created = new UserProfile(
            LocalUserProfile.Id,
            1,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["language"] = "en",
                ["preferredName"] = "friend"
            },
            now);
        LocalUserProfile.Validate(created.Preferences);
        await _store.SaveProfileAsync(created, 0, cancellationToken).ConfigureAwait(false);
        return created;
    }

    public async Task<IReadOnlyList<PublicAgentDescriptor>> ListAgentsAsync(
        CancellationToken cancellationToken = default)
    {
        var definitions = await _definitions.ListAsync(cancellationToken).ConfigureAwait(false);
        return definitions
            .GroupBy(definition => definition.Id, StringComparer.Ordinal)
            .Select(group => group.OrderByDescending(item => item.Version).First())
            .Select(definition => PublicHistory.FromDefinition(definition, _voice.IsAvailable(definition)))
            .ToArray();
    }

    public async Task<PublicAgentDescriptor> GetAgentAsync(
        string agentId,
        int? version,
        CancellationToken cancellationToken = default)
    {
        var definition = await _definitions.GetAsync(agentId, version, cancellationToken).ConfigureAwait(false)
            ?? throw AgentCoreErrors.NotFound($"Agent '{agentId}' was not found.");
        return PublicHistory.FromDefinition(definition, _voice.IsAvailable(definition));
    }
}

public sealed class VoiceAvailability
{
    public bool IsAvailable(AgentDefinition definition) =>
        definition.Voice.Enabled
        && !string.IsNullOrWhiteSpace(definition.ProviderPreferences.SpeechRecognizer)
        && !string.IsNullOrWhiteSpace(definition.ProviderPreferences.SpeechSynthesizer)
        && SpeechAdaptersResolved;

    /// <summary>
    /// Synthetic adapters count as resolved. Hosted speech adapters arrive in later milestones.
    /// </summary>
    public bool SpeechAdaptersResolved { get; init; }
}

public sealed class SessionRuntimeFactory(
    ILanguageModel languageModel,
    IAgentBrain brain,
    IMemoryStore store,
    IIdGenerator ids,
    TimeProvider time,
    Microsoft.Extensions.Logging.ILoggerFactory loggers,
    IInterruptionClassifier classifier,
    InteractionPolicy policy,
    ISpeechRecognizer recognizer,
    ISpeechSynthesizer synthesizer)
{
    public SessionRuntime Create(SessionSnapshot snapshot, ISessionOutput output) =>
        new(
            snapshot,
            languageModel,
            brain,
            store,
            output,
            ids,
            time,
            loggers.CreateLogger(typeof(SessionRuntime).FullName!),
            classifier,
            recognition: recognizer.Capabilities,
            policy,
            recognizer,
            synthesizer);
}
