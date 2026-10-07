# Persistence and Configuration

## Execution-local capability loads

Migration `CapabilityExecutionLoads` adds nullable `ConversationTurnExecutions.LoadedCapabilityIdsJson` and non-null `CapabilityLoadCount` default zero. Existing rows remain empty. In-memory and SQLite stores use the same Domain revision/claim/state/cancellation admission. Both reject an already-canceled admission token before mutating loaded IDs, call count or revision. Loaded state survives requeue/reclaim of the same execution and is fresh on a new execution. Upgraded EnsureCreated databases stamp the migration only when both fields exist; partial schemas fail closed.

Durable WorkItems retain explicit loaded IDs/count alongside result messages in their existing bounded checkpoint payload, preserving normal revision/claim/cancellation fencing. Recovered loaded names are filtered against pinned authority and current eligibility. No persistent per-Agent load set, new workspace state, PCM, provider schema or query log is added. Publication JSON carries exact authorization/projection policy without a new Definition table.

Already-accepted live occurrences retain the existing non-replayable `AcceptedLive` receipt. Their bounded loaded IDs/count are response/epoch-owned Session mailbox state, rechecking the current connection on admission; they survive continuation within that runtime and reset for each occurrence. They never inherit a completed user turn's IDs. Live user turns and durable WorkItems use their existing persisted execution records for same-execution reclaim; this change does not make accepted live occurrence receipts replayable.


## Workspace refinement storage and lifecycle

Home uses metadata, first-class directory rows and opaque immutable blobs under `Persistence:WorkspaceRoot/agent-<instanceN>/home/blobs/<opaqueBlobIdN>`. Every Session scratch root maps to `Persistence:WorkspaceRoot/agent-<instanceN>/sessions/session-<sessionN>/working`. Infrastructure derives both through `AgentWorkspacePhysicalPaths`; logical filenames never become home blob filenames. There is no separately configured home root, raw Session-id fallback or physical `workspace/` intermediate directory. Artifacts use their separate store/root; scratch has no artifacts/state directories. `.provisioned` sits beside `working/` to guard one-time template seeding and preserve user edits on reopen. Migration `20261007014134_UnifiedAgentWorkspace` drops the instance Compatibility column and requires Session AgentInstanceId. It rejects legacy null/empty owners and compatibility rows with a clear reset-required error before schema mutation; no automatic conversion or reset runs.

Logical paths are `/home` and `/working`, with owner/root selection derived from trusted metadata. Session deletion fences writers and removes only that Session tree; durable home survives. Archive preserves home inspection and disallows mutation. Existing hard-delete preconditions require Session cleanup before the instance purge removes home metadata/blobs. Empty parent ownership containers may remain after all Session directories are deleted; they contain no working data. Neither owner UUID directory names nor blob keys appear in model/client projections.

Cwd is transient Session Runtime mailbox metadata and starts at `/home` when a runtime is reconstructed or a new Session is created. It is not stored in SQLite or a file and never carries into future Sessions. Home content/tree revisions and hashes remain durable and survive reopen/Compose recreation. Compose configures both home and `Persistence:WorkspaceRoot` under the persistent `/data` volume, so a container recreation also preserves current Session scratch. Cross-root copy is an exact source snapshot followed by independently guarded destination import, not a cross-store transaction. Structural batch remains single-store with its existing partial execution reporting.


## Persistence model

EF Core 10 with SQLite is the MVP durable store, implemented behind IMemoryStore in Infrastructure. The base Synthetic/testing profile defaults to InMemoryMemoryStore; the normal native developer workflow explicitly selects SQLite as shown in [Operations](17-observability-and-operations.md#running-after-implementation). SQLite contract/integration suites use a temporary database. Real profile defaults to SQLite. No raw microphone frames, TTS chunks, credentials, provider request bodies, every token delta, or high-frequency controller internals are persisted.

| Entity | Key and fields | Rules |
| --- | --- | --- |
| Session | SessionId UUID string PK; required non-empty AgentInstanceId; AgentId, AgentVersion, DefinitionJson, Mode, PendingMode nullable, Status, PauseReason nullable, CreatedAtUtc, UpdatedAtUtc, Revision | Store pinned validated definition; terminal Ended is irreversible; PauseReason set when Status is Paused |
| ConversationEntry | EntryId UUID PK; SessionId FK; EntrySequence; SourceEventId nullable; Role; Text (display); ResponseId nullable; Status; DeliveryMode; HeardTextEndExclusive (speech coordinate); ReceivedTextEndExclusive (display); EnvelopeJson nullable; AttachmentRefsJson nullable; FailureReferenceJson nullable; CreatedAtUtc | Unique (SessionId,EntrySequence); unique (SessionId,SourceEventId) when not null; response ID unique per assistant entry. `FailureReferenceJson` (`20260929121048_P7FailureReference`) stores one safe failure reference on a failed assistant row and stays null otherwise, including legacy failed rows |
| SessionSnapshot | SessionId PK/FK; SchemaVersion=1; Summary; SummarizedThroughEntrySequence; SummaryFormatVersion; SummaryGeneratedAtUtc nullable; SummaryModelCatalogKey/ProviderAlias/ModelId/ReasoningEffort nullable; PendingTopic nullable; ProfileId nullable; LastEntrySequence; LastUserActivityAtUtc nullable; UpdatedAtUtc; additive LifecycleStatus/PurposeKind/PurposeDescription/DeadlineAtUtc/PurposeMetadataJson/AgentCompletion/UserCompletionAllowed/UserCancellationAllowed/LifecycleReason/LifecycleSource/LifecycleChangedAtUtc nullable | Persist coarse semantic continuity and last meaningful user activity for inactivity policy, never tasks/timers/active provider streams. Purpose metadata and completion policy are private. Protocol-v1 Session.Status stays compatible. Summary text and through-sequence stay canonical; missing summary metadata loads as format 0 and is not rewritten. |
| UserProfile | ProfileId UUID PK; PreferencesJson; Revision; UpdatedAtUtc | <=16 allowlisted preferences, <=2,000 total characters; MVP uses one local profile |

A reopened snapshot loads the newest restore window, not every raw row. Older conversation rows stay in the store. A committed summary is the model-facing copy of the closed prefix; compaction does not delete or rewrite those rows. Conversation memory is agent-proposed and Core-admitted. Natural-language pattern matching is not the authority for durable memory writes. `MemoryPolicy` enables a scope; it does not admit every proposal. The model never receives memory ids or owner ids, and it does not write the store. `MemoryAdmission` accepts a staged `MemoryProposal` from a model envelope, or a direct proposal from an application command, and then calls `StructuredMemoryService`. Model proposals are admitted once at successful response completion. A failed, interrupted, or superseded response does not write them. Model source is `userExplicit` or `agentInferred`. The completed assistant envelope stores the controller admission receipt apart from entry text and speech text. User-explicit failure stays visible after reload; inferred outcomes stay silent. Reliable proposals come from native structured output or the adapter response function. A marker is best effort. A model without either channel can still recall admitted memory and is told not to claim a new save. Structured memory lives in `StructuredMemories` (`20260923050000_StructuredSessionMemory`, scope columns in `20260923090000_MemoryScope`). Existing rows are Session scope. Active session rows are unique per session, kind, and normalized subject. Active IdentityUser rows are unique per agent instance, profile, kind, and normalized subject, and they keep origin memory and session ids. Deletion clears subject and content and keeps a tombstone. Durable session delete removes that session's Session-scoped rows and leaves IdentityUser and User rows. Active User rows are unique per profile, kind, and normalized subject (`20260923110000_UserMemory`). Rollback after these writes is a restored pre-migration SQLite backup; a destructive down-migration is not the recovery path. Agent instances live in `AgentInstances` (`20260923070000_AgentInstance`). Every Session stores a required non-empty `AgentInstanceId` and its pinned persona. Migration `20261007014134_UnifiedAgentWorkspace` removes the instance discriminator and rejects old compatibility owners or missing/empty Session ownership with a reset-required message. It never backfills or reassigns identity. Multiple real instances may share a Definition.

Store timestamps as UTC Unix milliseconds (`long`) with explicit EF conversions to DateTimeOffset, and GUIDs as canonical text. Do not depend on SQLite ordering arbitrary DateTimeOffset strings or rowversion support. Session.Revision is an application-managed optimistic concurrency token. Enable foreign keys, WAL and 5-second busy timeout; keep writes short. No DbContext instance is shared by workers. SQLite is a single local file, not a network filesystem/distributed store.

Domain snapshot contracts used by IMemoryStore:

```csharp
public enum ConversationRole { User, Assistant, ApplicationMessage }
public enum EntryStatus { Streaming, Completed, Interrupted, Failed }
public enum SessionMode { Text, Voice }
public enum SessionStatus { Created, Attached, Paused, Ending, Ended }
public sealed record ConversationAttachmentRef(Guid AttachmentId, string DisplayName, string ContentType);
public sealed record ConversationEntry(Guid EntryId, long Sequence,
    Guid? SourceEventId, ConversationRole Role, string Text, Guid? ResponseId,
    EntryStatus Status, SessionMode DeliveryMode,
    int HeardTextEndExclusive, int ReceivedTextEndExclusive,
    DateTimeOffset CreatedAt, ResponseEnvelope? Envelope = null,
    IReadOnlyList<ConversationAttachmentRef>? Attachments = null);
public sealed record UserProfile(Guid ProfileId, long Revision,
    IReadOnlyDictionary<string, string> Preferences, DateTimeOffset UpdatedAt);
public sealed record SessionSnapshot(int SchemaVersion, Guid SessionId,
    long Revision, AgentDefinition Definition, SessionMode Mode, SessionMode? PendingMode,
    SessionStatus Status,
    IReadOnlyList<ConversationEntry> Entries, string Summary,
    long SummarizedThroughEntrySequence, string? PendingTopic, Guid? ProfileId,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);
```

For bounded personal MVP sessions, `LoadMetadataAsync` does not load conversation rows. `LoadAsync` restores a bounded runtime window rather than the full transcript; cap a session at 1,000 entries and require a new session at the limit. Runtime prompts select a smaller window. `LastEntrySequence` is a durable snapshot coordinate. `ReadHistoryPageAsync` pages newest/`before`/`after` without loading every row. SaveAsync atomically updates Session, Snapshot and upserts supplied entries in one transaction; it never deletes unseen entries. Snapshot.Entries on a restored runtime is a window. Store revision must match expectedRevision; snapshot.Revision must equal expectedRevision+1. Insert uses expectedRevision=0 and snapshot.Revision=1. A retry of the same committed revision/content is idempotent; a differing stale write fails Conflict. InMemoryMemoryStore enforces identical rules.

**Follow-on P1 observed and frozen:** Additive `lifecycleStatus` and purpose/deadline/policy persist on the snapshot; Application `LifecycleTransition` is the persist path for pause/resume/terminal outcomes and deadline expiry. Trusted-host `/api/v2/host/sessions` is the create/configuration surface for purpose/policy; ordinary public SessionView, catalog, `session.ready`, and history stay private of those fields. RequestComplete evaluation is observed. Session `SpeechLocaleOverride` persists independently of agent conversation language. See [Technology Decisions](10-technology-decisions.md#decision-provider-neutral-effective-speech-locale).

**P2D session model selection (observed):** Persist concrete `SessionModelSelection` (catalog key, trusted provider alias, model ID, selection source, reasoning effort) on the snapshot. Default is not a stored pointer. Legacy sessions pin the configured default before the first post-upgrade generation. Assistant entries may store `ModelGenerationProvenance` for the turn that produced them; older rows may be null. Do not persist secrets. The shipped Real catalog default is `deepseek-v41-flash` (`deepseek/deepseek-v4.1-flash`, medium). Explicit session choices also include `gpt-4o-mini-2024-07-18` (`openai/gpt-4o-mini-2024-07-18`), `openrouter-free` (`openrouter/free`), `gpt-4.1` (`openai/gpt-4.1`), and `gpt-5.6-luna` (`openai/gpt-5.6-luna`, reasoning efforts `max`/`xhigh`/`high`/`medium`/`low`/`none`, catalog default `low`). `openrouter/free` is not the system default. See [Technology Decisions](10-technology-decisions.md#decision-session-model-selection-and-inference-controls).

Summary metadata is additive. Format 0 is the legacy default. A semantic compaction candidate stores format 1, the UTC generation time, and `ModelGenerationProvenance` for the model that produced it. Loading a snapshot does not rewrite an existing summary.

Stored Text retains full generated text. DeliveryMode records the interaction mode in which the entry was produced and never changes if the session later switches mode. ReceivedTextEndExclusive is the last validated browser-rendered prefix. Public history projects that prefix while an assistant entry is Streaming or Interrupted, but projects full stored text after Completed or Failed so a browser disconnect cannot turn a successfully completed durable response into a permanently truncated transcript. User entries set both offsets to Text.Length. On supersession, freeze HeardTextEndExclusive and ReceivedTextEndExclusive at their last validated values; late playback events/receipts do not revise that response. HeardTextEndExclusive remains separately conservative for voice-delivered context using [Spoken Until](06-realtime-voice.md#spoken-until); rendering terminal text after return is not evidence of hearing it. Prompt construction uses received prefix when DeliveryMode is Text and heard prefix when DeliveryMode is Voice.

## Write ordering and recovery

One persistence write is in flight per Session Runtime. Capture immutable snapshots and serialize write dispatch, coalescing pending periodic checkpoints to the newest state. The mailbox still accepts interrupt/end events while writes execute. After a store write, persist completion returns through the mailbox with persist-token identity; only a still-pending token may apply. Advance durable revision only for that acknowledged snapshot, then start the next write. Apply subsequent dirty state to the next revision. Never let an old snapshot overwrite new spoken-until or terminal state. Checkpoints waiting behind an in-flight write collapse to the newest queued checkpoint; user-turn, pause, terminal, and other callback-bearing saves are not coalesced.

Durability boundaries: save on creation, accepted final user turn, response terminal, mode change (including queued `PendingMode` while the session remains attached), pause/disconnect and end. On pause/disconnect persist `PendingMode=null` even if a voice request was queued; Mode stays the last **applied** mode (Text if voice never started). During active generation checkpoint at most once per second, preserving only currently assembled text and conservative heard/received offsets. A 1-second streaming checkpoint **must not** rewrite every historical conversation row. Persistence identifies/upserts changed or currently streaming entries plus changed snapshot/session metadata. Immutable completed historical entries are not rewritten every checkpoint. Transactional SaveAsync, expectedRevision and optimistic-concurrency semantics are unchanged; Snapshot.Entries in the in-memory contract may still list the full session, but the Infrastructure writer must not issue no-op UPDATEs for unchanged completed rows. User turn generation waits for its save acknowledgement; final response completion waits for terminal save acknowledgement. Checkpoints cannot mark a still-streaming response completed. Temporary storage failure pauses new generation, emits recoverable SessionPersistenceUnavailable and retries local save after 1/2/5 seconds (maximum 3 retries); continued failure cancels active output, invalidates the attachment and pauses a non-ending session for explicit reconnect. An Ending session remains Ending until its terminal save succeeds; it cannot resume. Conflict is fatal for that runtime because it indicates a second writer or bug.

A crash may lose up to the last checkpoint interval of streaming text/progress, but not a user turn whose save succeeded. On startup/load, reconcile Attached→Paused and Streaming→Interrupted transactionally; set `PendingMode=null` so crash recovery does not auto-enter voice; keep the last recorded heard offset, never assume unacknowledged audio was heard. After attach, a trailing completed-user suffix with no later started assistant entry is one next user-turn; if an assistant entry for that suffix already exists (including Interrupted after recovery), do not start another. Ending recovers by completing the end transaction before attach; it cannot return to active. Provider streams, audio, timer generations and connection leases are never resumed.

## P7 durable conversational execution

Migration `20260926175411_P7ConversationTurnExecution` adds `ConversationTurnExecutions`, separate from P6 `WorkItems`. Every accepted text user entry has one idempotent execution row keyed by `(SessionId, SourceEventId)` before `SendText` returns Accepted. Queued entries each own a row; entries intentionally batched into one next model turn share a stable response identity and terminal assistant entry. Rows pin Agent Instance/profile, definition version, persona value and model selection; claim generation plus lease fences one runner. `PinnedActiveSkillIdsJson` on the execution the prompt reads stores the ordered active Skill ids chosen from that definition version and the turn trigger text, the last user entry when several pending entries share one response. A later entry that replaces a compaction-deferred trigger stores that new selection before the model request. A null value is an empty set. After the model request is built, retry and recovery read that stored pin. `SkillLoadCount` (`20260930174851_P85SkillLoadCount`) counts `skills.load` admissions on that execution. Recovery skips keyword reselection when that count is already positive. `Queued`, `Running`, and `WaitingForApproval` are open execution states; terminal states are `Completed`, `Failed`, and `Cancelled`. An application-message row stores `Role` as `ApplicationMessage` and `ApplicationMessageEffectKey` (`20260930181733_P85ApplicationMessageEffectKey`). Its visible text is the entry text. It has no envelope. Admission dedupe is exactly-once within one live or recovered durable snapshot. The mailbox publishes `session.entry.upsert` without waiting for the SQLite commit. A process stop before that commit can drop a message the browser already showed, and a later replay can admit it again. The effect key is stored and is not a unique index. This matches asynchronous session persistence for other conversation entries. It is not a transactional external-delivery guarantee. A later externally consequential use should add an explicit durable effect record rather than treating this row as an outbox.

Terminal ordering is model/tools finish → construct terminal assistant entry → save the session/entry → attach the durable assistant entry id to every execution in that response batch → transition execution terminal → publish terminal live notification. Streaming checkpoints persist the first visible partial and then at most once per second. Attach exposes durable history plus stable conversation execution/response identity and polls durable history while live, so a completion racing snapshot/subscription converges without replaying generation or duplicating prefixes. Legacy current-model `EnsureCreated()` databases stamp this migration only after strict column and index validation; malformed shapes are rejected instead of hidden by `IF NOT EXISTS`.

Memory categories: ephemeral working state; persistent conversation history; persistent session summary; minimal persistent user profile. Allowlisted durable keys are `language`, `preferredName`, `locale`, and `timeZone`, stored as typed `UserProfileValue` records (value, provenance source, per-field `UpdatedAt`) in `PreferencesJson` without a table migration. Legacy string-only JSON rows load conservatively (`ApplicationProfile` provenance; historical `"friend"` treated absent) and rewrite to typed JSON on next save. Local demo setup seeds `language=en` with `ApplicationProfile` source and does not invent `preferredName`. Preferred name is trusted profile data only, not inferred from conversation. Owner-capability HTTP `GET|PATCH /api/v2/profile` mutates the local profile with server-side provenance stamping; live sessions receive newer revisions through the runtime mailbox on subsequent turns. No profile settings UI, embeddings, vector search or Redis.

## P7B definition lifecycle store (observed)

Migration `20260925071140_P7DefinitionLifecycle` adds `AgentDefinitionDrafts` and `AgentDefinitionPublications`. Draft rows hold a mutable candidate JSON payload, monotonic draft `Revision`, fork source metadata, and timestamps. Optional `skills` live in that candidate and publication JSON. A missing property loads as an empty Skill set, so Skills do not require a schema migration. Revision-protected draft deletion removes the draft row plus unpublished resource bindings and evaluation scenario/result rows in one transaction; content-addressed blobs may remain for other bindings, and that draft delete does not remove publication rows. A separate logical-definition delete, allowed only for a durable-only definition with no instance, session, work-item, approval, or conversation-execution references, purges that definition's drafts, draft resources, evaluation rows, publications, and publication bindings in one transaction and appends `DefinitionDeleted`. Admin history rows stay. Built-in file definitions are not purged. Publication rows store immutable definition JSON, server-assigned `(DefinitionId, Version)` unique across built-in file versions and durable rows, `MetadataRevision` for deprecation-only updates, and `Status` (`Active` / `Deprecated`). `IAgentDefinitionAdminStore` is implemented for InMemory (Synthetic) and SQLite (developer/Real). Runtime `IAgentDefinitionStore` composes file built-ins with durable publications without mutating `agents/*.json` or existing session snapshot definition JSON.

## P7C definition resource store (observed)

Migration `20260925084907_P7DefinitionResources` adds draft and publication resource binding rows (logical path, kind, media type, verified SHA-256, size) copied atomically inside the publish transaction. Owner draft publication consumes the source draft, its unpublished bindings, and evaluation rows in the same transaction after copying the immutable publication and resources. Rejected publication leaves the draft intact. Internal harness publication retains its candidate draft for its separate adoption/evidence lifecycle. Resource mutations require the parent draft's expected revision and bump that revision. Content bytes live in `IDefinitionResourceContentStore` (InMemory for tests, file-backed blobs for SQLite). `AgentResourceLimits` enforce per-item, count, and aggregate caps; textual uploads run secret-pattern checks. Logical paths are bounded POSIX-relative names; traversal, repository-seed prefixes, and forbidden targets are rejected. Published bytes and bindings are immutable; later draft edits do not alter earlier publication hashes. Batch bind writes every accepted path in one transaction and returns the full logical-path-ordered draft list. A rejected manifest, including a stale revision, does not bump `Revision` or leave a partial set. The same content hash may be bound at more than one path. Optional knowledge `resourcePath` lives in the candidate JSON and is omitted when null. Session `/working` remains the mutable session-owned area; publication template resources may seed `/working` once without write-back to definition storage.

## Admin authoring persistence (observed)

A new definition draft persists the server starter and `SourceKind.New`. The same definition id cannot be created again while a built-in version, durable publication, or draft still exists. After a successful logical-definition delete, the id may be reused. Managed instance hard delete removes only an archived instance row when `expectedRevision` matches and no live session, active identity-user memory item, trigger registration, trigger occurrence, work item, approval, or conversation execution references it. The delete appends `InstanceDeleted` in the same transaction and does not cascade. In a single process, `AdminLifecycleCoordinator` serializes instance delete with session creation for the same instance. For each logical definition id it serializes delete with explicit Agent Instance creation, new-draft, fork, publish, deprecate, draft update/delete, draft resource bind/upsert/remove, evaluation scenario/result persistence, and managed-instance creation. Pure reads and content-addressed blob upload stay outside the gate. Draft-scoped APIs resolve the definition id, acquire the gate, then re-load the draft inside the gate before mutating. Creating an instance with a persona writes that persona on the insert. The history row is `ManagedInstanceCreated` with `personaSource` Custom and no following `PersonaChanged`. Omit or null persona stores the definition identity as Default.

## P7D managed instance store (observed)

AgentInstances persist Lifecycle (`Active`/`Archived`), monotonic Revision, PersonaRevision and persona JSON. `UpdateWithExpectedRevisionAsync` applies persona, active-version and lifecycle CAS. Archive changes metadata only. Every Session requires a non-empty AgentInstanceId and pins persona/revision and exact Definition version. InMemory and SQLite enforce the same ownership and revision invariants. Historical migrations are preserved; the unified migration removes the former identity discriminator.

## P7F draft evaluation evidence (observed)

Draft evaluation rows bind Synthetic scenario results to draft revision, configuration fingerprint, and scenario version; stale configuration fingerprint, scenario version, or draft revision reject publish. SQLite and InMemory stores share parity for evaluation persistence and reopen.

## P7G Admin events (observed)

Migration adds append-only `AdminEvents` with unique `OperationId`, actor kind, operation name, target type/id, and bounded allowlisted summary JSON. No update/delete API. SQLite and InMemory `IAdminEventStore` implementations share idempotent append and list filters. Legacy SQLite reopen may repair empty malformed P7 tables before migration apply (`P7EnsureCreatedReopenMigrationTests`). See [P7G report](reports/p7g-history-rollback-final-gate.md).

## P5 trigger store (observed)

`ITriggerStore` is separate from `IMemoryStore` and structured memory. Migration `20260923160000_TriggerContracts` adds `TriggerRegistrations` and `TriggerOccurrences` with no session or memory foreign keys. Migration `20260923220000_OwnerScopedOccurrenceDedupe` replaces the global dedupe unique index with Agent Instance + profile + dedupe key. Timestamps are Unix milliseconds at offset zero. User mutations are owner-scoped and use `expectedRevision`. Schedule meaning changes `ScheduleRevision`. A scheduler advance bumps the row `Revision` and occurrence count, not `ScheduleRevision`. Routing changes bump `RoutingRevision` only. Due scans and claim methods are the scheduler/router's cross-owner path. A missing timezone on the trusted profile blocks a local-time schedule. Ambiguous local times use the earlier UTC offset. A gap in local time shifts forward across the missing clock time. Weekly interval phase follows the Monday-based local week of the stored next occurrence. A recurring `startDate` is the cadence anchor ("starting on this date"), including when that date is already past; omitting it anchors the phase at the first slot after creation. `startDate` is also a not-before bound. Occurrence dedupe is unique per Agent Instance, profile, and dedupe key, so the same application event id can admit one occurrence for each owner. Routing rejects a pending scheduled occurrence whose captured schedule revision no longer matches the registration. If the reserved runtime does not begin an occurrence after `AcceptedLive` is stored, that row returns to `Pending`.

Migration `20261003155703_P96ExecutionModelPin` adds nullable unattended model columns on `AgentInstances`, optional override columns and `RequiresVision` on `TriggerRegistrations`, and the execution-model pin columns on `TriggerOccurrences` (`ModelCatalogKey`, `ModelProviderAlias`, `ModelId`, `ModelReasoningEffort`, `ModelSource`). A null occurrence pin is resolved once and then stored. Routing updates do not clear it. `AgentInstanceRevisionUpdate.SetUnattendedModel` writes the instance columns without changing persona or definition version. InMemory and SQLite stores share that behavior. Migration `20261003163628_P96WorkCaptures` stores unattended browser captures apart from session artifacts: capture id, work item id, agent instance id, content type, byte size, sha256, created and retain-until timestamps, and a relative path under the artifact root. Accepted browser downloads use the same rows, owner, retention, and eight-item cap. The stored extension follows the content type. Retention is seven days from capture, or 24 hours after the work item becomes terminal, whichever is later. Expired rows and files are deleted when a capture is saved and on each durable work pass, including when no new capture is saved. A resumed unattended turn reloads a checkpointed `browser.capture` from that store so the next model call still receives the image. If those bytes are already gone, the tool result tells the model to capture the page again. At most eight captures are kept for one work item.

## Post-MVP planned until verified

Observed A–H persistence/layout. Phase I remains not-applicable: Support, Compliance, and `sandbox.run` do not continue after `RequestDeactivate`. P6 durable work is a separate schema. Migration `20260924060915_WorkItemContracts` adds `WorkItems` and `WorkApprovals`. Migration `20260924065742_OccurrenceDurableWorkLink` links an accepted occurrence to one work item. Owner is Agent Instance plus profile. `SourceOccurrenceId` is unique. `Revision` is the concurrency token. Waiting and terminal rows hold no claim. Public reads omit evidence, checkpoint payload, and prepared action JSON. Migration `20260929125852_P7WorkFailureDiagnosticId` adds nullable `FailureDiagnosticId` on `WorkItems` for a terminal failure only. Retry and cancellation leave it null. MVP Session.Status `Ended` remains irreversible terminal-end.

Durable WorkItem checkpoints retain the existing 64 KiB UTF-8 bound. Tool-result admission uses the remaining serialized checkpoint capacity as well as the shared tool-output budget; no checkpoint-size increase or schema migration is required. Capacity failures are terminal `checkpoint-capacity` outcomes. Recovery preserves completed/uncertain side-effect fences, including when a legacy full checkpoint cannot accommodate observation metadata. Checkpoint payloads remain absent from public work reads. Valid completion tool calls may carry optional compact summary data inside the existing private checkpoint JSON; readers retain compatibility with legacy `ArgumentsJson` checkpoints. This additive payload representation requires no database migration. Completion reserves derive from the actual maximum Thought/occurrence contracts, retain browser recovery space and preserve room for one Core-owned `finish_required` notice; no effect fence is cleared before its result checkpoint commits.

- **Owner capability grant:** hashed trusted-local token in SQLite; validate HTTP/hub callers; survive process restart.
- **Catalog fields:** Title, ArchivedAt, DeletedAt/pending-cleanup, WorkspaceOwnership (SessionId key), pinned AgentId+AgentVersion. Rename/archive/deactivate/delete take the same revision-checked save path as snapshots so they cannot be overwritten by a concurrent runtime checkpoint. Deactivate persists `Paused` and increments `RuntimeEpoch` without setting `ArchivedAt` or clearing history.
- **Attachments (observed store):** metadata and MessageAttachment relation in SQLite; bytes in `IAttachmentStore` opaque blob keys **outside** row payloads and outside developer `local/` scratch (`data/attachments` by default). Pending TTL 1 hour; bound originals immutable; SHA-256 unchanged after bind. Durable session delete removes that session's blobs and metadata. Derived extraction cache is keyed by AttachmentId plus processor version and is not conversation history.
- **Session workspace (observed):** lazy physical tree under `Persistence:WorkspaceRoot/agent-<instanceN>/sessions/session-<sessionN>/working` (never under `local/` or `local/tdp-workspace`). Logical `/working` writes cap at 250 MiB. `/agent` and `/attachments` are overlays, not copies. Optional templates come from `agents/templates/{templateId}` into `working` only. Archive/reopen/deactivate keep files; durable delete removes them. Do not persist CTS, provider streams, sockets, leases, timer handles, DbContext, live queues, or process handles. Docker sandbox bind-mounts only the physical Session `working/` directory at its private container `/workspace/working` mount and does not leave `acsbx-*` residue outside that session tree.
- **Rich receipts (observed):** persist display, speech-coordinate, and block delivery atomically with response status (R2) in `EnvelopeJson` plus heard/received offsets. Do not store a single offset that is applied to both text and speech. `EnvelopeJson` uses `speech.mode` plus optional `speech.text`; when mode is `same` but the stored playback coordinate differs from `displayText` (for example markdown-cleaned Voice prose), persist the coordinate in `speech.text` so SQLite reopen keeps heard-context aligned. Equivalent custom speech that matches display normalizes to `same` without duplicate `speech.text` on write.
- **Artifacts (observed store):** distinct metadata/blobs from Attachments under `data/artifacts/{sessionId}` (never under `local/`). 50 MiB each / 250 MiB per session, concurrent fail-closed. Explicit attachment materialize copies into `/working` with provenance (`SourceAttachmentId`) and matching SHA-256. Archive/deactivate keep blobs; durable delete cancels writers and removes them. Envelope authorization includes stored ids and `fixture-artifact-1`.
- **Migrations:** backfill workspace-ownership; preserve pins, revisions, receipts, and irreversible Ended rows on populated fixtures and fresh databases.

PostgreSQL migration later replaces EF provider, revisits GUID/time conversions, migrations and concurrency tests; business interfaces stay stable. Provider change alone does not magically make SQLite-specific SQL portable, so avoid provider SQL outside Infrastructure and test migration data explicitly.

## P7.5 persistence audit (observed)

This pass keeps the current stores. No persistence code changed. SQLite stays the durable provider. Domain and Application do not reference `Microsoft.Data.Sqlite` or EF Core.

| Check | Decision | Evidence |
| --- | --- | --- |
| SQLite SQL and locking | Keep | `sqlite_master` queries and `Microsoft.Data.Sqlite` stay in Infrastructure, including `SqliteMemoryStore`. `SqlitePragmaInterceptor` sets `journal_mode=WAL`, `busy_timeout`, and `foreign_keys`. `SqliteMemoryStore.BackupDatabaseWithRetryAsync` retries backup. |
| Migration determinism | Keep | EF migrations stay under `src/AgentCore.Infrastructure/Persistence/Migrations`. `P7EnsureCreatedReopenMigrationTests` reopens a legacy database and refuses to stamp an incompatible shape. No non-repeatable migration was shown. |
| Transactions | Keep | Explicit transactions cover `SqliteMemoryStore.SaveAsync`, definition `DeleteDraftAsync` and `PublishDraftAsync`, structured-memory writes, `SqliteTriggerStore.TryAdmitScheduledAsync`, work-item `MutateAsync`, `RecoverExpiredClaimsAsync`, and `ExpireDueApprovalsAsync`, `SqliteDurableWorkHandoff.AcceptCoreAsync`, attachment `BindToEntryAsync`, and Admin history append plus memory-scope reset. Managed instance history stages the instance row and `AdminEvent` in one `SaveChanges`. |
| Idempotency | Keep | Operation-id replay is covered by `AdminManagedInstanceHistoryTests`, `AdminInstanceDefinitionVersionHistoryTests`, `AdminInstancePersonaHistoryTests`, and `AdminInstanceLifecycleHistoryTests`. `WorkItemStoreContractTests.Create_is_owner_scoped_and_idempotent_for_one_source_occurrence` and `TriggerStoreContractTests.Occurrence_admission_dedupes_and_hides_other_owners` cover duplicate source events. |
| Process-local versus durable truth | Keep | The Session Runtime mailbox and the `SessionHost` connection table are process-local. Snapshots, `ConversationTurnExecution`, triggers, WorkItems, approvals, and Admin events are durable. Accepted user text creates the execution row from `EnsureConversationExecutionsForUserBatchAsync` on the save path. `AcceptedTurnDetachDurabilityTests.Detach_after_ack_before_response_still_executes` covers work that continues after detach. |
| Restart and reopen | Keep | `P7EnsureCreatedReopenMigrationTests`, `TriggerStoreContractTests.Sqlite_reopen_preserves_registration_and_occurrence`, `WorkItemStoreContractTests.Sqlite_reopen_preserves_cancellation_result_and_approval`, and `TriggerOccurrenceRoutingTests.Pending_occurrence_survives_sqlite_restart`. |
| Stale revision | Keep | `WorkItemStoreContractTests.Stale_revision_and_generation_cannot_checkpoint_or_complete`. Instance history updates reject a mismatched `ExpectedRevision` before `SaveChanges`. |
| Approval resume | Keep | `ToolApprovalTests.Approval_flow_executes_once_after_explicit_approve`, `DurableReminderTests.Approval_waits_without_a_claim_and_resumes_the_same_work_item`, and `WorkItemStoreContractTests.Approval_resume_claims_the_same_attempt_at_the_budget_limit`. |
| Logical `Path` checks | Keep | `WorkspaceFileNames` uses `GetFileName`, `GetExtension`, and `GetFileNameWithoutExtension` after slash normalization. `RolePermissions.AllowsLogicalPath` rejects a non-slash rooted logical path. `SessionToolExecutor.LooksLikeHostPath` rejects drive, UNC, and non-slash rooted tool arguments. Workspace search uses `GetFileName` on logical paths. Attachment materialization uses `GetFileName` on `LogicalPath`. None of these writes a host path. |
| Physical bytes | Keep | `FileSessionWorkspace`, attachment blobs, artifact blobs, and definition-resource content stay in Infrastructure. Contracts keep logical ids. |
| Separate stores | Keep | `IAttachmentStore`, `IArtifactStore`, `IAgentDefinitionResourceAdminStore`, and `ISessionWorkspace` stay separate. |
| Sandbox and workers | Keep | `DockerSandboxExecutor` remains the sandbox. There is no `ISandboxProvider`. `TriggerSchedulerHostedService`, `DurableWorkIntakeHostedService`, `DurableWorkHostedService`, and `ConversationExecutionHostedService` stay single-process. |
| Secrets | Keep | Credentials stay in the environment, `dotnet user-secrets`, or the gitignored Compose `.env`. `ProviderPreferences` names aliases only. `Admin_responses_do_not_leak_secret_sentinels` rejects `OPENROUTER_SECRET_SENTINEL` on definition, inventory, and effective-config responses. |

## Strongly typed options

Bind/validate on startup with standard .NET options and ValidateOnStart. These option type names match the root sections below. Nested providers use capability-specific records; do not inject IConfiguration into Domain/Application. Session factory receives a validated effective policy snapshot.

| Options type/section | Fields and purpose |
| --- | --- |
| AgentCoreOptions / AgentCore | Profile (Synthetic default), definition directory, session limits (`MaxActiveSessions` counts in-memory runtimes only), reconnect grace, mailbox capacity, context budgets |
| ProvidersOptions / Providers | LanguageModels map plus independent `Speech.Recognition` / `Speech.Synthesis` records (defaults Adapter=Synthetic). SpeechRecognizers / SpeechSynthesizers maps remain aliases: `primary-stt` / `primary-tts` bind when the nested Speech Adapter is unset. Recognition names after P1: Synthetic, Browser, OpenAI, OpenAICompatibleBatch. Synthesis: Synthetic, Browser, OpenAI. |
| InteractionOptions / Interaction | Candidate thresholds, classifier deadline, ducking, degraded policy, `PendingVoiceTimeoutMs` (default 30000) |
| VoiceOptions / Voice | Canonical format, frame size, queue budgets, utterance limit, playback progress |
| PersistenceOptions / Persistence | Provider, connection string, checkpoint interval, busy timeout, attachment blob root (`data/attachments`), workspace root (`data/workspaces`), template root (`agents/templates`), artifact blob root (`data/artifacts`); never under `local/` |
| ObservabilityOptions / Observability | Logging level, timeline limit, content logging opt-in, OTLP enable/endpoint |
| HostingOptions / Hosting | Same-origin/default local binding, allowed development origins, development proxy behavior, Compose published-port gateway trust (`TrustPublishedPortGateway`, default false; resolves only inside a container) |

Complete conceptual appsettings.json example, **Markdown only**:

```json
{
  "AgentCore": {
    "Profile": "Synthetic",
    "AgentDirectory": "agents",
    "MaxActiveSessions": 10,
    "MaxEntriesPerSession": 1000,
    "ReconnectGraceSeconds": 60,
    "MailboxCapacity": 256,
    "Context": {"MaxHistoryEntries": 20, "MaxHistoryCharacters": 24000, "MaxSummaryCharacters": 2000, "MaxProfileCharacters": 2000}
  },
  "Providers": {
    "LanguageModels": {
      "primary-llm": {
        "Adapter": "Scripted",
        "BaseUrl": "https://provider.example/v1/",
        "ApiKey": "",
        "DefaultModel": "configured-model",
        "AdditionalHeaders": {},
        "Timeouts": {"SetupSeconds": 10, "StreamIdleSeconds": 60, "TotalSeconds": 120}
      }
    },
    "ModelCatalog": {
      "DefaultKey": "scripted-alpha",
      "Models": [
        {
          "Key": "scripted-alpha",
          "DisplayName": "Scripted Alpha",
          "ProviderAlias": "primary-llm",
          "ModelId": "scripted-alpha",
          "Tools": true,
          "Vision": false,
          "StructuredOutput": false,
          "Reasoning": true,
          "SupportedReasoningEfforts": ["low", "medium", "high"],
          "DefaultReasoningEffort": "medium"
        }
      ]
    },
    "Speech": {
      "Recognition": {
        "Adapter": "Synthetic",
        "BaseUrl": "https://speech.example/v1/",
        "ApiKey": "",
        "DefaultModel": "configured-stt-model",
        "AdditionalHeaders": {},
        "Timeouts": {"SetupSeconds": 10, "StreamIdleSeconds": 60, "TotalSeconds": 120}
      },
      "Synthesis": {
        "Adapter": "Synthetic",
        "BaseUrl": "https://speech.example/v1/",
        "ApiKey": "",
        "DefaultModel": "configured-tts-model",
        "AdditionalHeaders": {},
        "Voices": {"default": "configured-voice", "fr-FR": "nova"},
        "Timeouts": {"SetupSeconds": 10, "StreamIdleSeconds": 60, "TotalSeconds": 120}
      }
    }
  },
  "Interaction": {
    "Classifier": "heuristic",
    "NoPartialPolicy": "speechAndFinal",
    "ActivityScoreThreshold": 0.7,
    "CandidateMinMs": 120,
    "BackchannelMaxMs": 700,
    "AmbiguousEvidenceMs": 250,
    "ClassifierTimeoutMs": 200,
    "SemanticFallbackMs": 500,
    "DegradedInterruptMs": 250,
    "FinalTranscriptTimeoutMs": 2000,
    "BatchFinalTranscriptTimeoutMs": 20000,
    "DuckEnabled": true,
    "DuckGain": 0.2,
    "DuckTimeoutMs": 600,
    "PendingVoiceTimeoutMs": 30000
  },
  "Voice": {
    "Encoding": "pcm_s16le", "SampleRateHz": 24000, "Channels": 1,
    "FrameDurationMs": 20, "InputQueueMs": 500, "OutputQueueMs": 2000,
    "PrebufferMs": 60, "MaxUtteranceSeconds": 120,
    "PlaybackProgressMs": 100, "PlaybackAckTimeoutMs": 5000,
    "Vad": {
      "NoiseFloorAdapt": 0.05, "StartThreshold": 0.7, "StartHangFrames": 3,
      "EndThreshold": 0.4, "EndHangFrames": 15, "MinActivityMs": 120,
      "ActivityScoreSmoothing": 0.3
    },
    "Segmentation": {"MinCharacters": 20, "ClauseMinCharacters": 40, "MaxDelayMs": 300, "SoftMaxCharacters": 120, "HardMaxCharacters": 240, "MaxPendingSegments": 4}
  },
  "Persistence": {"Provider": "InMemory", "ConnectionString": "Data Source=data/agent-core.db", "CheckpointMs": 1000, "BusyTimeoutMs": 5000, "AttachmentRoot": "data/attachments", "WorkspaceRoot": "data/workspaces", "TemplateRoot": "agents/templates", "ArtifactRoot": "data/artifacts", "DefinitionResourceRoot": "data/definition-resources"},
  "Observability": {"LogLevel": "Information", "TimelineCapacity": 500, "LogConversationContent": false, "OtlpEnabled": false, "OtlpEndpoint": "http://localhost:4317"},
  "Logging": {"LogLevel": {"Default": "Information", "Microsoft.AspNetCore": "Warning", "Microsoft.EntityFrameworkCore.Database.Command": "Warning"}},
  "Hosting": {"BindUrl": "http://localhost:5080", "AllowedOrigins": ["http://localhost:5173"], "UseViteProxy": true, "TrustPublishedPortGateway": false}
}
```

Language-model timeout defaults remain setup 10 / stream idle 60 / total 120 seconds. The native Real `http-openrouter` launch profile and Real Compose overlay explicitly use setup 30 / idle 60 / total 120; restart the host or recreate the Real container to apply the configuration change. Each provider call has its own total deadline; Session Runtime retries remain inside the existing overall execution budget. See [timeout and retry policy](12-backend-implementation-spec.md#timeouts-and-retry-policy).

The browser realtime protocol is not a server option. `VITE_AGENTCORE_REALTIME_PROTOCOL=json` selects SignalR JSON for that frontend process; unset or any other value stays MessagePack. Vite reads it when the SPA starts or is built (`cd web` and `VITE_AGENTCORE_REALTIME_PROTOCOL=json pnpm dev`, or gitignored `web/.env.local`; not repo-root `.env`). Restart the Vite dev server after changing it; a browser refresh alone is not enough. Setting the same name on the ASP.NET process or a running container after the SPA is built does not switch the transport. A JSON diagnostic image requires the variable as a Docker build arg (`docker compose build --build-arg VITE_AGENTCORE_REALTIME_PROTOCOL=json` then `docker compose up`). It does not change `protocolVersion` or persistence.

The shipped Synthetic catalog also includes `scripted-beta` with `StructuredOutput: true` (native envelope JSON) while `scripted-alpha` remains unstructured compatibility (`StructuredOutput: false`).

Configuration fields for inactive adapters are ignored after structural validation; Synthetic must never attempt example URLs or require ApiKey. Bind operator secrets from environment or user-secrets only: `OPENROUTER_API_KEY` into the OpenRouter-configured language-model `ApiKey`, and `OPENAI_API_KEY` into the OpenAI STT and TTS `ApiKey` fields. Nested `Providers__...__ApiKey` remains valid. Neither variable is required for Profile=Synthetic, default tests, or application boot in synthetic mode. **P3 optional Real adapters:** `BRAVE_SEARCH_API_KEY` (or `BraveSearch:ApiKey`) enables Brave `web.search` when Profile=Real. Without that key the tool is hidden by the configuration gate; that is the expected configuration check, not a reason to scrape HTML search pages. `Providers:LanguageModels:primary-llm:LogProviderErrorMessages` (default false) includes a redacted provider error message in local logs. Leave it disabled outside local diagnosis because the message can echo ordinary user text. `GMAIL_CLIENT_ID`, `GMAIL_CLIENT_SECRET`, and `GMAIL_REFRESH_TOKEN` (or `Gmail:ClientId` / `Gmail:ClientSecret` / `Gmail:RefreshToken`) enable Gmail `email.*` when Profile=Real. Committed `.env.example` lists placeholder names only; Synthetic profile keeps `SyntheticWebSearchProvider` and `SyntheticEmailProvider` without those keys. Real profile requires an explicitly configured non-synthetic LLM. STT/TTS adapters are required only when a loaded definition has `Voice.Enabled`. Preferred **Real/demo** configuration: OpenAICompatibleLanguageModel (Adapter=OpenAICompatible) pointing at OpenRouter for text with a **fixed** catalog default `DefaultModel` (shipped `deepseek/deepseek-v4.1-flash`; operators may retarget `DefaultKey` by setting `AGENTCORE_LLM_MODEL` to another catalog `ModelId`); hosted STT via `OpenAICompatibleBatch` (no interim partials) until `OpenAiSpeechRecognizer` realtime transcription is implemented and selectable; OpenAiSpeechSynthesizer (Adapter=OpenAI) for TTS. Recognition Adapter=`OpenAI` remains unselectable while the realtime session is a no-op. `openrouter/free` is an explicit Real catalog option and remaining opt-in adapter smoke target; it is not the shipped `DefaultKey` / `AGENTCORE_LLM_MODEL`. A Real **voice** process still needs speech keys when those adapters are selected; a Real **text** process needs only the OpenRouter key. Missing `OPENAI_API_KEY` must not fail synthetic/offline tests or block completing the text-conversation path. Browser STT/TTS need no backend speech key; they also do not make recognition or synthesis a guaranteed local/offline service. OpenAICompatibleSpeech is an optional compatible TTS adapter, not the primary streaming demonstration.

**Effective capabilities** come from the selected adapter. Optional `RequiredCapabilities` lists flags that must be **true**. Optional `DisabledCapabilities` may turn off optional adapter features (for example disable partials to test `speechAndFinal`). Do not use `RequiredCapabilities: false` to describe a batch adapter; select `OpenAICompatibleBatch`, which reports streaming/partials as false. Do not use a configuration `Capabilities` object to make an adapter claim unimplemented behavior. Built-in `OpenAI`/`Synthetic`/`Scripted` adapters define their own capabilities (Synthetic may withhold partials via adapter-specific test options). Generic compatible/local adapters may document operator assertions validated by contract tests. `heuristic` is the built-in classifier alias. `MaxActiveSessions` applies to in-memory runtimes on attach/activation; durable Created/Paused/Ended rows do not consume a slot.

AgentCore__Profile=Real selects hosted-oriented defaults. When `Persistence__Provider` is not set in the environment, the API host upgrades the base `InMemory` persistence entry to **Sqlite** so durable sessions survive restart; set `Persistence__Provider=InMemory` explicitly to keep ephemeral storage (for example Real Compose smoke). Example .NET environment overrides for OpenRouter text plus an **optional batch-STT degraded speech configuration** (operator supplies actual secrets later; these are placeholders):

```text
AgentCore__Profile=Real
Persistence__Provider=Sqlite
Persistence__ConnectionString=Data Source=data/agent-core.db
Providers__LanguageModels__primary-llm__Adapter=OpenAICompatible
Providers__LanguageModels__primary-llm__BaseUrl=https://openrouter.ai/api/v1/
Providers__LanguageModels__primary-llm__DefaultModel=deepseek/deepseek-v4.1-flash
OPENROUTER_API_KEY=<backend-secret>
Providers__Speech__Recognition__Adapter=OpenAICompatibleBatch
Providers__Speech__Synthesis__Adapter=OpenAI
Providers__Speech__Synthesis__DisabledCapabilities__TimingMarks=true
OPENAI_API_KEY=<backend-secret-when-using-openai-speech>
Hosting__AllowedOrigins__0=http://localhost:5173
```

Operators must also set real speech BaseUrl/DefaultModel/voice mapping for their selected endpoints. Environment keys with hyphens are supported by .NET configuration but are not valid shell variable assignment names; supply via a process environment map or quoted `env 'key=value'` arguments during implementation. Keep keys in backend user-secrets or environment, never VITE_* or React bundles. Production same-origin serving needs no permissive CORS. Restrict development origins; do not combine wildcard origins with credentials.

## Local personal workspace

Repository root `local/` is gitignored scratch (personal notes, temp files). Nothing under `local/` is shared in git. [Repository Structure](11-repository-structure.md) records the folder.

`local/` is **not** an application configuration source. Do not bind options from files in `local/`, do not load it with `IConfiguration`, and do not copy its contents into committed `appsettings*.json`. A gitignored root `.env` (copied from committed `.env.example`) is Compose interpolation for Real/hosted Compose and optional shell export for native processes; it is not loaded by ASP.NET. Bind `OPENROUTER_API_KEY` / `OPENAI_API_KEY` from process environment or `dotnet user-secrets` on the API project (for example `dotnet user-secrets set OPENROUTER_API_KEY <value> --project src/AgentCore.Api`). Optional P3 Real keys (`BRAVE_SEARCH_API_KEY`, `GMAIL_*`) follow the same binding rules. Never print keys in chat, logs, traces, docs, commits or shared command history. Missing hosted keys are not a test failure and must not block Synthetic/default suites.

## Hosted and on-prem provider configurations

Portable capability aliases live in Agent Definition/provider selection; concrete endpoints, model IDs, headers and secrets live only in backend Infrastructure options. Independent `Providers.Speech.Recognition` and `Providers.Speech.Synthesis` records are the primary speech selection. Keep LanguageModels plus optional SpeechRecognizers/SpeechSynthesizers maps: several text aliases can target different models for different identities, and `primary-stt`/`primary-tts` remain map fallbacks when nested Speech Adapter is unset. Do not replace them with one gateway-wide AI provider.

Preferred hosted text override (merge into the synthetic example when Profile=Real; configure speech separately):

```json
{
  "Providers": {
    "LanguageModels": {
      "primary-llm": {
        "Adapter": "OpenAICompatible",
        "BaseUrl": "https://openrouter.ai/api/v1/",
        "ApiKey": "<OPENROUTER_API_KEY>",
        "DefaultModel": "deepseek/deepseek-v4.1-flash",
        "AdditionalHeaders": {},
        "Timeouts": {"SetupSeconds": 10, "StreamIdleSeconds": 60, "TotalSeconds": 120}
      }
    }
  }
}
```

DefaultModel is the concrete model field; it is not copied into ModelRequest or public DTOs. Real/demo must use a **fixed** operator-selected OpenRouter model ID as the catalog default. The shipped development/demo default is `deepseek/deepseek-v4.1-flash`. Opt-in adapter smoke and the Real catalog option `openrouter-free` may use `openrouter/free`; do not assert quality or structured-output correctness of whatever free model is routed, and do not set it as `DefaultKey` / `AGENTCORE_LLM_MODEL`. To compare identities later, configure aliases `examiner-llm` and `support-llm` with different DefaultModel values, then select those aliases in each definition's ProviderPreferences.LanguageModel. No Agent Runtime condition branches on a provider/model name.

To migrate text inference on-prem, retain Adapter=OpenAICompatible and replace BaseUrl with e.g. `http://localhost:8000/v1/`, DefaultModel with the served local model name, and ApiKey/AdditionalHeaders with the local server's requirements. A vLLM-compatible endpoint is an example, not mandatory infrastructure. Direct OpenAI can use `https://api.openai.com/v1/` with its own credentials/model and verified compatibility settings. OpenRouter remains a hosted gateway; changing its model ID does not make inference local.

For hosted STT, select `OpenAICompatibleBatch` (no streaming input, no interim partials). Recognition Adapter=`OpenAI` (`OpenAiSpeechRecognizer` realtime transcription, recommended DefaultModel `gpt-live-transcribe`) remains unselectable while the live session is a no-op. For hosted TTS, select Adapter=`OpenAI` (`OpenAiSpeechSynthesizer`). Then independently replace the adapter alias, endpoint, model, credentials and required/disabled capability constraints as needed. Prefer streaming STT with partials and streaming TTS when a selectable adapter actually provides them; effective capabilities come from the adapter. Default automated tests keep Synthetic speech until `OPENAI_API_KEY` is supplied for a selected hosted speech adapter. Local speech may need a different concrete adapter if its protocol differs, but never changes ISpeechRecognizer/ISpeechSynthesizer or controller logic. All secrets/endpoints can be overridden using the same .NET double-underscore syntax, including:

```text
Providers__Speech__Recognition__BaseUrl=<hosted-or-local-stt-endpoint>
Providers__Speech__Recognition__DefaultModel=<stt-model>
Providers__Speech__Recognition__ApiKey=<backend-secret-or-empty>
Providers__Speech__Synthesis__BaseUrl=<hosted-or-local-tts-endpoint>
Providers__Speech__Synthesis__DefaultModel=<tts-model>
Providers__Speech__Synthesis__ApiKey=<backend-secret-or-empty>
Providers__Speech__Synthesis__Voices__default=<tts-voice>
```

[Technology Decisions](10-technology-decisions.md) owns the provider rationale. No actual configuration files are created by this documentation update; React receives only safe effective capabilities, never credentials, model IDs or endpoints.


## Provider selection and DI

Bind strongly typed SpeechRecognitionProviderOptions and SpeechSynthesisProviderOptions on `Providers.Speech.Recognition` / `Providers.Speech.Synthesis`, and LanguageModelProviderOptions as the values of the LanguageModels map. SpeechRecognizers / SpeechSynthesizers maps still bind the same option types for `primary-stt` / `primary-tts` when nested Speech Adapter is unset. Infrastructure `SpeechFactory` resolves those Adapter names into an `EffectiveSpeechPlan` (input/output transports, resolvability, and effective capabilities) plus optional `ISpeechRecognizer`/`ISpeechSynthesizer`. `Synthetic` registers the synthetic ports and `serverAudio`. `Browser` is a client transport (`clientTranscript`/`clientSpeech`) with no backend port; the plan still carries explicit client capabilities (partials and speech-boundary events for Browser STT; cancellation, voice selection and speaking rate for Browser TTS). `OpenAI` realtime recognition is not selectable while the live session is deferred; it is not replaced by Synthetic or by batch STT. `OpenAICompatibleBatch` is the supported hosted STT path: it resolves `OpenAICompatibleBatchSpeechRecognizer` on `serverAudio` when Adapter=`OpenAICompatibleBatch` and a backend API key is present, with StreamingAudio=false and PartialTranscripts=false. A missing key stays not resolvable. `OpenAI` TTS resolves `OpenAiSpeechSynthesizer` on `serverAudio` when Adapter=`OpenAI` and a backend API key is present; a missing key stays not resolvable and is not replaced by Synthetic. Default Synthetic/Browser suites still start without `OPENAI_API_KEY`. Live OpenAI TTS HTTP remains opt-in (`AGENTCORE_LIVE_OPENAI_TTS`). Selected non-Synthetic adapters are not silently replaced by Synthetic. An unknown or uninstalled **speech** adapter, or a gated hosted adapter, must not abort Synthetic or Real **text-only** host startup. Language-model selection remains independent of speech.

These are Markdown-only selection overrides merged with the existing full options example, not additional configuration schemas or actual files. Each selected adapter reports effective capabilities; optional RequiredCapabilities/DisabledCapabilities and timeouts/model/voice configuration still apply. Provider-specific model IDs and secrets stay in backend options.

Initial hosted configuration:

```json
{
  "Providers": {
    "LanguageModels": {"primary-llm": {"Adapter": "OpenAICompatible", "BaseUrl": "https://openrouter.ai/api/v1/", "DefaultModel": "deepseek/deepseek-v4.1-flash", "ApiKey": "<OPENROUTER_API_KEY>"}},
    "Speech": {
      "Recognition": {"Adapter": "OpenAICompatibleBatch", "BaseUrl": "https://api.openai.com/v1/", "DefaultModel": "<stt-model>", "ApiKey": "<OPENAI_API_KEY>"},
      "Synthesis": {"Adapter": "OpenAI", "BaseUrl": "https://api.openai.com/v1/", "DefaultModel": "<tts-model>", "Voices": {"default": "<voice>"}, "ApiKey": "<OPENAI_API_KEY>"}
    }
  }
}
```

Synthetic configuration (Profile=Synthetic; no keys or external calls):

```json
{
  "Providers": {
    "LanguageModels": {"primary-llm": {"Adapter": "Scripted"}},
    "Speech": {"Recognition": {"Adapter": "Synthetic"}, "Synthesis": {"Adapter": "Synthetic"}}
  }
}
```

Future local configuration (service names illustrate a Compose network):

```json
{
  "Providers": {
    "SpeechRecognizers": {"primary-stt": {"Adapter": "Local", "BaseUrl": "http://local-stt:8000/", "DefaultModel": "<local-stt-model>"}},
    "LanguageModels": {"primary-llm": {"Adapter": "OpenAICompatible", "BaseUrl": "http://local-llm:8000/v1/", "DefaultModel": "<local-text-model>"}},
    "SpeechSynthesizers": {"primary-tts": {"Adapter": "Local", "BaseUrl": "http://local-tts:8000/", "DefaultModel": "<local-tts-model>", "Voices": {"default": "<local-voice>"}}}
  }
}
```

Use the documented double-underscore environment overrides for endpoints and secrets in native processes or Compose. [Operations](17-observability-and-operations.md#docker-compose-integration-and-demo) owns the Real overlay file (`docker-compose.real.yml`) that applies hosted OpenRouter text (the same shape as the `http-openrouter` launch profile); Compose interpolates `OPENROUTER_API_KEY` from the host, never from committed files. When moving local, explicitly clear/replace any inherited hosted ApiKey and headers; the examples do not imply a public unauthenticated local service. React never receives credentials. Updating configuration/DI bindings is not an Agent Runtime code change. A provider lacking partials or timing marks selects existing capability-based policies; it does not require a new controller design. A new protocol may require a new Infrastructure adapter only. `ISandboxExecutor` is composed in Infrastructure as `DockerSandboxExecutor` using the same `ISessionWorkspace` / `IArtifactStore` registrations; it is not a second application service.

## P9.5 application connection (observed)

`ApplicationConnections` is one row per Agent Instance (`AgentInstanceId` unique). Columns are connection id, kind, display name, base URL, trusted-origin JSON, status, profile key, revision, created and updated UTC milliseconds, and an optional status code. The profile key stores the Agent Instance id. Migration `20261004020003_P96ExternalEventSource` drops the earlier webhook columns from this table. `ExternalEventSources` stores source id, display name, kind, public source key, credential hash, status, revision, and UTC timestamps. `SourceKey` is unique. The raw token is not a column. `ExternalEvents` stores event id, source id, source event id, event type, occurred and admitted timestamps, and normalized evidence. `SourceId` plus `SourceEventId` is unique. `ExternalEventDeliveries` stores the admission-time subscriber snapshot: event id, registration id, agent instance id, profile id, and status `Pending`, `Admitted`, or `Skipped`. The event id plus registration id is unique. `TriggerRegistrations` adds nullable `EventSourceId` and `EventType` for an `order.placed` subscription. A legacy database stamps `20261004020003_P96ExternalEventSource` only when `ExternalEventSources` already exists, and stamps `20261004065312_P96ExternalEventDelivery` only when `ExternalEventDeliveries` already exists. Cookies, tokens, and profile paths are not columns. SQLite and in-memory stores share `IApplicationConnectionStore`. Migration `P95ApplicationConnection` creates the table. A legacy database stamps that migration only when the table already exists, so `Migrate` still creates it otherwise. Reopen preserves `Connected`. Reset deletes `ProfileRoot/{agentInstanceId}` and its `.lock` file, not other instance directories and not the Agent Core SQLite file. Migration `20261002114349_P95WorkAttention` adds `WorkItems.ResultAttentionRequired` and `WorkAttentionAlerts`. A quiet completion stores the flag false and inserts no alert. An attention completion inserts one alert keyed by work item id and revision. The legacy stamp includes that migration only when the column already exists.

## P9 visible browser (observed)

`Browser` is its own configuration section, separate from `Hosting:AllowedOrigins`. `PolicyMode` is `OpenWeb` for the local Real/demo process and `Restricted` for Synthetic and CI. OpenWeb allows ordinary `http`/`https` navigation, interaction, and page resources, including loopback, and does not start or advertise the Record Lookup fixture. It still denies `file`, `javascript`, credential user-info, and link-local metadata addresses such as `169.254.169.254`. The model cannot change `PolicyMode`. Restricted keeps exact `NavigationOrigins`, `InteractionOrigins`, and `ResourceOrigins`. Base `appsettings.json` and the `http` Synthetic launch profile stay `PolicyMode` `Restricted` with `FixtureEnabled` true, `FixturePort` 5091, and `NavigationOrigins` and `InteractionOrigins` `http://127.0.0.1:5091`. The `http-openrouter` Real launch profile sets `Browser__PolicyMode` `OpenWeb`, `Browser__ProfileMode` `PersistentAgent`, `Browser__Channel` `chrome`, `Browser__FixtureEnabled` false, `Browser__Headless` false, and `Browser__InteractionMode` `InteractiveDemo`. `PersistentAgent` stores that Agent Instance's browser profile under `ProfileRoot` (`data/browser-profiles/<agent-instance-id>` or the Real launch override). It is not the human user's Chrome profile. Cookies and site storage survive chats and process restarts. The exact tab and element refs do not. `EphemeralSession` remains the Synthetic default: each session gets a disposable context. There is no profile branch in runtime code. In Restricted mode, add an exact origin such as `https://docs.nopcommerce.com` to `NavigationOrigins` to open that site, and to `InteractionOrigins` only when click, fill, select, press, check, or uncheck should be allowed there. OpenWeb does not need that list. `browser.*` is still hidden until Infrastructure confirms the configured launch target: Playwright Chromium when `Channel` is unset, or a successful launch probe of the configured `Channel` (for example `chrome`). The Docker image and Compose set `Browser__Enabled` false because that image does not install Chromium. `FixturePort` 0 binds an ephemeral port and retargets configured loopback origins to that assigned origin. A legacy `TargetOrigins` list, when set, is the navigation list. Tests and the Synthetic Playwright hosts set `Headless` true. Element refs are not stored in session snapshots. EphemeralSession browser state is not written to SQLite. `PersistentAgent` site cookies and storage live only under `ProfileRoot`, not in the conversation store.

## P9.7 instance preparation persistence

Migration `20261004092058_P97HarnessManagement` adds nullable `AgentInstances.HarnessManagementJson`; null defaults to Disabled. Chat-first authoring reuses this field and existing lifecycle tables without another migration. Legacy EnsureCreated bootstrap still recognizes the column before stamping the migration. Synthetic needs no provider credentials.

Instance mode/scopes/freeze, derived eligible configured tools, policy revision, internal candidate, exact approvals and revision-bound evidence/publication remain durable through existing CAS stores. Legacy source/budget fields remain compatible for advanced owner records; normal Chat uses ordinary tool authority and existing normal-generation budgets rather than a second preparation budget/runtime. Source receipts are execution-local and provenance persists on retained resources. Resource/draft mutation and history append remain atomic at each owner boundary. Adoption commits active version, completed result and actor-aware version-change event in one transaction. Failure can leave an unused immutable publication but preserves the prior instance version. Reopen preserves published content/evidence and pending legacy decisions. No uncertain approval is replayed; fresh authoring forks after failure. Freeze persists revocation while preserving adopted content and history.

## P9.8-P9.9 persistence and defaults

Experience JSON persists optional `CheckpointAtUtc` independently from source creation (`SourceAtUtc`); old JSON loads with a null checkpoint time and requires no schema migration. Deleted thoughts remain cancelled registrations while their instance exists. Safe archived-instance deletion purges only cancelled AdminThought registrations and rejected ThoughtActivation occurrences with no WorkItem link. Pending/claimed/intake occurrences and all durable work/history remain deletion blockers; ordinary trigger history retains its existing behavior.

Migration `20261005003621_P9899Experience` adds `ExperienceSettings` (instance key, enabled, revision) and `Experiences` (record key, instance/profile/source kind/id/cursor, created time, bounded record JSON, revision). Unique `(AgentInstanceId, SourceKind, SourceId, ThroughCursor)` enforces logical checkpoint identity; a recency index supports bounded retrieval. EF migration and legacy EnsureCreated schema adoption both support these tables. Source history stays in its existing store; Experience has no memory-table foreign semantics.

Settings default disabled/revision zero. Completed content is immutable. Visibility uses compare-and-swap revisions; suppression is reversible, deletion/reset clear content and retain tombstones so duplicate or late requests cannot restore it. Settings/visibility/reset and safe Admin history metadata commit in the same SQLite transaction (or InMemory owner lock). Pending requests persist generation model/Definition/persona pins and use deterministic WorkItem identities to repair an admission gap after restart. The recovery scan selects records without a generation WorkItem, so terminal failed jobs cannot starve missing admissions. Archived/deleted instances are not eligible for recall/admission; eventual allowed instance deletion purges derived records/settings. Session deletion makes pending source projection unavailable without reclassifying the source result.

Thoughts reuse existing TriggerRegistrations/Occurrences/WorkItems/WorkApprovals. `AdminThought` authorization provenance marks owner-only registrations; `Disabled` differs from automatic policy suspension. Fixed interval and prompt/model/revision are ordinary registration fields. Admission commits the exact thinking-prompt evidence, registration revision and execution model pin with occurrence creation; later edits affect future admissions. Busy/nonterminal occurrences or attached work (including approvals/retries) coalesce the schedule forward rather than creating overlap. The existing WorkItem lease/recovery/side-effect fence remains authoritative.

Default bounds: eight nondeleted thought registrations per instance; 15 seconds minimum, 604800 maximum interval; the Admin default remains one hour. The shared interval value supports this bound; ordinary user schedule policy retains its 60-second minimum; 2000-character prompt/output summary; 24 tool-loop steps/180 seconds overall and three attempts through existing work budgets. No new global scheduler/provider credential/configuration system is required. Synthetic remains credential-free; persistence provider remains independent of provider profile.

## Continuity follow-on storage and admission

Continuity retrieval uses projections and owned Session metadata queries; cadence adds the small `ContinuityMaintenanceSettings` table, without a vector service. Existing structured-memory statuses and policies, Experience enable/visibility/tombstones, Session durable deletion and SQLite reopen semantics remain authoritative. The host maintenance loop polls every minute by default and evaluates only due instances (five-minute default); it follows ascending UUID keyset pages of at most 100 instances/Sessions to exhaustion, so records beyond the first page are not starved. The separate per-instance evaluation timestamp survives restart; there is no durable page cursor. The existing unique instance/source-kind/source-id/through-cursor Experience key and pending-admission repair are its durable checkpoint/outbox. Disabled Experience prevents synthesis and Experience recall; existing eligible learned Memory/history remain available under their own policies.

Admin schedules reuse TriggerRegistrations, TriggerOccurrences, WorkItems and safe AdminEvents. `ScheduleRegistrationChanged` appends bounded metadata and an intent hash atomically with revisioned save; it never logs task text. Authorization-origin additions append enum values without renumbering existing persisted values. Manual Schedule evidence contains Core's internal `manualOwnerRun` marker and immutable intent/model snapshot. Routing bypasses a later timing-edit revision mismatch for that admitted snapshot, while cancellation, ownership and policy checks remain. No schema migration or new provider configuration is required.

Latest Schedule/Thought execution reads filter WorkItems by instance, trusted profile and registration before selecting the newest creation time/ID. Both InMemory and SQLite return one row or null; the general 100-row history limit does not apply to this lookup. This changes only reads and uses existing stored provenance, without changing admission, claiming, or execution.

## P9.10 identity maintenance durability

Migration `20261006013730_P910IdentityMaintenance` adds `StructuredMemories.DerivedFromMemoryIdsJson` (legacy default `[]`), nullable MaintenanceOrigin and `IdentityMaintenanceSettings` keyed by AgentInstanceId with AllowAgentConsolidation and Revision. Current EnsureCreated databases are stamped only when these columns/table already exist; migration-managed older databases run the additive migration normally. Instance deletion purges the maintenance row. No provider credential or process configuration enables consolidation.

Experience's existing payload JSON gains optional immediate `DerivedFromExperienceIds` and MaintenanceOrigin. Existing Session=0/WorkItem=1 source-kind values remain; Consolidation=2 is appended. Existing Eligible=0/Suppressed=1/Deleted=2 remain; Superseded=3 is appended. Older payloads deserialize with absent lineage and unchanged meaning. Superseded history remains content-bearing; deletion/reset remains destructive and prevents restoration. Automatic recall uses only current Active Memory and Eligible completed Experience.

SQLite transactions and in-memory locks own whole-operation atomicity. Deterministic payload/source-based result IDs provide exact retry after reopen. Source Memory state/content and Experience revision/visibility are checked inside the mutation boundary. Active subject indexes retain their uniqueness guarantee without counting superseded records as capacity. Immediate lineage, not a single promotion origin, describes a combined record. The setting is a separate revisioned owner contract, independent of Experience generation and Thought registration.

Migration `20261006042725_P910MaintenanceProvenance` adds nullable `MaintenanceAgentInstanceId`, `MaintenanceSessionId` and `MaintenanceWorkItemId` to StructuredMemories, and raises durable WorkApprovals preview metadata to 12,000 characters. Legacy rows retain null initiators; no historical attribution is invented. EnsureCreated databases stamp this migration only when all three new columns exist. Maintenance Session IDs describe initiation, independently of `OriginSessionId` promotion lineage; deleting a source Session does not cascade-delete consolidated identity/User state.

### Configurable continuity maintenance cadence

A separate revisioned `ContinuityMaintenanceSettings` record owns a nullable configured interval and `LastMaintenanceAtUtc`. Missing settings inherit the operator default (300 seconds). The timestamp means an eligible active-instance checkpoint evaluation was claimed, including evaluations with no stable source; it is persisted before reading source history. Configuration revisions advance only for Admin edits. A compare-and-set on the configuration revision and previous timestamp lets one concurrent scan claim each due evaluation. Restart preserves the timestamp; interval edits retain it and recompute due time from the new effective interval. Existing deterministic Experience admission and durable Retrospection recovery remain the final fences. Instance deletion purges these settings.

`ContinuityMaintenance` operator policy has poll/minimum/default/maximum intervals of 60/60/300/86400 seconds. Positive intervals and minimum <= default <= maximum are validated at startup; polling is bounded to 1–3600 seconds. One hosted loop pages candidates in batches of 100 and skips instances that are not due. It creates no timers, registrations or Thoughts per instance. Admin updates reject out-of-range input and stale revisions. If an operator changes the allowed range so a previously saved interval becomes invalid, effective maintenance explicitly falls back to the default and Admin exposes the mismatch for correction; it never rewrites the saved value. Experience enablement, autonomous consolidation permission, and Thought cadence remain separate.

An enabled active instance without a previous evaluation timestamp is due on the next host poll. Subsequent evaluations use its persisted claim timestamp plus the effective interval; process restart does not reset that timestamp.

## Agent Instance home persistence

`AgentWorkspaceItems` is a separate SQLite table keyed by ItemId, with AgentInstanceId, portable unique uppercase PathKey, revision concurrency token, byte size, opaque BlobKey and logical metadata JSON. SourceSessionId in metadata is provenance only; no source-session foreign key or cascade owns home bytes. The InMemory profile uses the same bounded filesystem store with ephemeral metadata. SQLite provides restart durability.

`Persistence:WorkspaceRoot` defaults to `data/workspaces`; Compose uses `/data/workspaces` in the SQLite volume. Home bytes use `agent-<instanceN>/home/blobs/<opaqueBlobIdN>`, never model-supplied physical names. Linked root/owner/blob paths are denied. Immutable complete blobs precede metadata commit; failed writes/commits leave no reachable partial item. Replacement removes the superseded blob, and first access after reopen performs a bounded sweep of unreachable opaque/partial blobs. Back up the SQLite database and every persistence root together. After logical instance deletion and its durable receipt commit, cleanup removes the complete agent tree only when deletion preconditions have removed all referenced Sessions. Cleanup retries never purge an existing instance.

Workspace storage requires **one authoritative Agent Core writer per persistence root** (database and blob root). Owner gates serialize file CAS and whole-tree checks inside that process; they do not coordinate separate hosts. Sharing this root between writable replicas is unsupported. A future replicated deployment must move revision/tree admission into persistent serialization or an owner lease before enabling additional writers.

SQLite hard deletion commits the instance removal, workspace metadata removal and `InstanceDeleted` Admin receipt together before purging physical blobs. A failed logical commit therefore preserves the archived owner, metadata and exact bytes. Physical cleanup is idempotent: failure or a host exit after commit can leave inaccessible blobs, while the deletion receipt remains the durable cleanup marker. The host retries all committed deletion receipts at startup and every five minutes, skipping any existing owner. An exact internal deletion-command retry with the same operation id, instance id, expected revision and actor also retries cleanup without appending another receipt. Cleanup failure does not resurrect the deleted owner; successful deletion returns only after its synchronous purge succeeds. InMemory follows the same ordering and retries within the running host; its receipts/metadata are ephemeral.

Core limits are 50 MiB per file, 250 MiB per managed instance, and 4096 files. List pages cap at 256. Revision/hash replacement excludes previous bytes from projected usage. Archive preserves metadata/bytes; coordinated hard deletion removes both idempotently. Session deletion, Definition publication, persona updates and memory reset do not cascade into home. There is no remote store, shared tenant home or generalized filesystem.

## Workspace directory persistence

`AgentWorkspaceItems.MetadataJson` now stores the optional `Directory` flag (false for legacy files). A directory row has size zero, inode/directory content type, an empty-content SHA-256 and no blob key. Empty directories therefore survive reopen, source Session deletion and container recreation; they are not inferred solely from file paths. Retain and native structure materialize missing logical parents as needed. Legacy file rows remain readable without a schema migration or eager backfill.

Home move preserves file/directory ids, changes logical path and increments revisions. Home copy allocates new ids and immutable exact-byte blobs. Each structural operation commits its row changes as one SQLite transaction under existing instance/lifecycle coordination; a batch is a sequence of operations and is not one transaction. Failed commits clean newly written blobs; startup recovery still cleans unreachable blobs. The whole-tree token hashes bounded sorted metadata, never leaks blob names, and prevents concurrent/stale structural admission. Quotas count bytes/files separately from the 8192-entry bound; directory bytes are zero. Existing configured persistence roots and Compose mounts remain unchanged.
