# TODO

Ordered by current dependency and product value.

Reviewed against `main` on **2026-09-28**.

---

# Current state

## Frozen phases

P0–P7 are closed/frozen unless a reproducible regression or a concrete new product requirement requires reopening a boundary.

Important freeze references:

```text
P1: dceaccb
P2: 47d6ff6
P3: 4dbb920
P4: 822028f / workflow 35806764609 green
P5: 4bbc0c1 / workflow 35954811544 green
P6: 30adaeb / workflow 36085265506 green
P7 Harness Admin: follow-up tree 2acb1a8
  last harness behavior: 1090535
  canonical W08 hosted gate: f4107d7 / workflow 36239630112 green
P7 verified follow-up tree (incl. session appendix): 2c4d46f
  last session behavior: 93cb2ab
  final gate/test stabilization: 2c4d46f
  hosted gate: workflow 36336971087 green on 2c4d46f
```

P7 evidence: `docs/reports/p7-freeze-candidate.md`.

P7 owns **Harness Admin W01–W08 only**. The `ConversationTurnExecution` observer-durability correction is **closed** session/runtime bookkeeping (not part of the P7 Admin contract).

Observer-durability closure (do not merge into Harness Admin):

```text
93cb2ab — last behavior-affecting
3e75934 — gate evidence (tests + history merge)
2c4d46f — final stabilization; workflow 36336971087 green
2b3188f → f45ea27 — scope/evidence documentation
```

## Current active phase

**P8 — harness/platform extensibility.**

P7.5 is frozen on `b112198`. Hosted workflow `36348699304` is green on that SHA. Closure report: `docs/reports/p7.5-freeze-candidate.md`.

Start P8 from one concrete provider need. Do not add a universal provider interface.

---

# Current roadmap

1. **P0–P7 — closed/frozen.**
2. **P7.5 — architecture consolidation and infrastructure readiness — frozen on `b112198`.**
3. **P8 — harness/platform extensibility — ACTIVE.**
4. **P9 — sandbox evolution when requirements justify it.**
5. **P10 — multi-user + production infrastructure when real hosting/pilot requirements justify it.**

Do not reopen a frozen phase without either:

- a reproducible regression; or
- a concrete new product requirement that belongs there rather than in a later phase.

---

# Frozen architectural baseline

Keep these invariants while refactoring.

## Conversation/runtime

- one logical Session owns one conversation;
- one Session Runtime owns mutable conversational state while resident;
- provider callbacks do not mutate runtime state directly;
- detach/reconnect does not create a second logical conversation;
- accepted work must not be silently lost because the browser disconnects;
- historical durable state and transient live streaming remain distinct;
- session runtime state is not stored in Agent Definition configuration.

## Agent lifecycle

Preserve:

```text
Reusable Agent Definition
        ↓ publish
Immutable Agent Definition Version
        ↓
Durable Agent Instance
        ↓
Session
```

Do not collapse these into one mutable `agent.json`.

Definition upgrade, identity/persona revision, learned-memory reset, and creating/forking an Agent Instance remain separate operations.

## Authority/context boundaries

Keep these concepts distinct:

```text
Instructions
= what the agent should do

Trusted persona/profile
= trusted identity/context

Knowledge/resources
= authoritative/reference material

Learned memory
= what the agent learned/remembers

Trigger registrations
= durable future event/runtime state

WorkItems
= durable execution state

Session workspace
= mutable session-owned files
```

Do not silently promote learned memory into instructions or trusted identity.

## Tool/security boundaries

Preserve:

- registry / policy / execution separation;
- exact action approval for sensitive tool execution;
- credentials outside model-visible context;
- trigger registration does not grant future sensitive-tool permission;
- background/headless execution is never a privileged authorization bypass;
- Admin uses the same security/policy boundaries rather than a hidden privileged runtime path.

## Workspace/resource boundaries

Preserve:

```text
Published definition resources
  immutable/versioned

Session workspace
  mutable
  isolated
  session-owned
```

Do not add a general cross-session Agent Instance filesystem without a concrete workflow that requires it.

---

# Maintainer notes

Always keep this section.

- [ ] Agent communication, multi-agent orchestration/workflows, and related concepts remain future ideas. Do not pull them into P7.5/P8 without a concrete requirement.

- [ ] Admin assistant agent remains a future idea.

- [x] Add proprietary license.

- [x] Establish trusted user/session-context baseline.

- [x] Establish useful general-assistant tool baseline through P3.

- [x] Close current-turn and historical-image usability.

- [x] Keep provider reasoning separate from assistant output.

- [x] Keep Model/Reasoning controls in the active composer.

- [x] Keep reusable Agent Definition separate from durable Agent Instance.

- [x] Keep trusted identity/persona separate from learned memory.

- [x] Keep runtime-local timers separate from durable scheduling.

- [x] Keep trigger registration separate from trigger execution.

- [x] Keep trigger authorization separate from later tool authorization.

- [x] Keep background responses/work that must outlive Session Runtime under P6 durable work.

- [x] Keep the P7 product surface simple even when the internal model is richer.

  Product mental model:

  ```text
  Instructions
  Capabilities
  Resources
  Identity
  Memory
  Automation
  Test & Publish
  ```

- [ ] Keep infrastructure boring until product/hosting requirements justify more complexity.

---

# P7 — Agent harness / Admin lifecycle

**Status: closed/frozen.**

P7 delivered a product-level lifecycle for reusable agents without weakening P1–P6 runtime contracts.

Observed product flow:

```text
create/edit draft
→ configure instructions/capabilities/resources
→ validate/test
→ review diff
→ publish immutable version
→ create/manage durable Agent Instance
→ manage persona
→ inspect/reset eligible learned memory
→ configure/inspect/revoke eligible automation
→ inspect effective non-secret configuration
→ review Admin history
→ upgrade/rollback/deprecate without rewriting history
```

Important P7 contracts:

- Admin is a distinct product surface from normal Chat.
- published definition versions are immutable;
- sessions resolve the definition/version they actually used;
- Agent Instance identity survives definition version reassociation;
- persona revisions are typed and durable;
- learned-memory reset is not identity reset;
- trigger registrations are runtime/user state, not definition files;
- Admin history is append-only lifecycle evidence, not enterprise audit infrastructure;
- validation/evaluation evidence is tied to the relevant draft/version;
- ordinary user sessions cannot mutate published harness resources;
- secrets are not exposed by effective-config/Admin projections.

Detailed P7 slice history belongs in `docs/reports`.

---

# P7.5 — Architecture consolidation and infrastructure readiness

## Goal

Consolidate the architecture **after** P7 has exercised Definition, Version, Instance, Session, Memory, Automation, Admin, evaluation, and background-work boundaries.

P7.5 is intentionally not a redesign milestone.

Use this rule:

> Refactor only where completed P1–P7 behavior demonstrates real architectural pressure.

P7.5 may change implementation structure, names, internal APIs, dependency direction, and persistence plumbing where safe.

P7.5 must not intentionally change user-visible product behavior.

Observed through `docs/reports/p7.5-freeze-candidate.md`. Hosted workflow `36348699304` is green on `b112198`:

- Ownership map is in `docs/03-system-architecture.md`. Authoring, Runtime, and Operations are responsibilities, not new projects.
- `EffectiveConfigurationComposer` owns the Admin projection and the shared memory-policy default. Session bind, detached work, and draft evaluation stay on their own use cases. No policy defect was found.
- Cohesion audit deferred splits of the large runtime files. History-free instance methods remain because tests still call them.
- Persistence audit kept SQLite, separate stores, explicit transactions, and the single-process scheduler. Recorded in `docs/15-persistence-and-configuration.md`.
- Observability audit kept existing counters and added bounded diagnostics for the silent lifecycle, approval, quota, and Admin paths. Recorded in `docs/17-observability-and-operations.md`.
- Admin draft status, durable publications, and instance identity spacing live in `web/src/app.css`. Chat behavior was rechecked on a Synthetic turn.
- Canonical handoff names P8 next. P7.5 is frozen on `b112198`. `.agents/context/PRODUCT.md` and `DESIGN.md` describe the shipped Admin surface. `/docs` still wins.
- The local Synthetic gate and `./scripts/compose-sqlite-volume.sh` passed. Hosted workflow `36348699304` is green on `b112198`.

---

## P7.5A — Architecture and ownership inventory

- [x] Update the top-level architecture model so Agent Core is no longer described primarily as only a Session Runtime.

Represent the system explicitly around:

```text
Authoring
  definitions
  drafts
  resources
  validation/evaluation
  publication

Runtime
  agent instances
  sessions
  conversation execution
  tools
  memory

Operations
  triggers
  work items
  approvals
  Admin lifecycle/history

Shared cross-cutting boundaries
  effective configuration
  policy/authorization
  persistence/storage
  observability
```

- [x] Map each durable entity to exactly one primary owner/lifecycle.

At minimum:

```text
AgentDefinitionDraft
AgentDefinitionVersion
AgentInstance
PersonaRevision
Session
ConversationTurnExecution
LearnedMemory
TriggerRegistration
TriggerOccurrence
WorkItem
Approval
AdminEvent
Attachment
Artifact
DefinitionResource
SessionWorkspace
```

- [x] Identify duplicated ownership, ambiguous lifecycle, and write paths that bypass the intended Application boundary.

- [x] Confirm Session Runtime is not becoming the owner of Admin, background-work, or definition lifecycle state.

- [x] Confirm Admin services do not duplicate runtime behavior.

### P7.5A stop condition

Architecture documents and code ownership describe the same system, and every major durable concept has one clear lifecycle owner.

---

## P7.5B — Effective configuration and policy consolidation

- [x] Establish one obvious server-side composition path for effective agent configuration.

Inputs may include:

```text
definition version
instance association
persona revision
model/runtime defaults
capability/tool policy
knowledge/resources
memory policy
trigger policy
background-execution policy
trusted caller/profile context
```

- [x] Ensure Chat, Admin preview/evaluation, Triggered Work, and future P8 integrations reuse the same authoritative resolution rules where semantics should match.

- [ ] Do not let each execution surface reconstruct effective configuration independently.

- [ ] Audit tool, trigger, background-work, Admin, memory, and resource authorization checks for duplicated or contradictory policy evaluation.

- [x] Centralize shared policy decisions only where there is a genuinely shared contract.

- [ ] Keep action-specific approval and ownership checks close enough to execution that stale configuration cannot bypass them.

### P7.5B stop condition

Effective configuration and policy decisions have explicit ownership and no known parallel implementations that can drift.

---

## P7.5C — Application complexity and domain cleanup

- [x] Identify services/classes that became orchestration god objects during P4–P7.

Refactor only when responsibilities are clearly separable.

- [ ] Remove transitional compatibility code that is no longer needed after the P7 freeze.

- [ ] Normalize naming where the same concept has accumulated multiple names.

- [ ] Keep Domain focused on durable business concepts/policies rather than provider/storage mechanics.

- [ ] Keep Application focused on use cases/orchestration/ports rather than HTTP, EF, file paths, provider DTOs, or frontend-specific details.

- [ ] Keep Infrastructure implementation-specific.

- [ ] Avoid adding:

  - a generic `Common` project;
  - MediatR solely for indirection;
  - generic repository-per-entity abstractions;
  - a universal internal event bus;
  - microservices merely to make boundaries look cleaner.

### P7.5C stop condition

The modular monolith remains easy to navigate, with fewer accidental dependencies and no speculative architectural layer added solely for P8.

---

## P7.5D — Persistence and infrastructure portability audit

Current development infrastructure should remain the default:

```text
.NET modular monolith
SQLite
local filesystem-backed stores
Docker / Docker Compose
single-process Session Runtime ownership
single-process scheduler/background workers
Synthetic/offline CI by default
optional hosted providers
```

Do **not** migrate to the production stack in P7.5.

### Persistence

- [x] Audit EF/persistence code for accidental SQLite-specific assumptions that would make a future PostgreSQL migration unnecessarily invasive.

- [x] Keep migrations deterministic and repeatable.

- [x] Preserve explicit transaction boundaries around operations that must be atomic.

- [x] Preserve idempotency/retry semantics for accepted turns, trigger occurrences, WorkItems, approvals, publication, and other side-effecting lifecycle operations.

- [x] Avoid using in-memory state as the durable source of truth.

### File/blob storage

- [x] Keep host filesystem paths out of Domain and wire contracts.

- [x] Preserve separate conceptual stores for:

  - attachments;
  - artifacts;
  - definition resources;
  - session workspaces.

- [x] Do not collapse immutable blobs/resources and mutable workspace semantics into one generic file store.

- [x] Keep the current local filesystem implementation while it satisfies development requirements.

### Sandbox

- [x] Keep Docker as the sandbox implementation.

- [x] Do not introduce a new sandbox-provider abstraction unless a real second implementation is being added.

### Scheduler/background workers

- [x] Keep the current single-process scheduler/background-worker model while one process is the real deployment shape.

- [x] Do not add a distributed queue, Redis lease system, Kafka/RabbitMQ, or distributed scheduler simply because durable WorkItems exist.

### Secrets

- [x] Continue using environment/user-secrets/gitignored local configuration for development credentials.

- [x] Keep secrets outside model-visible configuration, effective-config projection, Admin history, and logs.

### P7.5D stop condition

The current dev stack remains simple, while core/application behavior is not unnecessarily coupled to SQLite, local disk, Docker-host paths, or single-process implementation details.

---

## P7.5E — Observability and operational shape

Do not deploy a large observability platform yet, but keep instrumentation production-ready.

- [x] Maintain structured logs/metrics/traces for:

  - model/provider selection and calls;
  - session/response/execution lifecycle;
  - interruption/queue/steer/detach/reattach;
  - tool calls and approvals;
  - sandbox execution;
  - compaction/memory mutation;
  - definition/version/instance resolution;
  - draft/publish/rollback/deprecation;
  - trigger registration/occurrence/scheduler lag;
  - WorkItem lifecycle;
  - policy denials;
  - resource limits.

- [x] Avoid sensitive content in operational telemetry unless explicitly required and protected.

- [ ] Keep health/readiness checks meaningful.

- [ ] Make it possible to export through OpenTelemetry later without requiring OpenTelemetry infrastructure as a local-development dependency now.

### P7.5E stop condition

Operational events are structured enough for future hosted diagnostics without requiring a production monitoring stack during development.

---

## P7.5F — UI review and closure evidence

The phase outcome is the A–E consolidation above, the shared Admin spacing slice, then documentation and context sync. It is not a separate refactor-only gate.

- [x] Shared Admin spacing for draft status, publications, and instance identity uses the 8/12/16 tokens in `web/src/app.css` (`b0c2e09`).

- [x] Run the complete deterministic test suite after architectural changes.

- [x] Run meaningful Synthetic Playwright journeys covering:

  - chat;
  - reconnect/durable streaming;
  - tools/approval;
  - memory;
  - automation;
  - background WorkItems;
  - Admin draft/publish/instance lifecycle.

- [x] Keep behavior changes out of refactor commits where practical.

- [ ] Update:

  - `docs/03-system-architecture.md`;
  - `docs/04-backend-interfaces.md`;
  - `docs/10-technology-decisions.md`;
  - `docs/11-repository-structure.md`;
  - persistence/configuration docs;
  - relevant implementation-plan/report references.

- [x] Record a P7.5 closure report with:

  - what architectural pressure was found;
  - what was refactored;
  - what was deliberately left unchanged;
  - which potential P8 abstractions are now justified;
  - which abstractions remain speculative and therefore deferred.

### P7.5 stop condition

P7.5 is complete when:

```text
P1–P7 behavior remains green
architecture docs match implementation
effective configuration has a clear authority path
policy ownership is explicit
major lifecycle boundaries remain distinct
persistence/storage dependencies remain replaceable enough
no premature production infrastructure was added
P8 extension seams are evidence-based rather than speculative
```

---

# Infrastructure stance before production

Until a real external pilot/hosting requirement appears, prefer:

```text
SQLite
local filesystem
Docker / Compose
single application process
single scheduler/background-worker process
Synthetic deterministic CI
optional hosted model/speech/integration providers
```

Do not add by default:

```text
PostgreSQL
Redis
Kafka / RabbitMQ / SQS
Kubernetes
service mesh
distributed cache
distributed scheduler
remote object storage
cloud secret manager
multi-node SignalR
microservices
```

This is not a prohibition on those technologies.

It is a requirement that each infrastructure dependency solve a real product/operational problem before becoming part of the base stack.

Production-shaped properties that **should** exist now:

- durable source-of-truth state where required;
- explicit ownership IDs;
- idempotent/retry-safe side effects;
- deterministic migrations;
- cancellation/timeouts;
- bounded work;
- secrets outside model context;
- storage/provider ports where an actual boundary already exists;
- health checks;
- structured observability;
- clear transactional boundaries;
- no Domain/Application dependence on host paths.

---

# P8 — Harness/platform extensibility

P7.5 is frozen on `b112198`. P8 may begin from one concrete provider need.

## Goal

Make the established Agent Core harness extensible without turning every implementation detail into a plugin API.

## Platform/extensibility

- [ ] Use P7.5 findings to identify extension seams backed by real second implementations.

Potential boundaries:

```text
model providers
tool providers
integration providers
trigger/event sources
sandbox providers
```

Do not force these into one generic provider abstraction.

- [ ] Add external tool-provider/plugin extensibility only when another real provider/integration justifies it.

MCP-like providers may be adapters.

Requirements:

- native Agent Core tools remain first-class;
- external tools still pass through Agent Core policy/authorization;
- provider credentials remain outside model context;
- provider-specific DTOs do not leak into Domain/Application contracts;
- extension failure does not corrupt durable agent/session state.

- [ ] Define extension identity/versioning/compatibility only to the degree required by actual extensions.

- [ ] Extend P7 validation for provider/plugin-specific concerns:

  - provider availability/capability mismatches;
  - missing provider references;
  - provider-specific permissions/configuration;
  - compatibility/versioning;
  - extension-specific validation/evaluation.

- [ ] Preserve one authoritative effective-configuration path when extensions are enabled.

- [ ] Revisit a durable mutable Agent Instance workspace only when a concrete cross-session file workflow requires it.

- [ ] Revisit richer reusable evaluation suites when multiple harness/provider implementations make them useful.

## P8 non-goals

Do not automatically turn P8 into:

- a plugin marketplace;
- visual workflow builder;
- multi-agent orchestration engine;
- generic distributed job platform;
- enterprise tenant/RBAC implementation;
- Kubernetes migration;
- microservice decomposition.

---

# P9 — Sandbox evolution

- [x] Docker is the current sandbox implementation.

- [ ] Keep Docker while it satisfies current requirements.

- [ ] Introduce `ISandboxProvider` only when a second implementation is genuinely required.

- [ ] Evaluate OpenSandbox when requirements include:

  - remote execution;
  - stronger multi-tenant isolation;
  - pools/faster provisioning;
  - distributed workers;
  - multiple runtime images;
  - remote resource controls.

- [ ] Keep model-facing `sandbox.run` stable while changing implementation providers.

- [ ] Consider Kubernetes only when deployment/scaling requirements justify it.

Do not adopt Kubernetes merely to replace a working Docker sandbox.

---

# P10 — Multi-user and production infrastructure

Start this phase when Agent Core moves beyond trusted single-owner/local development or begins a real external hosted pilot.

Do not start all P10 infrastructure at once. Add it in dependency order.

---

## P10A — Authentication, authorization, and tenant ownership

- [ ] Authentication.

- [ ] User/Admin authorization.

- [ ] Tenant/organization model only when required.

- [ ] Per-user/tenant resource ownership and quotas.

- [ ] Enforce ownership across:

  - Agent Definition;
  - Agent Definition Version;
  - Agent Instance;
  - Session;
  - memory;
  - triggers;
  - WorkItems;
  - approvals;
  - attachments/artifacts;
  - resources;
  - integrations.

- [ ] Remove trusted-local assumptions from externally reachable paths.

- [ ] Separate browser/user credentials from host/service authority.

---

## P10B — Production persistence

Expected first major production infrastructure migration:

```text
SQLite
  ↓
PostgreSQL
```

Trigger this when real requirements include concurrent external users, stronger production operations, multi-process services, or hosted reliability needs.

- [ ] Add PostgreSQL provider/configuration.

- [ ] Verify migrations and transaction semantics on PostgreSQL.

- [ ] Verify concurrency/revision protections under real database concurrency.

- [ ] Keep SQLite available where useful for local/Synthetic development unless maintaining both becomes counterproductive.

- [ ] Define backup/restore policy and verify restore, not only backup creation.

Do not introduce a new generic persistence architecture solely for the provider swap if EF Core boundaries are already sufficient.

---

## P10C — Production object storage and secret management

Add only when local-disk assumptions no longer fit deployment.

Potential migration:

```text
attachments/artifacts/immutable resources
local disk
    ↓
S3 / Azure Blob / compatible object storage
```

- [ ] Preserve distinct workspace semantics rather than treating mutable session workspace as an ordinary immutable blob.

- [ ] Add lifecycle/retention policy where required.

- [ ] Add production secret management when deployment requires it.

Potential implementations:

```text
cloud secret manager
Vault-like service
platform-managed secrets
```

Credentials must remain outside model context and normal Admin projections.

---

## P10D — Hosted operations

When externally hosted:

- [ ] TLS/reverse-proxy/ingress hardening.

- [ ] Production logging/metrics/tracing backend.

- [ ] OpenTelemetry export where useful.

- [ ] Alerting for meaningful failure conditions.

- [ ] Backup + restore drills.

- [ ] Deployment rollback strategy.

- [ ] Rate limits/resource quotas where required.

- [ ] Public-hosting security review.

- [ ] Expand P7 Admin history into enterprise-grade audit/compliance only where required.

---

## P10E — Distributed scaling only when load requires it

Do not assume this is required for the first production deployment.

Potential future needs:

```text
multiple API nodes
multi-node SignalR
distributed scheduler/work claims
worker pools
shared ephemeral coordination
horizontal Session Runtime
```

Only then evaluate:

- [ ] Redis or another coordination/cache technology where a specific use case exists.

- [ ] SignalR backplane when multiple realtime nodes require it.

- [ ] Distributed trigger/work claiming/leases.

- [ ] Queue/broker infrastructure when durable DB-backed work is no longer sufficient.

- [ ] Horizontal/distributed Session Runtime only when single-process ownership is a measured bottleneck.

- [ ] Kubernetes only when deployment/scale/isolation requirements justify it.

Avoid:

```text
Redis because sessions exist
Kafka because events exist
Kubernetes because Docker exists
microservices because modules exist
```

---

# Deferred / optional provider work

These items do not block P7.5.

## Real P3 provider verification

- [ ] Revisit the known Real/OpenRouter historical-image reread gap separately from architecture work.

Keep it bounded, credential-gated, provider-specific, and outside default CI.

## Hosted voice verification

- [ ] Run **HOSTED-04**: one actual non-Synthetic end-to-end Voice smoke using an explicitly selected hosted configuration.

## Realtime hosted STT

- [ ] Finish or replace realtime `OpenAiSpeechRecognizer` only when a concrete need exists.

## Native speech-to-speech / realtime reasoning

- [ ] Revisit only if measured latency/quality demonstrates that the composed `STT → text model → TTS` pipeline is insufficient.

---

# Continuous quality work

- [ ] Keep Synthetic/offline verification as the default deterministic path.

- [ ] Keep `main` green before beginning the next architectural slice.

- [ ] Add regression coverage with every lifecycle, response, speech, multimodal, tool, memory, identity, trigger, background-work, definition, publishing, Admin, and infrastructure change.

- [ ] Maintain Playwright coverage for meaningful user-visible workflows.

- [ ] Make asynchronous/race-sensitive tests deterministic.

Prefer explicit gates/events and `TimeProvider` over wall-clock sleeps.

- [ ] Keep hosted-provider tests explicitly opt-in unless a provider-specific requirement is under acceptance.

- [ ] Keep hosted infrastructure optional for local development.

- [ ] Keep docs synchronized with observed implementation.

- [ ] Keep TODO focused on current/future work.

Detailed historical gate counts, correction narratives, and workflow evidence belong in `docs/reports`.

---

# Implemented baseline

Keep this compact. It is orientation, not another roadmap.

- [x] .NET 10 / C# 14 modular monolith.
- [x] React / Vite / TypeScript frontend.
- [x] Ant Design v6 conversation-first UI.
- [x] SignalR + MessagePack realtime transport.
- [x] Synthetic deterministic test/development profile.
- [x] OpenAI-compatible streaming model adapter and OpenRouter configuration.
- [x] Trusted model catalog with per-session model/reasoning selection.
- [x] Provider reasoning separated from public assistant output.
- [x] Validated display/speech response semantics.
- [x] First-class transient progress semantics.
- [x] Durable multi-session catalog/history and paginated conversation history.
- [x] Bounded runtime restore.
- [x] Durable accepted conversation-turn execution / observer reattachment behavior.
- [x] Session purpose/lifecycle/completion policy and terminal read-only history.
- [x] Trusted owner profile API/persistence.
- [x] Session attachments/artifacts and current/historical image access.
- [x] Capability-aware model/image admission.
- [x] Repeated proactive initiative.
- [x] Explicit queue vs intentional steer, Stop, interruption reasons, detach grace, reattach recovery, and approval replay.
- [x] Session-owned isolated workspaces.
- [x] Typed bounded tool execution.
- [x] Workspace, knowledge/attachment, artifact, sandbox, web, generic HTTP, and email tools.
- [x] Live approval UI/protocol.
- [x] Synthetic / Browser / hosted-compatible speech abstractions.
- [x] Voice interruption and heard/received tracking.
- [x] Impeccable UI skill integration.
- [x] Shared `develop` and `document` composition skills.
- [x] P4 semantic compaction and durable summary boundary.
- [x] P4 structured session memory.
- [x] Durable Agent Definition vs Agent Instance separation.
- [x] IdentityUser/User learned-memory scopes and layered prompt composition.
- [x] Durable trigger registration/scheduler — P5 frozen on `4bbc0c1`.
- [x] Durable triggered/background execution — P6 frozen on `30adaeb`.
- [x] P7 Harness Admin: draft/version/publish, resources, instances/persona, memory/automation administration, validation/evaluation, history/rollback.

---

# Next implementation item

**P8** is next.

P7.5 is frozen on `b112198`. Hosted workflow [`36348699304`](https://github.com/trannamtrung1st/agent-core/actions/runs/36348699304) is green on that SHA. The closure report is `docs/reports/p7.5-freeze-candidate.md`. Start P8 from one concrete provider need. Do not add a universal provider interface.

Do **not** begin by replacing SQLite, Docker, local storage, the single-process scheduler, or the modular monolith.
