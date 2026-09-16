using AgentCore.Application.Agents;
using AgentCore.Application.Events;
using AgentCore.Application.Ports;
using AgentCore.Application.Tools;
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
    private readonly IAttachmentStore? _attachments;
    private readonly RoleKnowledgeService? _knowledge;
    private readonly ISessionWorkspace? _workspace;
    private readonly IArtifactStore? _artifacts;

    public SessionManager(
        IAgentDefinitionStore definitions,
        IMemoryStore store,
        IIdGenerator ids,
        TimeProvider time,
        VoiceAvailability voice,
        IAttachmentStore? attachments = null,
        RoleKnowledgeService? knowledge = null,
        ISessionWorkspace? workspace = null,
        IArtifactStore? artifacts = null)
    {
        _definitions = definitions;
        _store = store;
        _ids = ids;
        _time = time;
        _voice = voice;
        _attachments = attachments;
        _knowledge = knowledge;
        _workspace = workspace;
        _artifacts = artifacts;
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
            now,
            SessionTitles.Default,
            RuntimeEpoch: 0,
            WorkspaceOwned: true);

        await _store.SaveAsync(snapshot, expectedRevision: 0, cancellationToken).ConfigureAwait(false);
        return snapshot;
    }

    public async Task<SessionSnapshot> GetAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
        var snapshot = await _store.LoadAsync(sessionId, cancellationToken).ConfigureAwait(false)
            ?? throw AgentCoreErrors.NotFound("Session was not found.");
        if (snapshot.DurablyDeletedAt is not null)
        {
            throw AgentCoreErrors.NotFound("Session was not found.");
        }

        return snapshot;
    }

    public Task<SessionCatalogPage> ListCatalogAsync(
        string? cursor,
        int limit,
        bool includeArchived,
        CancellationToken cancellationToken = default) =>
        _store.ListCatalogAsync(cursor, limit, includeArchived, cancellationToken).AsTask();

    public async Task<SessionSnapshot> RenameAsync(Guid sessionId, string title, CancellationToken cancellationToken = default)
    {
        var trimmed = title.Trim();
        if (trimmed.Length is 0 or > SessionTitles.MaxLength)
        {
            throw AgentCoreErrors.Validation("Title must be between 1 and 200 characters.");
        }

        return await MutateAsync(
                sessionId,
                snapshot => snapshot with { Title = trimmed },
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<SessionSnapshot> ArchiveAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
        return await MutateAsync(
                sessionId,
                snapshot => snapshot.ArchivedAt is not null
                    ? snapshot
                    : snapshot with { ArchivedAt = _time.GetUtcNow() },
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<SessionSnapshot> UnarchiveAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
        return await MutateAsync(
                sessionId,
                snapshot => snapshot.ArchivedAt is null
                    ? snapshot
                    : snapshot with { ArchivedAt = null },
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<SessionSnapshot> ReopenAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
        return await MutateAsync(
                sessionId,
                snapshot =>
                {
                    if (snapshot.Status == SessionStatus.Ended)
                    {
                        throw AgentCoreErrors.Validation("Ended sessions cannot be reopened.");
                    }

                    if (snapshot.ArchivedAt is not null)
                    {
                        throw AgentCoreErrors.Validation("Archived sessions must be unarchived before reopen.");
                    }

                    return snapshot with { RuntimeEpoch = snapshot.RuntimeEpoch + 1 };
                },
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<SessionSnapshot> DeactivateAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
        return await MutateAsync(
                sessionId,
                snapshot =>
                {
                    if (snapshot.Status == SessionStatus.Ended)
                    {
                        throw AgentCoreErrors.Validation("Ended sessions cannot be deactivated.");
                    }

                    if (snapshot.ArchivedAt is not null)
                    {
                        throw AgentCoreErrors.SessionArchived();
                    }

                    if (snapshot.Status == SessionStatus.Paused)
                    {
                        return snapshot;
                    }

                    return snapshot with
                    {
                        Status = SessionStatus.Paused,
                        PendingMode = null,
                        RuntimeEpoch = snapshot.RuntimeEpoch + 1
                    };
                },
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task DurablyDeleteAsync(Guid sessionId, long expectedRevision, CancellationToken cancellationToken = default)
    {
        var snapshot = await GetAsync(sessionId, cancellationToken).ConfigureAwait(false);
        if (snapshot.Revision != expectedRevision)
        {
            throw AgentCoreErrors.Conflict("Stale session revision.");
        }

        var deleted = snapshot with
        {
            Entries = [],
            Summary = string.Empty,
            PendingTopic = null,
            DurablyDeletedAt = _time.GetUtcNow(),
            Revision = snapshot.Revision + 1,
            UpdatedAt = _time.GetUtcNow()
        };
        await _store.SaveAsync(deleted, snapshot.Revision, cancellationToken).ConfigureAwait(false);
        if (_attachments is not null)
        {
            await _attachments.DeleteSessionAsync(sessionId, cancellationToken).ConfigureAwait(false);
        }

        if (_workspace is not null)
        {
            await _workspace.DeleteSessionAsync(sessionId, cancellationToken).ConfigureAwait(false);
        }

        if (_artifacts is not null)
        {
            await _artifacts.DeleteSessionAsync(sessionId, cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task EnsureAttachmentsAllowedAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
        var snapshot = await GetAsync(sessionId, cancellationToken).ConfigureAwait(false);
        if (snapshot.ArchivedAt is not null)
        {
            throw AgentCoreErrors.SessionArchived();
        }

        if (snapshot.Status == SessionStatus.Ended)
        {
            throw AgentCoreErrors.Validation("Ended sessions cannot accept attachments.");
        }
    }

    private async Task<SessionSnapshot> MutateAsync(
        Guid sessionId,
        Func<SessionSnapshot, SessionSnapshot> mutate,
        CancellationToken cancellationToken)
    {
        var snapshot = await GetAsync(sessionId, cancellationToken).ConfigureAwait(false);
        var next = mutate(snapshot);
        if (ReferenceEquals(next, snapshot) || MemoryStoreEqual(snapshot, next))
        {
            if (next.Revision == snapshot.Revision && next.UpdatedAt == snapshot.UpdatedAt)
            {
                return snapshot;
            }
        }

        if (next.Revision == snapshot.Revision)
        {
            next = next with
            {
                Revision = snapshot.Revision + 1,
                UpdatedAt = _time.GetUtcNow()
            };
        }

        await _store.SaveAsync(next, snapshot.Revision, cancellationToken).ConfigureAwait(false);
        return next;
    }

    private static bool MemoryStoreEqual(SessionSnapshot left, SessionSnapshot right) =>
        left.Title == right.Title
        && left.ArchivedAt == right.ArchivedAt
        && left.RuntimeEpoch == right.RuntimeEpoch
        && left.Status == right.Status;

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
        try
        {
            await _store.SaveAsync(ended, snapshot.Revision, cancellationToken).ConfigureAwait(false);
        }
        catch (AgentCoreException ex) when (ex.Code is "SessionPersistenceUnavailable" or "Conflict")
        {
            var loaded = await GetAsync(sessionId, cancellationToken).ConfigureAwait(false);
            if (loaded.Status != SessionStatus.Ended)
            {
                throw;
            }
        }
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
        try
        {
            await _store.SaveProfileAsync(created, 0, cancellationToken).ConfigureAwait(false);
            return created;
        }
        catch (AgentCoreException ex) when (ex.Code == "Conflict")
        {
            var raced = await _store.LoadProfileAsync(LocalUserProfile.Id, cancellationToken).ConfigureAwait(false);
            if (raced is null)
            {
                throw;
            }

            LocalUserProfile.Validate(raced.Preferences);
            return raced;
        }
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

    public async Task<KnowledgeDocument> RetrieveKnowledgeAsync(
        Guid sessionId,
        string identity,
        CancellationToken cancellationToken = default)
    {
        if (_knowledge is null)
        {
            throw AgentCoreErrors.Forbidden("Knowledge retrieval is not configured.");
        }

        var snapshot = await GetAsync(sessionId, cancellationToken).ConfigureAwait(false);
        return await _knowledge.RetrieveAsync(snapshot.Definition, identity, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<WorkspaceNode>> ListWorkspaceAsync(
        Guid sessionId,
        string prefix,
        CancellationToken cancellationToken = default)
    {
        var snapshot = await GetAsync(sessionId, cancellationToken).ConfigureAwait(false);
        var workspace = RequireWorkspace();
        RolePermissions.EnsureLogicalPathAllowed(string.IsNullOrWhiteSpace(prefix) ? "/" : prefix, sessionId);
        await workspace.EnsureAsync(sessionId, snapshot.Definition, cancellationToken).ConfigureAwait(false);
        return await workspace.ListAsync(sessionId, snapshot.Definition, prefix, cancellationToken).ConfigureAwait(false);
    }

    public async Task<WorkspaceContent> ReadWorkspaceAsync(
        Guid sessionId,
        string logicalPath,
        CancellationToken cancellationToken = default)
    {
        var snapshot = await GetAsync(sessionId, cancellationToken).ConfigureAwait(false);
        var workspace = RequireWorkspace();
        RolePermissions.EnsureLogicalPathAllowed(logicalPath, sessionId);
        await workspace.EnsureAsync(sessionId, snapshot.Definition, cancellationToken).ConfigureAwait(false);
        return await workspace.ReadAsync(sessionId, snapshot.Definition, logicalPath, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task WriteWorkspaceAsync(
        Guid sessionId,
        string logicalPath,
        ReadOnlyMemory<byte> bytes,
        CancellationToken cancellationToken = default)
    {
        var snapshot = await GetAsync(sessionId, cancellationToken).ConfigureAwait(false);
        if (snapshot.ArchivedAt is not null)
        {
            throw AgentCoreErrors.SessionArchived();
        }

        if (snapshot.Status == SessionStatus.Ended)
        {
            throw AgentCoreErrors.Validation("Ended sessions cannot accept workspace writes.");
        }

        var workspace = RequireWorkspace();
        RolePermissions.EnsureLogicalPathAllowed(logicalPath, sessionId);
        await workspace.EnsureAsync(sessionId, snapshot.Definition, cancellationToken).ConfigureAwait(false);
        await workspace.WriteAsync(sessionId, logicalPath, bytes, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ArtifactRecord> MaterializeAttachmentAsync(
        Guid sessionId,
        Guid attachmentId,
        CancellationToken cancellationToken = default)
    {
        var snapshot = await GetAsync(sessionId, cancellationToken).ConfigureAwait(false);
        if (snapshot.ArchivedAt is not null)
        {
            throw AgentCoreErrors.SessionArchived();
        }

        if (snapshot.Status == SessionStatus.Ended)
        {
            throw AgentCoreErrors.Validation("Ended sessions cannot materialize attachments.");
        }

        var attachments = _attachments ?? throw AgentCoreErrors.Forbidden("Attachments are not configured.");
        var artifacts = _artifacts ?? throw AgentCoreErrors.Forbidden("Artifact store is not configured.");
        var workspace = RequireWorkspace();
        var source = await attachments.GetAsync(sessionId, attachmentId, cancellationToken).ConfigureAwait(false)
            ?? throw AgentCoreErrors.NotFound("Attachment was not found.");
        await using var stream = await attachments.OpenContentAsync(sessionId, attachmentId, cancellationToken)
            .ConfigureAwait(false);
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
        var bytes = buffer.ToArray();
        if (!string.Equals(source.Sha256Hex, Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant(), StringComparison.OrdinalIgnoreCase))
        {
            throw AgentCoreErrors.Conflict("Attachment original hash could not be verified.");
        }

        await workspace.EnsureAsync(sessionId, snapshot.Definition, cancellationToken).ConfigureAwait(false);
        var existing = await workspace.ListAsync(sessionId, snapshot.Definition, "/workspace/working", cancellationToken)
            .ConfigureAwait(false);
        var taken = existing
            .Where(node => !node.Directory)
            .Select(node => Path.GetFileName(node.LogicalPath).ToLowerInvariant())
            .ToHashSet(StringComparer.Ordinal);
        var fileName = WorkspaceFileNames.Deduplicate(WorkspaceFileNames.Sanitize(source.DisplayName), taken);
        var logical = "/workspace/working/" + fileName;
        await workspace.WriteAsync(sessionId, logical, bytes, cancellationToken).ConfigureAwait(false);
        var artifact = await artifacts.CreateAsync(
                sessionId,
                fileName,
                source.ContentType,
                bytes,
                source.AttachmentId,
                logical,
                cancellationToken)
            .ConfigureAwait(false);
        if (!string.Equals(artifact.Sha256Hex, source.Sha256Hex, StringComparison.OrdinalIgnoreCase))
        {
            throw AgentCoreErrors.Conflict("Materialized artifact hash diverged from the attachment original.");
        }

        return artifact;
    }

    public async Task<IReadOnlyList<ArtifactRecord>> ListArtifactsAsync(
        Guid sessionId,
        CancellationToken cancellationToken = default)
    {
        _ = await GetAsync(sessionId, cancellationToken).ConfigureAwait(false);
        var artifacts = _artifacts ?? throw AgentCoreErrors.Forbidden("Artifact store is not configured.");
        return await artifacts.ListAsync(sessionId, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ArtifactRecord> GetArtifactAsync(
        Guid sessionId,
        Guid artifactId,
        CancellationToken cancellationToken = default)
    {
        _ = await GetAsync(sessionId, cancellationToken).ConfigureAwait(false);
        var artifacts = _artifacts ?? throw AgentCoreErrors.Forbidden("Artifact store is not configured.");
        return await artifacts.GetAsync(sessionId, artifactId, cancellationToken).ConfigureAwait(false)
            ?? throw AgentCoreErrors.NotFound("Artifact was not found.");
    }

    public async Task<Stream> OpenArtifactContentAsync(
        Guid sessionId,
        Guid artifactId,
        CancellationToken cancellationToken = default)
    {
        _ = await GetAsync(sessionId, cancellationToken).ConfigureAwait(false);
        var artifacts = _artifacts ?? throw AgentCoreErrors.Forbidden("Artifact store is not configured.");
        return await artifacts.OpenContentAsync(sessionId, artifactId, cancellationToken).ConfigureAwait(false);
    }

    private ISessionWorkspace RequireWorkspace() =>
        _workspace ?? throw AgentCoreErrors.Forbidden("Session workspace is not configured.");
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
    ISpeechSynthesizer synthesizer,
    IAttachmentStore attachments,
    IAttachmentProcessor processor,
    IArtifactReferenceAuthorizer artifacts,
    SessionToolExecutor tools)
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
            synthesizer,
            attachments: attachments,
            processor: processor,
            artifacts: artifacts,
            tools: tools);
}
