using System.Diagnostics;
using AgentCore.Application.Agents;
using AgentCore.Application.Events;
using AgentCore.Application.Models;
using AgentCore.Application.Observability;
using AgentCore.Application.Ports;
using AgentCore.Application.Speech;
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
    private readonly IModelCatalog? _models;

    public SessionManager(
        IAgentDefinitionStore definitions,
        IMemoryStore store,
        IIdGenerator ids,
        TimeProvider time,
        VoiceAvailability voice,
        IAttachmentStore? attachments = null,
        RoleKnowledgeService? knowledge = null,
        ISessionWorkspace? workspace = null,
        IArtifactStore? artifacts = null,
        IModelCatalog? models = null)
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
        _models = models;
    }

    public async Task<SessionSnapshot> CreateAsync(
        string agentId,
        int? agentVersion,
        SessionMode mode,
        CancellationToken cancellationToken = default,
        SessionPurpose? purpose = null,
        SessionCompletionPolicy? policy = null,
        TimeSpan? maxDuration = null,
        string? speechLocaleOverride = null,
        string? modelKey = null,
        string? reasoningEffort = null,
        ModelSelectionSource modelSource = ModelSelectionSource.SystemDefault)
    {
        if (string.IsNullOrWhiteSpace(agentId))
        {
            throw AgentCoreErrors.Validation("agentId is required.");
        }

        var definition = await _definitions.GetAsync(agentId, agentVersion, cancellationToken).ConfigureAwait(false)
            ?? throw AgentCoreErrors.NotFound($"Agent '{agentId}' was not found.");

        var localeOverride = SpeechLocale.NormalizeOverride(speechLocaleOverride);
        var effectiveLocale = SpeechLocale.Resolve(localeOverride, definition.ConversationPolicy.Language).Effective;
        var voiceAvailable = _voice.IsAvailable(definition, effectiveLocale);
        if (mode == SessionMode.Voice && !voiceAvailable)
        {
            throw AgentCoreErrors.VoiceUnavailable();
        }

        var now = _time.GetUtcNow();
        var profile = await EnsureLocalProfileAsync(now, cancellationToken).ConfigureAwait(false);
        SessionPurpose resolvedPurpose;
        try
        {
            resolvedPurpose = SessionLifecycle.ResolvePurpose(purpose, now, maxDuration);
        }
        catch (ArgumentOutOfRangeException ex)
        {
            throw AgentCoreErrors.Validation(ex.Message);
        }
        var resolvedPolicy = policy ?? SessionCompletionPolicy.Default;
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
            LastUserActivityAt: now,
            Title: SessionTitles.Default,
            RuntimeEpoch: 0,
            WorkspaceOwned: true,
            LifecycleStatus: SessionLifecycleStatus.Active,
            Purpose: resolvedPurpose,
            CompletionPolicy: resolvedPolicy,
            LifecycleSource: LifecycleTransitionSource.System,
            LifecycleChangedAt: now,
            SpeechLocaleOverride: localeOverride,
            ModelSelection: BindCreatedModel(definition, modelKey, reasoningEffort, modelSource));
        if (SessionLifecycle.DeadlineElapsed(resolvedPurpose, now))
        {
            snapshot = LifecycleTransition.Apply(
                snapshot,
                SessionLifecycleStatus.Expired,
                LifecycleTransitionSource.System,
                now,
                "deadline");
        }

        await _store.SaveAsync(snapshot, expectedRevision: 0, cancellationToken).ConfigureAwait(false);
        return snapshot;
    }

    public async Task<SessionSnapshot> GetAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
        var snapshot = await _store.LoadMetadataAsync(sessionId, cancellationToken).ConfigureAwait(false)
            ?? throw AgentCoreErrors.NotFound("Session was not found.");
        if (snapshot.DurablyDeletedAt is not null)
        {
            throw AgentCoreErrors.NotFound("Session was not found.");
        }

        return await PersistDeadlineExpiryAsync(snapshot, cancellationToken).ConfigureAwait(false);
    }

    public async Task<SessionSnapshot> LoadRuntimeAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
        var snapshot = await _store.LoadAsync(sessionId, cancellationToken).ConfigureAwait(false)
            ?? throw AgentCoreErrors.NotFound("Session was not found.");
        if (snapshot.DurablyDeletedAt is not null)
        {
            throw AgentCoreErrors.NotFound("Session was not found.");
        }

        return await PersistDeadlineExpiryAsync(snapshot, cancellationToken).ConfigureAwait(false);
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
        var snapshot = await GetAsync(sessionId, cancellationToken).ConfigureAwait(false);
        if (LifecycleTransition.IsTerminal(snapshot))
        {
            throw AgentCoreErrors.Validation("Ended sessions cannot be reopened.");
        }

        if (snapshot.ArchivedAt is not null)
        {
            throw AgentCoreErrors.Validation("Archived sessions must be unarchived before reopen.");
        }

        if (snapshot.Status is SessionStatus.Attached)
        {
            throw AgentCoreErrors.SessionInUse();
        }

        var resuming = snapshot.Status == SessionStatus.Paused
            && SessionPauseSemantics.RequiresExplicitResume(snapshot.PauseReason);
        if (!resuming)
        {
            return snapshot;
        }

        return await TransitionLifecycleAsync(
                sessionId,
                SessionLifecycleStatus.Active,
                LifecycleTransitionSource.User,
                cancellationToken,
                "reopen")
            .ConfigureAwait(false);
    }

    public async Task<SessionSnapshot> DeactivateAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
        var started = Stopwatch.GetTimestamp();
        var snapshot = await GetAsync(sessionId, cancellationToken).ConfigureAwait(false);
        if (LifecycleTransition.IsTerminal(snapshot))
        {
            throw AgentCoreErrors.Validation("Ended sessions cannot be deactivated.");
        }

        if (snapshot.ArchivedAt is not null)
        {
            throw AgentCoreErrors.SessionArchived();
        }

        if (snapshot.Status == SessionStatus.Paused)
        {
            RuntimeTelemetry.Record("initiative", RuntimeTelemetry.ElapsedMs(started));
            return snapshot;
        }

        var result = await TransitionLifecycleAsync(
                sessionId,
                SessionLifecycleStatus.Paused,
                LifecycleTransitionSource.User,
                cancellationToken,
                "manual")
            .ConfigureAwait(false);
        RuntimeTelemetry.Record("initiative", RuntimeTelemetry.ElapsedMs(started));
        return result;
    }

    public async Task DurablyDeleteAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
        var started = Stopwatch.GetTimestamp();
        var snapshot = await _store.LoadAsync(sessionId, cancellationToken).ConfigureAwait(false)
            ?? throw AgentCoreErrors.NotFound("Session was not found.");
        if (snapshot.DurablyDeletedAt is null)
        {
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
        }

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

        RuntimeTelemetry.Record("cleanup", RuntimeTelemetry.ElapsedMs(started));
    }

    public async Task EnsureAttachmentsReadableAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
        var snapshot = await GetAsync(sessionId, cancellationToken).ConfigureAwait(false);
        if (snapshot.ArchivedAt is not null)
        {
            throw AgentCoreErrors.SessionArchived();
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
        CancellationToken cancellationToken,
        bool touchCatalogOrder = true)
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
                UpdatedAt = touchCatalogOrder ? _time.GetUtcNow() : snapshot.UpdatedAt
            };
        }

        await _store.SaveAsync(next, snapshot.Revision, cancellationToken).ConfigureAwait(false);
        return next;
    }

    private static bool MemoryStoreEqual(SessionSnapshot left, SessionSnapshot right) =>
        left.Title == right.Title
        && left.ArchivedAt == right.ArchivedAt
        && left.RuntimeEpoch == right.RuntimeEpoch
        && left.Status == right.Status
        && left.LifecycleStatus == right.LifecycleStatus;

    public async Task<ConversationHistoryPage> ReadHistoryPageAsync(
        Guid sessionId,
        long? after,
        long? before,
        int limit,
        CancellationToken cancellationToken = default)
    {
        if (after is not null && before is not null)
        {
            throw AgentCoreErrors.Validation("History cursor is invalid.");
        }

        if (limit is < 1 or > 100
            || after is < 0
            || before is < 1)
        {
            throw AgentCoreErrors.Validation("History cursor is invalid.");
        }

        var page = await _store.ReadHistoryPageAsync(sessionId, after, before, limit, cancellationToken)
            .ConfigureAwait(false);
        return page ?? throw AgentCoreErrors.NotFound("Session was not found.");
    }

    public async Task<IReadOnlyList<ConversationEntry>> ReadHistoryAsync(
        Guid sessionId,
        long after,
        int limit,
        CancellationToken cancellationToken = default) =>
        (await ReadHistoryPageAsync(sessionId, after, before: null, limit, cancellationToken).ConfigureAwait(false)).Items;

    public async Task EndAsync(Guid sessionId, CancellationToken cancellationToken = default) =>
        await TransitionLifecycleAsync(
                sessionId,
                SessionLifecycleStatus.Ended,
                LifecycleTransitionSource.Legacy,
                cancellationToken,
                "ended")
            .ConfigureAwait(false);

    public async Task<SessionSnapshot> TransitionLifecycleAsync(
        Guid sessionId,
        SessionLifecycleStatus target,
        LifecycleTransitionSource source,
        CancellationToken cancellationToken = default,
        string? reason = null)
    {
        var snapshot = await GetAsync(sessionId, cancellationToken).ConfigureAwait(false);
        if (target == SessionLifecycleStatus.Ended && LifecycleTransition.IsTerminal(snapshot))
        {
            return snapshot;
        }

        var next = LifecycleTransition.Apply(snapshot, target, source, _time.GetUtcNow(), reason);
        if (target == SessionLifecycleStatus.Paused
            && snapshot.LifecycleStatus != SessionLifecycleStatus.Paused
            && next.LifecycleStatus == SessionLifecycleStatus.Paused)
        {
            SessionPauseTelemetry.Record(reason ?? "manual");
        }

        if (ReferenceEquals(next, snapshot) || MemoryStoreEqual(snapshot, next))
        {
            if (next.Revision == snapshot.Revision && next.UpdatedAt == snapshot.UpdatedAt)
            {
                return snapshot;
            }
        }

        if (next.Revision == snapshot.Revision)
        {
            next = next with { Revision = snapshot.Revision + 1 };
        }

        try
        {
            await _store.SaveAsync(next, snapshot.Revision, cancellationToken).ConfigureAwait(false);
            return next;
        }
        catch (AgentCoreException ex) when (ex.Code is "SessionPersistenceUnavailable" or "Conflict")
        {
            var loaded = await GetAsync(sessionId, cancellationToken).ConfigureAwait(false);
            if (loaded.LifecycleStatus == target || (target == SessionLifecycleStatus.Ended && LifecycleTransition.IsTerminal(loaded)))
            {
                return loaded;
            }

            throw;
        }
    }

    public async Task<SessionSnapshot> SetSpeechLocaleAsync(
        Guid sessionId,
        string? locale,
        CancellationToken cancellationToken = default)
    {
        var snapshot = await GetAsync(sessionId, cancellationToken).ConfigureAwait(false);
        var normalized = SpeechLocale.NormalizeOverride(locale);
        if (string.Equals(snapshot.SpeechLocaleOverride, normalized, StringComparison.Ordinal))
        {
            return snapshot;
        }

        var next = snapshot with
        {
            SpeechLocaleOverride = normalized,
            Revision = snapshot.Revision + 1,
            UpdatedAt = _time.GetUtcNow()
        };
        await _store.SaveAsync(next, snapshot.Revision, cancellationToken).ConfigureAwait(false);
        return next;
    }

    public async Task<SessionSnapshot> SetModelAsync(
        Guid sessionId,
        string? modelKey,
        string? reasoningEffort,
        ModelSelectionSource source,
        CancellationToken cancellationToken = default)
    {
        var snapshot = await GetAsync(sessionId, cancellationToken).ConfigureAwait(false);
        if (LifecycleTransition.IsTerminal(snapshot))
        {
            throw AgentCoreErrors.Validation("Ended sessions cannot change model.");
        }

        if (_models is null)
        {
            throw AgentCoreErrors.Validation("Model catalog is not configured.");
        }

        var selection = SessionModelBinder.Bind(
            _models,
            modelKey,
            reasoningEffort,
            source,
            snapshot.Definition.ModelDefaults);
        if (snapshot.ModelSelection == selection)
        {
            return snapshot;
        }

        var next = snapshot with
        {
            ModelSelection = selection,
            Revision = snapshot.Revision + 1,
            UpdatedAt = _time.GetUtcNow()
        };
        await _store.SaveAsync(next, snapshot.Revision, cancellationToken).ConfigureAwait(false);
        return next;
    }

    public SessionSnapshot PinModelSelection(SessionSnapshot snapshot)
    {
        if (snapshot.ModelSelection is not null || _models is null)
        {
            return snapshot;
        }

        return snapshot with
        {
            ModelSelection = SessionModelBinder.PinDefault(_models, snapshot.Definition)
        };
    }

    private SessionModelSelection? BindCreatedModel(
        AgentDefinition definition,
        string? modelKey,
        string? reasoningEffort,
        ModelSelectionSource source)
    {
        if (_models is null)
        {
            return null;
        }

        return SessionModelBinder.Bind(_models, modelKey, reasoningEffort, source, definition.ModelDefaults);
    }

    private async Task<SessionSnapshot> PersistDeadlineExpiryAsync(
        SessionSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        var now = _time.GetUtcNow();
        if (LifecycleTransition.IsTerminal(snapshot) || !SessionLifecycle.DeadlineElapsed(snapshot.Purpose, now))
        {
            return snapshot;
        }

        var expired = LifecycleTransition.Apply(
            snapshot,
            SessionLifecycleStatus.Expired,
            LifecycleTransitionSource.System,
            now,
            "deadline");
        if (expired.Revision == snapshot.Revision)
        {
            expired = expired with { Revision = snapshot.Revision + 1 };
        }

        try
        {
            await _store.SaveAsync(expired, snapshot.Revision, cancellationToken).ConfigureAwait(false);
            return expired;
        }
        catch (AgentCoreException ex) when (ex.Code is "SessionPersistenceUnavailable" or "Conflict")
        {
            var loaded = snapshot.Entries.Count > 0
                ? await _store.LoadAsync(snapshot.SessionId, cancellationToken).ConfigureAwait(false)
                : await _store.LoadMetadataAsync(snapshot.SessionId, cancellationToken).ConfigureAwait(false);
            if (loaded is not null && LifecycleTransition.IsTerminal(loaded))
            {
                return loaded;
            }

            throw;
        }
    }

    private async Task<UserProfile> EnsureLocalProfileAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        var existing = await _store.LoadProfileAsync(LocalUserProfile.Id, cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            LocalUserProfile.Validate(existing.Preferences);
            if (!LocalUserProfile.InventedPreferredNameNeedsRemoval(existing.Preferences))
            {
                return existing;
            }

            var cleaned = new UserProfile(
                existing.ProfileId,
                existing.Revision + 1,
                LocalUserProfile.WithoutInventedPreferredName(existing.Preferences),
                now);
            LocalUserProfile.Validate(cleaned.Preferences);
            try
            {
                await _store.SaveProfileAsync(cleaned, existing.Revision, cancellationToken).ConfigureAwait(false);
                return cleaned;
            }
            catch (AgentCoreException ex) when (ex.Code == "Conflict")
            {
                var raced = await _store.LoadProfileAsync(LocalUserProfile.Id, cancellationToken).ConfigureAwait(false);
                if (raced is null)
                {
                    throw;
                }

                LocalUserProfile.Validate(raced.Preferences);
                return raced with
                {
                    Preferences = LocalUserProfile.WithoutInventedPreferredName(raced.Preferences)
                };
            }
        }

        var created = new UserProfile(
            LocalUserProfile.Id,
            1,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["language"] = "en"
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
        var started = Stopwatch.GetTimestamp();
        await workspace.EnsureAsync(sessionId, snapshot.Definition, cancellationToken).ConfigureAwait(false);
        await workspace.WriteAsync(sessionId, logicalPath, bytes, cancellationToken).ConfigureAwait(false);
        RuntimeTelemetry.Record("workspace", RuntimeTelemetry.ElapsedMs(started));
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
        var started = Stopwatch.GetTimestamp();
        await workspace.WriteAsync(sessionId, logical, bytes, cancellationToken).ConfigureAwait(false);
        RuntimeTelemetry.Record("workspace", RuntimeTelemetry.ElapsedMs(started));
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
    public EffectiveSpeechPlan? Plan { get; init; }
    public bool SpeechAdaptersResolved { get; init; } = true;
    public ISpeechLocaleSupport LocaleSupport { get; init; } = UnrestrictedSpeechLocaleSupport.Instance;

    public EffectiveSpeechPlan EffectivePlan =>
        Plan ?? new EffectiveSpeechPlan(
            SpeechTransport.ServerAudio,
            SpeechTransport.ServerAudio,
            SpeechAdaptersResolved,
            SpeechAdaptersResolved,
            RecognitionCapabilities: null,
            SynthesisCapabilities: null);

    public bool IsAvailable(AgentDefinition definition, string? locale = null)
    {
        if (!definition.Voice.Enabled
            || string.IsNullOrWhiteSpace(definition.ProviderPreferences.SpeechRecognizer)
            || string.IsNullOrWhiteSpace(definition.ProviderPreferences.SpeechSynthesizer)
            || !EffectivePlan.RecognitionResolvable
            || !EffectivePlan.SynthesisResolvable)
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(locale))
        {
            return true;
        }

        return LocaleSupport.CanRecognize(locale) && LocaleSupport.CanSynthesize(locale);
    }
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
    ISpeechRecognizer? recognizer,
    ISpeechSynthesizer? synthesizer,
    VoiceAvailability voice,
    IAttachmentStore attachments,
    IAttachmentProcessor processor,
    IArtifactReferenceAuthorizer artifacts,
    SessionToolExecutor tools,
    ILanguageModelResolver? models = null,
    IModelCatalog? catalog = null)
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
            recognition: voice.EffectivePlan.RecognitionCapabilities
                ?? recognizer?.Capabilities
                ?? new RecognitionCapabilities(false, false, false, false),
            policy,
            recognizer,
            synthesizer,
            voice: voice,
            attachments: attachments,
            processor: processor,
            artifacts: artifacts,
            tools: tools,
            modelResolver: models,
            catalog: catalog);
}
