# TODO

Living roadmap: current status, active requirements, future dependency order, and cross-phase invariants.

Detailed architecture and behavior live in `/docs`. Historical implementation and freeze evidence live in `docs/reports/`. This file does not duplicate either.

Reviewed against `main` through **`aedea70`** on **2026-10-01**. Pre-P8 bounded follow-up after P7.7 is **closed** (hosted Synthetic [**`36667172857`**](https://github.com/trannamtrung1st/agent-core/actions/runs/36667172857) green on **`1cadf46`**). **P8** remains **frozen** on **`ca3eb23`** (hosted Synthetic [**`36696902928`**](https://github.com/trannamtrung1st/agent-core/actions/runs/36696902928) green). **Post-freeze P8 follow-up is closed** on **`c9aec29`** (provider-contract **`6fda4c5`**, CI stabilization **`c9aec29`**, hosted Synthetic [**`36745126226`**](https://github.com/trannamtrung1st/agent-core/actions/runs/36745126226) green). That closure does not move or reopen the P8 freeze. **P8.5** is **closed** on **`1461567`** (hosted Synthetic [**`36770385588`**](https://github.com/trannamtrung1st/agent-core/actions/runs/36770385588) green). Bounded post-closure corrections through **`0d1cfdd`** (assistant routing, truncation budgets, Playwright sync) are **closed** on hosted Synthetic [**`36807383922`**](https://github.com/trannamtrung1st/agent-core/actions/runs/36807383922) green. A second bounded post-closure chain through **`fff7761`** (SSE idle at `ReadAsync`, Skill editor comma draft, no-chat Agent Step admission and execution state, response-function `displayText`) is **closed** on hosted Synthetic [**`36818061198`**](https://github.com/trannamtrung1st/agent-core/actions/runs/36818061198) green. A third bounded post-closure stabilization chain through **`aedea70`** (realtime and history convergence, durable `session.ready` paging with live streaming overlay, direct-user response contract and inspectable failures, gated and budgeted `app.message.send`, test and CI synchronization) is **closed** on hosted Synthetic [**`36851267423`**](https://github.com/trannamtrung1st/agent-core/actions/runs/36851267423) green; [appendix](docs/reports/p8.5-freeze-candidate.md). Real probes on DeepSeek V4.1 Flash and GPT-4o mini showed substantive work, intermediate application messages, and terminal `chat.respond`. None of these chains moves the P8 or P8.5 freeze SHAs. **P9 — Visible browser** is in progress and is not closed.

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

**P8.5 — Application Messaging & Dynamic Skill Activation** is **closed** on `1461567` ([closure report](docs/reports/p8.5-freeze-candidate.md); hosted Synthetic [`36770385588`](https://github.com/trannamtrung1st/agent-core/actions/runs/36770385588) green). `app.message.send` and `skills.load` continue the same bounded user-turn execution. They do not change the P8 freeze. Post-closure corrections through **`aedea70`** are recorded in that report's appendix (truncation **`36807383922`** on **`0d1cfdd`**, SSE/no-chat/editor **`36818061198`** on **`fff7761`**, stabilization **`36851267423`** on **`aedea70`**); they do not move the P8.5 closure SHA. **P9 — Visible browser** is in progress and is not closed. P10 and P11 stay requirement-triggered.

---

# Current roadmap

1. **P0–P8 — frozen.** P7.5 is frozen on `70a5720`; P7.6 on `17d89ae`; P7.7 on `40a1d92`; and P8 on `ca3eb23`. Post-P7.7 / pre-P8 bounded follow-up is closed on `1cadf46`. Post-freeze P8 follow-up is **closed** on `c9aec29` (hosted Synthetic **`36745126226`** green).
2. **P8.5 — Application Messaging & Dynamic Skill Activation — closed** on `1461567` (post-closure corrections **closed** on **`aedea70`**, hosted Synthetic [**`36851267423`**](https://github.com/trannamtrung1st/agent-core/actions/runs/36851267423) green).
3. **P9 — Visible browser — in progress, not closed.**
4. **P10 — Sandbox Evolution — when the current sandbox is insufficient.**
5. **P11 — Multi-user + Production Infrastructure — when a real hosting or pilot requirement appears.**

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

**Status: long-term direction. P8 is the frozen bounded slice on `ca3eb23`. P8.5 is closed on `1461567`. P9 visible browser is in progress and is not closed. The rest is future guidance.**

This section says why later phases exist. P8, P8.5, and P9 say what to implement and verify. It does not reopen frozen phases, widen P8 past its stop condition, or pull P9, P10, or P11 forward.

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

## Application Binding — future

An Application Binding would describe how one durable identity participates in one application without redefining that identity. Example: Sam in application A is support, in application B is a team member, and in application C is a researcher. A future binding may carry role, application instructions, resources, permitted capabilities, policy, and application-scoped memory, workspace, or triggers where a real workflow needs them.

```text
Definition
→ immutable Version
→ durable identity / Agent Instance
→ Application Binding
→ Session / Task / Event
```

Do not add that persistence until a second concrete application defines ownership, mutation, versioning, authorization, and portability. It must not rewrite the Definition, persona, or identity-wide state. P8 uses Chat as the first application adapter and does not build this schema.

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

Browser use is access to logged-in applications. Availability is not authority. Do not share one authenticated browser profile across identities or applications. Do not persist every click, selector, or DOM observation as product history. Durable receipts are for meaningful observations, approvals, and side effects. Profile ownership, application and session scope, allowlists, cookie retention, and tenant isolation are unresolved until a real browser workflow forces the design.

P9 is where that first concrete browser provider is evaluated. Its execution path builds on the frozen P8 contract and the P8.5 messaging and Skill-activation semantics. Browser capability must not define either of those semantics.

---

# Infrastructure stance before production

The development stack in the architectural baseline stays the default. Add a production dependency only when it solves a real hosting or pilot problem. P11 is that phase. P8.5 and P9 must not start it early.

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

Chat is the first application that must prove the path. Browser and other providers are P9. Sandbox and production infrastructure are P10 and P11.

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

P8.5 consumes this frozen contract. It does not move P8 evidence. P9 waits for P8.5 so browser/platform work can reuse current-application messaging and dynamic Skill activation instead of defining them.

---

# P8.5 — Application Messaging & Dynamic Skill Activation

**Status: closed** on `1461567`. Hosted Synthetic [`36770385588`](https://github.com/trannamtrung1st/agent-core/actions/runs/36770385588) is green. Evidence: [p8.5-freeze-candidate.md](docs/reports/p8.5-freeze-candidate.md). P9 visible browser is in progress and is not closed.

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

P8.5 closes when an agent can send a bounded intermediate message to the trusted current application context and continue; intermediate messaging is distinct from terminal `chat.respond` and system operational progress; the model cannot choose an unauthorized destination; Skill activation is no longer semantically dependent only on keyword matching; the agent can request Skills as work unfolds; Core validates, budgets, and pins active Skill IDs; Skill loading never grants capability or authority; deterministic matching is only an optional optimization; existing P8 execution and security behavior remains intact; and P9 can start without inventing messaging or Skill-selection semantics.

---

# P9 — Harness / platform extensibility

**Status: in progress. Not closed.**

Observed so far: `general-assistant` v11 allowlists `browser.navigate`, `browser.observe`, and `browser.act` and publishes skill `browser.record.lookup`. Application owns the provider-neutral browser port and target policy. Infrastructure owns a direct Playwright adapter and an isolated loopback fixture. Chat progress for a `browser.*` tool reuses `agent.progress` kind `runningTool` with message `Using browser…`. The scripted Synthetic journey for a user message containing `record AC-1042` sends one application message and one answer, `AC-1042 is In review.` Full regression, Compose, the headed demo, the hosted workflow, and the freeze report are still outstanding. P8 and P8.5 freeze SHAs are unchanged.

P9 starts only after P8.5 closes. Its prerequisites are the frozen P8 Agent Step, controller/action boundary, and Chat proof, plus P8.5 bounded current-application messaging and Core-admitted dynamic Skill activation. P9 consumes those contracts. It does not define them.

Start from one concrete provider need. Do not add a universal provider interface. The North Star is not a P9 backlog. A second real implementation is what proves which extension seams are needed.

Browser automation is the broad compatibility path. Native integrations are the optimized path. The first concrete exercise should look like:

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
browser provider
   ↓
external web application
```

**First candidate to evaluate:** Playwright, Playwright MCP, or a narrow adapter around Playwright. Choose the narrowest shape that fits the P8 architecture. Do not decide that shape before the concrete need is in hand.

MCP is an optional edge adapter. It is not Agent Core’s semantic center and it is not an authority source. Native Agent Core tools stay first-class. External tools still pass through Agent Core policy. Provider credentials stay outside model context. Provider DTOs stay out of Domain and Application. An extension failure must not corrupt durable agent, session, or WorkItem state. Browser/platform adapters do not choose message destinations, define Skill activation authority, or turn Skill requirements into permissions.

Repository evidence today: Playwright and Playwright MCP exist for frontend tests and development. Neither is a runtime capability. Tool registration and dispatch are still static and in-process, so a real browser provider would exercise the second tool-host seam noted in P7.5. Browser profile ownership, authentication isolation, application and domain scope, durable receipts, and live versus detached execution are still unresolved. Resolve them from the concrete workflow, using the North Star constraints: no shared authenticated profile, no unrestricted authority, and no click-level product history.

- [ ] Add an external tool or integration provider only when that second implementation justifies the seam.
- [ ] Define extension identity and versioning only to the degree that extension requires.
- [ ] Reuse shared policy primitives. Do not invent a second policy or one universal configuration object.
- [ ] Revisit a durable Agent Instance workspace only for a concrete cross-session file workflow, with an explicit scope. Do not generalize `SessionWorkspace`.
- [ ] Revisit richer reusable evaluation suites only when multiple provider implementations make them useful.

P9 is not a plugin marketplace, a prepared-worker package system, a visual workflow builder, a multi-agent engine, an unrestricted browser agent, a distributed job platform, tenant RBAC, Kubernetes, or a microservice split.

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

**P11B — PostgreSQL.** Move from SQLite when concurrent external users, multi-process services, or hosted reliability require it. Verify migrations, transactions, and revision concurrency. Keep SQLite for local and Synthetic development unless maintaining both becomes counterproductive. Verify restore, not only backup. Do not invent a new persistence architecture if the EF boundary is already sufficient.

**P11C — Object storage and secrets.** Move attachments, artifacts, and immutable resources off local disk when deployment requires it. Keep mutable session workspace distinct from immutable blobs. Add retention where required. Add a secret manager when deployment requires it. Credentials stay outside model context and ordinary Admin projections.

**P11D — Hosted operations.** TLS and ingress, a production logging and tracing backend, OpenTelemetry export where useful, alerting, backup and restore drills, rollback, quotas, and a public-hosting security review. Expand Admin history into enterprise audit only where required.

**P11E — Distributed scaling.** Only after a measured bottleneck. Possible needs are multiple API nodes, a SignalR backplane, distributed work claims, worker pools, and a horizontal Session Runtime. Then evaluate Redis or another coordination technology, a queue, and Kubernetes for a specific use. Do not add Redis because sessions exist, a broker because events exist, Kubernetes because Docker exists, or microservices because modules exist.

---

# Deferred / requirement-triggered work

These items are recorded decisions. They do not block P8.5. They are not reasons to start P9, P10, or P11 early. Each waits for a concrete requirement.

**Production infrastructure and distributed scheduling.** The P11 stack, plus Hangfire, Quartz, or another job framework. If a scheduler is ever justified, it implements wake-up and dispatch under Trigger → Occurrence → WorkItem. It does not replace that model. Misfire policies beyond current coalesce-to-latest (`SkipMissed`, `CatchUp`) wait for a workflow that needs them.

**Platform and packaging.** A universal plugin framework, plugin marketplace, Skill marketplace, application marketplace, prepared-worker marketplace, ZIP or package import/export, Git resource sync, remote filesystem or object-storage browsing, live folder sync, and FTP/SFTP. P9 may add one concrete provider seam. It may not absorb this list.

**Bindings, messaging, and skills beyond P8.5.** Full Application Binding persistence. Cross-application and arbitrary-recipient messaging, including Slack/Teams/email delivery implementations. An independently versioned Skill shared across many Definitions. Skill or plugin marketplaces. Embeddings/vector Skill retrieval and a dedicated Skill-router model.

**Agent scope.** Multi-agent coordination or an agent swarm. A visual workflow builder. An arbitrary autonomous loop. A persistent general Agent Instance filesystem. An Admin assistant agent.

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

- [ ] Allow self-learning, self skill management, and self-improvement (configurable, on/off) [TBD].

---

# Implemented baseline

Orientation for what P8.5 can build on. Detail and gate history are in `/docs` and `docs/reports/`.

**Conversation and runtime.** Session-owned conversation, purpose and lifecycle, queue versus steer, Stop, interruption, detach and reconnect, durable accepted turns, observer reattachment, history and restore, semantic compaction, and display/speech response semantics with transient progress.

**Identity and memory.** Reusable Definition versus durable Agent Instance, persona separate from learned memory, trusted owner profile, Identity and User memory scopes, and layered prompt composition. Provider reasoning stays out of assistant output.

**Tools and capabilities.** Typed tools for workspace, knowledge, attachments, artifacts, sandbox, web, HTTP, and email. Execution-time policy, exact-action approval, and detached-execution restrictions. Trusted model catalog with per-session model and reasoning selection.

**Voice and realtime.** SignalR with MessagePack as the default transport, optional JSON diagnostic mode with the same contract, independently replaceable STT and TTS, voice interruption, and heard versus received tracking. Synthetic speech requires no provider credentials.

**Workspace and resources.** Session-owned workspaces, attachments and artifacts, current and historical images, and versioned definition resources.

**Triggers and background work.** Durable trigger registration and occurrences, and durable WorkItems for work that must outlive the Session Runtime. One process schedules them.

**Admin.** Draft, Form and JSON authoring, validation, evaluation, immutable publish, instances, persona, memory and automation administration, effective configuration, and history. P7.6 authoring closure is frozen.

**Diagnostics.** One server-owned `DiagnosticId` on unexpected failures, safe to copy, with server-side exception detail. Clients cannot set it.

**Infrastructure and testing.** .NET modular monolith, React / Vite / Ant Design v6, SQLite, local filesystem, Docker sandbox, Compose, and Synthetic deterministic CI.

---

# Next implementation item

P9 — Harness / Platform Extensibility.
