# Technology Decisions

## Space ownership and Definition execution policy (2026-10-25)

The approved breaking successor reserves Environment for Space-owned typed variable profiles. Rename the existing Definition policy to `DefinitionExecutionPolicy` / `ExecutionPolicy` / serialized `executionPolicy`, retaining its existing behavior and version pins. No deserialization alias, dual-write or automatic data reset is added. Space/Membership persistence extends the existing IAM transaction owner; it adds no service process or authorization engine. Full cutover status and qualification belong to the [implementation evidence](reports/space-environment-implementation-2026-10-25.md). Historical reports retain their original terminology and evidence.

## Language-independent Automation authorization (2026-10-16)

**Decision:** The agent interprets intent and constructs complete structured Automation proposals. An independent semantic verifier checks each exact action against the current user request. Core validates trusted current-user provenance, ownership, revisions, permissions and structural schedule validity without natural-language patterns. Retire heuristic command authorization, timed-intent/continuation/interval/activation matching and text-confirmation bypasses. Historical referents/drafts remain Evidence only.

Verifier failure retains the exact action for owner UI approval bound to its fingerprint and response through the existing durable Run workflow. Deny/Ambiguous require clarification. Revalidation after approval preserves current policy and revision fencing. Semantic verification and approval authorize only the proposed mutation, not future effects. No Automation service, scheduler, store or approval subsystem is replaced. Synthetic tests inject typed decisions rather than production language rules. [Architecture](03-system-architecture.md#automation-and-event-admission) and [verification](reports/semantic-automation-authorization-verification-2026-10-16.md).

The Activation/AgentRun cutover supersedes earlier execution-owner and recovery decisions below. Historical P5–P9 freeze decisions remain evidence of their original milestones; current execution uses one SessionRuntime/SessionHost path and strict fresh/current-schema startup.

## Decision: unified Activation, AgentRun and background Sessions

Adopt one durable execution model: each admitted agent turn has an immutable Activation and exactly one AgentRun. Multiple accepted user entries may form one Activation; message acknowledgement does not imply a separate model invocation. Attempts retry the same run, while a later conversational turn gets a new run. Preserve stable response identity, conservative voice/history projections, exact-action approval, frozen model/Definition/persona/Skill state, current capability authorization and non-replayable effect fencing.

Asynchronous work creates another normal same-instance Session, with its own scratch, Session-owned artifacts and immutable origin. Automations create one child per accepted occurrence. Origin and UI visibility are separate from Session purpose/lifecycle. The initial run may complete quietly without fabricated chat prose; Continue in chat preserves the Session identity. Report-back is a trusted completion-only admission for the eligible initial child run, not a context-reference API or an agent delegation system.

The final migration removes both old execution schemas and their production stores, runners, hosted services, APIs and UI contracts. No compatibility reader, adapter, fallback, dual writing, old route alias or historical execution conversion is supported. Disposable execution data requires an explicit operator reset; permanent home, credentials and unrelated data must not be deleted at startup. Immutable historical migrations and freeze reports remain factual records.

The current worktree has adopted AgentRun dispatch in SessionRuntime/SessionHost, real background Session intake, immediate admission, completion receipts and Session-first APIs/UI. Approved destructive schema retirement and native live occurrence recovery are complete. Final local backend/browser and hosted acceptance pass on behavior `8cec78c5d47a43e0236a5c38f2e312f4e36ce283`; all five hosted jobs are [green](https://github.com/trannamtrung1st/agent-core/actions/runs/37756244306). The [implementation plan](18-implementation-plan.md#activation-agentrun-and-background-sessions-cutover) owns acceptance requirements; the [verification report](reports/activation-agent-run-background-sessions-verification.md) distinguishes targeted evidence from final acceptance.

Physical Infrastructure paths use one `Persistence:WorkspaceRoot`: `tenant-<tenantN>/spaces/space-<spaceN>/home/` contains ordinary Space-owned durable files and `agent-<instanceN>/sessions/session-<sessionN>/working/` contains ordinary Session-private files. The model sees `/home`, its own Private Space through `/private`, and `/working`. Both home aliases share the actual Space scope and quota. The canonical layout marker is `canonical-filesystem-2`; older roots are preserved and require the explicit operator cutover procedure. Artifacts, attachments, credentials and definition resources retain separate unmounted roots. The sibling `.provisioned` marker guards one-time template seeding outside the mounted working directory.

## Decision: scalable capability authorization and contextual projection

The accepted capability enhancement removes the arbitrary 32-grant validation limit without replacing it. Selected and All publication modes persist exact names and an authorization fingerprint. All is current-catalog authoring intent, never wildcard runtime permission. The new projection policy chooses an authorized always set; remaining loadability derives from registry discoverability and current eligibility. Optional missing configuration may coexist with new-mode authority; runtime still denies execution. Legacy publication configuration rules remain compatible.

Use deterministic lexical discovery through one `capabilities.load` bootstrap. Project active Skill requirements and execution-local loaded IDs; preserve current execution policy and approvals. Context-only tools cannot be forced into an always set (the loader may dedupe there). No embeddings, LLM routing, keyword capability routing, parallel permission list or model-tool ceiling. Measure normalized schema bytes before considering any future provider/model projection budget. Historical workspace decisions and freezes stay historical; dynamic projection is now this separate authorized enhancement.


## Historical decision: canonical filesystem and direct sandbox mounts (2026-10-15)

The 2026-10-25 Space decision supersedes this decision’s Instance-home ownership and layout version. Its ordinary-file and direct-mount mechanisms remain, with tenant/Space ownership and current Workspace IAM restrictions. The original decision and dated evidence below describe their historical scope.

The owner-authorized breaking cutover supersedes the SQLite home metadata/blob,
file revision/hash/tree-CAS and scratch synchronization decisions below. One
`Persistence:WorkspaceRoot` contains ordinary Instance-owned `agent-<id>/home`
and Session-owned `agent-<id>/sessions/session-<id>/working` directories. Filesystem
names, bytes, links, modes and timestamps are authoritative. There is no file index,
copy-back stage, rollback, compatibility reader or automatic conversion. Startup
must refuse incompatible installed data and require an explicit backed-up reset.

Workspace tools and sandbox commands operate on these exact directories. Trusted
ownership determines physical roots; descriptor-relative operations enforce
containment. `/home` mounts RW, RO or is absent according to the most restrictive
admitted/current policy; `/working` mounts RW for its Session alone. Generation
reuse requires matching root identity and home access. Restriction enforcement
requires confirmed retirement of previously broader generations. Separate bind
mounts and verified Unix identity must prevent cross-root hard-link aliases.

Mailbox-owned logical cwd applies to workspace tools and each sandbox dispatch,
starting at `/home` or `/working` with disabled home. Explicit command cwd affects
only that command. Files use ordinary concurrent-writer semantics; atomic file
replacement is allowed, ordered batches can partially succeed, and failed/canceled
commands may leave writes behind. Run/configuration/profile/lease revisions and
effect deduplication remain mandatory. Artifacts remain immutable and separate.

Mounted storage limits must identify verified kernel enforcement versus sampled
observations or unknown usage. Unsupported topology, Unix identity or mount access
fails visibly as `MountIncompatible`; never use hidden staging or world-writable
permissions. Native and packaged Core must share proven daemon-visible backing
storage. This release supports local/offline Git only: sandbox generations use deny-all egress with no approved remote-Git opt-in. Remote clone/fetch/push requires a separately specified and verified narrow host/Profile authorization; it is outside this release. Existing Core web/Drive download capabilities retain their independent authority.

Implementation and scoped local acceptance completed on 2026-10-16; the
[verification ledger](reports/canonical-workspace-cutover-verification-2026-10-16.md) records exact source, native/packaged/runtime evidence and deployment limits.
Earlier decision text below is historical context for the superseded implementation; it is not authority for retaining compatibility.

## Superseded decision: one Agent Instance and workspace model

Every Session requires a real active Agent Instance at creation and pins its active Definition/persona. Every instance owns durable `/home`; every Session owns temporary `/working`. The runtime catalog contains supported built-ins, including capability-aware General Assistant v18 with `background.start` and deliberately retained published versions for pinned owners. Unsupported historical versions remain in Git history. A fresh Chat with no instances directs the owner to Admin. No identity backfill, Definition-based Session creation, version fallback or workspace mode discriminator exists.

Choose transient mailbox-owned Session cwd with `/home` initialization and deterministic reset on runtime reconstruction. Reject moving/deleting cwd or ancestors. Resolve relative model paths from cwd within an authorized logical root; `/agent` and `/attachments` are read-only. HTTP execution-view paths default to `/home`, independent of runtime cwd. Sandbox remains `/working`-oriented through exact replica transfer, with no canonical host bind; `/home` remains Core-managed.

Keep SQLite metadata and opaque immutable blobs for home, with scratch nested beneath its owner. Stable item IDs, revision/hash CAS, whole-tree tokens, exact bytes, trusted source-Session provenance, portability validation, quotas and symlink denial remain authoritative. Ordinary home write/patch requires current revision/hash when replacing a file. `workspace.copy` transfers exact files or complete trees, including empty directories, within or across `/home` and `/working`; it never merges and only guarded durable-file replacement can overwrite. Same-root home structural copy requires the current tree token. Cross-root move and cross-store batches are excluded.

Canonical registry descriptors govern both direct offers and capability discovery. Move/rename always handles files and complete subtrees within a single writable root, requires a current home tree token, rejects collisions/cycles and protects cwd/ancestors. `workspace.list` reports active authorized home entries writable; archived home and read-only overlays report false. Historical narrow move and retain/checkout contracts are removed from runtime.

The shared tree planner preflights portable conflicts, parent creation, recursive intent and cumulative quotas before mutation. Delete and all batches use exact approval. Batches contain at most 16 concrete operations and 16 KiB, stay in one scope, and report partial outcomes on an unexpected execution failure rather than promising atomic rollback. Traversal stays bounded at 8192 entries/eight levels. Copy/move/delete/batch remain non-replayable; idempotent mkdir may replay. Scratch approval binds paths and recursive intent; scratch content fingerprints remain deferred.

Archive preserves inspection and blocks mutation. Instance deletion commits logical removal, home metadata removal and `InstanceDeleted` before physical purge. Startup/five-minute recovery retries receipt-backed leftovers and never purges an existing owner. Retained execution references still block hard deletion. One authoritative writer owns each database/blob persistence root. Artifacts remain immutable Session deliverables.

Legacy databases with compatibility owners or missing Session ownership fail clearly with **Legacy data reset required**. Operators explicitly back up/reset local data using [Operations](17-observability-and-operations.md#unified-workspace-reset-and-isolated-verification); startup never silently converts or deletes it. This migration supersedes the historical [workspace refinement](reports/agent-workspace-refinement-verification.md) and [bounded workspace](reports/agent-instance-workspace-final-verification.md) runtime contracts without moving earlier phase freeze SHAs or opening P10/P11.

These decisions are the implementation baseline. Resolve package patches at implementation time to the latest supported compatible releases; do not scatter transient patch pins across these docs. .NET 10 is LTS and pairs with C# 14. See the [official support policy](https://dotnet.microsoft.com/en-us/platform/support/policy/dotnet-core) and [.NET 10 download/version information](https://dotnet.microsoft.com/en-us/download/dotnet/10.0).

| Area | Decision | Rationale / trade-off | Future migration path |
| --- | --- | --- | --- |
| Backend | .NET 10 LTS, C# 14, ASP.NET Core 10 Minimal APIs | One supported runtime, simple HTTP surface | Upgrade supported runtime as one unit |
| Structure | Modular monolith, Domain/Application/Contracts/Infrastructure/Api | Explicit boundaries without distributed operations | Services only for measured scaling needs |
| Concurrency | System.Threading.Channels, one async mailbox reader per session | Deterministic state ownership; bounded queue design required | Scale independent sessions, preserve ownership |
| Live transport | ASP.NET Core SignalR + MessagePack, WebSockets | Small bidirectional binary surface; application handles PCM buffering | WebRTC adapter if media constraints justify it |
| JSON | System.Text.Json | Built into platform; explicit versioned records | Schema-version migration |
| Persistence | EF Core 10 + SQLite | Single-app operation, transactional history; one-writer constraints | PostgreSQL EF provider with tested conversions/migrations |
| Providers | Narrow application ports, first LLM direct OpenAI-compatible HttpClient/SSE | Vendor neutrality; own parser/error handling | More adapters, no runtime vendor objects |
| HTTP lifetime/resilience | IHttpClientFactory + Microsoft.Extensions.Http.Resilience | Managed clients and explicit failure budgets | Tune measured policy; no blind stream replay |
| Time/IDs | TimeProvider, injectable IIdGenerator; UUIDv7 event/response IDs, random UUIDv4 session IDs | Deterministic tests; session IDs retain random bearer entropy | Keep wire UUID strings stable |
| Speech | Independent STT/TTS, canonical PCM16 mono 24 kHz | Canonical composed text-first pipeline; format conversion at adapters | Future-only INativeRealtimeProvider optimization |
| Definitions | Versioned JSON files via store | No YAML dependency; immutable identities | Alternative store preserving schema/version semantics |
| Frontend | React SPA, Vite, pnpm, strict TypeScript, Ant Design v6, minimal app-specific CSS | Simple personal chat; client-only audio; generic controls come from AntD rather than a custom visual system | Expand UI only for product needs; do not add a second UI kit |
| Client state/API | Zustand; fetch wrapper; @microsoft/signalr + @microsoft/signalr-protocol-msgpack | Small active state store, few HTTP endpoints | Add data caching only with evidence |
| Observability | Microsoft.Extensions.Logging, OpenTelemetry via ActivitySource/Meter | Correlate conversational latency with privacy defaults | Optional OTLP export |
| Tests | xUnit, WebApplicationFactory; Vitest, React Testing Library, Playwright | Offline deterministic behavior and boundary tests | Explicit opt-in live-provider smokes; skip when keys are missing |
| Follow-on history | Evolve `IMemoryStore`; durable `LastEntrySequence`; newest/`before`/`after` pages | Open long sessions without a full transcript | Implement in follow-on P1A ([decision](#decision-bounded-history-and-durable-lastentrysequence)); **observed** for P1A restore, paging, and Load earlier messages |
| Follow-on lifecycle | Additive `lifecycleStatus`; one `TransitionLifecycle` | Semantic outcomes stay distinct from protocol-v1 `status` | Implement in follow-on P1B ([decision](#decision-additive-semantic-lifecycle-beside-protocol-v1-status)); **observed** |
| Follow-on speech locale | Effective locale: session override > agent default > fallback | Speech locale stays independent of text language and of SessionRuntime vendor branches | Implement in follow-on P1C ([decision](#decision-provider-neutral-effective-speech-locale)); **observed** including real Chrome `fr-FR` STT/TTS smoke |
| P2D session model | Trusted catalog, system default, persisted per-session resolved choice, session-aware resolver, reasoning effort | Existing sessions stay pinned when the operator default changes | Implement in P2D ([decision](#decision-session-model-selection-and-inference-controls)); **observed** |

MessagePack is case-sensitive; use explicit camelCase string keys and binary DTOs tested with the JavaScript client, as described in [Microsoft's SignalR MessagePack documentation](https://learn.microsoft.com/en-us/aspnet/core/signalr/messagepackhubprotocol?view=aspnetcore-10.0). `VITE_AGENTCORE_REALTIME_PROTOCOL=json` is a diagnostic transport on the same hub. Vite reads it at dev or build time from `web/` env, not repo-root `.env`. Restart Vite after changing it. It does not replace MessagePack as the canonical default. Http resilience policies must explicitly account for unsafe methods; see [Microsoft HTTP resilience guidance](https://learn.microsoft.com/en-us/dotnet/core/resilience/http-resilience). Project-specific timeout/retry choices are specified in [Backend Implementation](12-backend-implementation-spec.md), not implied library defaults.

## Decision: composed voice pipeline

**Decision:** The primary and default MVP voice architecture is STT → Interaction Controller → text Agent Runtime / LLM → Speech Segmenter → TTS. It does not use speech-to-speech reasoning. Input capture remains active while output plays.

**Rationale:** Independent capabilities simplify testing, debugging and provider replacement; preserve Agent Core's control of interruption; support different models for different identities; permit deterministic synthetic providers and per-stage latency measurement; and support a future fully on-prem deployment.

**Trade-off:** Extra recognition, segmentation and synthesis stages can add latency compared with a native end-to-end speech model.

**Mitigation:** Stream STT and use partials when available, apply fast controller heuristics, stream model text into natural Speech Segments, stream TTS into AudioWorklet playback, optionally duck locally, and measure each stage separately.

**Future migration:** INativeRealtimeProvider may later offer an optional optimized path while preserving normalized events and response/session invariants. It is excluded from all MVP implementation milestones and must not complicate the composed path.

## Decision: OpenRouter first for hosted text reasoning

**Decision:** Tenant-owned protected provider connections and model offerings select among native OpenAI Responses, Anthropic Messages, Google Gemini Content, OpenRouter Chat and custom compatible endpoints. Shared enabled starter presets contain metadata only. A tenant explicitly chooses its execution default; no vendor or global model key is required for Real boot. Local compatible services use host-owned origin policy and an explicitly verified capability/transport declaration.

**Rationale:** Model identity includes the tenant, connection, offering and configuration/credential epochs. This keeps duplicate external model IDs on different connections unambiguous and preserves source/claim revocation. All adapters implement the existing Agent Core model contract; runtime/controller and effect ownership remain unchanged. Native Anthropic/Gemini use ordered trusted Core/Runtime sections and native structured semantic final output. Forced tool use is refused where unsupported.

**Trade-off:** Metadata listing does not certify tool, schema, hierarchy or vision support. Operators explicitly adopt known presets or verify manual declarations. Agent Core does not auto-failover between providers. Synthetic remains an independent offline profile. Optional live checks require explicit opt-in and never run merely because credentials exist.

## Decision: STT/TTS are replaceable providers

**Decision:** Hosted STT and TTS remain independently replaceable Infrastructure adapters. Observed selectable hosted speech is OpenAI TTS (`OpenAiSpeechSynthesizer`) and OpenAI-compatible batch STT (`OpenAICompatibleBatchSpeechRecognizer`, no streaming input and no interim partials). OpenAI realtime transcription (`OpenAiSpeechRecognizer`, recommended model `gpt-live-transcribe`) stays the planned streaming STT adapter and is **not selectable** while its live session remains a no-op. Optional vendor transcription parameters such as `delay` are adapter-local and are sent only when the selected API/model documents them; they are not Agent Core defaults. Changing STT, LLM or TTS must not require changes to Agent Runtime or Interaction Controller. Do not block development, default tests or CI on a real OpenAI key. Automated tests use Synthetic STT/TTS plus fake Browser transports. Real OpenAI STT/TTS integration tests and manual voice verification wait for explicit opt-in plus `OPENAI_API_KEY`. Missing OpenAI credentials must not fail normal build/test.

**Rationale:** Support future on-prem deployment, deterministic testing, cost/quality experimentation, no vendor lock-in and independent evolution of speech and reasoning. Speech credentials arrive later than the text adapter without changing architecture.

**Consequence:** Application depends on capability interfaces; configuration and DI select Infrastructure adapters. Capabilities may differ, so existing degraded interaction policies remain supported. Vendor-specific code stays at the edge. [Provider ports](04-backend-interfaces.md#speech-provider-replacement-rule) and [configuration](15-persistence-and-configuration.md#provider-selection-and-di) own the concrete contracts and examples.

## Decision: speech provider is not speech transport

**Decision:** Independently configured STT and TTS adapters are Infrastructure providers. The session's input and output **transports** are provider-neutral: `serverAudio` (backend port + PCM), `clientTranscript` (browser STT, no `ISpeechRecognizer`), and `clientSpeech` (browser TTS, no `ISpeechSynthesizer`). `SpeechFactory` produces an `EffectiveSpeechPlan` and optional backend ports. Browser is a client transport, not a fake backend adapter; the plan still carries explicit effective client capabilities so Interaction Controller interruption timing is not degraded to no-partials. A selected non-Synthetic adapter must not be silently replaced by Synthetic. The deferred OpenAI realtime recognizer is not selectable while its live session remains a no-op; `OpenAICompatibleBatch` is the supported hosted STT adapter and must advertise no interim partials.

**Rationale:** Session Runtime and Interaction Controller stay replaceable-provider-agnostic. Mixing Browser STT with a future hosted TTS (or the reverse) is a transport pair, not a vendor mash-up inside the mailbox.

**Consequence:** [Architecture](03-system-architecture.md) owns the ownership split; [Interfaces](04-backend-interfaces.md#speech-provider-replacement-rule) owns ports versus Browser; [Voice](06-realtime-voice.md) owns how PCM versus client transcripts enter the pipeline; [Configuration](15-persistence-and-configuration.md#provider-selection-and-di) owns adapter binding.

## Decision: default verification is offline; live providers are explicit opt-in

**Decision:** Default verification is fully offline and deterministic (Synthetic adapters plus HTTP/SSE fixtures). External-provider smoke tests run only when the operator explicitly opts in. Presence of provider credentials or `OPENAI_API_KEY` in the environment must not cause `dotnet test` or other default suites to call hosted APIs. Opt-in smokes skip cleanly when the required key is missing. Normal development/test loops must not silently spend API credits. Deferred live-provider testing does not weaken or skip synthetic contract tests. Do not introduce additional external services solely for testing.

**Rationale:** Keep milestone gates reproducible without secrets or network, while still allowing a bounded live check of the real adapters when keys exist.

**Trade-off:** Hosted speech quality and free-router phrasing remain unverified until an explicit smoke or manual pass.

**Consequence:** [Testing Strategy](16-testing-strategy.md) owns fixtures, skip/opt-in mechanics and suite gates. [Implementation Plan](18-implementation-plan.md) applies this policy to existing milestones rather than adding test-only architecture.

## Decision: local git CLI now; GitHub Actions is intended CI

**Decision:** Day-to-day implementation uses the local `git` CLI. Repository CI is **GitHub Actions**, optional and manual-only via `workflow_dispatch`. Dispatch, rerun or wait for CI only on explicit user request; routine tasks use focused local verification. Optional unrun CI does not block task completion. This supersedes earlier automatic/mandatory hosted CI expectations for current work; historical acceptance evidence remains unchanged. Do not create full application workflow files until the corresponding .NET/`web` projects and test scripts exist. When introduced, core CI must remain offline: `dotnet` build/test, frontend install/build/unit tests, then synthetic Playwright after those milestones, never requiring OpenAI/OpenRouter keys, internet inference, microphone, speaker or GPU. Real-provider smokes stay opt-in.

**Rationale:** Keep early work unblocked, then make the promised offline gates reproducible on the repository host.

**Consequence:** [Implementation Plan](18-implementation-plan.md) and [Testing Strategy](16-testing-strategy.md) own when workflows appear. Milestone 5 adds `.github/workflows/synthetic.yml` for key-free backend, frontend, and synthetic Playwright gates.

## Decision: Docker Compose for reproducible local integration

**Decision:** Docker is optional for development; Docker Compose is supported for reproducible integration/demo. The fast loop is native .NET + Vite. The production-like MVP artifact is one application container serving API, SignalR and built SPA, with a persistent SQLite volume.

**Rationale:** Provide a one-command demo environment, environment parity and a simple path to adding local inference services later.

**Trade-off:** Container builds/restarts are slower than the native hot-reload loop, so Compose does not block early runtime/frontend work.

**Consequence:** Add the first usable key-free Compose environment at Milestone 5 (after the synthetic browser text path). Milestone 12 hardens containers, SQLite volume/restart, real-provider configuration, hybrid topology and operations. Default `docker compose up` remains Synthetic. Real/hosted Compose is `docker-compose.real.yml` overlaid on that file; secrets stay in the host environment. Hosted, hybrid and on-prem topologies keep the same runtime semantics. Docker is deployment tooling, not an Agent Core dependency. [Operations](17-observability-and-operations.md#docker-compose-integration-and-demo) owns topology and workflow details.

## Decision: Ant Design v6 as MVP generic UI system

**Decision:** The MVP generic UI system is **Ant Design v6** (`antd`), consumed directly by product components, with ConfigProvider and Ant Design `App` at the application root and near-default theming. App-specific CSS is limited to sizing, overflow, product layout/content, preview sizing, and necessary accessibility fixes. Do not add AppButton/AppInput/AppSelect or other generic wrappers, Ant Design Pro/ProComponents/X, another component or CSS framework, or a replacement custom design system.

**Status:** **Verified.** The SPA presents identity, session navigation, transcript, and composer with Ant Design v6. The retired custom visual system is not an active fallback. Session, realtime, voice, attachment, and catalog contracts remain with their existing owners. Evidence: [antd-migration-handoff](reports/antd-migration-handoff.md).

**Rationale:** A custom Pixel Dialogue Field visual system was unnecessary cost for validating Agent Core product behavior. Ant Design is an explicit UI-system change only.

**Consequence:** [Frontend Implementation](13-frontend-implementation-spec.md) owns screens and behavior. `.agents/context/DESIGN.md` may hold only lightweight visual guidance; if it conflicts with `/docs`, `/docs` wins.

## Decision: history-derived user-text queue

**Decision:** Wire clients express queued user text with additive `user.text.behavior` of `queue` or `interrupt`. Omitted `behavior` remains `interrupt` for protocol-v1 clients. Unknown values are rejected. Accepted user entries (and their attachment binds) persist on the existing ordered conversation history before success ACK. The pending batch is that history's trailing user suffix; P0 does not add a second durable queue store, `PendingUserEntryIds`, or `deliveryPolicy`. `queue` while a response is active keeps that response alive. `interrupt` supersedes the active response after the new user entry is persisted so already accepted queued entries stay earlier in sequence. The shipped first-party web app uses a client-side pending-send FIFO while a response is live ([Frontend](13-frontend-implementation-spec.md)); it does not send `behavior=queue` for that UX. Explicit stop is `agent.response.cancel` / `CancelResponse` with the top-level expected `responseId`; it creates no user entry and must not cancel a newer response.

**Status:** **Verified** in Application fake-time/gated-model tests, hub admission tests, and loopback Kestrel JavaScript SignalR/MessagePack scenarios.

**Rationale:** A parallel queue table would have to stay reconciled with history. Ordered entries already express arrival, attachments, and recovery without command replay.

**Consequence:** [Interaction Controller](05-interaction-controller.md) and [Protocol](14-api-and-realtime-protocol.md) own the live contracts. After a response is durably terminal (complete, fail, or explicit cancel), the runtime starts at most one next user-turn for the current trailing user suffix. Restart/attach uses the same helper on durable history. A second store remains forbidden unless a later proven invariant is documented first.

## Decision: bounded history and durable LastEntrySequence

**Decision:** Evolve `IMemoryStore` rather than adding a repository framework. Session metadata, one bounded runtime restore window, and one public history page must be loadable without materializing the full transcript. `LastEntrySequence` is a first-class durable snapshot coordinate, independent of the in-memory `Entries` window. Bounded saves retain older `ConversationEntries` rows and must not renumber them. Runtime restore includes required prompt context, interrupted response/delivery coordinates, and the complete trailing unresolved user suffix. The existing messages HTTP endpoint is extended for limit-only newest, `before` older, and existing `after` forward pages; `before`+`after` is rejected; newest/backward pages take `limit+1` internally, return items ascending, and expose `hasOlder` plus the next older cursor. One public history projection applies to active, paused, and terminal sessions. The first-party UI opens on the newest page and uses an explicit Load earlier messages control with merge/dedup by stable identity/sequence and scroll-anchor preservation on prepend.

**Status:** **Observed** for bounded `IMemoryStore` restore, HTTP newest/`before`/`after` paging, first-party Load earlier messages, Application `LifecycleTransition` with additive public `lifecycleStatus`, RequestComplete evaluation, minimal terminal UI, Application effective speech locale (override > agent default > fallback) on readiness/capability data, Browser/hosted adapter locale wiring, and Speech locale UI. P1-Final deterministic Synthetic/Compose gates and the unedited Chrome 153 Voice checklist re-run (`p1-final-chrome-probe-run.log`) are recorded on HEAD `df0a12cecb5b60a12499488eed9c101cb01b45b2`. The unedited `fr-FR` Browser STT/TTS smoke (`p1-final-fr-smoke-r2.json` / `p1-final-fr-smoke-r2.log`) is recorded on HEAD `5764010d989da965b252f5389e07669594c8bc29`. Optional hosted multilingual checks are unverified. **P1 freeze:** `dceaccbad9a4db8908af147b5353805a2b1af288` (`dceaccb`, 2026-09-19) with CI/Synthetic + Compose green on that HEAD. Historical repair `15930985f54e2e6bf4019dd0d8040796883021c7` remains earlier evidence. P2A/P2B have not started.

**Rationale:** Opening a long session must not transfer or materialize the entire transcript. Deriving the cursor from `Entries[^1]` is wrong once `Entries` is a window.

**Consequence:** [Interfaces](04-backend-interfaces.md) owns store operations; [Persistence](15-persistence-and-configuration.md) owns durable cursor and row retention; [Protocol](14-api-and-realtime-protocol.md) owns paging query shape; [Frontend](13-frontend-implementation-spec.md) owns lazy history UX; [Testing](16-testing-strategy.md) and [Implementation Plan](18-implementation-plan.md#follow-on-p1-history-lifecycle-and-multilingual-speech) own gates.

## Decision: additive semantic lifecycle beside protocol-v1 status

**Decision:** Do not extend today's `created|attached|paused|ending|ended` enum as the semantic model. Introduce durable `lifecycleStatus` conceptually Active/Paused/Completed/Expired/Cancelled/Ended additively, while protocol-v1 `status` remains compatible during migration. Archive (`ArchivedAt`) stays orthogonal. Generic `SessionPurpose` is Ongoing|Goal with optional description (context, not an executable rule), optional absolute `deadlineAt` (resolve `maxDuration` once at create), and optional bounded opaque host metadata that is not public. Completion-authority policy covers agent disabled/advisory/allowed, user complete/cancel allowed/denied; host/system authority is always allowed. One Application `TransitionLifecycle` owns the graph, terminalization obligations, and idempotent same-outcome repeats. Attached deadlines use TimeProvider mailbox timers; detached admission checks expiry atomically with no durable scheduler. A separate configured completion evaluator may yield `continue` or `RequestComplete`; it is not `RequestDeactivate` and is not an inline response marker. Domain-specific completion rules stay in host integrations.

**Status:** **Observed** for purpose/policy persistence, `LifecycleTransition` graph/terminalization, attached TimeProvider deadlines, detached atomic expiry, the RequestComplete evaluator (continue / advisory intent / allowed-after-terminalization), and the first-party terminal read-only UI.

**Rationale:** Mixing transport attachment with semantic outcomes would break protocol-v1 clients and collapse Completed/Expired/Cancelled into one Stopped state.

**Consequence:** [Controller](05-interaction-controller.md) owns transition behavior; [Events](07-event-model.md) owns the corresponding lifecycle event; [Protocol](14-api-and-realtime-protocol.md) owns additive wire fields; [Persistence](15-persistence-and-configuration.md) owns migration including Ending recovery; [Frontend](13-frontend-implementation-spec.md) owns minimal terminal UI.

## Decision: provider-neutral effective speech locale

**Decision:** Effective speech locale precedence is session override > agent conversation-language default > provider/default fallback. Validate BCP-47-like tags at the Application boundary. Persist the session override without rewriting agent text-language settings. Expose the resolved locale on session readiness/capability data, not only the public agent descriptor. Evaluate language/voice support in speech abstractions/adapters; `SessionRuntime` has no Browser/OpenAI locale branches. Browser STT uses the effective tag. Browser TTS selects exact locale, then reasonable base language, then compatible configured/default voice, or fails Voice clearly. Hosted batch STT receives locale hints where supported; hosted TTS compatibility stays in adapters. Unsupported speech locale disables or fails Voice while text remains usable. Realtime OpenAI STT remains unselectable. No automatic language detection.

**Status:** **Observed** for Application validation/precedence, persisted session override without rewriting text language, public effective locale on session readiness/capability data, provider-neutral `ISpeechLocaleSupport` (SessionRuntime has no Browser/OpenAI locale branches), Browser STT/TTS locale selection, hosted batch STT language hints, hosted TTS locale/voice compatibility in adapters, a first-party Speech locale Select that does not hide text chat, and one real Chrome 153 `fr-FR` STT/TTS conversation (unedited `p1-final-fr-smoke-r2`: STT `Bonjour`; TTS Daniel French France) on HEAD `5764010`.

**Rationale:** Conversation language and speech locale must stay independent so missing voices never silently speak the wrong language or break text chat.

The effective speech locale also governs the model's spoken wording during Voice mode. A different display language uses the existing separate speech projection; changing speech locale does not rewrite the conversation-language policy. Selecting a voice alone cannot translate generated text.

**Consequence:** [Voice](06-realtime-voice.md) owns Browser/hosted speech behavior; [Interfaces](04-backend-interfaces.md) owns capability evaluation; [Protocol](14-api-and-realtime-protocol.md) owns readiness fields; [Frontend](13-frontend-implementation-spec.md) owns override/fallback presentation.

## Decision: session model selection and inference controls

**Decision:** Operator configuration owns a trusted, provider-neutral model catalog and the system default. A new session may choose Default or an allowed catalog key; Default resolves to a concrete model when the session is created. Persist `SessionModelSelection` (catalog key, trusted provider alias, concrete model ID, selection source, reasoning effort). Default is never stored as a future dynamic pointer. Changing the global default, or the model behind the same catalog key, must not silently rewrite existing sessions. Legacy sessions without a selection pin the configured default before the first post-upgrade generation. Precedence is explicit host/user selection, then optional agent default/constraint, then the system default. Keep this generic; do not hard-code exam/application model IDs into Agent Core. The tenant model-management successor retires that static catalog and its global default. DeepSeek V4.1 Flash 1017 (`deepseek/deepseek-v4.1-flash-1017`) is an adoptable OpenRouter preset; tenant-owned offerings and explicit defaults determine execution. [Configuration](15-persistence-and-configuration.md#real-model-catalog) owns current profiles, transport settings and the explicit snapshot migration. No automatic promotion or cost fallback is introduced.

Tenant LLM work resolves through `ILanguageModelResolver` using the actual admitted Run's captured binding and a server-owned `ModelInvocation`, for Conversation, Initiative, CompletionEvaluation and TriggerAuthorization. Each generation checks live Run claim/source authority and exact tenant connection/offering/profile/credential epochs. Reasoning effort is validated against the captured profile, never a global unchecked enum. The per-Run trigger classifier uses that route's least declared effort; action JSON supplies no authority. Session changes persist before activation and reject an active generation/tool/evaluation with `SessionBusy`. Record captured route and safe labels in generation provenance. Public selection carries offering identity and effort only; credentials, endpoint and arbitrary provider JSON remain server-owned.

**Status:** **Observed** as P2D after the key-free gate. P1 remains frozen on `dceaccb`. P2A is observed. P2B is **observed/frozen** on `e0e8a55`.

**Rationale:** Operators need a Codex-like default plus durable per-session control without letting browser input choose provider routing or silently retarget historical chats.

**Consequence:** [Interfaces](04-backend-interfaces.md) owns catalog/resolver ports; [Persistence](15-persistence-and-configuration.md) owns durable selection and provenance; [Protocol](14-api-and-realtime-protocol.md) owns catalog and session model HTTP; [Frontend](13-frontend-implementation-spec.md) owns Default/effort UX; [Implementation Plan](18-implementation-plan.md) owns the P2D gate.

## Decision: bounded application messaging and dynamic Skill load

**Decision:** On a tool-capable user turn, `app.message.send` and `skills.load` continue the existing model/tool pump inside one accepted execution. They are not Agent Step actions, not a second hub, and not a second generation started by `Continue`. The message destination is the bound session. The visible role is `applicationMessage`: stored, omitted from prompt history, and not spoken. Chat shows it before that response’s assistant answer. `skills.load` admits canonical keys from the execution's pinned effective catalog. Enabled Always procedures start active; OnDemand procedures load explicitly, in live turns and durable Work, with the shared procedure budget. Catalog and procedures are frozen before execution; no keyword matching or active-count ceiling remains. Loading never grants authority. An admitted application message is exactly-once inside one live or recovered durable snapshot, not a transactional delivery across a crash before that snapshot commits. Stale, cancelled, and superseded executions do not emit a late message.

**Rationale / trade-off:** Intermediate communication and later Skill discovery stay inside the frozen P8 execution fence. Keyword matching remains a preload, not the only admission path.

**Future migration path:** P9 consumes these contracts. It does not redefine destination binding or Skill authority. Cross-application messaging, a Skill marketplace, and embeddings stay out of this decision.

## Decision: first-class transient response progress

**Decision:** Live response progress is a first-class transient Application event (`ResponseProgressOutput`, wire `agent.progress`) owned by the outer envelope `responseId`. Nested tool or attachment work may carry a runtime-generated `operationId`. Kinds are `preparing`, `readingAttachments`, `runningTool`, `waitingExternal`, and `finalizing`; states are `started`, `updated`, `completed`, and `failed`. Progress is never durable history, `session.ready` replay, provider reasoning, conversational assistant text, response blocks, or TTS/`clientSpeech` input. Trusted bounded `message` values are status copy only. Coarse `session.state.changed.outputState` remains. The browser keeps one replaceable `activeProgress` status. Telemetry records `agent.progress.event` with `kind` and `state` tags only, plus optional kind-tagged `agent.progress.active_ms`.

**Status:** **Observed** as P2A after the key-free Synthetic plus Compose gate recorded on git HEAD `5effbb5e0942b2176c970c3a6f1b79fbaa985f8d` (`5effbb5`) with the P2A working tree (P2A-1..4 not a separate published commit). Domain 64; Infrastructure 133 passed / 12 skipped; Application 436 with `--blame-hang --blame-hang-timeout 5m`; API 155; web 376 unit tests and production build; `CI=1` Playwright 36 including progress-to-final, reload/history, disconnect/reconnect, and session-switch; `scripts/compose-sqlite-volume.sh` passed. Optional Real-provider probes were not run. P1 remains frozen on `dceaccb`. P2B is **observed/frozen** on `e0e8a55`.

**Rationale:** Operators and participants need a single understandable live status while attachments or tools run, without turning operational noise into history or speech.

**Consequence:** [Protocol](14-api-and-realtime-protocol.md) owns `agent.progress`; [Voice](06-realtime-voice.md) owns the TTS exclusion; [Operations](17-observability-and-operations.md) owns progress instruments; [Implementation Plan](18-implementation-plan.md) owns the P2A gate.

## Decision: validated provider-neutral assistant response

**Decision:** Conversational generation uses one validated semantic envelope (`displayText`, `speech.mode` `same`|`custom`|`none`, optional `blocks`). SessionRuntime always attaches `ModelResponseContract`. Native JSON is requested only when trusted catalog `StructuredOutput` is true; otherwise Infrastructure injects a bounded compatibility instruction and marker parser (including `[[speech:none]]`). Domain persists the envelope; provider JSON never crosses into Application. When Voice derives a playback coordinate that differs from display while `speech.mode` stays `same`, persistence stores that coordinate in `EnvelopeJson` `speech.text` without promoting it to public custom speech. Attachment references require a session-owned attachment id. Progress `finalizing` starts only at structured semantic validation (not during LLM streaming) and is never durable, reasoning, history, or TTS.

**Status:** **Observed** as P2B after the key-free Synthetic plus Compose gate on `e0e8a55b111b190b63dfe5a2a53d0c59d0a06a59` (`e0e8a55`, 2026-09-20; workflow run `35496178496`). Public/history `speechText` exposes custom semantic speech only. `agent.speech.projection` carries `mode` and may expose same-mode playback telemetry; clients must not promote same/none projections to public `speechText`. Do not reopen P2B without a reproducible regression. **P2E** multimodal image-input capability closure is **observed/frozen** (2026-09-21 gate on P2E freeze HEAD). **P2C** trusted personalization boundary is **observed/frozen** (2026-09-21 gate on P2C freeze HEAD). **P2 closed/frozen** on `47d6ff6` (2026-09-21) after mandatory whole-output review; closure repair and §23 gate on that HEAD (workflow run `35552740853`). Optional Real probes skipped/unverified. **P3B–P3F** (tools, public web, approval, email, workspace search, and bounded `http.request`) extends the reopened `27efe17` / `e255916` correction. Current `general-assistant` is v7. `workspace.search` and `workspace.move` use working-directory paths. `http.request` is an approval-gated public HTTP escape hatch with no model-supplied credentials; sandbox networking stays deferred and is not a P3 blocker. See the [P3 closure report](reports/p3-freeze-candidate.md). Public-web fetches validate every DNS answer used for connection and every redirect hop; private/local/metadata targets and IPv4-mapped equivalents are denied in Infrastructure before connect. Do not reopen P2 without a reproducible regression. **P3 key-free freeze** is on `4dbb920` (implementation bulk `da93489`; hosted Synthetic green on `3243d58`, workflow `35642864725`). Real GPT-4o mini historical-image reread is an **open Real-provider gap** (not P4). Roadmap focus: **P4**. P4A session compaction (valid summary boundary, semantic candidate, mailbox commit) is implemented and is not a P4 freeze. P4B session-scoped structured memory has a local checkpoint for five kinds, correction, deletion, profile precedence, session isolation, SQLite reopen, and prompt projection. P4C-0 durable agent instances are separate from reusable definitions. IdentityUser memory is a copied item owned by that instance and the trusted profile. User-scope memory is a copied profile-owned item retrieved only when that definition enables User retrieval. P6 is closed/frozen on `6900bc1d0f0331f8696fc59acdfe7be49d50ebf2` (workflow `35990145456` attempt 2). P7 is next and has not started.

**Rationale:** Display, speech, and blocks must share one validated contract so Voice, history, and UI do not depend on inline markers or provider JSON.

**Consequence:** [Interfaces](04-backend-interfaces.md) owns semantic model events; [Voice](06-realtime-voice.md) owns TTS selection from `speech.mode`; [Protocol](14-api-and-realtime-protocol.md) owns public `speechText` and speech labels; [Implementation Plan](18-implementation-plan.md) owns the P2B gate.

No native speech-to-speech/realtime model in MVP. No microservices, Kafka, RabbitMQ, Redis requirement, Kubernetes requirement, Orleans/Akka actor framework, MediatR merely for layer forwarding, generic workflow engine, multi-agent system, vector database, RAG platform, plugin marketplace, OAuth/login system for MVP, WebRTC in the first version, mobile app, native desktop app, elaborate avatar system, SSR, or Next.js. Ant Design v6 is the verified MVP generic UI system (see the decision above); do not add another component or CSS framework, Ant Design Pro/ProComponents/X, or a replacement custom design system. No autonomous tools/platform, distributed event bus or generic repository framework. No extra hosted mock or third-party test-only inference service. Reconsider only when actual requirements justify the cost.

## Decision: durable triggers stay outside the Session Runtime

The scheduler remains outside SessionRuntime and owns due Automation occurrences through `ITriggerStore`. Core validates owner, trigger policy and model. Creation requires current user authority and does not approve a later sensitive effect. Stable occurrence IDs support at-least-once routing. Authored Schedule/Event/manual occurrences enter durable intake, which atomically admits their explicit existing Session or new child target. The coordinator and SessionHost execute both through SessionRuntime. Native live occurrence reservation, quiet settlement and exact-target repair remain unchanged. Historical P5/P6 engines and freeze evidence stay historical.

## Post-MVP planned until verified

Historical MVP acceptance is unchanged. Phases A–H and the Phase I decision are **observed** in the decisions below (and in [Implementation Plan](18-implementation-plan.md#post-mvp-phases-planned-until-verified)). The heading is retained as a stable fragment. Do not invent a second numbered specification series. Product behavior is described here and in docs/01–18; the original proposal pack is not required to understand the system.

### Decision: trusted-local owner capability (R1)

**Decision:** One reusable **trusted-local owner capability** authorizes session catalog, lifecycle, hub attach, upload, bind, artifact access, workspace execution-view, and content retrieval. `SessionId` identifies the resource; it is not a credential. This is not tenant isolation, OAuth, or public multi-user hosting. Historical MVP still excludes those.

**Contract:**

- Obtain: `POST /api/v1/local/owner-capability` succeeds only for a trusted-local caller. Native processes require loopback `RemoteIpAddress`. Compose publishes `127.0.0.1:host:container` while Kestrel listens inside the container; Docker NAT may present that traffic as this container's IPv4 default gateway, so Compose sets `Hosting:TrustPublishedPortGateway=true` to trust **that one hop** when `DOTNET_RUNNING_IN_CONTAINER` is set (official runtime images). Gateway resolution is disabled outside a container even if the flag is true. Private ranges are not trusted. Response is an opaque token (not a SessionId) plus expiry metadata.
- Persist: hash the token in SQLite as a single local-owner grant so **API process restart** can still validate a restored token. The browser stores the token in `localStorage` (`agentcore.ownerCapability`) and restores it on reload; if missing, revoked, or hash-mismatch, re-obtain on loopback and fail closed.
- Present: HTTP header `X-AgentCore-Owner-Capability`. Hub `session.attach` requires the same token (capability field on the attach command, not the connection-lease `attachmentId`).
- Fail closed: unauthenticated → 401; wrong-owner, revoked, or cross-session → 403 or a uniform 404 that does **not** leak other sessions' existence. Deleted and archived sessions deny upload/bind/content without enumerating foreign ids. Safe errors omit other sessions' titles and ids.
- Tests: unauthenticated, wrong token, revoked, deleted, archived, cross-session, reload restore, API process restart restore.

Hub **connection lease** (`attachmentId` on SignalR commands) remains a per-connection protocol lease. It is not a user-uploaded **Attachment**. User-uploaded files use `AttachmentId` on HTTP upload/bind/content routes only.

### Decision: independent speech and display receipts (R2)

**Decision:** One parent `ResponseId` owns `reply.text`, optional `reply.speech`, Markdown, and attachment/artifact reference blocks. Speech-coordinate playback progress, display receipts, and block visibility/delivery persist atomically with response status.

**Contract:**

- Display receipts and speech-coordinate consumed samples are independent. Do not apply speech offsets to display text.
- Heard/voice context uses conservative speech receipts only.
- When `reply.speech` is absent, TTS synthesizes `reply.text` once. A later speech field on the same response must not duplicate TTS.
- Disconnect before render leaves display, speech, and block receipts undelivered; reconnect must not treat unseen content as shown.
- Late receipts/blocks after supersession or disconnect are rejected. Unknown blocks degrade to a safe fallback. Artifact-reference authorization in Phase C uses fixtures, not generated artifacts.

### Decision: bind and catalog mutations use runtime persistence order (R3)

**Decision:** Accepted user messages and attachment binds share the Session Runtime mailbox revision stream: stable command/entry identity, idempotent retries, all-or-nothing validation, and a race-safe pending-to-bound claim. Catalog rename/archive/unarchive/versioned-delete participate in the same revision ordering so a later snapshot cannot resurrect stale titles, archive flags, or deleted rows.

**Failure:** lost acknowledgement retries, duplicate command ids, failed commits, bind-versus-TTL, bind-versus-delete, concurrent quota reservation, and crash recovery must leave no duplicate entries and must not delete a just-bound blob.

### Decision: v1 terminal-end stays; durable delete is additive (R4)

**Decision:** `DELETE /api/v1/sessions/{id}` remains irreversible terminal-end. Existing Ended rows stay labeled Ended in the catalog; they are not archived, not reopenable, and not physically deleted by v1 DELETE. Read-only HTTP history (GET session / GET messages) remains available without attach. GET attachment list/metadata/content remains readable on that view; upload, stage, abort, and materialize stay rejected. Versioned durable deletion and runtime deactivation are additive routes. Archive preserves data and rejects activation/messages/uploads until unarchive. Deactivation is not archive and is not deletion. Migrations backfill workspace-ownership keyed by SessionId, preserve pins, revisions, and receipts, and keep Ended irreversible.

### Decision: finite silent initiative and at-cap deactivation (R5)

**Decision:** `MaxConsecutiveProactiveTurns` is read from the pinned Agent Definition (documented default only when omitted). Visible Speak increments the counter once; StaySilent consumes cooldown without increment; user activity resets to 0. Finite silent-evaluation/backoff and inactivity bounds stop unpaid and paid silent reasoning independently of the visible-speech cap. After a non-zero cap is reached, further Speak is denied and `RequestDeactivate` remains permitted. A zero-cap/passive role never Speaks; it does not auto-deactivate on the first silence, but silent-evaluation/inactivity bounds or explicit deactivate still apply. Environment events remain separately governable. No overlapping Speak. Stale timers/decisions/receipts are rejected. **Observed** in SessionRuntime + FakeTimeProvider tests and `POST /api/v2/sessions/{id}/deactivate`.

### Decision: A–H mandatory; Phase I conditional; concrete sandbox (R6)

**Decision:** Phases A–H are mandatory, including rename and archive/unarchive. Phase H is a concrete container sandbox behind the execution capability boundary (not unit fakes alone). **Historical Phase H, superseded by the [trusted-local OpenSandbox decision](#trusted-local-opensandbox-decision):** `sandbox.run` used `DockerSandboxExecutor` (`busybox:1.36`, network none, read-only root, dropped capabilities, 64 MiB / 0.5 CPU / 32 PIDs / 8 s, session working-dir bind only, kill/reap, bounded output, `IArtifactStore` export). Broad `process`/`shell` remain disabled and are not granted by H. **Phase I observed not-applicable:** implemented G/H workflows do not require typed persisted WorkItems. In-flight tools are off-mailbox and epoch-guarded (`ToolWorkflowRuntimeTests.Deactivation_rejects_stale_workspace_write`); sandbox containers are killed and reaped; shipped Support/Compliance allowlists omit `sandbox.run`. Session-owned attachments, workspace files, and artifacts already survive deactivation through Phases D and F. **Historical Phase I trigger:** add detached workflow recovery only if a later accepted Support, Compliance, or `sandbox.run` workflow must continue or resume after SessionRuntime deactivation without repeating the user turn. Until then do not leave untracked host processes as a substitute. P6 durable work for trigger occurrences is a separate observed capability and does not satisfy this trigger.

### Planned resource limits

Unless a later item records a tested change:

| Limit | Value |
| --- | --- |
| Attachments per message | 10 |
| Attachment size | 25 MiB each |
| Session attachment bytes | 250 MiB |
| Pending upload TTL | 1 hour |
| Decoded image pixels | 32 megapixels |
| Workspace writes | 250 MiB |
| Artifact size | 50 MiB each |
| Session artifact bytes | 250 MiB |
| Extraction output | 256 KiB |
| Parser timeout | 10 s |
| Parser memory | 256 MiB |
| Tool steps, standard | Default 24; configurable up to host ceiling 144 |
| Tool steps, interactive browser | Standard 48; Extended 96; Deep Workflow 144 |
| Tool steps, unattended bound browser | Default 32; independent class profile |
| Per-tool timeout | Default/host maximum 30 s; custom 1–30 s |
| Overall active execution deadline, standard | Default 180 s; host ceiling 900 s |
| Overall active execution deadline, interactive browser | Standard 300 s; Extended 600 s; Deep Workflow 900 s |
| Requested cleanup reserve, interactive browser | 60 s plus final 30 s inside total; 4–12 steps derived from profile; 8 KiB checkpoint headroom; authorized hover/scroll/wait/verify/key interactions included |
| Reply finalization reserve, interactive browser | Last 30 s inside admitted total; no new tools; 2,048 output tokens max |
| Overall active execution deadline, unattended bound browser | Default 240 s; independent class profile |
| Tool output | 8 MiB |
| Durable Run checkpoint | 256 KiB system ceiling after measured compaction |
| Sandbox memory | 1024 MiB default, 2048 MiB ceiling |
| Sandbox CPUs | 1 default, 2 ceiling |
| Sandbox PIDs | 64 (server enforced) |
| Sandbox command timeout | 120 s default, 180 s ceiling, profile/Run/deadline clamped |
| Sandbox foreground output | 32 KiB stdout + 32 KiB stderr |

P9.5 records a tested change to the tool-loop rows above. Real external-application browser workflows use a larger Core-owned execution budget than ordinary tool workflows. The original 24-step/180-second values remain the standard profile. A user turn whose authorized tools include a browser tool uses 48 steps and 300 seconds. A scheduled or application-event AgentRun that holds the bound application-browser lease uses 32 steps and 240 seconds. Core resolves that profile from trusted execution context before the loop. The model has no tool or argument that raises the budget. Per-tool timeout, tool output, `skills.load`, `app.message.send`, approval semantics, and browser origin policy stay unchanged. Infrastructure recovery retries still count toward the same step budget. The change exists because a real nopCommerce product-publish-and-verify journey exhausted the standard profile before its storefront postcondition could be established.

Owners: [Protocol](14-api-and-realtime-protocol.md) (capability, leases vs Attachment, additive routes); [Persistence](15-persistence-and-configuration.md) (revision/bind/cleanup); [Controller](05-interaction-controller.md) (initiative/receipts); [Implementation Plan](18-implementation-plan.md) (phase gates).

## P7B definition lifecycle (observed)

Current tenant successor: immutable file built-ins implement both `IBuiltInAgentDefinitionStore` and the generic `IAgentDefinitionStore` registration. `TenantDefinitionService` and `TenantRunConfigurationResolver` resolve owned publications through `ITenantDefinitionStore`; retained unowned Admin rows never enter the shared catalog. The composite runtime catalog and old owner-protected lifecycle described below are historical. Stored Session/Run snapshots remain immutable.

**Decision:** Repository `agents/*.json` built-ins remain immutable seeds. Durable definition drafts and published versions live in application-owned storage (`IAgentDefinitionAdminStore`) with optimistic draft revision, exact-revision publish, immutable publication payload, and metadata-only deprecation. Runtime `IAgentDefinitionStore` is a `CompositeAgentDefinitionStore` over file built-ins and durable publications so Session Runtime keeps a single lookup port; exact lookup returns deprecated versions, default lookup skips them. Admin mutates lifecycle only through owner-protected HTTP; stored session definition/persona snapshots are not rewritten when later versions publish or deprecate. Transitional W02 publish runs available validation before immutable publish; eval-gated publish and human-readable diff are P7F. **Observed** on W02 slice gate `4a2bf99` (review 0020). See [Architecture](03-system-architecture.md#definitions-and-effective-configuration), [Interfaces](04-backend-interfaces.md#p7b-definition-lifecycle-observed), [Persistence](15-persistence-and-configuration.md#p7b-definition-lifecycle-store-observed), and [P7B report](reports/p7b-definition-lifecycle.md).

## P7C harness resources (observed)

**Decision:** Admin-managed harness content is versioned definition resources in application-owned storage, not repository `agents/*` or `.agents/*` mutation. Draft bindings are mutable with optimistic draft revision; publication bindings and content hashes are immutable. Runtime exposes resources through the existing read-only `/agent` projection; session `/workspace` remains the only normal mutable file area with no write-back. Managed chat for P7C evidence uses explicit Agent Instances and v2 `agentInstanceId` session create. **Observed** on W03 slice gate `e25cd46` (see [P7C report](reports/p7c-harness-resources-workspace.md)).

## Native Playwright browser contract

**Decision:** retain Application-owned `IBrowser` and replace indexed browser automation with a thin native .NET adapter. One typed ordinary execution path maps semantic queries to GetByRole/Text/Label/Placeholder/AltText/Title/TestId and scoped/frame Locators. Playwright owns matching, actionability, auto-waiting, rerender tolerance and browser events. Core owns authorization, origin/resource policy, owner/profile leases, protected sinks, cancellation fencing, artifacts and output budgets. No MCP server, Node worker, raw model selector/script language, ARIA parser, full-page discovery index or compatibility engine is introduced.

The complete semantic-action cutover on `develop/branch-1a` supersedes the previous private Locator-ref protocol. Ordinary actions take a direct native target; optional focused find returns semantic details without authority. Unique native scope and target checks reject duplicates. Production at pinned Microsoft.Playwright 1.63 uses ordinary ARIA snapshots because the installed public SDK cannot resolve AI refs. No private API or custom ref store substitutes for that missing API.

General Assistant v21 and Secretary v8 publish the current protocol. Prior built-in browser versions are removed from the executable catalog; their immutable bytes remain in Git and historical owner records. Known retired instruction vocabulary is rejected during publication, model-context construction and direct browser execution. Checkpoints stamp browser contract version 1; historical browser checkpoints without that version cannot resume, while their receipts remain readable. Owners adopt current instructions and create new Sessions intentionally; no credentials, profiles, workspace or historical receipts are erased. Security/lifecycle, privileged operations, supported feature exclusions and the existing 300-second budget with cleanup/finalization reserves remain unchanged. See [port](04-backend-interfaces.md#native-browser-contract), [data cutover](15-persistence-and-configuration.md#browser-semantic-contract-cutover) and [verification](reports/browser-semantic-redesign-verification.md). Earlier native-wrapper/Browser v2 reports are historical evidence, not acceptance of this cutover.

## P9.5 connection and attention (closed)

**Historical decision, superseded:** nopCommerce proved persistent browser reuse in P9.5. The P9.5 nopCommerce Application Connection was a bounded proving slice and is retired by System Credentials. Historical P9.5/P9.6 freeze evidence remains unchanged; current behavior uses explicit credential grants, generic capabilities, and Agent-Instance-owned browser profiles. Attention remains one durable result flag/alert, and the default Compose image still does not package Chromium. See [P9.5 evidence](reports/p9.5-freeze-candidate.md).

## System credential authority

**Decision:** replace the bounded nopCommerce connection domain with reusable System Credentials and explicit Agent Credential Bindings. Use predefined Password/ApiKey/Token/Certificate/PrivateKey/Generic security kinds, dynamic non-secret metadata and independent origin policy. Bindings are mutable instance resource grants; they do not change Definition authorization, contextual capability projection or capability fingerprints. The first secure sink is interactive Browser password fill; other kinds are stored but have no secure sink yet. Use the existing .NET Data Protection local provider behind `ICredentialProtector`, persisting its key ring with the database. No external vault dependency is needed locally. P11C reserves a replacement provider when deployment requires a vault/KMS/HSM. Generic browser state remains independent of grants. No nopCommerce Core service or browser authentication state machine is retained.

**Deferred audit history:** system Credential create/update/disable/replace/delete and Agent bind/unbind currently enforce owner authorization and revision checks without appending Admin history receipts. This is an explicit follow-up gap, not an audited-operation claim. Future receipts must contain safe operation/resource/revision metadata only; protected payloads, raw values and encoded variants must never enter history.

Future Application Binding means a client application embedding/invoking an agent, with delegated context and permissions. Operating a website through generic Browser is not such a binding.

The P9.5 nopCommerce Application Connection was a bounded proving slice and is retired by System Credentials. Historical P9.5/P9.6 freeze evidence remains unchanged; current behavior uses explicit credential grants, generic capabilities, and Agent-Instance-owned browser profiles.

## Post-P9.5 bounded browser settle

**Decision:** Browser observations may use a bounded settle, and the agent can request a settled re-observation through `browser.wait_for` `condition` `stable`. `stable` is a bounded observational condition, not page completion. The provider owns the wait and the truthful current observation. The agent owns whether the evidence is sufficient. The model does not receive a sleep tool, selectors, or Playwright locators. This enhancement does not reopen P9 or P9.5 and does not move `bba1de4` or `1012653`. P10 and P11 remain requirement-triggered.

## Post-P9.5 browser and store-review enhancement (recorded)

**Decision:** The post-P9.5 browser and store-review follow-up is recorded on `6f6ff42`. Hosted Synthetic [`37128161642`](https://github.com/trannamtrung1st/agent-core/actions/runs/37128161642) is green. It covers bounded browser settle, observation-repeat termination, compacted page receipts, daily-review activation, and store-review evidence that trusts a filter only when the observed rows support it. P9.5 stays closed on `1012653`. This record does not move `bba1de4` or `1012653`.

## P9.6 execution model, browser v1, and order.placed (observed)

**Decision:** Model intent is pinned before live-versus-durable routing. Precedence is the trigger override, then the Agent Instance unattended default, then the conversation or catalog default. The pin is immutable after admission. A user Chat model selection is not an unattended input. Browser Capability v1 adds history navigation, richer actions, page lifecycle, bounded waits, and an explicit viewport capture. At that historical closure, capture was offered only to a vision-capable pin; Browser v2 also supports text-only Artifact delivery while coordinate actions remain vision-gated. `POST /api/v1/hooks/{sourceKey}` checks one SHA-256 bearer owned by an Event Source, admits one External Event, fans out matching subscriptions, and returns before the agent or browser runs. A trusted application event uses the same unattended browser lease and 32-step/240-second budget as a scheduled WorkItem. A succeeded side effect whose tool result was not checkpointed is not replayed. `secretary` v2 is additive; v1 and `general-assistant` v1–v12 stay unchanged. P9 stays frozen on `bba1de4`. P9.5 stays closed on `1012653`. P10 and P11 were not started. This record is observed behavior, not a phase closure.

## What may still be measured

Provider selection within independently configured speech ports, VAD thresholds, frame size within the allowed range, TTS phrase segmentation and latency optimization are tuning variables. The default behavior and degraded paths are specified; measurement must not reopen project ownership, transport, storage or response identity decisions. No guaranteed provider-dependent SLA is implied.

## P9.7 — Conversational Authoring through existing owners

Normal Chat uses its existing model/tool/approval loop for instance-authorized harness learning. A separate preparation model runtime is retired. Authority comes from enabled contextual instance policy and trusted-local execution, never Definition text or a user assertion. Source authorization uses ordinary tools and content-bearing execution receipts; no mandatory secondary exact-source catalog exists. Tool eligibility is derived from configured, already-authorized ordinary capabilities. Managed knowledge saves to its own Instance; explicit shared promotion uses a reviewed draft; instructions and every tool selection/configuration remain approval-bound. Session creation provenance stays immutable; adoption applies to the next new Run, including in an existing Session. Existing lifecycle/resource/CAS/audit/SQLite owners remain authoritative. P11A records future principal/application/tenant delegation; it is not implemented in P9.7.

## Decision: unified Automation and semantic continuity

Automation is one owner-authored future behavior: a concise Name, bounded Instructions, and a Schedule or Event trigger. Thinking, reminders, event reactions, Experience review and identity maintenance are ordinary instructions. A Run is its bounded execution through Trigger → Occurrence → targeted Session/Activation/AgentRun. A successful NoAction stays quiet. Permitted effects and attention use the existing capability, approval and delivery contracts. Connections owns webhook ingress and credentials; Continuity owns retained context. Each accepted occurrence uses its configured Session destination; a background child can later continue in Chat.

The accepted unified Automation proposal supersedes behavioral Thought/Schedule/subscription resources and special retrospective/maintenance loops. Preserve one scheduler/executor/policy, both persistence providers, CAS/provenance, model pinning, exact approvals, restart recovery and effect fences. Reset disposable demo records rather than implement compatibility readers. Experience source inspection/recording and identity maintenance use ordinary eligible tool contexts. This does not move historical freeze SHAs or start P10/P11. Current acceptance belongs to [the new verification report](reports/unified-automation-model-verification.md).

## Decision: P9.10 semantic identity maintenance

Long-lived identity state supports accumulation and bounded maintenance. Reuse the existing structured Memory and Experience stores, transactions, exact approvals and generic Automation/AgentRun loop. Consolidation preserves immediate multi-parent provenance and supersedes sources; it never compacts source Sessions or rewrites trusted instructions. Experience generalization stays an observation in its own store. A single instance-owned, default-off CAS setting controls safe autonomous consolidation independently of activation.

Stable deterministic result identities derive from the trusted owner, operation kind, sorted source identities and canonical proposed payload. Exact retries return the established result; deleted or suppressed results remain unavailable. This provides idempotent local semantic effects through restart without claiming exactly-once external effects. No new scheduler, runtime, continuous cognition, automatic forgetting or generic model-facing CRUD is introduced. Existing P9.8/P9.9 freezes remain on `0a3330db`.


### Decision: instance-owned Skills and execution snapshots

Definition versions own reusable immutable procedures; Agent Instances own stable-id enabled choices and independent mutable local procedures. Explicit Customize is copy plus source disable, never inheritance, overrides or name shadowing. Ordinary authorized Skill capabilities replace Harness Skill authoring. Full catalog snapshots keep mutable procedures stable throughout a turn/Work execution and recovery. Writes apply to subsequent executions. Definition version transitions initialize state atomically and retain dormant ids; provenance is informational only. The cutover deliberately supports no legacy execution/keyword interpretation. Reset disposable local/demo data containing old Skill JSON or executions. Historical P8/P8.5/P9.7 verification remains evidence for the older behavior; current migration evidence is recorded separately in [Instance Skills verification](reports/instance-skills-migration-verification.md).

## Attachment image decoder

Image sanitization uses MIT-licensed SkiaSharp 4.153.1 in Infrastructure, with matching `SkiaSharp.NativeAssets.Linux.NoDependencies` for the minimal Linux container. The former ImageSharp dependency is removed; NuGet vulnerability auditing remains enabled. Inspect dimensions before allocating decoded pixels, enforce the existing pixel limit, decode the first frame, and encode fresh PNG/JPEG bytes without source metadata. GIF/WebP normalize to PNG. Processor cache identity is `attachment-processors/5` (Office coverage/continuation and distinct feature/limit failures); historical `/2` and `/4` evidence remains historical. Vision admission, MIME truthfulness and provider ports are unchanged. The user approved replacing ImageSharp when the patched version required a Release-build license.

## Decision: explicit Automation destinations and report-back

**Decision:** Persist ExistingSession(exact ID) or BackgroundSession and a separate None/ToSession completion policy. Freeze both into each occurrence. Chat supplies a safe authoring mode; Core binds IDs. Admin uses current owned Session validation. Provenance and attached-browser heuristics grant no targeting authority. Existing-target work uses pinned conversational output/model; background work uses a terminal outcome contract.

**Rationale:** A conversational reminder belongs in its conversation, while independent work needs retained child history. Delivery is a requested response obligation, so spontaneous Initiative does not gate it. Explicit output contracts avoid conflating scheduled source with background result format.

**Consequences:** No second runner, timer or generic context-sharing primitive. User-paused/ended/deleted/archived targets fail visibly without fallback. Shared per-Session claims/mailboxes serialize work. Initial-child receipts deduplicate per occurrence; NoAction stays quiet, and completed report admission is distinct from successful delivery. Current owner/policy/model and effect fences still apply. Migration deterministically preserves old Automations as BackgroundSession/None without resetting unrelated data.

## Decision: durable completion handoff and same-Run waits

Extend the existing completion receipt into the canonical durable completion inbox; retain one AgentRun store, mailbox and coordinator. Do not copy child result content into inbox storage or introduce a second queue/runner. Claims and acknowledgment intent are provisional; successful parent final response and Handled accounting share the outcome transaction. Requested automatic delivery waits until active parent work ends and remains recoverable. A typed WaitingForSignal checkpoint resumes with a new generation without another provider attempt. Apply a forward SQLite migration preserving Sessions, Automations and historical receipts. Historical freeze evidence remains unchanged.

## Shared Events and Admin ownership

**Decision:** A shared Event has stable ID, immutable validated unique key, editable name, hashed bearer secret and Active/Revoked lifecycle. Global Connections owns Events and System Credentials; Instance Credentials owns grants and Automation owns behavior. Schedule/Event Automation triggers use timing or Event ID exclusively. Generic bounded JSON data replaces the retired Event Source + Event Type selector without a type catalog. Authentication, receipt/delivery dedupe, subscriber snapshots, correlation bounds and existing Occurrence/Activation/Session/AgentRun policy/recovery owners remain. The data-preserving migration updates existing URL keys and occurrence dedupe identity; no compatibility API or new execution system is introduced. Local proof is in the [enhancement report](reports/admin-events-ux-verification.md); historical P9.6 closure above describes its original implementation.

Execution cleanup diagnostics favor conservative evidence over authentication heuristics: generic predicate verification and model claims do not establish application sign-out. Reservation hints remain deterministic, bounded and independent of authorization. Admin reads enforced maxima from Core instead of maintaining another ceiling definition. Existing resource ceilings, checkpoint compaction, reserves and recovery ownership remain unchanged.

## Decision: Core Event filters and editable presets

Keep one Automation execution substrate. A trusted committed-state outbox supplies the owner-scoped catalog (including the Email monitoring successor); a shared predicate decides delivery before model admission. Presets are opt-in versioned templates, disabled on creation, with ordinary editable instructions/targets/models. Experience review validates each exact represented source through `experience.source` before `experience.record`; consolidation and Harness recipes retain their current permission, scope and approval requirements.

Infrastructure pins Jint 4.4.1 behind `IEventFilterEvaluator`. An independent expression grammar plus JavaScript parse validation rejects executable language features before evaluation. Every admitted nonempty evaluation owns a fresh, deterministically disposed engine. Engine-owned JSON values parsed by Jint without JavaScript bootstrap execution, no CLR bindings, four simultaneous evaluator slots, 1 KiB source, 128 nodes/depth 12, 8 KiB envelope, 4 MiB Jint allocation budget (excluding the full CLR/process heap), 256 statements and a 100 ms engine/external cancellation deadline bound JavaScript execution. Restricted parsing and cold engine initialization precede that deadline; execution reuses the prepared expression. Worker contention and execution timeouts are transient, with bounded durable recovery. Only strict Boolean results match. No calls, loops, mutation, assignments, dynamic paths, functions, regexes or prototype/constructor access are allowed. The envelope and decision contract are engine-independent. Numeric values must be finite, within JS safe-integer magnitude and round-trip through the decimal comparison guard; high precision amounts should use integer minor units or strings.

## Decision: scoped resources, sparse settings and next-Run adoption

The approved configuration scope is Definition baseline → sparse Instance override → frozen AgentRun configuration. Section Customize is presentation state; only edited fields become overrides. Per-field clear and section reset restore inheritance. Explicitly nullable fields preserve null independently from absence. The existing per-class budget profile, ceilings, source attribution, accounting and requested cleanup pipeline remain separate and unchanged.

New inherited Skills and resources use nullable enablement choices; historical materialized Skill booleans migrate as explicit values. IDs remain stable through true Definition forks/publications. Independently copied local content has a separate namespace and source provenance. General settings reject authority expansion and invalid provider/model selection.

Adoption is exact and explicit, with validated preview and atomic reconciliation. The next new Run in an existing Session uses current effective Instance configuration; already admitted Runs remain frozen. Managed Chat no longer publishes a shared Definition as an ordinary self-management effect. Shared reuse requires explicit selection, draft review, validation/evaluation and publication. No second UI kit, merge framework, budget pipeline or automatic cross-Instance adoption is introduced.

## Decision: unified structured composer

The approved unified-composer proposal extends the existing Ant Design v6 Chat shell with one headless Lexical editor. Lexical owns selection, composition, history and atomic inline nodes; it supplies no component kit or visual tokens. AntD remains the only generic UI. Canonical ordered user-message parts, never editor JSON/HTML, cross the existing SendText transport. `/` selects explicit Skills, `@` selects owned contextual references, and `+` exposes the same discovery actions. This is an additive post-MVP initiative; P10/P11 and prior freezes remain unchanged.

## Decision: account-scoped IMAP/SMTP Email

The Custom transport retains protected Password material, typed TLS endpoints, MailKit/MimeKit in Infrastructure and public destination allowlisting. The original Gmail App Password preset is superseded by the following OAuth decision. Account ID equals Credential ID; bindings never grant capabilities. Exact immutable draft approval and at-most-once dispatch remain required.

## Decision: Gmail OAuth and REST API

Gmail uses Authorization Code + PKCE S256, offline access and server-side Google identity validation (`Google.Apis.Auth` 1.76.0), followed by mailbox identity verification. Operator configuration belongs to one confidential Google OAuth client per deployment; individual owners authorize in Admin without Cloud credentials or passwords. Request only openid/email, gmail.readonly and gmail.send. Refresh material uses existing account-bound protection and secret CAS; short-lived tokens stay ephemeral. Logical revision and authorization epoch advance for authority changes, not routine refresh. Custom Password transport remains independent. No generic OAuth platform, provider Draft sync, Graph, aliases, reply threading, attachments, labels or bulk sends is introduced. Google policy/Testing expiry can require reconnect; live acceptance is separate from offline contract tests.

## Decision: account-owned Email monitoring

Use one watch per Credential and one central API worker, Gmail History and Custom read-only IMAP UIDs, with an additive SQLite table and InMemory parity. Reuse the Core Event outbox transaction and existing Automations; introduce no Pub/Sub, webhook bridge, queue or polling in the one-second TriggerScheduler. Owner enablement is explicit and starts from now. Watch settings do not change Credential revisions. Current account, binding, Instance and Definition authority remain independent from event discovery and send approval.


## Office productivity, preview and Drive decisions

Open XML SDK 3.5.1 and ClosedXML 0.105.1 produce real OOXML in Infrastructure. ClosedXML.Parser 2.0.0, ExcelNumberFormat 1.1.0, RBush.Signed 4.0.0 and System.IO.Packaging 10.0.2 use MIT; the pinned SixLabors.Fonts 1.0.0 package uses Apache-2.0 (not the license of newer Fonts releases). Keep these exact versions and review license terms before upgrades. No paid document service or conversion middleware is used.

Ordinary DOCX HTTP/HTTPS and single-recipient mailto hyperlinks are permitted only as guarded inert relationship metadata in Word content parts. Existing Open XML SDK inspection/edits preserve unrelated links; hyperlink-containing edit targets remain unsupported and preview navigation remains stripped. Active external relationships and all XLSX external relationships stay blocked. No additional library or conversion layer is added. The [backend guard](12-backend-implementation-spec.md#bounded-office-processing) owns details; the [serviced runtime runbook](17-observability-and-operations.md#serviced-net-runtime-and-safe-redeployment) owns the patched deployment floor and update/restart procedure.

XLSX content reading stays ClosedXML-first (`CellsUsed(XLCellsUsedOptions.Contents)` plus bounded safety-scan address hints for explicit empty strings, which ClosedXML omits). `CachedValue` avoids formula recalculation. The separate sparse profile retains 10 MiB compressed/32 MiB expanded packages, 512 entries and 50 sheets. It permits 250,000 stored cell records, 50,000 meaningful cell markers plus conservative formula-range materialization area, 50,000 shared-string entries, worksheet XML parts up to 16 MiB/1,500,000 nodes, other parts up to 4 MiB/250,000 nodes, 3,000,000 package XML nodes and depth 64. Sparse pages contain at most 500 cells and fit the existing output budget. Initial XLSX excerpts additionally fit 16 KiB UTF-8. Preview, explicit rectangular reads, creation and editing keep their strict 4 MiB-part/250,000-node/50,000-stored-cell profile. Synchronous loading remains cooperatively bounded, not hard-preemptible; each stateless page reopens the source. No streaming engine, persistent cache or parser dependency is introduced. [Local evidence](reports/closedxml-sparse-reading-verification-2026-10-14.md) owns the measured cost and remaining limitations.

Papa Parse 5.7.0 keeps CSV typing disabled. Shiki 4.5.0 uses a JavaScript regex engine and imports only javascript/typescript/jsx/tsx/json/yaml/toml/xml/csharp/python/sql/css and github-dark; themes/grammar copyright notices remain required beyond the Shiki MIT wrapper. Its release pins tm-grammars 1.32.22 / tm-themes 1.12.12. Retain selected upstream MIT/TextMate permissive terms in [shipped notices](../web/public/notices/shiki-grammars-themes.txt). No other grammar is imported. docx-preview 0.4.1 uses Apache-2.0, DOMPurify 3.4.17 chooses its Apache-2.0 option, JSZip 3.10.2 chooses MIT, and pako 1.0.11 retains MIT/Zlib terms. [Frontend notices](../web/public/notices/office-preview-frontend.txt) and [backend notices](../src/AgentCore.Infrastructure/Notices/office-dependencies.txt) ship with their respective build outputs.

Drive uses official direct REST, a separate Google Drive Account purpose/profile/grant/cookie/callback and only openid/email + drive.file. Stateless PKCE/identity proof primitives can be shared with Gmail; account authorization is never shared. Existing owner grants, tool policy, Session execution and key ring remain authoritative. Persistent generated-ID receipts are required before creates; uncertain completion only reconciles the known ID. Use resumable upload, no overwrite/conversion. Remote rename/move/trash preconditions are best effort, with exact approval and no atomic remote-write claim.

Ant Design v6 remains the only generic UI. PDF, LibreOffice, PowerPoint, legacy Office, macros, WYSIWYG editing, native Google editing/export, Shared Drives, Picker, broad Drive scopes, sync/watchers and paid middleware remain deferred. [The plan](18-implementation-plan.md#office-file-preview-and-drive-successor) owns exact-final-source acceptance.

Exact new frontend production package inventory:

| Package | Version | License expression |
|---|---|---|
| @shikijs/core | 4.5.0 | MIT |
| @shikijs/engine-javascript | 4.5.0 | MIT |
| @shikijs/engine-oniguruma | 4.5.0 | MIT |
| @shikijs/langs | 4.5.0 | MIT |
| @shikijs/primitive | 4.5.0 | MIT |
| @shikijs/themes | 4.5.0 | MIT |
| @shikijs/types | 4.5.0 | MIT |
| @shikijs/vscode-textmate | 10.0.2 | MIT |
| docx-preview | 0.4.1 | Apache-2.0 |
| dompurify | 3.4.17 | (MPL-2.0 OR Apache-2.0) |
| immediate | 3.0.6 | MIT |
| jszip | 3.10.2 | (MIT OR GPL-3.0-or-later) |
| lie | 3.3.0 | MIT |
| oniguruma-parser | 0.12.2 | MIT |
| oniguruma-to-es | 4.3.6 | MIT |
| pako | 1.0.11 | (MIT AND Zlib) |
| papaparse | 5.7.0 | MIT |
| regex | 6.1.0 | MIT |
| regex-recursion | 6.0.2 | MIT |
| regex-utilities | 2.3.0 | MIT |
| saxes | 6.0.0 | ISC |
| setimmediate | 1.0.5 | MIT |
| shiki | 4.5.0 | MIT |
| xmlchars | 2.2.0 | MIT |


## Public binary download limits

The approved public-file enhancement reuses the public HTTP transport, owned Workspace commits and existing Artifact/Office capabilities. No additional HTTP client, storage engine or document conversion stack is introduced. Binary transfer buffering is bounded: default 10 MiB, configurable through `PublicWeb:MaxDownloadBytes` from 1 byte to the existing 50 MiB home-file ceiling; Separate home/working usage and tool-file limits apply at the present-path write. The default 15-second total download deadline is configurable from 1 to 30 seconds and remains subject to the admitted Run's per-tool/overall budgets. Text fetch/HTTP response limits remain 2 MiB and text projection remains 256 KiB characters. Neither declared MIME types nor a media allowlist restrict ordinary binary downloads; basic known-extension signature checks reject obvious mismatches, while Office inspection owns full supported-format validation.

## Trusted-local OpenSandbox decision

The approved P10 execution backend is `Alibaba.OpenSandbox` exactly 1.1.1, paired with the 1.1.1 server/execd/egress digests in [images.json](../scripts/opensandbox-proof/images.json). The SDK stays in Infrastructure. No Docker CLI or daemon client remains in production Core. Deployment/acceptance scripts may operate only their disposable project resources. A single trusted server uses the existing local Docker daemon; no DinD, additional VM, Kubernetes or alternate runtime is introduced for this local-demo target.

Profiles pin immutable OCI registry digests; explicit local-development configuration may allow a content-addressed local image ID. The optional maintainer General Workbench fixture preinstalls Python 3.13.7, Node 22.20.0, TypeScript 5.9.3, pandas/numpy/openpyxl/python-docx/matplotlib/pytest/reportlab, LibreOffice and utilities. Python hashes, base-image digests and a Debian snapshot fix build inputs. Initialization is optional, bounded and once per generation, after direct mount verification; setup is bounded by the saved command budget.

The trusted server selects the deployment’s non-root workload identity before startup, with capabilities dropped, no-new-privileges, 64 PIDs, bounded CPU/memory/TTL and deny-default egress (see the ordinary-image successor below). The actual SDK acceptance probes resource enforcement, DNS/external IP, metadata, private lifecycle and gateway access. Own execd loopback remains reachable; trusted local use explicitly accepts that limitation. Session `/working` defaults to a 250 MiB sampled budget and permits saved host-bounded budgets up to 16 GiB. Shared `/home` has a separate 250 MiB sampled budget and 50 MiB tool-file bound. There is no obsolete snapshot entry ceiling; unreliable scans stop execution with unknown usage. Docker writable-layer disk quota is independent and not enforced by these directory budgets. Production isolation and signed supply-chain attestation remain separate deferred hardening.

Ordinary per-tool ceilings remain 30 seconds. Sandbox commands have a separate pinned Run allowance, default 120 seconds and maximum 180 seconds, clamped to the profile and remaining Run deadline. The OCI catalog cutover retires executable `sandbox.run` completely; old publications require explicit modern republishing and Instance adoption. Earlier BusyBox limits above are historical Phase H values, superseded by this decision.

### OCI-only profile catalog decision (2026-10-14)

The authenticated local owner registers multiple prebuilt OCI images through the existing versioned `SandboxProfile` catalog. Each Session generation uses one primary image. Saved remote identities require an explicit lowercase registry/repository and immutable sha256 digest. Owner onboarding accepts tags and Docker Hub short names, resolves them at save time, and stores only the resulting digest; exact local sha256 content IDs require the host gate. `AllowedImagePrefixes` matches complete registry and repository segments. Digests identify bytes, not trusted provenance. No image builder, importer, composition editor or additional workload lifecycle is introduced.

`ContainerRegistry` System Credentials own protected password/PAT/token values and typed exact registry/repository scope. Profiles store only their credential ID. They cannot be bound to agents. Infrastructure supplies transient SDK `ImageAuth` (username/password, including PATs) to both disposable verification and provisioning. Remote manifest authorization (anonymous for public images) is rechecked before either operation even if the daemon caches the image. HTTPS is default; exact loopback insecure registry exceptions and cross-origin HTTPS token services require explicit host configuration. Secrets never enter workload env/files or public projections.

Server reachability is independent of catalog eligibility. Enabled intent is insufficient: new Runs/generations require an exact current-contract verification and current credential revision/secret epoch, source policy and explicit Definition authorization. Historical observations remain readable and require reverification. New saves append CAS revisions; verification never advances them. Source revocation fences commands and supervised cleanup; ordinary configuration edits retain existing generation pins until explicit reset/expiry. `WorkbenchImage` bootstrap, implicit profile fallback and executable legacy mapping are removed. Workbench build/notices remain an optional maintainer fixture. Trusted single-user isolation, own-execd loopback and writable-layer quota limitations are unchanged.

## FortuneSheet read-only preview decision

`@fortune-sheet/react` is pinned to 1.0.4; the lockfile pins core 1.0.4 and formula-parser 0.2.13. Its published peer range is React/ReactDOM >=18.2; local React 19/Vite 7 build and runtime evidence belong to the [verification report](reports/fortune-sheet-preview-verification-2026-10-14.md). ClosedXML and Papa Parse remain the interpreters. FortuneSheet is a specialized lazy document renderer, not a second generic UI kit or editor. Only inert bounded display values reach it; its bundled formula runtime is never supplied active formulas or recalculation authority. Operational preview defaults move to the validated backend host policy, with immutable Office guards and original downloads separate.

The exact added runtime closure uses MIT except bessel's Apache-2.0 terms; jStat's installed copyright/license is MIT despite its legacy package metadata omitting the license field. Full redistribution notices are shipped in [fortune-sheet.txt](../web/public/notices/fortune-sheet.txt). Retain these notices and audit the installed closure on upgrades. Chart/pivot/drawing/conditional-format fidelity, exact Excel width metrics, complex split panes and clipped merges are not claimed.

## Ordinary Linux sandbox images (owner-authorized 2026-10-15)

The canonical filesystem decision above supersedes the earlier per-generation workspace transport. Compatible Linux images require shell/proc/Git/Unix file utilities; Python and pre-created workspace directories are optional. Canonical home and working are exact binds with independent effective home access. The pinned trusted server’s narrow Docker creation adapter sets the deployment’s non-root USER without building or replacing the selected immutable image. It records original image ID/default USER in container labels and retains drop-ALL/no-new-privileges. Images that cannot start or run under these constraints remain unsupported.

The Space cutover keeps workspace operation contract 3 and requires profile verification/mount compatibility contract 5, including captured tenant/Space/Private ownership and independent root access. Profile verification requires exact authenticated daemon backing-path, generation, image and runtime identity attestation plus behavioral file/Git probes. Strict Linux root ownership remains mandatory. A positively identified Docker Desktop Linux daemon can accept translated `0:0` root metadata only with private modes, owned host roots, bidirectional canonical bytes, independent mount policies and link checks. Git receives command-local exact directory exceptions only after translation is observed, clearing inherited safe-directory entries; no wildcard or global configuration is written. Old verification is ineligible. Source epochs, Profile/Run/lease revisions, receipts and action hashes remain authoritative.

### Tenant generation retirement proof

The existing trusted Docker server also owns a negative generation retirement fence in its existing persistent provider metadata volume. The installed pinned provider creates in a background thread; an empty filtered list cannot fence a lost create reply. Retirement commits the exact tenant/Session/executor/generation tombstone, waits for that existing call and retires only matching containers. Core checks capability before new tenant generation effects and accepts only a bounded matching proof. This is technical provider lifecycle evidence, without IAM grants, a new Core operation table or an alternative supervisor. [Focused evidence](reports/tenant-sandbox-recovery-verification-2026-10-18.md) records controlled runtime coverage and unrun live qualification.

The same primitive seals disposable tenant verification by its real tenant/attempt identity. Three normal Home probes share that attempt without sealing between probes; final success/failure or technical cleanup seals it before purge or compatibility promotion. Exact attempt metadata is rechecked on known handles, and current original-human/source/Instance authority is observed during SDK work. Verification fences share the existing provider state volume and cannot authorize a command. [Verification retirement evidence](reports/tenant-sandbox-verification-retirement-verification-2026-10-18.md).

### Sandbox selection and recovery successor (owner-authorized 2026-10-15)

The existing OpenSandbox backend retains multiple exact authorized profiles with one active generation per Session. Definition authority remains bounded at 16; sparse Instance allowed subsets only narrow it. Runs pin effective revisions, eligibility and source epochs. Selection survives Runs/reopen/expiry and reset returns to the effective default. Safe switching confirms old process retirement before a new generation mounts the current canonical directories. Cleanup/authority uncertainty cannot be bypassed by selection.

Descriptor-relative canonical access/purge uses platform-specific no-follow primitives. Private roots and compatible execution UID/GID preserve Core access without shared writable modes. Directory sampling is advisory; unsupported strict quotas fail closed. Known cancellation retains partial files and the original receipt; unknown effects never authorize replay. Historical selection/recovery reports describe their original revisions only.

## Generic Linux retirement (owner-authorized 2026-10-15)

Use bounded generic process-identity inspection and exact whole-container retirement for residual writers. The pinned process-group API does not prove escaped-descendant cleanup, so Core never guesses reused numeric IDs. Authoritative absence settles process evidence only: canonical writes already occurred. Reuse safe containers and preserve files/selection across recycle. Setup remains once-per-generation and never automatically replays an uncertain script. No alternative supervisor or synchronization service is introduced.

### Tenant webhook fanout bound

The tenant event conversion preserves an atomic receipt/subscription snapshot. A single Webhook receipt may capture at most 1,024 matching active Automation registrations across its tenant. Exceeding that ceiling refuses the whole receipt with `409 webhook_fanout_limit`; it never truncates the target set or admits partial work. A source/Automation owner must reduce active subscriptions before a deliberate new sender attempt. Core events remain scoped to the actual Instance owner, bounded by its active registrations. Existing source-body, filter, bucket packing, recovery-page and approval limits remain independent. This bound is independent of the authored child-count limit.

Tenant event admission retains bounded consent serialization separately from authored configuration: canonical event admission intent is capped at 256 KiB and its stored approval payload at 1 MiB, to carry both current/next copies of the existing bounded multi-subscription owner. Schedule/manual intent retains its prior 64 KiB canonical/256 KiB stored limits. These bounds do not enlarge event evidence or predicate limits. Maximum-shape consent verification remains required before module qualification.


### Tenant email watch bounded source commit

The canonical email watch conversion uses the existing three-account polling concurrency, sixty-second deadline, ninety-second persisted lease, twenty-five message-reference page and bounded Core bucket owners. One cursor commit may produce at most 1,024 factual receipt/child-delivery targets in total; each receipt has at least one captured child. Excess fanout refuses the entire batch without advancing its cursor or publishing partial recipients; the safe `email_watch_fanout_limit` status directs deliberate source/subscription reduction before retry. Source polling is account-scoped and creates no executor principal or permission grant. Focused local qualification is recorded in [tenant email monitoring verification](reports/tenant-email-watch-verification-2026-10-21.md); full proposal acceptance remains open.

The Space workspace cutover extends trusted sandbox mount verification to contract 5. Each generation pins the captured context and exact working/home/private root identities and access modes. Workspace read is required for a mounted root; RW requires both current write and delete authority. HomeAccess can narrow those rights. Profiles and Environment selection cannot widen them. Current original-human/executor authority repeats at protected boundaries. Prior generations must be retired before changed context or access can execute. A human context switch uses the existing OpenSandbox owner to retire an idle generation and requires confirmed remote cleanup before persisting the selection. An in-flight command returns `sandbox_execution_busy`; finish or stop it through the existing control path, then retry with fresh revisions. Both stores recheck that no live generation remains, and pending next-turn selection prevents an older Run from reserving a new mismatched generation. Uncertain retirement remains fenced for existing bounded recovery; it never implies mount removal.


## Decision: Message execution and Chat simplification

Retain Session → Activation → AgentRun, separate message and response identity, voice supersession fences and transport/business lifecycle projections. No Turn table or new execution engine. Replace internal Legacy lifecycle provenance with Host; no privileged Legacy parser remains. Agent closure is explicit `session.complete`, separate from background `work.complete`, and follows durable final delivery. Retention belongs to Session, tenant defaults pin at creation, and expiry is authoritative before eventual physical cleanup. Large text uses deliberate canonical attachment intake, never a larger wire-message limit.

## Decision: optional self-hosted Decision Intelligence

The owner-approved [Initiative 6 proposal](proposals/self-hosted-decision-intelligence.md) permits one optional Kev inference sidecar as a narrow exception to the microservice non-goal. The .NET modular monolith retains all policy, IAM, admission, persistence and execution ownership. No broker, second database, agent or scheduler is introduced. Kev is not an inference offering and never receives provider credentials.

Pin upstream Kev source `6b719c3c3f367295f6ef336f4f751cf5ff970abc` (kev-1.0), with its frozen uv lock, and Kev-4B artifact revision `6cfce5c2fa4b4bd64026336ab649c5ca78857d52` (resolved v1.0). Serving must witness that exact immutable run; a mutable model alias alone is insufficient. Default disabled, explicit internal bearer authentication, loopback/internal-only transport, no HTTP redirects/proxies, bounded input/output/concurrency/deadline, and no automatic backend/model failover. The Decision Engine has no CUDA/NVIDIA dependency: the same pinned artifact may run natively through MLX. An optional external-service Compose overlay connects through configurable `Endpoint` on loopback, `decision`, or `host.docker.internal`, using an operator-selected HTTP port and private-address verification for named Docker destinations. No GPU reservation or inference container is added by that overlay. Upstream probability is not measured Agent Core correctness; measured native smoke and held-out calibration belong to the dated verification report, and NVIDIA performance qualification remains platform-specific.

## Evaluation capability admission

Scripts and Evaluation extend the current projects and IAM/store/provider owners. Immutable version rows plus bounded structured findings are sufficient; no workflow engine, queue service, second agent principal or Session/Run replacement is added. An evaluation model call is admitted by its durable attempt with a fresh capability identity and pinned route, rather than forging a Run identity or reviving terminal consent. Run-originated model invocation keeps its existing context/authority path. Sandbox script dispatch remains fail closed until the incumbent native effect owner can establish independent exact Review and isolated inputs.

## Explicit Session Goals replace Goal-purpose outcomes — 2026-10-27

**Decision:** Outcomes belong to optional Session Goal aggregates with ordered checkpoints and independently qualified evidence. Session lifecycle and described Ongoing purpose remain separate. Retire `SessionPurposeKind.Goal` in the coordinated forward conversion; do not maintain aliases or duplicate completion state. This supersedes the Goal-purpose portion of the earlier additive lifecycle decision, while preserving Session deadline, completion delegation and protocol-v1 status behavior.

**Consequences:** Core owns checkpoint verification/completion; material plans and attributable acceptance need current human authority. Exact Goal task bindings join existing Activation/Run lineage. Bounded optional continuation uses the incumbent coordinator, current IAM/Space/Membership/model checks and durable finite count/time limits. Domain/HTTP/persistence/SPA deploy together. [Architecture](03-system-architecture.md#session-goals), [ports](04-backend-interfaces.md#session-goal-ports), [cutover](15-persistence-and-configuration.md#session-goal-persistence-and-cutover) and [qualification](reports/session-goals-delivery-2026-10-27.md) own details. Task Delegation and Group Chat remain out of scope.
