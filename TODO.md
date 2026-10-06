# TODO

Living roadmap: current status, active requirements, future dependency order, and cross-phase invariants.

Detailed architecture and behavior live in `/docs`. Historical implementation and freeze evidence live in `docs/reports/`. This file does not duplicate either.

Reviewed against `main` through **`d033bc6198f856e9dd007df7b6235c413eafa2c0`** on **2026-10-04**. Pre-P8 bounded follow-up after P7.7 is **closed** (hosted Synthetic [**`36667172857`**](https://github.com/trannamtrung1st/agent-core/actions/runs/36667172857) green on **`1cadf46`**). **P8** remains **frozen** on **`ca3eb23`** (hosted Synthetic [**`36696902928`**](https://github.com/trannamtrung1st/agent-core/actions/runs/36696902928) green). **Post-freeze P8 follow-up is closed** on **`c9aec29`** (provider-contract **`6fda4c5`**, CI stabilization **`c9aec29`**, hosted Synthetic [**`36745126226`**](https://github.com/trannamtrung1st/agent-core/actions/runs/36745126226) green). That closure does not move or reopen the P8 freeze. **P8.5** is **closed** on **`1461567`** (hosted Synthetic [**`36770385588`**](https://github.com/trannamtrung1st/agent-core/actions/runs/36770385588) green). Bounded post-closure corrections through **`0d1cfdd`** (assistant routing, truncation budgets, Playwright sync) are **closed** on hosted Synthetic [**`36807383922`**](https://github.com/trannamtrung1st/agent-core/actions/runs/36807383922) green. A second bounded post-closure chain through **`fff7761`** (SSE idle at `ReadAsync`, Skill editor comma draft, no-chat Agent Step admission and execution state, response-function `displayText`) is **closed** on hosted Synthetic [**`36818061198`**](https://github.com/trannamtrung1st/agent-core/actions/runs/36818061198) green. A third bounded post-closure stabilization chain through **`aedea70`** (realtime and history convergence, durable `session.ready` paging with live streaming overlay, direct-user response contract and inspectable failures, gated and budgeted `app.message.send`, test and CI synchronization) is **closed** on hosted Synthetic [**`36851267423`**](https://github.com/trannamtrung1st/agent-core/actions/runs/36851267423) green; [appendix](docs/reports/p8.5-freeze-candidate.md). Real probes on DeepSeek V4.1 Flash and GPT-4o mini showed substantive work, intermediate application messages, and terminal `chat.respond`. None of these chains moves the P8 or P8.5 freeze SHAs. **P9 — Visible browser** is **closed** on `bba1de4` ([closure report](docs/reports/p9-freeze-candidate.md); hosted Synthetic [`36890525463`](https://github.com/trannamtrung1st/agent-core/actions/runs/36890525463) green). **Post-closure browser runtime hardening** is **closed** on **`3400d64`** (hosted Synthetic [**`36981513602`**](https://github.com/trannamtrung1st/agent-core/actions/runs/36981513602) green); that chain does not move the P9 freeze SHA. **P9.5 — Proactive Secretary / Real Assistant Demo** remains **closed** on `1012653`; post-closure enhancements through the reviewed HEAD do not move or reopen P9 or P9.5. **P9.6 — Visual Browser, Reactive Triggers & Deterministic Unattended Execution** is **closed** on `d033bc61` ([closure report](docs/reports/p9.6-freeze-candidate.md); hosted Synthetic [`37187663286`](https://github.com/trannamtrung1st/agent-core/actions/runs/37187663286) green), including nopCommerce `INopStartup` consumer registration and native `order-14-placed` emission on guest checkout **order 14**.

Closure reports through P7.7 still say the next phase was P8. That sentence records the handoff at freeze time, when P8 meant harness/platform extensibility. Those reports were not rewritten. In this TODO that work is P9.

---

# Current state

## Frozen phases

Reopen a frozen phase only for a reproducible regression, or for a new product requirement that belongs in that phase rather than a later one. Do not rewrite closure evidence to match later numbering.

| Phase            | Freeze                                                                             | Hosted gate                               | Report                                            |
| ---------------- | ---------------------------------------------------------------------------------- | ----------------------------------------- | ------------------------------------------------- |
| P1               | `dceaccb`                                                                          | —                                         | phase history in `docs/08-development-roadmap.md` |
| P2               | `47d6ff6`                                                                          | workflow `35552740853` green              | roadmap / implementation plan                     |
| P3               | `4dbb920`                                                                          | —                                         | roadmap / implementation plan                     |
| P4               | `822028f`                                                                          | workflow `35806764609` green              | `docs/reports/p4-freeze-candidate.md`             |
| P5               | `4bbc0c1`                                                                          | workflow `35954811544` green              | `docs/reports/p5-freeze-candidate.md`             |
| P6               | `30adaeb`                                                                          | workflow `36085265506` green              | implementation plan                               |
| P7 Harness Admin | follow-up tree `2acb1a8`; last harness behavior `1090535`; canonical W08 `f4107d7` | workflow `36239630112` green on `f4107d7` | `docs/reports/p7-freeze-candidate.md`             |
| P7.5             | `70a5720`                                                                          | workflow `36368766449` green              | `docs/reports/p7.5-freeze-candidate.md`           |
| P7.6             | `17d89ae`                                                                          | workflow `36427670239` green              | `docs/reports/p7.6-freeze-candidate.md`           |
| P7.7             | `40a1d92`                                                                          | workflow `36594702224` green              | `docs/reports/p7.7-freeze-candidate.md`           |

P7 owns Harness Admin W01–W08 only. Observer durability for `ConversationTurnExecution` is closed session bookkeeping, not part of the Admin contract: last behavior `93cb2ab`, stabilization `2c4d46f`, workflow `36336971087` green. Do not merge that appendix into Harness Admin.

P7.6 does not redefine P7 or P7.5. P7.7 does not reopen P7.6. The Admin lifecycle follow-up in `docs/reports/p76-admin-lifecycle-followup.md` is finished follow-up evidence. It is not a new freeze, it is not hosted-green, and it does not move the P7.6 SHA. Bounded work after P7.7 freeze **`40a1d92`** (diagnostics, learned-memory admission, memory receipts) is **closed** on **`1cadf46`** with hosted Synthetic [**`36667172857`**](https://github.com/trannamtrung1st/agent-core/actions/runs/36667172857) green. Evidence: `docs/reports/p7.7-diagnostic-followup.md`. That follow-up does not move the P7.7 SHA. The hosted green gate on **`40a1d92`** alone does not cover those corrections.

### What the frozen phases still mean

**P7 — Harness Admin.** Draft, validate, diff, publish an immutable Definition version, manage a durable Agent Instance and persona, inspect or reset eligible learned memory, configure or revoke eligible automation, read non-secret effective configuration, and review append-only Admin history. Admin is a product surface separate from Chat. It is not a run-control console and it does not own the Session Runtime mailbox.

**P7.5 — Architecture consolidation.** Authoring, Runtime, and Operations are responsibilities inside the modular monolith, not new projects. Effective configuration is shared resolution primitives plus use-case composition, not one universal runtime object. The dev stack stayed SQLite, local filesystem, Docker, and one process. Evidence: `docs/03-system-architecture.md`, `docs/15-persistence-and-configuration.md`, `docs/reports/p7.5-freeze-candidate.md`.

**P7.6 — Admin usability.** The existing Definition can be authored through Form or Advanced JSON on one revisioned draft. New Definition and New Instance flows are explicit. Knowledge Sources bind to Knowledge resources. Published versions stay immutable. Evidence: `docs/reports/p7.6-freeze-candidate.md`.

**P7.7 — Operational diagnosability.** Delivered a server-owned safe `DiagnosticId`, structured server-side failure logging, optional same-hub JSON realtime mode, and unchanged scheduling ownership. MessagePack stays canonical. Evidence: `docs/reports/p7.7-freeze-candidate.md`. Post-freeze diagnostics, learned-memory admission, and memory-receipt corrections are **closed** follow-up evidence in `docs/reports/p7.7-diagnostic-followup.md` (**`1cadf46`**, workflow **`36667172857`** green), not a new freeze.

Retained diagnostic rules, because later phases must reuse them:

```text
DiagnosticId    = one specific failure occurrence safe to show or copy
CorrelationId   = existing logical operation / turn / causal flow
TraceId         = infrastructure trace id only when a real Activity exists
```

Clients cannot supply trusted diagnostic, correlation, or causation identity. JSON realtime is a diagnostic transport on the same hub and the same event contract. It is not a second protocol. A missed recurrence still coalesces to the latest due occurrence. `SkipMissed` and `CatchUp` are not implemented.

**Pre-P8 memory admission.** Conversation memory is agent-proposed and Core-admitted. Proposals are staged with semantic output and admitted once when that response completes successfully. A memory policy enables scope; it does not store every proposal. Phrase matching is not the write authority. A reliable proposal channel is native structured output or the adapter response function. `[[memory:...]]` is best effort. Without that channel, recall still works and autonomous creation is unavailable. Model source is `userExplicit` or `agentInferred`. The admission receipt is controller-owned metadata on the assistant envelope. It is not appended to display text or speech. Receipt **`scopes[]`** lists memory layers successfully established or confirmed by admission (`session`, `identityUser`, `user`); **`outcome`** reflects successful session-layer admission and stays coherent with partial promotion. User-explicit failure stays visible; inferred outcomes stay quiet. Admission rejects credentials and blob-like content. It does not yet classify sensitive personal attributes. The open choice is never to store them, to store them only when the user explicitly asks, or to follow application policy. The current lean is explicit request only. That taxonomy is not implemented. Bounded follow-up through **`1cadf46`** is closed in `docs/reports/p7.7-diagnostic-followup.md`. This does not start P8.

## Active phase

**P8 — Agent Execution Contract, Application Actions & Skills** is **frozen** on `ca3eb23` (workflow [`36696902928`](https://github.com/trannamtrung1st/agent-core/actions/runs/36696902928) green). Post-freeze bounded follow-up (provider contract **`6fda4c5`**, CI stabilization **`c9aec29`**) is **closed** on hosted Synthetic [**`36745126226`**](https://github.com/trannamtrung1st/agent-core/actions/runs/36745126226) green. See `docs/reports/p8-freeze-candidate.md` (freeze narrative + appendix).

**P8.5 — Application Messaging & Dynamic Skill Activation** is **closed** on `1461567` ([closure report](docs/reports/p8.5-freeze-candidate.md); hosted Synthetic [`36770385588`](https://github.com/trannamtrung1st/agent-core/actions/runs/36770385588) green). `app.message.send` and `skills.load` continue the same bounded user-turn execution. They do not change the P8 freeze. Post-closure corrections through **`aedea70`** are recorded in that report's appendix (truncation **`36807383922`** on **`0d1cfdd`**, SSE/no-chat/editor **`36818061198`** on **`fff7761`**, stabilization **`36851267423`** on **`aedea70`**); they do not move the P8.5 closure SHA. **P9 — Visible browser** is **closed** on `bba1de4` ([closure report](docs/reports/p9-freeze-candidate.md); hosted Synthetic [`36890525463`](https://github.com/trannamtrung1st/agent-core/actions/runs/36890525463) green). **Post-closure browser runtime hardening** is **closed** on **`3400d64`** (hosted Synthetic [**`36981513602`**](https://github.com/trannamtrung1st/agent-core/actions/runs/36981513602) green); that chain does not move the P9 freeze SHA. Browser navigation, interaction, and subresource origins are separate host lists. The Record Lookup site is an optional fixture. `target_denied` stays a normal tool result. **P9.5 — Proactive Secretary / Real Assistant Demo** is **closed** on `1012653`. Two post-P9.5 follow-ups stay on that closure: Chat and Admin project one application connection by display name, and browser observations use a bounded settle with `browser.observe` `waitFor` `stable`. Neither follow-up moves the P9 or P9.5 SHAs. The post-P9.5 browser and store-review enhancement is recorded on `6f6ff42` (hosted Synthetic [`37128161642`](https://github.com/trannamtrung1st/agent-core/actions/runs/37128161642) green) and does not move `1012653`. **P9.6 — Visual Browser, Reactive Triggers & Deterministic Unattended Execution** is **closed** on `d033bc61` ([closure report](docs/reports/p9.6-freeze-candidate.md); hosted Synthetic [`37187663286`](https://github.com/trannamtrung1st/agent-core/actions/runs/37187663286) green). P10 and P11 stay requirement-triggered. **P9.7 is frozen on behavior SHA `8f5afa00`** ([hosted Synthetic green](https://github.com/trannamtrung1st/agent-core/actions/runs/37224218680)); [final verification](docs/reports/p9.7-final-verification.md) records the full DeepSeek Kubernetes sequence and GPT-4o mini limitations. Historical first-gate evidence is in [its report](docs/reports/p9.7-chat-first-freeze-candidate.md).

---

# Current roadmap

1. **P0–P8 — frozen.** P7.5 is frozen on `70a5720`; P7.6 on `17d89ae`; P7.7 on `40a1d92`; and P8 on `ca3eb23`. Post-P7.7 / pre-P8 bounded follow-up is closed on `1cadf46`. Post-freeze P8 follow-up is **closed** on `c9aec29` (hosted Synthetic **`36745126226`** green).
2. **P8.5 — Application Messaging & Dynamic Skill Activation — closed** on `1461567` (post-closure corrections **closed** on **`aedea70`**, hosted Synthetic [**`36851267423`**](https://github.com/trannamtrung1st/agent-core/actions/runs/36851267423) green).
3. **P9 — Visible browser — closed** on `bba1de4`. Post-closure browser runtime hardening is **closed** on **`3400d64`** (hosted Synthetic [**`36981513602`**](https://github.com/trannamtrung1st/agent-core/actions/runs/36981513602) green); that chain does not move the P9 freeze SHA.
4. **P9.5 — Proactive Secretary / Real Assistant Demo — closed** on `1012653` (hosted Synthetic [`37111979501`](https://github.com/trannamtrung1st/agent-core/actions/runs/37111979501) green). Post-closure follow-ups project one application connection by display name and add a bounded browser settle. Neither moves the P9 or P9.5 SHAs. The post-P9.5 browser and store-review enhancement is recorded on `6f6ff42` (hosted Synthetic [`37128161642`](https://github.com/trannamtrung1st/agent-core/actions/runs/37128161642) green) and does not move `1012653`.
5. **P9.6 — Visual Browser, Reactive Triggers & Deterministic Unattended Execution — closed** on `d033bc61` ([closure report](docs/reports/p9.6-freeze-candidate.md); hosted Synthetic [`37187663286`](https://github.com/trannamtrung1st/agent-core/actions/runs/37187663286) green). Includes source-owned `order.placed` ingress, subscriber-snapshot delivery-ledger fan-out and recovery, Browser v1, execution-model pin, unattended browser parity, four Secretary modes, and nopCommerce `INopStartup` consumer registration. Native guest checkout **order 14** proved automatic plugin → webhook → delivery → WorkItem with dedupe; unattended completion on the connected profile was proved on an earlier opt-in path in the same report. Optional: one more native order-to-**Completed** on the same chain for investor demo evidence only.
6. **P9.7 — Conversational harness learning — frozen on behavior SHA `8f5afa00`.** Final gates and Real DeepSeek verification passed; stop P9.7 architecture work and stabilize the investor demo. P9.8/P9.9 implementation is tracked in the [continuity candidate report](docs/reports/p9.8-p9.9-freeze-candidate.md).
7. **P9.8 — Session Retrospection / Agent Experience — frozen on behavior SHA `0a3330db`.**
8. **P9.9 — Autonomous Thought Activation / Agent Initiative — frozen on behavior SHA `0a3330db`.**
9. **P10 — Sandbox Evolution — requirement-triggered when the current sandbox is insufficient.**
10. **P11 — Multi-user + Production Infrastructure — requirement-triggered when a real hosting or pilot requirement appears.**

---

# Frozen architectural baseline

These rules constrain P8 and later work. Do not rediscover them only by reading old phase reports.

## Conversation and runtime

- One logical Session owns one conversation.
- One Session Runtime owns mutable conversational state while it is resident.
- Provider callbacks do not mutate runtime state directly. The model does not own runtime transitions.
- Detach and reconnect do not create a second logical conversation.
- Accepted durable work survives browser disconnect.
- Transient live streaming and durable historical state stay distinct.
- Session runtime state is not stored in Agent Definition configuration.
- Provider reasoning stays off display, speech, history, and TTS input.
- Runtime-local timers stay separate from durable scheduling.
- Trigger registration, trigger execution, and later tool authorization stay separate.
- Background work that must outlive Session Runtime uses durable WorkItems.

## Lifecycle

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

Definition upgrade, persona revision, learned-memory reset, and creating or forking an Agent Instance are separate operations. A session resolves the Definition version it actually used. Changing role, application context, definition version, persona, memory scope, or authority is not an identity reset. Definition fields named “identity” are configuration, not the durable continuity record. Ordinary sessions cannot mutate published definition resources.

## Concepts that must stay distinct

```text
Instructions / Standing Instructions
= what the agent should do

Trusted persona / profile
= trusted identity and context

Skills
= procedural know-how
  a requirement does not grant a capability

Knowledge / resources
= authoritative reference material

Learned memory
= what the agent learned

Capabilities / tools
= operations that can potentially be performed

Authority / policy
= what is allowed in this context

Trigger registrations
= durable future event state

WorkItems
= durable execution state

Session workspace
= mutable session-owned files

Application context
= where the identity is participating

Task / session context
= what it is working on now
```

```text
Instructions ≠ Persona
Memory ≠ Instructions
Knowledge ≠ Memory
Skill ≠ tool, credential, permission, resource, binding, or memory
Skill requirements ≠ authority
Tool availability ≠ authority
Trigger registration ≠ permission
Identity ≠ Definition
Identity ≠ Role
Identity ≠ Application
Identity ≠ Session
Identity ≠ Authority
```

Learned memory must not silently become instructions, persona, or authoritative knowledge.

## Tool and security boundary

```text
registered capability
        ↓
contextual availability
        ↓
policy / authorization
        ↓
exact-action approval when required
        ↓
execution
        ↓
result / receipt
```

- Registry, policy, and execution stay separate.
- Registration does not grant permission. Availability does not grant authority.
- Exact-action approval stays exact.
- Credentials, tokens, and secrets stay outside model-visible context, definitions, Admin projections, and ordinary history or logs.
- Execution-time checks are authoritative, including detached and background work.
- Background or unattended execution is not an authorization bypass.
- Admin and first-party Chat use the same security boundary. Neither gets a hidden privileged path.
- Application actions use this boundary. They do not get a second policy.
- Failures reuse P7.7 `DiagnosticId` semantics. Do not add a second error system.

## Scheduling ownership

```text
TriggerRegistration
    ↓
TriggerOccurrence
    ↓
routing
    ↓
WorkItem
    ↓
Agent execution
```

Agent Core owns trigger semantics, ownership, authorization, recurrence meaning, occurrence admission and deduplication, WorkItem lifecycle, approvals, and execution semantics. Hosted services only wake and dispatch. A later scheduler, if a real requirement appears, sits under that model. It does not replace it.

Routing decides whether admitted work runs through a compatible live Session Runtime or a durable WorkItem. It must not accidentally decide which model runs that work. An admitted execution keeps a deterministic model pin across retry, recovery, approval resume, and default changes.

## Workspace and resources

```text
Published definition resources
  immutable / versioned

Session workspace
  mutable, isolated, session-owned
```

Attachments, artifacts, definition resources, and session workspace stay separate stores. Do not add a general cross-session Agent Instance filesystem without a concrete workflow that requires it.

## Development infrastructure

Until a real external pilot or hosting requirement appears, keep:

```text
.NET modular monolith
SQLite
local filesystem
Docker / Compose
one application process
one scheduler / background-worker process
Synthetic deterministic CI
optional hosted model, speech, and integration providers
```

Properties that already belong in the current stack: durable source of truth where required, explicit ownership ids, idempotent or retry-safe side effects, deterministic migrations, cancellation and timeouts, bounded work, secrets outside model context, ports only where a boundary already exists, `GET /health` (`status`, `profile`, `protocolVersion`), structured logs and the existing `AgentCore.Runtime` `ActivitySource` / `Meter`, and no Domain or Application dependence on host paths.

`Observability:OtlpEnabled` defaults false and does not attach an exporter. Do not add PostgreSQL, Redis, a broker, Kubernetes, a distributed scheduler, remote object storage, or a cloud secret manager merely because those categories of data exist.

---

# Product / Architecture North Star

**Status: long-term direction. P8 is the frozen bounded slice on `ca3eb23`. P8.5 is closed on `1461567`. P9 visible browser is closed on `bba1de4`. P9.5 is closed on `1012653`. P9.6 is closed on `d033bc61`. It does not reopen earlier phases. The rest is future guidance.**

This section says why later phases exist. P8, P8.5, and P9 record what those phases implemented and verified. It does not reopen closed phases, widen P8 past its stop condition, or pull P10 or P11 forward.

> Agent Core hosts durable AI identities that can participate in applications, conversations, tasks, and events with scoped memory, capabilities, authority, and working context.

The conversation runtime remains valid. The product direction is to host durable identities that can keep continuity and participate in more than one application. “AI worker” or “digital employee” is product language, not a new architecture layer, and it is not a claim that a general worker platform exists today.

A worker, when the underlying model earns the metaphor, combines a stable identity, persona, standing instructions, skills, memory, knowledge, capabilities, scoped authority, application context, workspaces, tasks, triggers, approvals, and execution history. Agent Core is the identity and runtime layer. Chat, an examiner, a support experience, or another product is an application on that layer.

```text
Durable Agent Identity
        │
        ├── Persona / Identity
        ├── Standing Instructions
        ├── Skills
        ├── Memory
        ├── Knowledge
        └── capability bindings
                  │
            current context
                  │
       Session / Task / Event
                  │
                Agent
                  │
           structured step
                  │
              Controller
                  │
        authorized application actions
```

The same identity may later participate in more than one application. Context and authority change. The identity does not.

```text
same durable identity
       │
       ├── App A context / capabilities
       ├── App B context / capabilities
       └── App C context / capabilities
```

P8 proves the step, controller, and authorized-action path for Agent Core Chat only. It does not prove a general Application Binding schema. The implemented lifecycle remains Definition → immutable Version → Agent Instance → Session. The multi-application diagram above is not implemented.

The current `AgentInstance` is the continuity anchor. Do not merge Definition, persona, memory, and session into one mutable aggregate.

```text
Identity ≠ Definition
Identity ≠ Role
Identity ≠ Application
Identity ≠ Session
Identity ≠ Authority
```

## Application Binding — bounded P9.5 proving slice, broader model future

An Application Binding would describe how one durable identity participates in one application without redefining that identity. Example: Sam in application A is support, in application B is a team member, and in application C is a researcher. A future binding may carry role, application instructions, resources, permitted capabilities, policy, and application-scoped memory, workspace, or triggers where a real workflow needs them.

```text
Definition
→ immutable Version
→ durable identity / Agent Instance
→ Application Binding
→ Session / Task / Event
```

P9.5 may introduce only the minimum durable application connection/binding semantics that authenticated nopCommerce participation proves necessary: application identity/type and base scope, Agent Instance ownership, reference to that instance's browser profile, safe connection status, and connect/reauthenticate/revoke/reset lifecycle. It must not rewrite the Definition, persona, identity-wide state, or expose credentials. Do not generalize that slice into a universal Integration, Plugin, Application Binding, portability, or marketplace model. The post-P9.5 projection uses Application connection, display name, and base URL. Chat projects the display name and status. Admin shows a read-only Application type of nopCommerce because establishment still opens that store's admin sign-in. One connection per Agent Instance stays the limit. A separate post-P9.5 browser settle waits for a quiet visible page and lets `browser.observe` request `waitFor` `stable`. It does not add a nopCommerce-specific wait. Broader persistence waits for another concrete application to prove ownership, mutation, versioning, authorization, and portability requirements. P8 used Chat as the first application adapter and did not build this schema.

## Create, teach, and hire — future product surface

Ordinary users should eventually see a simple surface: create a worker, teach it, start from a prepared worker, or hire one into an organization. They should not have to operate prompts, MCP, provider configuration, memory-scope ids, policy evaluators, or WorkItems directly.

“Teach” means configure and improve a worker through standing instructions, skills, knowledge, examples, policy, capabilities, memory, evaluations, and routines. It does not mean model fine-tuning. A simpler UI must not collapse those lifecycles internally.

A prepared worker package must not share one customer’s identity, memory, authority, browser state, or working history with another customer. Hiring instantiates a new durable identity. Skills in a future package describe procedure. They do not grant capabilities or authority. Do not create a marketplace or package format until a real requirement defines trust, versioning, provenance, installation, and ownership.

## Memory, workspace, and durable work — future scopes

Memory scopes, if portability requires them, stay explicit: identity, relationship, application, task, and session. Memory does not automatically cross applications.

Workspace scopes, if a concrete workflow requires them, stay separate lifecycles: identity/home, application, task, and session. They are not one shared filesystem. Definition resources, attachments, artifacts, and mutable workspace data stay distinct. `SessionWorkspace` remains the only implemented mutable workspace.

Unattended work extends Trigger, Occurrence, and WorkItem. It is not a permanent `while(true)` model loop. The shape is a goal or task, a WorkItem, an authorized action, a checkpoint, then continue, wait for an event or approval, or complete. Waiting must not require a live model call or browser session. Delegation between identities, if it is ever required, carries identity, authority, provenance, and work ownership. It is not an agent swarm.

P8 makes Chat one authorized application action on this path. That proof is frozen on `ca3eb23`. Multi-agent coordination stays future.

## Browser and native integration — strategic direction

> Browser automation is the broad compatibility path; native integrations are the optimized path.

A bounded browser capability can reach many existing web applications before a dedicated integration exists for each one. Where a capability becomes important and repeatable, a native provider is the better constraint. Equivalent work should not change identity or task meaning just because the provider is a browser.

Browser use is access to logged-in applications. Availability is not authority. Do not share one authenticated browser profile across identities or applications. Do not persist every click, selector, or DOM observation as product history. Durable receipts are for meaningful observations, approvals, and side effects.

P9 closed the first concrete provider on `bba1de4`. Agent Core exposes a provider-neutral browser capability owned by Application; Infrastructure implements it with Playwright. The model does not receive raw Playwright. At that freeze, one browser context belonged to one Session and the visible proof was the loopback AC-1042 journey, not navigation to arbitrary public sites. Authenticated profiles and cookie retention across sessions were unresolved at the freeze; the post-closure `PersistentAgent` behavior recorded below now provides them for local Real/demo use without moving the P9 SHA. Production tenant isolation remains future.

---

# Infrastructure stance before production

The development stack in the architectural baseline stays the default. Add a production dependency only when it solves a real hosting or pilot problem. P11 is that phase. Closed P8.5 and P9 work must not start it early.

Each of these waits for its own trigger: PostgreSQL, Redis, a broker, Kubernetes, a service mesh, a distributed cache or scheduler, remote object storage, a cloud secret manager, multi-node SignalR, and microservice splits.

---

# P8 — Agent Execution Contract, Application Actions & Skills

**Status: frozen** on `ca3eb23` (workflow [`36696902928`](https://github.com/trannamtrung1st/agent-core/actions/runs/36696902928) green). Post-freeze provider-contract correction **`6fda4c5`**, CI stabilization **`c9aec29`**, and hosted closure workflow [**`36745126226`**](https://github.com/trannamtrung1st/agent-core/actions/runs/36745126226) green are recorded in the report appendix; they do not move the freeze SHA. Closure report: `docs/reports/p8-freeze-candidate.md`.

P7.7 stays frozen on `40a1d92` (workflow `36594702224` green). Post-freeze / pre-P8 bounded follow-up is **closed** on `1cadf46` (workflow [`36667172857`](https://github.com/trannamtrung1st/agent-core/actions/runs/36667172857) green). P8 does not reopen P7.7.

## Goal

The current path is conversation-output-centric. A trigger and context reach `IAgentBrain`, which may return `AgentDecision.Speak` with a `ModelRequest`. Generation then yields a `ModelSemanticResponse` (`DisplayText`, `Speech`, `Blocks`) into the Chat pipeline. That path is the conversational MVP. Keep it until a narrower path proves parity.

P8 introduces a provider-neutral execution result so controller decisions and requested application effects are no longer the same thing as “the assistant spoke.”

```text
Conversation / Task / Event / Application input
                    ↓
             Durable Agent Identity
                    ↓
               agent / model
                    ↓
       normalized structured Agent Step
                    ↓
             Agent Core controller
              ┌─────┴─────┐
              ↓           ↓
      runtime disposition   requested actions
                              ↓
                    capability / tool policy
                              ↓
                         application
```

`AgentStep` is the conceptual name, not a required class name. Choose the domain name from the existing lifecycle during implementation.

Chat is the first application that must prove the path. Browser was deferred to P9 and is closed there. Sandbox and production infrastructure are P10 and P11.

## P8A — Provider-neutral Agent Step

The result has two parts:

```text
1. controller / runtime disposition
2. requested actions / effects
```

Controller semantics are not external actions. Do not register `agentcore.wait`, `agentcore.complete`, or `agentcore.continue` as tools. Current lifecycle evidence does not justify that.

Existing decisions already separate a proposal from runtime ownership:

- `StaySilent` may carry `NextWaitMs`. The runtime owns the wait.
- `Speak` carries a `ModelRequest`. It does not itself append history or start playback.
- `RequestDeactivate` asks the runtime to leave an active posture.
- `ContinueSession` and `RequestComplete` propose completion. Session lifecycle (`Active`, `Paused`, `Completed`, `Expired`, `Cancelled`, `Ended`) stays runtime-owned.
- WorkItem already distinguishes `Running`, `WaitingForApproval`, `WaitingToRetry`, `Completed`, `Failed`, and `Cancelled`.

Disposition families follow those meanings. Exact identifiers are an implementation choice:

```text
Continue
  more work is expected in this activation

Wait
  pause for a person, event, approval, retry time, or silence policy

Complete
  this activation or goal is finished

Cannot continue
  blocked or failed, and must not be recorded as success
```

Requested actions are effects the controller may authorize. They are not a second encoding of Continue, Wait, or Complete.

The controller remains authoritative for lifecycle, continuation, completion, waiting, cancellation, stale-result rejection, existing retry behavior, durable execution, WorkItem checkpoints, policy, approval, and failure handling. The model proposes a step. It must not mutate Session Runtime state. Provider callbacks still must not own mutable runtime state.

### Structured output stays at the edge

Use structured model output where the contract needs it. OpenAI structured output, OpenAI tool calls, OpenRouter behavior, MCP, and provider JSON schemas are provider protocols, not the Agent Core model.

```text
provider-specific model protocol
             ↓
Infrastructure / provider adapter
             ↓
normalized Agent Step
             ↓
Application / controller
```

`ModelCapabilities.StructuredOutput`, `ModelToolCall`, and `ModelToolDefinition` may participate in an adapter. They do not define the architecture. Providers may satisfy the same contract by different mechanisms.

Do not put chain-of-thought or hidden reasoning on the Agent Step. `ModelReasoningDelta` stays private.

- [ ] Carry a runtime disposition and zero or more requested actions.
- [ ] Normalize provider structured output and tool calls at the Infrastructure edge.
- [ ] Reject invalid or malformed provider results before they become transitions or effects.
- [ ] Keep disposition inside the controller.
- [ ] Keep provider reasoning out of the step, history, and user-visible output.

## P8B — Chat is the first application action

Agent output is not inherently a Chat response. Interaction with an application is an authorized capability executed through Agent Core. Chat is the migration proof, not disposable legacy.

```text
user message
    ↓
Agent Core Chat input
    ↓
Agent
    ↓
structured Agent Step
    ↓
requested Chat action
    ↓
controller / policy
    ↓
Chat application adapter
    ↓
user-visible output
```

Name actions in the existing `ToolCatalog` style (`knowledge.retrieve`, `email.send`). These names are examples:

```text
chat.send_message
chat.present_artifact
chat.request_input
```

Prove first the action that preserves today’s conversational response: display text, speech, and blocks. Add another Chat action only when current Chat behavior needs it.

A later application may expose different actions, such as `teams.reply_thread` or `exam.present_question`, without creating a different identity. P8 does not implement those applications, and it does not implement Application Binding persistence.

### Streaming is delivery

Do not turn each text delta into a tool execution.

```text
semantic application action
        ↓
delivery begins
        ↓
transient streaming deltas / progress
        ↓
terminal durable result
```

`ModelTextDelta` and `ModelDisplayDelta` stay transient projections. `ModelSemanticResponseReady` is the current terminal semantic envelope. One semantic source of truth, not a protocol per token.

MessagePack remains the default realtime transport. JSON stays the optional diagnostic transport with the same ordering, reconnect, and response-identity rules.

### Migration

Preserve text, voice, DisplayText versus Speech, streaming, interrupt, Stop, queue versus steer, detach and reconnect, durable accepted work, history, heard and received semantics, attachments and images, tools, approval, memory, semantic compaction, triggers, WorkItems, and diagnostic failures.

Treat `AgentDecision.Speak`, `ModelSemanticResponse`, and `AssistantSemanticProjection` as migration inputs. Do not delete or broadly rename them only because the later abstraction is clearer.

```text
model / provider
    ↓
existing semantic response machinery
    ↓
normalized Agent Step / application action
    ↓
existing Chat delivery pipeline
```

Use another narrow transition only when implementation evidence supports it. Remove the old semantic path only after parity evidence is green.

- [ ] Deliver today’s Chat response as an authorized application action.
- [ ] Keep transient streaming and durable history distinct.
- [ ] Prove Chat parity before removing the old semantic path.
- [ ] Leave Application Binding unimplemented.

## P8C — Skills

Skills are the missing first-class concept. They are procedural know-how for a kind of work. They are not tools, credentials, permissions, knowledge resources, application bindings, or learned memory.

```text
Skill requires capability
≠
Skill grants capability
```

A skill may name a purpose, a procedure, required capability identities, and optional resource references. A refund-handling example might require something like `orders.read`, `refund.execute`, and `chat.send_message`, and might reference a refund-policy resource. Those names are illustrative. Use real catalog identities when a skill is authored. Required references may be validated. They never authorize execution. Skill instructions stay below Agent Core runtime and security policy. User or untrusted application content cannot silently redefine a skill.

### Version them with the Definition

Do not build an independent Skill platform in P8.

```text
AgentDefinitionCandidate
    Skills[]
        ↓ publish
immutable AgentDefinition version
    Skills[] frozen with it
```

A minimal skill is enough until authoring pressure says otherwise:

```text
id
name
description
instructions / procedure
activation hints
required capability identities
optional resource references
```

Do not add `SkillDefinition`, `SkillDefinitionVersion`, `SkillInstance`, `SkillInstallation`, `SkillPackage`, a Skill registry, a Skill marketplace, a dependency graph, or a Skill permission system. Extract an independent lifecycle only when a real requirement needs one skill version shared across many Definitions.

### Activate a relevant subset

A Definition may contain many skills. Do not inject all of them into every model request.

```text
Available Skills
       ↓
trigger / task / application / session context
       ↓
active Skills
       ↓
trusted model context
```

Example: support-triage, refund-handling, meeting-preparation, and invoice-review may all be available, while a refund conversation activates only the first two.

Use the simplest inspectable mechanism the real Chat and trigger cases support. No vector search, marketplace, or autonomous planner is required. Active selection must be testable. Inactive skills must not consume context budget without a reason. Activation does not grant tools or authority.

- [ ] Drafts can carry multiple skills. Publishing freezes them on the Definition version.
- [ ] Reject duplicate skill identity, invalid required fields, a missing referenced capability or required resource, and invalid activation configuration.
- [ ] Select active skills from the current context and leave inactive skills out of that request’s trusted context.
- [ ] Record which skills were active so the choice can be tested.
- [ ] Prove that declaring a required capability does not grant it.

## P8D — Persona and standing instructions

Do not merge `SystemInstructions` into Persona.

```text
Instructions
= what the agent should do

Trusted persona / profile
= trusted identity and context
```

`SystemInstructions` is the existing provider-facing property. “System” is largely a provider message-authority concept, not the whole identity model. Do not rename it unless implementation of this contract genuinely requires that rename.

Trusted context is composed from distinct sources: Agent Core runtime and security rules, persona, standing instructions, goals or role, application context, active skills, available capabilities and effective authority, knowledge, memory, task or session context, and the conversation or event input. Some of these may later be serialized into a provider `system` message. They remain separate concepts. Runtime and security rules outrank skill instructions and untrusted input.

## P8E — Admin authoring

Author skills inside the current Definition lifecycle. Do not create a separate Skills product.

The shipped Admin surface is Instructions, Capabilities, Resources, Identity, Memory, Automation, and Test & Publish. A reasonable extension, decided against the current editor, is a Skills area on the Definition for the list, the editor, capability requirements, and resource references, next to the existing sections.

Reuse draft validation, evaluation, diff, and immutable publication. There is no parallel Skill publish flow.

- [ ] Author skills on the Definition draft.
- [ ] Publish them only by publishing the Definition version.

## P8F — Security

```text
Agent Step requests an action
        ↓
resolve the registered capability
        ↓
effective contextual availability
        ↓
policy / authorization
        ↓
exact-action approval when required
        ↓
execution
        ↓
result / receipt
        ↓
continuation or completion
```

This is the baseline tool boundary applied to application actions. Chat does not bypass it. Terminal failures carry the existing diagnostic id.

- [ ] Dispatch application actions through registry, policy, and execution.
- [ ] Recheck authority at execution time, including detached and background work.

## P8G — Verification and documentation

Deterministic coverage should include:

```text
provider result → normalized Agent Step
structured-result validation
invalid or malformed provider result
controller disposition
application-action dispatch, policy, and approval
diagnostic propagation

Definition skill round-trip
draft editing and immutable published skills
multiple skills, activation, and inactive exclusion
required capability reference does not grant the capability
skill / resource binding

Chat, streaming, and voice parity
DisplayText / Speech
interruption, queue / steer, Stop
detach / reconnect, history / reload
attachments / images
tools, approval, memory, compaction
triggered and background WorkItems
diagnostics
```

- [ ] Add Synthetic end-to-end coverage that the existing Chat application works through the new semantic path.
- [ ] Close on Synthetic evidence. Do not require hosted model credentials when Synthetic can prove the contract.
- [ ] Keep live provider structured-output checks opt-in.
- [ ] Run the deterministic, backend, frontend, Playwright, and Compose gates that the touched behavior requires.

If Skills or Definition UI changes: implement the behavior, review the observed UI with the Impeccable workflow, polish desktop and narrow viewports, and verify keyboard, focus, error, loading, and validation before synchronizing design context.

Update a canonical doc only when its contract actually changes. Likely owners are `docs/03-system-architecture.md`, `docs/04-backend-interfaces.md`, `docs/12-backend-implementation-spec.md`, `docs/13-frontend-implementation-spec.md`, `docs/15-persistence-and-configuration.md`, `docs/17-observability-and-operations.md`, and `docs/18-implementation-plan.md`. Update `docs/10-technology-decisions.md` only if a real decision changes. Update `.agents/context/PRODUCT.md` and `DESIGN.md` only to the degree completed P8 behavior justifies. Do not rewrite the product context into a claim that a general autonomous platform already exists.

- [ ] Produce a bounded P8 closure report when implementation is complete. This roadmap is not that report.

## P8 non-goals

P8 does not include browser automation as its main goal, a universal provider or plugin abstraction, MCP as the internal semantic protocol, provider structured output as a Domain contract, chain-of-thought persistence, Application Binding persistence, an application or Skill marketplace, an independently versioned global Skill platform, a visual workflow builder, multi-agent orchestration, an arbitrary autonomous loop, or a persistent general Agent Instance filesystem.

It also does not include the deferred infrastructure in the section below. Chat must not become a privileged bypass of tool policy. Streaming must not become one tool call per delta.

## P8 stop condition

P8 is complete when Agent Core owns a provider-neutral structured execution contract; provider-native structured output and tool calls are normalized at the edge; controller disposition is distinct from requested external actions; the model does not own runtime transitions; application interaction goes through authorized capabilities; Chat is the first application proving that model; streaming stays a delivery concern; existing text, voice, realtime, and durable conversation behavior remains intact; Definitions can contain multiple typed skills frozen with the published version; skills represent procedure rather than permission and can require capabilities or resources without granting them; relevant skills can be activated without injecting every skill into every request; persona, standing instructions, skills, knowledge, capabilities, authority, memory, application context, and task context stay distinct; deterministic and Synthetic gates are green; and documentation matches observed Admin and Chat behavior.

P8.5 consumes this frozen contract. It does not move P8 evidence. P9 waited for P8.5 and reused current-application messaging and dynamic Skill activation. It did not define them. P9 is closed on `bba1de4`.

---

# P8.5 — Application Messaging & Dynamic Skill Activation

**Status: closed** on `1461567`. Hosted Synthetic [`36770385588`](https://github.com/trannamtrung1st/agent-core/actions/runs/36770385588) is green. Evidence: [p8.5-freeze-candidate.md](docs/reports/p8.5-freeze-candidate.md). P9 visible browser is closed on `bba1de4`.

P8.5 sits between the frozen P8 contract and P9 platform work. A tool-capable user turn can send one bounded intermediate message to the current session and can ask Core to pin another Skill from that definition version. Keyword preload remains an optimization. It is not the only way a Skill becomes active.

## P8.5A — Application messaging

The continuation tool is:

```text
app.message.send(...)
```

Meaning: send a user-visible message into the **trusted current application context**, then return control to the agent so it can continue the same bounded execution.

```text
app.message.send
= intermediate communication; agent continues

chat.respond
= terminal AgentStep application action
```

This is general application messaging, not assistant-specific `report_progress`. System-owned operational states such as `Searching…`, `Running tool…`, and `Reading attachment…` remain separate and do not require this capability.

```text
Agent execution
   ├ tool/browser/workspace invocation
   ├ app.message.send(...)
   ├ continue work
   └ terminal AgentStep
         └ chat.respond(...)
```

Reuse the existing bounded model/tool continuation mechanics. Do not introduce an `AgentStep → model → AgentStep → model → ...` cycle solely for intermediate messaging.

Observed admission:

- The model supplies trimmed `text` only, at most 2000 characters. Routing fields are rejected. Core binds the destination to the current session.
- Arbitrary cross-application or arbitrary-recipient messaging is a separate future capability with separate authorization.
- Reuse existing capability, policy, approval where applicable, execution, cancellation, and response/execution-identity boundaries. Messaging grants no authority and widens no owner, session, or application scope.
- A stale, cancelled, or superseded execution cannot emit a late message. An already admitted message stays. A steered turn drops a buffered send that has not been admitted.
- At most three admitted messages per execution. The same response text, or the same effect key `v1:{executionId}:{toolCallId}`, does not create a second visible entry. That dedupe is exactly-once within one live or recovered durable snapshot. It is not a transactional guarantee if the process stops before session persistence commits.
- The role is `applicationMessage`. It is stored and reloaded, omitted from prompt history, and not spoken. Chat shows it before that response’s assistant answer, with the visible status “Still working”. Runtime activity such as Thinking stays a separate row.

## P8.5B — Dynamic Skill activation

The P8 deterministic keyword selector is useful scaffolding and may remain as a cheap preload optimization. It is not the long-term authority for Skill activation.

Current limitations:

- keyword and synonym sensitivity, with possible false matches;
- compound tasks;
- keyword preload still stops at three; one execution pin can reach four after `skills.load`;
- the relevant Skill may become apparent only after tool or browser observations;
- current activation is focused on user turns.

The observed load path is:

```text
compact available Skill catalog
      ↓
agent
      ↓
skills.load(["skill.id"])
      ↓
Agent Core validates / admits
      ↓
pin active Skill IDs
      ↓
expose trusted full procedures
      ↓
agent continues
```

The tool name is `skills.load`. Before activation, a tool-capable model sees a compact catalog that omits procedure bodies. A model without tools does not receive that catalog or the instruction to call `skills.load`. Full procedure text is injected only for ids Core has pinned.

Core admission ensures:

- the requested Skill exists on the pinned Definition version and is available in the current context;
- the activation/context budget permits it;
- the accepted execution still owns the work;
- active Skill IDs remain inspectable and stable for retry and recovery.

```text
Skill requires capability
≠
Skill grants capability
```

Loading a Skill never adds a tool to an allowlist, grants credentials or application access, satisfies approval, widens owner/session/application scope, or overrides runtime or security policy.

The architecture must permit later loading during the same bounded execution when observations reveal the need. Do not add a mandatory second LLM Skill-router call before every turn.

```text
obvious deterministic match → optional preload
ambiguous or later-discovered need → agent requests Skill
```

Keyword preload still pins at most three Skills. A later `skills.load` on the same execution can raise the pin to four. Aggregate procedure text is capped at 8000 characters, one load accepts at most four ids, and one execution accepts at most two load invocations. Recovery reads the stored pin when a load has already happened and does not run keyword selection again.

Do not add embeddings, vector search, a Skill marketplace, independently versioned shared Skills, or a dedicated Skill-router model in P8.5.

## Why P8.5 precedes P9

Browser/platform work creates longer and less predictable executions. P9 must consume these semantics rather than invent them:

```text
longer execution
   ├ inspect / use tools
   ├ dynamically load relevant Skill
   ├ app.message.send useful intermediate communication
   ├ continue browser / tool work
   └ terminal AgentStep + chat.respond
```

Browser capability must not define messaging or Skill-selection semantics.

## P8.5 non-goals

P8.5 does not include browser implementation, MCP implementation, full Application Binding persistence, cross-app or arbitrary-recipient messaging, Slack/Teams/email messaging implementation, a Skill or plugin marketplace, embeddings/vector Skill retrieval, independently versioned global Skills, multi-agent coordination, an autonomous infinite loop, or production infrastructure.

## P8.5 stop condition

P8.5 closes when an agent can send a bounded intermediate message to the trusted current application context and continue; intermediate messaging is distinct from terminal `chat.respond` and system operational progress; the model cannot choose an unauthorized destination; Skill activation is no longer semantically dependent only on keyword matching; the agent can request Skills as work unfolds; Core validates, budgets, and pins active Skill IDs; Skill loading never grants capability or authority; deterministic matching is only an optional optimization; existing P8 execution and security behavior remains intact; and P9 could start without inventing messaging or Skill-selection semantics. That stop condition is met. P9 is closed on `bba1de4`.

---

# P9 — Harness / platform extensibility

**Status: closed** on `bba1de4`. Hosted Synthetic [`36890525463`](https://github.com/trannamtrung1st/agent-core/actions/runs/36890525463) is green. Evidence: [p9-freeze-candidate.md](docs/reports/p9-freeze-candidate.md). Post-closure browser runtime hardening is **closed** on **`3400d64`** (hosted Synthetic [**`36981513602`**](https://github.com/trannamtrung1st/agent-core/actions/runs/36981513602) green); that chain does not move the P9 freeze SHA. Earlier hosted Synthetic on [`d310b9e`](https://github.com/trannamtrung1st/agent-core/actions/runs/36907229401) and [`8092731`](https://github.com/trannamtrung1st/agent-core/actions/runs/36910823830) remains in the P9 report appendix.

`general-assistant` v11 allowlists `browser.navigate`, `browser.observe`, and `browser.act` and publishes skill `browser.record.lookup`. v12 adds `browser.close`, which shuts the live window and keeps the on-disk profile. Existing agent instances stay on their pinned version until reassociated. Application owns the provider-neutral browser port and target policy. Infrastructure owns a direct Playwright adapter and an isolated loopback fixture. Chat progress for a `browser.*` tool reuses `agent.progress` kind `runningTool` with message `Using browser…`. The scripted Synthetic journey for a user message containing `record AC-1042` sends one application message and one answer, `AC-1042 is In review.` Host configuration separates `NavigationOrigins`, `InteractionOrigins`, and `ResourceOrigins`. Local Real/demo uses `PolicyMode` `OpenWeb`; Synthetic and CI stay `Restricted`. The Record Lookup fixture is optional and is not advertised as the browser home page in OpenWeb. `target_denied` is a normal tool result. `browser.*` is offered only when Browser is enabled and Infrastructure confirms the configured launch target (Playwright Chromium when `Channel` is unset, or the configured `Channel` such as `chrome` on the Real profile). The Docker image leaves Browser disabled because it does not install Chromium. P8 and P8.5 freeze SHAs are unchanged. P10 and P11 stay requirement-triggered.

P9 started after P8.5 closed. It consumed the frozen P8 Agent Step, controller/action boundary, and Chat proof, plus P8.5 bounded current-application messaging and Core-admitted dynamic Skill activation. It did not define those contracts.

The closed provider is direct Playwright, not MCP and not a second tool host. The North Star is not a backlog of extra providers. A second real implementation is what proves which extension seams are needed.

Browser automation is the broad compatibility path. Native integrations are the optimized path. The closed path looks like:

```text
Agent
   ↓
bounded execution
   ├ optionally load a Core-admitted Skill
   ├ optionally message the trusted current application
   ↓
terminal AgentStep
   ↓
Controller
   ↓
authorized browser capability action
   ↓
Playwright browser provider
   ↓
host-configured navigation origin
```

The Record Lookup fixture is the deterministic proof when it is the only navigation origin. Extra exact origins are host configuration, not model authority, and are not general public-site navigation.

MCP remains an optional edge adapter. It is not Agent Core’s semantic center and it is not an authority source. Native Agent Core tools stay first-class. External tools still pass through Agent Core policy. Provider credentials stay outside model context. Provider DTOs stay out of Domain and Application. An extension failure must not corrupt durable agent, session, or WorkItem state. Browser/platform adapters do not choose message destinations, define Skill activation authority, or turn Skill requirements into permissions.

The frozen P9 proof is a Restricted, session-scoped ephemeral browser (`ProfileMode` `EphemeralSession`: one disposable context per Session, released when that conversation ends). The current local Real/demo launch profile is OpenWeb with `ProfileMode` `PersistentAgent`: one on-disk browser profile per `AgentInstanceId` (installed Chrome channel via `Browser__Channel`, not Playwright bundled Chromium alone), reused across chats and restarts under `ProfileRoot`; Synthetic and CI stay Restricted and ephemeral. A persistent profile keeps site sign-in across chats and restarts. It does not keep element refs or promise the same tab. It is not the human user's Chrome profile. Profile reset is a follow-up, not part of this enhancement. Observed runtime: opaque element refs stay session-scoped, no arbitrary selectors or page JavaScript from the model, and no browser tools on detached, background, or occurrence execution. Frontend Playwright and Playwright MCP stay test and development tools. Cross-identity authentication isolation and durable browser receipts beyond the session remain future. The closed provider already refuses a shared authenticated profile and click-level product history.

- [ ] Add an external tool or integration provider only when that second implementation justifies the seam.
- [ ] Define extension identity and versioning only to the degree that extension requires.
- [ ] Reuse shared policy primitives. Do not invent a second policy or one universal configuration object.
- [ ] Revisit a durable Agent Instance workspace only for a concrete cross-session file workflow, with an explicit scope. Do not generalize `SessionWorkspace`.
- [ ] Revisit richer reusable evaluation suites only when multiple provider implementations make them useful.

P9 is not a plugin marketplace, a prepared-worker package system, a visual workflow builder, a multi-agent engine, a distributed job platform, tenant RBAC, Kubernetes, or a microservice split. OpenWeb is a post-closure local/demo host policy. It is not part of the frozen `bba1de4` proof, and it is not a model-controlled unrestricted agent.

---

# P9.5 — Proactive Secretary / Real Assistant Demo

**Status: closed** on `1012653`. Hosted Synthetic [`37111979501`](https://github.com/trannamtrung1st/agent-core/actions/runs/37111979501) is green. This is a vertical product-validation phase, not a new general platform-abstraction phase. P9 remains closed/frozen on `bba1de4`; post-P9 browser enhancements and hardening remain post-closure evidence and do not create a new P9 freeze. Local Journey A session `fcd46bb2-d1fb-4ea1-9560-4b39b0873281` established the storefront postcondition. Journey D is accepted on the recovery result in the freeze report. See [docs/reports/p9.5-freeze-candidate.md](docs/reports/p9.5-freeze-candidate.md).

## Goal

Prove the already-built Agent Core architecture through one coherent real assistant use case:

```text
Durable Secretary Agent Instance
        │
        ├── persona / standing instructions
        ├── learned memory
        ├── Skills
        ├── broad demo capability set
        ├── normal authority / approval policy
        ├── triggers / WorkItems
        │
        └── authenticated nopCommerce participation
                    ↓
              visible browser
```

A viewer should understand that one persistent AI identity can remember context, perform miscellaneous owner-assistant work, wake proactively under bounded rules, and participate in an external application. nopCommerce is the first concrete proving application; it is not the Secretary's identity and must not become a nopCommerce-specific Agent Core architecture.

## Existing behavior P9.5 consumes

Do not reimplement or claim the following as new P9.5 browser work:

- Infrastructure already provides direct `Microsoft.Playwright`; it is not MCP and not a second tool host.
- `general-assistant` v12 already includes `browser.close`, which closes the live browser while preserving the saved Agent Instance profile.
- Real `http-openrouter` already uses `Browser.ProfileMode=PersistentAgent`: one on-disk profile per `AgentInstanceId`, reusable across chats and process restarts, using installed Chrome and never the operator's normal Chrome profile.
- Synthetic and CI remain `EphemeralSession` and `Restricted`; Real/demo uses `OpenWeb`.
- Opaque element refs remain session-scoped even when authentication state persists.
- Browser login, registration, and human-verification boundaries are detected and safely handed back. Credentials, cookies, password values, storage secrets, and equivalent material remain outside model-visible observations.
- Recent ordinary-failure and challenge hardening remains post-P9 closure evidence.
- The existing Docker Agent Core image intentionally does not install Chromium and keeps Browser disabled.

## P9.5A — Prepared Secretary identity

Create a prepared Secretary demo Definition and Agent Instance rather than mutating historical `general-assistant` versions. It has:

- a professional secretary/assistant persona and standing instructions for general owner assistance;
- an appropriate cross-session learned-memory policy;
- relevant procedural Skills, including store operations, product management, promotion/marketing, and order review;
- broad access to the existing registered capability set so the demo can exercise most of Agent Core;
- scheduling and the normal browser capability enabled;
- existing unrelated capabilities such as workspace, attachments, artifacts, knowledge, web, and email where supported.

Skills remain procedures:

```text
Skill requirement ≠ capability grant
```

The same Secretary must remain useful for ordinary general-assistant work unrelated to nopCommerce.

## P9.5B — Broad trusted demo authority without bypass

Define a broad trusted owner/demo policy, provisionally named `TrustedOwnerDemo`, suitable for a disposable owner-controlled environment. It may deliberately grant broad authority and minimize approvals where safe, but it is configuration through the normal boundary:

```text
registered capability
    ↓
contextual availability
    ↓
policy / authorization
    ↓
exact-action approval when required
    ↓
execution
    ↓
result / receipt
```

There is no `if demo: skip authorization` branch. Background and unattended work recheck the same capability, policy, approval, execution, and receipt rules. The model cannot create or widen its own authority.

## P9.5C — nopCommerce demo environment and application relationship

Treat self-hosted nopCommerce as an external application. The primary integration is its visible browser UI. Do not couple Agent Core to nopCommerce internals or add a native nopCommerce API/tool unless implementation evidence shows that the required demo cannot be completed through browser automation.

Provide a reproducible Docker Compose-based nopCommerce environment using the least surprising repository convention: a profile or a narrow demo/nopCommerce overlay. Document simple start, stop, and reset workflows. Seed or bootstrap deterministic products, customers, orders, and stock conditions where useful so the demo can return to a known state.

The nopCommerce database is an external demo-application dependency. Its database choice does not migrate Agent Core from SQLite and does not start P11. Keep Agent Core native in the Real profile with a headed installed Chrome while nopCommerce runs under Compose unless implementation proves browser packaging must change. Do not automatically add Chromium to the existing Agent Core image.

Make nopCommerce an explicit, minimal assistant/application relationship. Define only the connection/binding behavior this workflow proves:

- application identity/type and allowed base URL/application scope;
- owning `AgentInstanceId`;
- reference to that Agent Instance's persistent browser profile, never another identity's profile;
- safe connected/authenticated/available status;
- connect/login, reauthentication, revoke/disconnect, and profile/session reset lifecycle.

Build on `PersistentAgent`; do not reimplement durable authentication. Profile reset is in scope. Passwords, cookies, tokens, browser storage, and equivalent credentials must not enter model context, ordinary history, logs, or ordinary Admin projections. The model receives only bounded trusted state such as “nopCommerce is connected/authenticated/available.” Do not create a universal Integration, Plugin, Application, or marketplace model.

## P9.5D — Bounded proactivity and unattended browser execution

No unrestricted autonomous or infinite loop is added. Durable cross-session proactivity uses existing ownership:

```text
TriggerRegistration
    ↓
TriggerOccurrence
    ↓
routing
    ↓
WorkItem
    ↓
bounded Secretary execution
```

Candidate routines include a morning store review, low-stock inspection, pending-order review, a previously scheduled product/price change, owner notification when attention is needed, and quiet completion when nothing useful needs reporting. Existing finite conversational initiative may remain, but schedules and WorkItems are the primary durable mechanism.

P9.5 must explicitly close the current gap that browser tools are not offered to detached, background, or occurrence execution. Add only a narrow authorized unattended browser path for the Secretary/nopCommerce workflow. It must:

- originate from an admitted `TriggerOccurrence`/`WorkItem` owned by the correct Agent Instance;
- use only that Agent Instance's persistent browser profile;
- recheck contextual capability availability and policy at execution time;
- preserve exact-action approval and authority semantics;
- stay bounded by time, tool, continuation, and cancellation budgets;
- recover across process restart without replaying completed side effects;
- preserve profile and secret isolation;
- avoid granting unrestricted browser access to every background WorkItem.

Browser ownership does not move into the scheduler. The scheduler routes work; it does not own browser state or let the model create authority.

## P9.5E — Trusted-owner proactive result delivery

P8.5 `app.message.send` remains bounded to the trusted current application/session execution. Do not silently redefine it for durable work that may have no live Chat session.

Add the smallest first-party owner-visible completion/attention delivery behavior needed by this demo. The later implementation proposal may choose a first-party Chat notification, activity/inbox item, durable assistant message, or another narrow surface consistent with current architecture, provided that:

- the destination is the trusted owner/current Agent Core application context, never an arbitrary model-selected recipient;
- this does not become Slack, Teams, email, cross-application, or arbitrary-recipient messaging;
- completed background work remains inspectable when live delivery fails;
- delivery is idempotent and retry-safe enough to avoid duplicate spam;
- work that finds nothing needing attention can complete quietly.

## P9.5F — Acceptance journeys

**Product publishing**

> “Add AC Keyboard for $99. Use the image I provided, write a suitable description, publish it, and tell me when it is ready.”

The Secretary loads a relevant Skill when needed, communicates useful intermediate progress, opens the visible authenticated browser, creates or updates and publishes the product in nopCommerce Admin, opens the storefront, verifies the observable resulting application state, and only then reports completion. Clicking Save alone is not verification.

**Authentication continuity**

Connect/login once, close the browser, then start another Chat with the same Agent Instance and/or restart Agent Core. The profile remains authenticated. A different Agent Instance cannot inherit or borrow that authentication.

**Proactive store check**

A scheduled occurrence becomes bounded work. The Secretary uses its authorized nopCommerce profile, inspects a relevant condition, takes only authorized action, produces a trusted-owner-visible result when attention is warranted, and completes quietly when nothing matters.

**General-assistant proof**

The same Secretary completes an unrelated task through existing capabilities, such as research, attachment summarization, artifact creation, policy-governed email, a reminder, or learned memory. This proves nopCommerce is an application the identity participates in rather than what the identity fundamentally is.

## P9.5G — UX and Admin quality

Add user-visible surfaces only where the workflow requires them. Likely surfaces are safe application connection/authentication status; connect, reauthenticate, revoke, and profile-reset controls; browser opening/closing state; owner-visible proactive outcomes; intervention/failure handling; and Admin configuration of the prepared Secretary and demo authority.

Reuse Ant Design v6 and existing Chat/Admin patterns. Do not add a browser-control dashboard, click log, raw-cookie viewer, or secret-management UI. Run Impeccable against affected surfaces, address relevant findings before closure, then synchronize canonical frontend/design documentation.

## P9.5H — Verification and closure evidence

Default Synthetic CI remains key-free, deterministic, offline where expected, and independent of public websites or a live nopCommerce deployment. Prove new Core semantics with deterministic fixtures/fakes where possible. Keep actual nopCommerce/real-model evidence in a separate optional demo/integration gate rather than making the whole default CI depend on a heavyweight third-party application.

Closure evidence must include:

- unit/integration coverage for application/profile ownership and profile-reset semantics;
- cross-Agent-Instance authentication isolation;
- cross-session and process-restart profile reuse;
- unattended WorkItem authorization, budget, cancellation, recovery, and no completed-side-effect replay;
- proactive result-delivery idempotency and quiet completion;
- no secret leakage into model observations, history, logs, or ordinary Admin projections;
- the deterministic Synthetic regression suite green, including existing P8, P8.5, and P9 behavior;
- one real-model Real-profile end-to-end nopCommerce demonstration with a headed browser;
- documented demo start, stop, and reset procedure.

Hosted Synthetic remains the standard regression gate. Real-model/nopCommerce evidence may be an explicit manual or opt-in closure gate.

## P9.5 non-goals

P9.5 does not include P10 sandbox evolution; P11 multi-user/production infrastructure; migration of Agent Core from SQLite; Kubernetes, Redis, a broker, or a microservice split; MCP-first redesign; a universal plugin/integration framework or marketplace; a native nopCommerce integration; required Facebook, Mastodon, or social-network support; arbitrary cross-application or arbitrary-recipient messaging; an unrestricted autonomous/infinite loop; multi-agent coordination or a swarm; a visual workflow builder; a persistent general Agent Instance filesystem; authorization bypasses; the operator's normal Chrome profile; or model-visible credentials/browser storage.

## P9.5 stop condition

P9.5 closes when a developer can use one simple documented workflow to bring up and reset the demo environment, open a prepared Secretary Agent Instance, and demonstrate with a real model that the same durable identity can converse normally and perform an unrelated assistant task; use Skills, learned memory, capabilities, policy, approvals, execution, scheduling, WorkItems, messaging, and browser behavior through existing Core contracts; operate self-hosted nopCommerce in a visible browser; retain nopCommerce authentication across Chats and an Agent Core process restart without sharing it across Agent Instances; create or modify something in nopCommerce and verify the resulting storefront/application state; execute at least one bounded scheduled proactive nopCommerce check through Trigger → Occurrence → WorkItem; deliver an appropriate trusted-owner-visible result when attention is warranted and complete quietly otherwise; and do all of this without model-visible credentials, authorization bypasses, an unrestricted loop, or reopening P9. Synthetic/offline regressions must remain green, and the user-visible workflow must complete the normal UI, Impeccable, and canonical-documentation review.

---

# P9.6 — Visual Browser, Reactive Triggers & Deterministic Unattended Execution

**Status: closed** on `d033bc61` ([closure report](docs/reports/p9.6-freeze-candidate.md); hosted Synthetic [`37187663286`](https://github.com/trannamtrung1st/agent-core/actions/runs/37187663286) green). P9 remains closed on `bba1de4` and P9.5 remains closed on `1012653`. P9.6 does not move either closure SHA and does not start P10. Closure includes nopCommerce `NopStartup` registration of `OrderPlacedConsumer`, native guest checkout **order 14** (`order-14-placed` without `emit-order-placed.sh`), delivery-ledger fan-out, and dedupe at one WorkItem. Unattended browser completion on the connected demo store was observed on an earlier opt-in path (`order-13-placed` after storefront order 12); order 14’s WorkItem ended `attempts-exhausted`. A single native checkout that also reaches terminal **Completed** on the same chain remains optional demo evidence, not a reopening criterion.

## Implemented state at the reviewed HEAD

Do not rebuild or describe these implemented capabilities as future work:

- durable `TriggerRegistration → TriggerOccurrence → WorkItem` admission, routing, deduplication, execution, cancellation, approval, retry, restart recovery, and side-effect fencing;
- source-owned authenticated `order.placed` webhook ingress with hashed Event Source credentials;
- admission-time subscriber snapshots, one `ExternalEventDelivery` per matching registration, and restart-safe pending-delivery fan-out;
- scheduled and application-event unattended browser execution with bounded budgets and execution-time authorization;
- one persistent authenticated browser profile owned by an Agent Instance, with trusted application-connection scope;
- trusted-owner attention results, idempotent delivery state, inspectable completion, quiet completion, and retained failure/capture evidence;
- deterministic execution-model policy resolved before live-versus-durable routing, with `WorkModelPin` persisted on durable WorkItems;
- a provider-neutral Agent Core browser port implemented by Playwright, not raw Playwright exposed to the model;
- model-facing `browser.navigate`, `browser.observe`, `browser.act`, `browser.capture`, `browser.pages`, and `browser.close`;
- bounded navigation history, multi-page lifecycle, semantic observation, explicit viewport capture, download ownership, and stale page/ref rejection;
- ordinary typed browser actions including click, double-click, fill, select, press, check/uncheck, upload, hover, scroll, and drag;
- deterministic Synthetic coverage for Browser v1, event admission/fan-out, model pinning, unattended parity, and the four Secretary modes.

The subsections below preserve the reviewed P9.6 requirements and stop condition. Their imperative or future-tense wording is the acceptance contract recorded at closure; implemented behavior is evidenced in the closure report and hosted Synthetic gate above.

## P9.6A — Browser Capability v1

Keep the dependency direction:

```text
Agent
  ↓
Agent Core Browser Capability
  ↓
Playwright
```

> Playwright is the browser driver/implementation. Agent Core owns the capability contract, authority, observations, side-effect semantics, durability, and auditability.

Do not expose raw Playwright as the model-facing protocol. Keep a small, coarse provider-neutral surface. The exact names remain an implementation decision, but the capability categories should remain equivalent to:

```text
browser.navigate
browser.observe
browser.act
browser.capture
browser.pages
browser.close
```

Do not add one agent tool per Playwright function or build a second browser engine. Broaden ordinary browser behavior coherently:

**Navigation**

- navigate, back, forward, and reload;
- preserve target policy, trusted application scope, cancellation, bounded settle, and meaningful post-navigation observation.

**Semantic observation**

- retain bounded visible text and role/name/action-based elements;
- retain opaque refs as the preferred interaction mechanism;
- retain stable-settle behavior and stale-observation rejection;
- do not encourage model-generated CSS or XPath selectors.

**Visual observation**

- add bounded screenshot/current-page visual capture suitable for rendered-state verification;
- request screenshots when useful rather than automatically after every action;
- treat captured images as bounded artifacts/model inputs with explicit size, lifetime, ownership, and redaction rules;
- validate model vision capability before admitting work that requires image interpretation; a text-only model must not silently be treated as able to inspect a screenshot;
- keep semantic observation available when visual interpretation is unsupported or unnecessary.

**Interaction**

- support click, double-click, fill/type, press, select, check/uncheck, hover, scroll, drag/drop, and upload;
- add other ordinary Playwright interactions only when they fit the same bounded semantic action contract;
- preserve action-specific authorization, exact-action approval, replay classification, and side-effect fencing.

**Page lifecycle**

- support multiple tabs/pages and popup/new-page adoption;
- list, switch, and close active pages without exposing provider handles;
- bind opaque page identity to the owning browser/profile scope and reject stale page observations.

**Waiting and synchronization**

- provide bounded waits for navigation, semantic state, and element readiness;
- prefer semantic readiness and observed stability over arbitrary sleeps;
- keep waits cancellable and inside the existing execution budget.

**Artifacts**

- support screenshots and useful download capture through explicit bounded ownership;
- keep PDF capture optional and requirement-triggered;
- do not turn browser artifacts into a general Agent Instance filesystem.

## P9.6B — Elevated browser capabilities stay gated

The normal Browser v1 contract does not include:

- arbitrary JavaScript `evaluate`;
- arbitrary Playwright scripts;
- CDP sessions;
- arbitrary request interception;
- unrestricted cookie or storage manipulation;
- extension loading;
- unrestricted filesystem access.

If a later concrete workflow requires one of these, treat it as an explicit elevated capability with its own authority, policy, approval, limits, and audit semantics. Do not smuggle it through `browser.act`.

## P9.6C — Authenticated webhook trigger ingress

Extend the existing Trigger/ApplicationEvent architecture:

```text
external event
    ↓
authenticated webhook ingress
    ↓
validate + normalize + deduplicate
    ↓
TriggerOccurrence
    ↓
existing routing
    ↓
live execution OR durable WorkItem
    ↓
Agent execution
```

- Authenticate the Event Source bearer before admitting an External Event. Agents subscribe with `eventSourceId` and `eventType`. Rotation and revocation belong to the source, not an application connection.
- Require stable source-event identity and owner-scoped deduplication.
- Normalize a bounded allowlisted payload into untrusted evidence. Webhook content is input, never authority, trusted instructions, policy, or a capability grant.
- Persist/admit the occurrence before asynchronous work and return the HTTP response quickly; do not execute the agent in the request.
- Reuse existing Trigger policy, occurrence routing, WorkItem lifecycle, approval, cancellation, diagnostics, retry, side-effect, and recovery semantics.
- Keep the modular monolith, SQLite/local filesystem, and one-process scheduler/background-worker topology.
- Do not add a broker, generic event platform, or webhook-specific execution system.

## P9.6D — One `order.placed` proof

Start with exactly one nopCommerce demo event:

```text
nopCommerce order.placed
        ↓
authenticated webhook
        ↓
Agent Core TriggerOccurrence
```

A tiny nopCommerce-side adapter/plugin may emit the event. It is an event source only: it grants no authority and exposes no model-facing nopCommerce application API.

```text
nopCommerce event → webhook → Agent Core trigger

Agent investigation/action → Browser Capability
```

The Secretary investigates and acts through the browser. Do not add a native nopCommerce agent API merely to simplify the demo, and do not add more commerce event types until this proof establishes a requirement.

## P9.6E — Extend unattended execution

Preserve the shipped Agent-Instance-owned scheduled/background browser path and extend it only as required for webhook-originated and broader Browser v1 work:

- Agent Instance and trusted profile ownership;
- persistent authenticated browser-profile continuity;
- trusted application-origin scope;
- execution-time capability and policy checks;
- exact-action approval;
- bounded model, tool, time, continuation, output, capture, and artifact budgets;
- cancellation, retry, restart recovery, approval resume, and side-effect fencing;
- idempotent result delivery and inspectable completion;
- quiet completion when owner attention is not required.

Application-event WorkItems must receive the same scoped unattended browser semantics as eligible scheduled WorkItems; they must not gain a broader path. Reconcile the known `tool-result-lost` terminal-result gap without replaying a completed external side effect. Do not introduce an unrestricted autonomous loop.

## P9.6F — Deterministic execution-model policy

> Routing decides where execution runs. It must not accidentally decide which LLM runs it.

Introduce an explicit execution-model policy capable of expressing:

```text
Agent
├── conversation/default model
├── unattended/background default model
└── optional per-trigger override
```

The final schema and authoring surface remain TBD. The contract must:

- resolve the intended execution model before live-versus-durable routing, or otherwise guarantee equivalent deterministic semantics;
- apply the same rule to scheduled and application-event/background work;
- pin catalog key, provider alias, model id, and reasoning effort for each admitted execution;
- preserve and reuse `WorkModelPin` for durable execution rather than replacing it;
- keep the pin stable across retries, restart recovery, approval resume, and resumed WorkItems;
- prevent later default changes from silently changing already-admitted work;
- validate required model capabilities, including tools and vision, for the admitted work;
- fail explicitly when the pinned model is unavailable or incompatible instead of silently switching models.

## P9.6G — Secretary showcase

Demonstrate the same durable Secretary Agent Instance in four modes:

1. **Interactive.** The owner asks the Secretary to perform a store task through the browser.
2. **Visual verification.** The Secretary requests visual observation when appropriate and uses it to verify rendered state; a screenshot is evidence, not automatic ceremony after every action.
3. **Scheduled proactive.** A scheduled store review runs without a user chat turn and reports only useful findings.
4. **Reactive.** A customer places an order; authenticated `order.placed` admission creates or deduplicates an occurrence; the Secretary wakes through live or durable routing, uses its persistent profile to inspect nopCommerce and relevant store context, uses visual observation when useful, completes quietly for the normal case, and reports an attention-worthy case to the trusted owner.

The showcase must make visible that one Agent Instance can:

- observe semantically and visually;
- act through the provider-neutral browser capability;
- preserve authenticated browser continuity;
- react to time and external events;
- continue outside a live Chat session;
- use deterministic model-selection policy;
- selectively report to its trusted owner.

## P9.6H — Verification, non-goals, and stop condition

Default verification remains key-free, deterministic, and Synthetic. Add deterministic coverage for browser capability normalization, stale refs/pages, bounded capture, text-only versus vision-capable model admission, webhook authentication/validation/deduplication/fast response, live-versus-durable model equivalence, model-pin recovery, application-event unattended browser authorization, approval/cancellation/retry, side-effect recovery, attention delivery, and quiet completion. Keep actual nopCommerce plus real-model visual/browser evidence in an explicit opt-in demonstration gate. Exercise the interactive, visual, scheduled, and reactive journeys before closure.

P9.6 does not include P10 sandbox expansion; raw Playwright model-facing APIs; arbitrary JavaScript or browser scripting; a generic integration/plugin or webhook marketplace; many webhook event types; Redis or another broker; distributed workers; Kubernetes or microservices; multi-agent coordination or swarms; unrestricted autonomous loops; or a general cross-session Agent Instance filesystem.

P9.6 closes when Browser Capability v1 provides the bounded semantic, visual, ordinary-interaction, page-lifecycle, synchronization, and artifact categories above without exposing raw Playwright; one authenticated `order.placed` event is durably admitted through the existing Trigger → Occurrence → live-or-WorkItem path; webhook evidence cannot grant authority; scheduled and reactive unattended browser work preserve P9.5 ownership, authorization, recovery, side-effect, approval, delivery, and quiet-completion semantics; the execution-model choice is deterministic and pinned independently of routing; the four-mode Secretary showcase passes; Synthetic/offline regressions are green; and P9, P9.5, P10, and P11 remain unopened.

---

# P9.7 — Conversational harness learning

**Status: frozen on behavior SHA `8f5afa00`; [hosted Synthetic green](https://github.com/trannamtrung1st/agent-core/actions/runs/37224218680).** [Final verification](docs/reports/p9.7-final-verification.md) records current full regressions and Real DeepSeek create → new-session use → partial update → new-session use → validation repair. GPT-4o mini accepts the fixed payload but failed content fidelity; use DeepSeek for this demo. [Historical first-gate report](docs/reports/p9.7-chat-first-freeze-candidate.md) owns the prior acceptance evidence. The prior `11a3d25c` Admin-first closure is [historical pre-pivot evidence](docs/reports/p9.7-freeze-candidate.md). P8/P8.5/P9/P9.5/P9.6 freezes are unchanged; P10/P11 remain unopened.

- Normal trusted-local single-owner Chat receives instance-policy/scoped authoring only with tools support and attached UserTurn context. User text expresses intent, never authority. Stale offers require live execution recheck.
- Existing normal tools authorize source access; successful content-bearing execution receipts or current owner material permit retention with provenance. No mandatory secondary source catalog or backdoor source client.
- Fixed semantic knowledge/Skill/instruction/tool operations call existing Authoring owners. Skills cannot grant authority. Eligibility derives from configured, already-authorized tools; policy/scope/owner/credentials cannot be self-authored. This leaves a known limitation: Chat cannot propose a newly configured tool outside that authorized set. Instruction replacement stays outside the default empty scopes and always needs exact owner approval when enabled.
- Managed knowledge/Skills may verify/publish/adopt internally. Assisted mutations, all instructions and every tool selection/configuration need exact Chat approval. Later concrete sensitive actions need their own approval.
- Internal candidates preserve CAS, revision-bound evidence, actual Core readback/activation, immutable publication, atomic adoption, safe diagnostics, durable history and SQLite recovery. Agent assessment remains partial; destructive/production/subjective outcomes require external evidence.
- Future Sessions use adopted versions. Current Session pins never silently change. Admin is policy/inspection/freeze with advanced legacy candidate discard; no required Prepare/Continue/Verify/Publish workflow or second LLM runtime.
- Completion passed all 20 correction criteria, journeys A–F, affected full regressions, MCP runtime verification, responsive Impeccable/design synchronization, docs consistency and hosted Synthetic green on the final behavior SHA. No continuous autonomous loop, arbitrary executable plugin generation or new persistence/runtime owner.

---

# P9.8 — Session Retrospection / Agent Experience

**Status: closed/frozen on behavior SHA `0a3330db`; local and hosted acceptance evidence is recorded in [final verification](docs/reports/p9.8-p9.9-final-verification.md).** The purpose is durable work continuity: an Agent Instance should be able to understand what it has experienced while working, as well as what it knows.

- Derive bounded retrospection from completed or recent Sessions and meaningful executions. Capture useful experience such as goal/purpose, meaningful actions attempted, important decisions, successes/failures, user corrections or feedback, unresolved work, relevant tool/environment difficulties, and useful lessons or follow-up observations.
- Keep experience/retrospection distinct from raw conversation history, semantic compaction/session summaries, learned memory, trusted persona, instructions, and authoritative knowledge. Retrospection may inform later memory promotion, but observations do not automatically become durable learned memory.
- Preserve provenance to the relevant Session/execution where practical. Keep the first version bounded and understandable; do not introduce a general cognitive or learning framework.

```text
session / execution
        ↓
retrospection
        ↓
experience / learnings
        ↓
optional existing memory admission
```

**Intended outcome:** useful, inspectable experience can carry across work while existing memory admission remains the only route to durable learned memory.

---

# P9.9 — Autonomous Thought Activation / Agent Initiative

**Status: closed/frozen on behavior SHA `0a3330db`; local and hosted acceptance evidence is recorded in [final verification](docs/reports/p9.8-p9.9-final-verification.md).** Add a bounded decision step so an Agent Instance can be activated periodically to think and decide whether useful action exists. The first primitive is a configurable interval/time-based activation with a user-configured thinking prompt. For example: periodically review recent Sessions and retrospectives, investigate a meaningful recurring harness problem when justified, or do nothing.

```text
scheduled task = trigger → predetermined work
thought activation = trigger → agent reasons → decides whether / what to do
```

- No-op / do nothing is a normal successful outcome. Activation must not incentivize manufacturing work.
- Reuse existing execution, tools, WorkItem/background execution, diagnostics, approval, and authorization primitives where they fit. P9.6 already provides deterministic Trigger → Occurrence → WorkItem unattended execution; P9.9 adds the decision step and does not duplicate the scheduler, create another background-work model, or introduce a separate autonomous-agent runtime.
- Compose with P9.7 Harness Management when useful, such as investigating a repeated issue found in retrospection. Do not add a separate autonomous self-improvement policy system. The model's decision is not authorization: existing capability, policy, approval, and execution-time checks still govern actions.
- Make activation/execution origin available as policy context where needed (conceptually UserTurn, ThoughtActivation, ScheduledWork, ApplicationEvent, and Webhook/Event). Core uses server-owned TriggerKind/WorkSourceKind and ToolExecutionAdmission. Existing policy must be able to decide whether the Agent Instance may perform an operation in an autonomous activation context; do not create another policy engine.
- Preserve P9.7's authority boundary. Thought activation cannot self-grant capabilities, expand tool authorization, weaken approvals, alter owner/trust/security policy, expose/create credentials, increase its own management/autonomy authority, or bypass execution-time policy because work is unattended. Already-authorized inspection, diagnostics, verification, and Skill/knowledge refinement may be allowed/configured under normal P9.7 policy. Authority-changing operations require existing authorization/approval or remain denied.

```text
Trigger
   ↓
Thought activation
   ↓
Normal agent reasoning/execution
   ↓
Agent chooses capabilities if useful
   ↓
Existing capability + policy + approval boundary
   ↓
action / approval-needed / denied / no-op
```

P9.9 should consume P9.8 retrospection plus existing memory, Session, and harness context. The intended progression is:

```text
experience → reflection → initiative → permitted action → new experience
```

The simple interval plus thinking-prompt model is the first primitive. Richer schedules, webhook/application-event or condition/state-driven activation, internal events such as Session completion or retrospection creation, longer-lived goals, adaptive initiative, and agent/team triggers or coordination remain future directions, not this phase's design. Do not introduce unrestricted permanent reasoning loops, model-driven scheduler mutation without policy, a general planner or goal hierarchy engine, multi-agent orchestration, reinforcement-learning machinery, another Session Runtime, WorkItem implementation, memory subsystem, or Harness Management subsystem.

**Intended outcome:** an Agent Instance can wake, consider bounded context, choose a permitted action or a successful no-op, and leave experience for later retrospection, using existing execution and authority boundaries.

---

# P10 — Sandbox evolution

**Status: requirement-triggered.**

Docker is the current sandbox. Keep it while it satisfies requirements. Keep the model-facing `sandbox.run` capability stable if the provider changes.

- Introduce a sandbox provider abstraction only when a second implementation is genuinely required.
- Evaluate a stronger remote or multi-tenant sandbox, including pools, multiple images, or remote resource controls, only when a workflow needs them.
- Consider Kubernetes only when deployment, scale, or isolation requirements justify it. Do not adopt it to replace a working Docker sandbox.

---

# P11 — Multi-user and production infrastructure

**Status: requirement-triggered.**

Start when Agent Core leaves trusted single-owner local development or begins a real external hosted pilot. Do not start the whole phase at once.

**P11A — Authentication and ownership.** Authentication, user and Admin authorization, and a tenant or organization model only when required. Enforce ownership of definitions, versions, instances, sessions, memory, triggers, WorkItems, approvals, attachments, artifacts, resources, and integrations. Remove trusted-local assumptions from externally reachable paths. Separate browser and user credentials from host authority.

Future Chat authority must derive from authenticated principal/service identity + tenant/organization ownership + calling application + instance capability boundary + application delegation + caller permissions + session/execution grants + resource policy. User text never grants authority. Record owner/Admin/member/service/application principals; application-scoped delegation; session/execution capability grants; expiry/revocation; differing authority for the same instance by caller/application; effective tool offering and live execution recheck; background Trigger/Occurrence/WorkItem delegated authority/provenance; tenant-safe ownership of Definitions, instances, memory, Sessions, resources, approvals and integrations; audit principal/application/session; and authorization separate from exact-action approval.

Examples: owner Chat may manage knowledge/Skills; employee Chat gets operational tools without harness mutation; a customer application gets support tools without mutation; trusted onboarding may get a temporary Knowledge/Skill grant expiring with the Session. Current P9.7 explicitly uses trusted-local single owner → authorized for enabled instance self-management. This is the replacement seam, not implemented RBAC/ABAC/tenant infrastructure.


**P11B — PostgreSQL.** Move from SQLite when concurrent external users, multi-process services, or hosted reliability require it. Verify migrations, transactions, and revision concurrency. Keep SQLite for local and Synthetic development unless maintaining both becomes counterproductive. Verify restore, not only backup. Do not invent a new persistence architecture if the EF boundary is already sufficient.

**P11C — Object storage and secrets.** Move attachments, artifacts, and immutable resources off local disk when deployment requires it. Keep mutable session workspace distinct from immutable blobs. Add retention where required. Add a secret manager when deployment requires it. Credentials stay outside model context and ordinary Admin projections.

**P11D — Hosted operations.** TLS and ingress, a production logging and tracing backend, OpenTelemetry export where useful, alerting, backup and restore drills, rollback, quotas, and a public-hosting security review. Expand Admin history into enterprise audit only where required.

**P11E — Distributed scaling.** Only after a measured bottleneck. Possible needs are multiple API nodes, a SignalR backplane, distributed work claims, worker pools, and a horizontal Session Runtime. Then evaluate Redis or another coordination technology, a queue, and Kubernetes for a specific use. Do not add Redis because sessions exist, a broker because events exist, Kubernetes because Docker exists, or microservices because modules exist.

---

# Deferred / requirement-triggered work

These items are recorded decisions. P9.5–P9.9 take only the narrow exceptions named in their scopes. The remaining items are not reasons to reopen those phases or start P10 or P11 early. Each waits for a concrete requirement.

**Production infrastructure and distributed scheduling.** The P11 stack, plus Hangfire, Quartz, or another job framework. If a scheduler is ever justified, it implements wake-up and dispatch under Trigger → Occurrence → WorkItem. It does not replace that model. Misfire policies beyond current coalesce-to-latest (`SkipMissed`, `CatchUp`) wait for a workflow that needs them.

**Platform and packaging.** A universal plugin framework, plugin marketplace, Skill marketplace, application marketplace, prepared-worker marketplace, generic webhook marketplace, ZIP or package import/export, Git resource sync, remote filesystem or object-storage browsing, live folder sync, and FTP/SFTP. P9.5 owns only the narrow nopCommerce connection/binding behavior its workflow proved. P9.6 owns only authenticated `order.placed` ingress and a tiny emitting adapter. Neither phase absorbs this list.

**Bindings, messaging, and skills beyond P9.9.** Full/universal Application Binding persistence remains deferred; P9.5 owns only a minimal Agent-Instance-owned nopCommerce relationship. Cross-application and arbitrary-recipient messaging remains deferred; P9.5 owns only narrow trusted-owner proactive delivery. P9.7 owns Definition-versioned Skill authoring only. An independently versioned Skill shared across many Definitions, Skill/plugin marketplaces, embeddings/vector Skill retrieval, and a dedicated Skill-router model remain deferred.

**Agent scope.** Multi-agent coordination or an agent swarm, a visual workflow builder, a persistent general Agent Instance filesystem, and an Admin assistant agent remain deferred. P9.5/P9.6 provide bounded Trigger → Occurrence → WorkItem execution; P9.9 is the narrow, policy-governed thought-activation exception that adds a decision step. Unrestricted/arbitrary autonomous loops remain deferred.

**Voice and hosted providers.** The known Real/OpenRouter historical-image reread gap: bounded, credential-gated, outside default CI. HOSTED-04, one non-Synthetic voice smoke on an explicitly selected hosted configuration. Replacing realtime `OpenAiSpeechRecognizer` only when a concrete need exists. Native speech-to-speech only if measured latency or quality shows that `STT → text model → TTS` is insufficient.

---

# Continuous quality

- Keep Synthetic and offline verification as the default path. Hosted provider tests stay opt-in. Hosted infrastructure stays optional for local development.
- Keep `main` green before the next architectural slice.
- Add regression coverage with lifecycle, response, speech, multimodal, tool, memory, identity, trigger, background-work, definition, publishing, Admin, and infrastructure changes.
- Keep Playwright coverage for meaningful user-visible workflows.
- Make race-sensitive tests deterministic. Prefer explicit gates and `TimeProvider` over wall-clock sleeps.
- Update canonical docs when observed behavior changes. Keep historical gate narratives in `docs/reports/`.

---

# Maintainer notes

Always keep this section.

- [ ] Admin assistant agent remains a future idea.
- [x] P9.7 owns bounded, configurable agent-assisted/self-managed harness authoring; do not track a second generic “self-improvement” runtime here.
- [x] P9.8 — Session Retrospection / Agent Experience (bounded experience with provenance; optional existing memory admission).
- [ ] team work, agent communication, workflow, orchestration, etc .... like grok bot
- [x] P9.9 — Autonomous Thought Activation / Agent Initiative (interval + thinking prompt; decision step over existing Trigger → Occurrence → WorkItem and policy boundaries).
- [ ] extra/optimization/enhancements: more tools, sandbox, security, smart routing, monitoring etc ...

---

# Implemented baseline

Orientation for what later work can build on. Detail and gate history are in `/docs` and `docs/reports/`.

**Conversation and runtime.** Session-owned conversation, purpose and lifecycle, queue versus steer, Stop, interruption, detach and reconnect, durable accepted turns, observer reattachment, history and restore, semantic compaction, and display/speech response semantics with transient progress.

**Identity and memory.** Reusable Definition versus durable Agent Instance, persona separate from learned memory, trusted owner profile, Identity and User memory scopes, and layered prompt composition. Provider reasoning stays out of assistant output.

**Tools and capabilities.** Typed tools for workspace, knowledge, attachments, artifacts, sandbox, web, HTTP, email, and a provider-neutral Agent Core browser capability backed by Playwright. The current browser surface is `navigate`, `observe`, `act`, `capture`, `pages`, and `close`; ordinary actions include click, double-click, fill, select, press, check/uncheck, upload, hover, scroll, and drag. It retains opaque refs, bounded page identity, stale-observation rejection, stable-settle observations, explicit bounded viewport capture, and download ownership without exposing raw Playwright. Execution-time policy, exact-action approval, and detached-execution restrictions remain authoritative. The trusted model catalog supports per-session model and reasoning selection.

**Voice and realtime.** SignalR with MessagePack as the default transport, optional JSON diagnostic mode with the same contract, independently replaceable STT and TTS, voice interruption, and heard versus received tracking. Synthetic speech requires no provider credentials.

**Workspace and resources.** Session-owned workspaces, attachments and artifacts, current and historical images, and versioned definition resources.

**Triggers and background work.** Durable trigger registration and occurrences, and durable `WorkModelPin`-carrying WorkItems for work that must outlive the Session Runtime. Schedules and source-owned authenticated `order.placed` events use the same occurrence routing model. Event admission snapshots matching subscribers into a delivery ledger so fan-out can resume after restart without adding later subscribers. Scheduled and application-event background browser execution can reuse an Agent-Instance-owned persistent authenticated profile. Attention-worthy results have idempotent trusted-owner delivery; ordinary completion can remain quiet. One process schedules and executes this work.

**Admin.** Draft, Form and JSON authoring, validation, evaluation, immutable publish, instances, persona, memory and automation administration, effective configuration, and history. P7.6 authoring closure is frozen.

**Diagnostics.** One server-owned `DiagnosticId` on unexpected failures, safe to copy, with server-side exception detail. Clients cannot set it.

**Infrastructure and testing.** .NET modular monolith, React / Vite / Ant Design v6, SQLite, local filesystem, Docker sandbox, Compose, and Synthetic deterministic CI.

---

# Current implementation status

**P9.8 and P9.9 are closed/frozen on behavior SHA `0a3330db`.** All implementation slices A-F, canonical documentation, Admin UX, Synthetic demo and mandatory local gates are complete. [Acceptance mapping](docs/reports/p9.8-p9.9-freeze-candidate.md) covers all 36 criteria; [final verification](docs/reports/p9.8-p9.9-final-verification.md) records 2367 passing backend tests, 601 frontend tests and 88 browser tests. [Hosted Synthetic and Compose](https://github.com/trannamtrung1st/agent-core/actions/runs/37260497161) passed on that exact SHA, including the explicit continuity browser gate. P9.7 remains frozen on `8f5afa00`, and earlier P8/P8.5/P9/P9.5/P9.6 freezes remain unchanged. P10/P11 remain unopened and requirement-triggered.

## P9.10 — Identity state consolidation (closed/frozen)

**Closed/frozen on verified candidate `71a9e6fd`**, including configurable continuity maintenance cadence. [Hosted Synthetic and Compose `37452986171`](https://github.com/trannamtrung1st/agent-core/actions/runs/37452986171) passed all five jobs on that exact commit on 2026-10-06. [Final verification](docs/reports/p9.10-final-verification.md) records AC1–AC20, Real semantic evidence, CI repairs, 2,468 passing hosted backend tests, 683 frontend tests, 112 browser tests and Compose volume survival. Stop P9.10 expansion. P9.8/P9.9 freeze SHAs remain unchanged; P10/P11 and optional Continuity v2 phase 6 remain requirement-triggered/deferred.


## Bounded post-P9.10 Artifact delivery

Artifact delivery completes Chat presentation of the existing session-owned Artifact store: canonical metadata, an accessible downloadable card, lazy owner-authenticated exact-byte download, bounded metadata deduplication, local Retry and retained read-only history. Verification/closure evidence: [Artifact delivery report](docs/reports/artifact-delivery-ux.md). P9.8/P9.9/P9.10 freezes stay unchanged; P10/P11 remain unopened.
