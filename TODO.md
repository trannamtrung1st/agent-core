# TODO

Ordered by current dependency and product value.

Reviewed against `main` at `76818a9` on **2026-09-29**, then updated for the post-P7.6 Admin lifecycle follow-up in `docs/reports/p76-admin-lifecycle-followup.md` and for **P7.7 — Operational Diagnosability & Realtime Debuggability**, now frozen on `40a1d92` (workflow `36594702224` green) before P8. The Admin lifecycle follow-up is not a new freeze and is not hosted-green. P7.6 remains frozen on `17d89ae`. P7.7 does not reopen P7.6.

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

P7.5 is frozen on `70a5720`. Hosted workflow `36368766449` is green on that SHA. Closure report: `docs/reports/p7.5-freeze-candidate.md`.

## Current active phase

**P7.7 — Operational Diagnosability & Realtime Debuggability — frozen on `40a1d92`.** Hosted workflow `36594702224` is green on that SHA. Closure report: `docs/reports/p7.7-freeze-candidate.md`.

P7.6 is frozen on `17d89ae`. Hosted workflow `36427670239` is green on that SHA. Closure report: `docs/reports/p7.6-freeze-candidate.md`.

P7.7 is a bounded product/operational requirement discovered after P7.6. It does not reopen P7.6.

A later Admin lifecycle follow-up is specified in `docs/reports/p76-admin-lifecycle-followup.md`. It remains a bounded follow-up, does not redefine P7.6, does not move the P7.6 freeze SHA, and does not start P8.

P7.6 does **not** redefine what P7 or P7.5 previously meant or invalidate their closure evidence.

**P8 — harness/platform extensibility** is next. When P8 begins, start from one concrete provider need and do not add a universal provider interface. This record does not start P8 implementation.

---

# Current roadmap

1. **P0–P7 — closed/frozen.**
2. **P7.5 — architecture consolidation and infrastructure readiness — frozen on `70a5720`.**
3. **P7.6 — Admin usability closure — frozen on `17d89ae`.**
4. **P7.7 — Operational Diagnosability & Realtime Debuggability — frozen on `40a1d92` (workflow `36594702224` green).**
5. **P8 — harness/platform extensibility — next after P7.7.**
6. **P9 — sandbox evolution when requirements justify it.**
7. **P10 — multi-user + production infrastructure when real hosting/pilot requirements justify it.**

Do not reopen a frozen phase without either:

- a reproducible regression; or
- a concrete new product requirement that belongs there rather than in a later phase.

P7.6 and P7.7 are examples of new bounded requirements after frozen phases. Keep their changes attributable to the correct phase rather than rewriting frozen P7/P7.5/P7.6 history.

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

# Product / Architecture North Star

**Status: deferred/future guidance, not an active implementation phase.**

This section records long-term product and architecture direction. It does not reopen P0–P7, P7.5, or P7.6; widen P8; or pull P9/P10 infrastructure forward. Keep the concepts unnumbered until concrete workflows establish their dependencies and implementation order.

Long-term product direction:

> Agent Core hosts durable AI identities that can participate in applications, conversations, tasks, and events with scoped memory, capabilities, authority, and working context.

The current conversation runtime remains valid. The long-term product should evolve from primarily hosting conversations toward hosting durable agent identities that can retain continuity and participate in more than one application.

An ambitious but grounded product interpretation is:

> Agent Core can evolve into a runtime for persistent AI workers / digital employees.

“AI worker” is product language, not a new architecture layer. The metaphor becomes useful only when the underlying durable identity can increasingly combine:

```text
stable durable identity
persona
learned memory
relationships/context
knowledge/resources
capabilities
application bindings
scoped authority
working context/workspaces
tasks/goals
triggers/events
execution history
approvals
```

Such a worker may eventually participate in applications, hold different roles in different applications, receive work, perform authorized actions, wait for external events, resume later, ask for approval, complete goals, communicate or delegate to other identities, and retain appropriate continuity over time.

Agent Core remains the identity/runtime layer. A conversational assistant, examiner, customer-service experience, operations tool, or another product is an application built on that layer. This is internal architectural positioning, not a claim that a general AI-worker product is implemented today.

Conceptually:

```text
Agent Definition
      ↓
Definition Version
      ↓
Durable Agent Identity / Agent Instance
      │
      ├── persona / trusted identity
      ├── learned memory
      ├── capability bindings
      ├── workspace scopes
      └── application bindings
                │
        ┌───────┼────────┐
        ↓       ↓        ↓
      App A    App B    App C
        │       │        │
      role    role     role
      policy  policy   policy
      tools   tools    tools
        │       │        │
      sessions / tasks / events
```

The existing durable `AgentInstance` is the closest current continuity anchor. Treat “durable agent identity” as a long-term product concept grounded in that lifecycle, not as permission to merge current entities into one mutable aggregate.

Preserve these distinctions:

```text
Identity ≠ Definition
Identity ≠ Role
Identity ≠ Application
Identity ≠ Session
Identity ≠ Authority
```

Also preserve:

- Agent Definition is reusable, versioned configuration;
- Agent Definition Version is immutable published configuration;
- durable identity / Agent Instance anchors continuity and ownership;
- persona/trusted identity, learned memory, workspaces, capability bindings, application bindings, Sessions, Triggers, WorkItems, and tasks retain their own ownership and lifecycle;
- changing a role, application binding, definition version, persona, memory scope, or authority grant must not silently become an identity reset;
- current Definition fields named “identity” remain Definition configuration and are not by themselves the durable continuity record.

## Application Binding — future concept

An **Application Binding** should describe how the same durable identity participates in one host/application without redefining that identity.

For example:

```text
Sam + Application A → customer-support role
Sam + Application B → team-member role
Sam + Application C → researcher role
```

A future binding may include:

- role;
- application-specific instructions/context;
- knowledge/resources;
- permitted capability/tool bindings;
- authority and policy;
- application-scoped memory where required;
- application-scoped workspace where required;
- trigger/event subscriptions.

Application Binding is not implemented today. Do not add it until a concrete multi-application workflow defines ownership, mutation, versioning, authorization, and portability requirements. It must not become a back door for rewriting the underlying Agent Definition, persona, or identity-wide state.

Where an application binding is eventually required, preserve this composition without replacing today’s direct Instance → Session lifecycle:

```text
Definition
→ immutable Version
→ durable identity / Agent Instance
→ Application Binding
→ Session / Task / Event
```

## Product surface: create, teach, and hire — future

The internal model should remain rigorous while the future product surface becomes simpler for nontechnical users. Ordinary users should not need to understand prompts, MCP, provider configuration, memory-scope identifiers, policy evaluators, WorkItems, or orchestration internals.

Possible user-facing concepts include:

```text
Create your own worker
Teach/configure the worker
Start from a prepared worker
Hire/add a prepared worker to an organization
```

“Teach” or “train” is primarily product language for configuring and improving a worker through:

- instructions;
- knowledge/resources;
- examples and corrections;
- policies and permissions;
- tool/capability configuration;
- learned memory;
- evaluation cases;
- recurring work/routines.

It does not imply model fine-tuning. For example, a user might teach an Operations worker:

```text
"These are our SOPs."
"This is how we process invoices."
"These are the applications we use."
"Ask before paying anything."
"Check these systems every morning."
```

The product may translate that experience into separate Definition changes, resources, policy, memory, triggers, evaluations, and other existing or future primitives. A simpler UI must not collapse those lifecycles internally.

A normal user should eventually be able to think:

```text
This is Sam.

Sam works in Operations.

Sam can use:
- company chat
- CRM
- calendar
- browser

Sam remembers:
- appropriate organization context
- my working preferences

Sam must ask before:
- sending external messages
- purchasing
- deleting important data
```

The user should not need to reason directly about `AgentDefinitionVersion`, `ApplicationBinding`, `TriggerOccurrence`, `WorkItem`, MCP servers, provider adapters, memory-scope identifiers, or policy evaluators.

## Prepared worker / hire model — future possibility

A reusable worker offering must not share one customer’s persistent identity, memory, authority, browser state, or working history with another customer.

Prefer:

```text
Prepared Worker / Role Package
"Operations Assistant"
        │
        │ instantiate / hire
        ▼
New durable Agent Identity
"Sam"
        │
        ├── organization-specific memory
        ├── organization-specific permissions
        ├── organization-specific application bindings
        └── its own working history
```

Another organization hiring the same prepared worker receives another durable identity. It does not gain access to Sam’s learned state.

A prepared package may eventually bundle:

```text
Agent Definition
skills/capability requirements
knowledge structure
policies
recommended application bindings
evaluation cases
workflow/routine templates
UX/setup guidance
```

This is a future product/business possibility, not an implementation commitment. Do not create a marketplace or package/import/export architecture until a real product requirement defines portability, trust, versioning, provenance, installation, upgrade, and ownership semantics.

## Capabilities, tools, and authority — future direction

A durable identity does not directly own arbitrary tool implementations or credentials.

Preserve the conceptual flow:

```text
registered capability/provider
        ↓
identity/application capability binding
        ↓
policy/authorization
        ↓
execution
```

Requirements:

- registry, policy, and execution remain separate;
- capability/provider registration does not grant an identity permission to use it;
- capability availability does not imply execution authority;
- identity-wide and application/context-specific grants must be distinguishable and enforceable;
- credentials remain outside model-visible context, definitions, bindings, Admin projections, and normal history/logs;
- approvals remain action-specific where appropriate;
- background or unattended execution cannot bypass authorization;
- execution rechecks current ownership, scope, policy, and any exact-action approval;
- provider-specific DTOs and secrets remain at Infrastructure edges.

P8 may extend a real provider seam, but it must not implement this entire future binding model speculatively.

## Browser / computer use — strategic capability direction

Browser automation is a particularly attractive early extension because it can let a worker operate many existing web applications through interfaces users already know, before Agent Core has a dedicated integration for every application.

Instead of requiring all of these first:

```text
App A plugin
App B plugin
App C plugin
CRM plugin
chat plugin
ERP plugin
internal-tool plugin
```

a bounded browser capability may provide broad initial reach:

```text
                 Agent Identity
                       │
                 capabilities
                       │
          ┌────────────┼────────────┐
          ▼            ▼            ▼
       Browser     Native tools     MCP/providers
          │
          ▼
     Web applications
```

Potential browser-mediated work includes opening company chat, CRM, internal applications, or other web tools; reading information; navigating workflows; filling forms; preparing changes; and performing authorized actions.

The strategic principle is:

> Browser automation is the broad compatibility path; native integrations are the optimized path.

```text
Browser / computer use
    broad application coverage
    fast expansion
    visible to the user
          │
          │ where usage justifies it
          ▼
Native integration / API / dedicated provider
    more reliable
    more efficient
    easier to constrain
```

An early worker might use a browser for email. If email becomes a major recurring capability, a dedicated email provider may be preferable. Identity and task semantics should not depend on browser versus native execution when their normalized meaning is equivalent, but browser automation does not eliminate the need for native integrations.

### Preferred first browser approach to evaluate

Evaluate **Playwright**, potentially through Playwright MCP or a bounded native adapter around Playwright, as the first concrete browser approach. Do not decide in advance whether the final shape is a direct Playwright tool, Playwright MCP provider, browser capability adapter, or a combination. P8 should choose the narrowest shape that fits the existing architecture.

Intended boundary:

```text
Playwright / Playwright MCP
          ↓
Agent Core capability/provider boundary
          ↓
Agent Core policy + authorization
          ↓
approval / scope / execution
          ↓
Agent
```

MCP is an optional adapter/protocol, not Agent Core’s semantic center:

```text
Agent Core semantics/policy
        ↓
provider/integration adapter
        ↓
MCP where useful
        ↓
external capability
```

Never treat “an MCP tool exists” as permission for unrestricted model execution. Native Agent Core tools remain first-class, and MCP-backed tools must obey the relevant ownership, policy, approval, credential, observability, cancellation, and failure boundaries.

### Visible work and durable evidence

A headed or otherwise observable browser is product UX as well as execution infrastructure:

```text
Sam is working…

→ opened Support App
→ opened customer record
→ inspected order
→ drafted response
→ requested approval
→ sent after approval
```

Visibility can improve trust, demonstrations, debugging, intervention, approval, and accountability. Do not persist every click, selector, DOM observation, or low-level browser action as product history. Distinguish transient execution detail from meaningful durable action receipts/evidence for important observations, approvals, and side effects.

### Browser authority and isolation constraints

Browser/computer use can be equivalent to access to logged-in applications, so it must remain behind strict Agent Core authority:

- browser availability does not imply unrestricted authority;
- provider/tool registration does not grant execution permission;
- Application Bindings and policy should eventually constrain browser access;
- credentials, cookies, tokens, and secrets remain outside model-visible context wherever possible;
- do not expose raw passwords to the model merely to automate login;
- sensitive actions remain eligible for exact-action approval;
- interactive and background/unattended browser execution use the same authorization rules;
- browser execution cannot bypass existing tool policy;
- important side effects produce useful execution evidence/receipts;
- cancellation, timeout, bounded execution, and failure recovery are required;
- a compromised, failed, or non-cooperative browser provider must not corrupt durable Agent Identity, Session, or WorkItem state.

Future designs must resolve, without assuming one global logged-in profile:

```text
browser profile ownership
identity-scoped vs application-scoped browser state
session-scoped browser state
authenticated profile isolation
domain/application allowlists
cookie/session retention
multi-user/tenant isolation
```

Do not make one shared authenticated browser profile available to every identity or application.

## Scoped workspace direction — future

Keep current `SessionWorkspace` semantics intact: it is mutable, isolated, and Session-owned. Do not turn P8 into a universal persistent Agent Instance filesystem.

Persistent-worker workflows may eventually justify distinct scopes:

```text
Identity/Home Workspace
Application Workspace
Task Workspace
Session Workspace
```

These are separate ownership, visibility, retention, authorization, and cleanup lifecycles—not directories in one giant shared filesystem. Introduce a scope only when a concrete workflow requires it, and preserve immutable Definition resources, Attachments, Artifacts, and mutable workspace data as distinct concepts.

## Scoped memory direction — future

Continue separating:

```text
trusted persona/profile
instructions
knowledge/resources
learned memory
runtime/session state
```

Identity portability may eventually require learned-memory scopes such as:

```text
Identity/general memory
Relationship/user memory
Application-scoped memory
Task memory
Session memory
```

Memory does not automatically propagate across applications. Admission, visibility, retrieval, mutation, reset, retention, and portability must follow explicit scope, policy, authority, and ownership. Learned memory must not silently override trusted persona/profile, instructions, or authoritative knowledge.

## Tasks, teams, and durable execution — future

Durable agent identities may eventually communicate, send/receive tasks, delegate work, coordinate, react to events/triggers, and participate in a team or organization. This is not authorization to start a speculative multi-agent framework or universal orchestration engine.

Prefer extending existing primitives when concrete workflows require it:

```text
identity
authorization
triggers
WorkItems
approvals
sessions
memory
durable execution
```

Unattended or goal-oriented execution must not be a permanent `while(true)` LLM loop. The target is resumable, bounded, durable work:

```text
Goal / Task
    ↓
WorkItem
    ↓
agent action
    ↓
checkpoint / observation
    ↓
continue
or wait for event
or wait for approval
or complete
```

Execution may resume from a trigger, external application event, another agent, scheduled occurrence, human approval, or retry/recovery.

Termination conditions may include:

- goal satisfied;
- explicit stop condition;
- deadline/time limit;
- budget/resource limit;
- policy stop;
- human cancellation;
- unrecoverable failure.

P5 Trigger registrations/occurrences and P6 durable WorkItems are the existing architectural foundation for this direction. Future work should extend those contracts where justified rather than introduce a competing execution model.

One future destination example:

```text
Goal:
"Find a venue for the company dinner next month.
Check availability, request quotes, and prepare a recommendation.
Do not book anything without approval."

Durable Agent Identity
        ↓
WorkItem
        ↓
Browser → research venues
Calendar → inspect availability
Browser/email → request quotes
        ↓
wait
        ↓
external response/event resumes WorkItem
        ↓
compare results
        ↓
request human approval
        ↓
Browser/native integration → book
        ↓
goal complete
```

The architectural point is the composition of identity, capabilities, browser/native integrations, scoped memory, WorkItems, triggers/events, checkpoints, approvals, and stop conditions. No LLM call, process, or browser session should need to remain continuously running while waiting for real-world events.

Future team behavior should use the same durable principals:

```text
Sam — Operations
Alice — Research
Bob — Support

Sam
  → delegates research WorkItem to Alice
Alice
  → returns result/event
Sam
  → continues goal
Bob
  → may communicate/request work
```

Communication and delegation should carry explicit identity, authority, provenance, and work ownership. Do not create a magical “agent swarm” disconnected from Identity, WorkItem, Trigger, Approval, and policy boundaries.

The long-term runtime model therefore expands conceptually from only:

```text
Agent → Session → Conversation
```

toward:

```text
                Durable Agent Identity
                         │
          ┌──────────────┼──────────────┐
          ↓              ↓              ↓
     Conversation       Task           Event
          │              │              │
       Session        WorkItem       Trigger
```

This is a product/architecture north star, not a claim that the current conversation runtime is obsolete or that these future capabilities are implemented.

In summary:

> Agent Core is a runtime for durable AI identities that can grow into persistent AI workers: identities that remember appropriately, participate in multiple applications, perform authorized work, resume tasks over time, and eventually collaborate with other identities.

---

# Maintainer notes

Always keep this section.

- [ ] Admin assistant agent remains a future idea.

- [ ] Progress report via tools call [TBD].

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

- [x] Finish P7.6 Admin authoring usability before widening the P8 extension surface. P7.6 is frozen on `17d89ae`.

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

Checklist items use an explicit status. **Completed** means the work is in the tree. **Verified, no change** means the audit found the existing behavior already satisfies the item. **Deferred** means it stays as it is on purpose and is not open P7.5 work. **Not applicable** means the literal wording does not match the accepted architecture.

Observed through `docs/reports/p7.5-freeze-candidate.md`. Hosted workflow `36368766449` is green on `70a5720`:

- Ownership map is in `docs/03-system-architecture.md`. Authoring, Runtime, and Operations are responsibilities, not new projects.
- Effective configuration is shared resolution primitives plus use-case-specific composition. `EffectiveConfigurationComposer` is the Admin projection and the shared memory-policy default, not a universal runtime object. The closure audit found no contradictory model, tool, resource, memory, or trigger default.
- Cohesion audit deferred splits of the large runtime files. History-free instance methods remain because tests still call them.
- Persistence audit kept SQLite, separate stores, explicit transactions, and the single-process scheduler. Recorded in `docs/15-persistence-and-configuration.md`.
- Observability audit kept existing counters and added bounded diagnostics for the silent lifecycle, approval, quota, and Admin paths. Recorded in `docs/17-observability-and-operations.md`.
- Admin draft status, durable publications, and instance identity spacing live in `web/src/app.css`. Chat behavior was rechecked on a Synthetic turn.
- At the P7.5 freeze, the canonical handoff named P8 next. The later P7.6 requirement below is a bounded product follow-up and does not rewrite the P7.5 closure.
- `docs/13` describes the shipped Admin surface through P7G. `.agents/context/PRODUCT.md` and `DESIGN.md` stay aligned with that surface and defer behavior to `/docs`.
- The local Synthetic gate and `./scripts/compose-sqlite-volume.sh` passed. Hosted workflow `36368766449` is green on `70a5720`.

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

- [x] **Completed.** Shared resolution primitives are the composition authority. Inputs may include definition version, instance association, persona revision, model defaults, tool policy, knowledge/resources, memory policy, trigger policy, background-execution policy, and trusted caller/profile context. `EffectiveConfigurationComposer.ComposeAdmin` projects those for Admin. It is not a universal runtime configuration object.

- [x] **Verified, no change.** Chat bind, Admin projection, triggered work, and draft evaluation reuse `MemoryPolicyOf`, `SessionModelBinder.PinDefault`, `ToolCatalog.For`, `ToolPolicy.EvaluateExecution`, `OccurrenceCompatibility`, and `RoleEnvironments` where the semantics match. The closure audit found no contradictory model, tool, resource, memory, or trigger default.

- [x] **Not applicable as one object.** Session bind, durable work, and draft evaluation compose their own state from those primitives. They do not call `ComposeAdmin`. Forcing one configuration object would mix Admin projection, live selection, and detached execution.

- [x] **Verified, no change.** Tool execution stays on `ToolPolicy.EvaluateExecution`. Trigger eligibility stays on `OccurrenceCompatibility`. Memory default stays on `MemoryPolicyOf`. Resource bytes stay on the definition-resource owner. Admin reads project configuration. They do not authorize execution.

- [x] **Completed.** Shared policy decisions are centralized only where the contract is shared. Use-case composition stays at the caller.

- [x] **Verified, no change.** Approval and ownership are rechecked at execution. Detached admission denies session-scoped tools. A stale Admin projection cannot grant a tool or an approval.

### P7.5B stop condition

Effective configuration and policy decisions have explicit ownership and no known parallel implementations that can drift.

---

## P7.5C — Application complexity and domain cleanup

- [x] **Completed.** The cohesion audit identified the large runtime and Admin files. Splits were deferred where a split would not remove a second owner. Recorded in `docs/03-system-architecture.md`.

- [x] **Deferred.** History-free `AgentInstanceService` managed methods remain because tests still call them. Production HTTP and `SessionManager` do not. Retiring them is not P7.5 work.

- [x] **Verified, no change.** `AgentInstanceService` and `AdminAgentInstanceService` are different use cases. `EffectiveConfigurationComposer` and `AdminEffectiveConfigurationResolver` are the assembler and the projection return. Those names stay, and the composer is documented as Admin-specific plus the memory-policy default. No product concept was renamed in this closure.

- [x] **Verified, no change.** Domain stayed on durable concepts and policies. P7.5 did not move provider or storage mechanics into Domain.

- [x] **Verified, no change.** Application stayed on use cases, orchestration, and ports. HTTP mapping, EF, and provider DTOs stayed out of it.

- [x] **Verified, no change.** Infrastructure remains the SQLite, filesystem, Docker, and provider implementation.

- [x] **Verified, no change.** P7.5 did not add a `Common` project, MediatR, a generic repository, an internal event bus, or a microservice split.

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

- [x] **Verified, no change.** `GET /health` returns `status`, `profile`, and `protocolVersion` after persistence initialization. That is the readiness response for native, Compose, and CI. No second health service was added.

- [x] **Verified, no change.** Instruments use the BCL `ActivitySource` and `Meter` named `AgentCore.Runtime`. No OpenTelemetry package is referenced. `Observability:OtlpEnabled` defaults false and, when true, requires an absolute endpoint without attaching an exporter. A later host can listen to the existing meter and activity source. Local and Synthetic runs do not need a collector.

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

- [x] **Completed** for the owners that changed: `docs/03-system-architecture.md`, `docs/04-backend-interfaces.md`, `docs/13-frontend-implementation-spec.md`, `docs/15-persistence-and-configuration.md`, `docs/17-observability-and-operations.md`, and the implementation-plan/report references. **Verified, no change** for `docs/10-technology-decisions.md` and `docs/11-repository-structure.md`: no technology or repository-boundary decision changed.

- [x] **Completed.** The 2026-09-28 Admin visual audit reviewed home, the definition editor, and an instance error at desktop and 390px, including loading, error, validation, dirty draft, delete and discard confirmations, and keyboard focus. One shared inventory row placed the status tag beside a short description and below a longer one. `.admin-inventory-row-description` now takes the full row so the tag stays on the next line. Deprecate and archive confirmations use the same confirmed-dialog pattern; this disposable catalog had no durable publication or managed instance, so those two dialogs did not render.

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

# P7.6 — Admin usability closure

**Status: closed/frozen on `17d89ae`.**

The unchecked acceptance items below are retained as the historical phase specification, not as open work. Observed closure evidence is recorded at the end of P7.6 and in `docs/reports/p7.6-freeze-candidate.md`.

## Goal

Finish the Admin authoring and lifecycle UX for the model that already exists before widening Agent Core with P8 extensions.

P7.6 is intentionally bounded:

```text
complete Definition authoring
explicit Definition / Instance creation
clear Definition/version semantics
clear Knowledge Source ↔ Resource binding
multi-file + folder resource ingestion
final Admin UX/test/documentation closure
```

P7.6 may make small model/API corrections where the current product surface exposes a real mismatch. It must not become an architecture redesign or a platform/plugin phase.

## Current-state facts to preserve

The current implementation already establishes:

- `AgentDefinitionCandidate` as the mutable draft payload and immutable `AgentDefinition` as the published payload;
- durable new-draft creation and forked-draft creation;
- immutable published Definition versions;
- managed Agent Instance creation against a published version;
- typed Persona revision and Active/Archived lifecycle;
- Definition resources with immutable content and draft/publication bindings;
- validation, evaluation, diff, and publish gates tied to draft revision/fingerprint;
- read-only Admin effective configuration;
- resource limits of 8 MiB/item, 64 items/draft, and 64 MiB aggregate;
- existing single-file resource upload;
- Knowledge Sources as semantic identity/title/citation references;
- current legacy knowledge resolution by implicit `knowledge/{identity}` logical path.

Do not duplicate these concepts to implement the new UX.

---

## P7.6A — Complete Definition Form + Advanced JSON authoring

- [ ] Expose structured editing for the complete supported `AgentDefinitionCandidate`.

At minimum:

```text
Identity
Goals
System Instructions

Behavior Policy
Conversation Policy
Initiative Policy

Model Defaults
Provider Preferences
Voice

Memory Policy
Trigger / Automation Policy

Environment / Capabilities
Metadata
```

- [ ] Keep the existing product mental model understandable. Do not create one top-level tab for every low-level policy object.

A suitable shape is:

```text
Definition
  Form | Advanced JSON

  Identity & goals
  Instructions
  Behavior & conversation
  Initiative
  Model & providers
  Voice
  Memory policy
  Automation policy
  Advanced / metadata

Capabilities
  Harness
  Tools
  Knowledge Sources
  Workspace / attachments

Resources

Test & Publish
```

- [ ] Form and Advanced JSON must edit **one candidate draft**, not two parallel representations.

Required behavior:

```text
Form edit
  ↓
same local candidate
  ↓
JSON view

JSON edit
  ↓ parse
same local candidate
  ↓
Form view
```

- [ ] Switching Form/JSON views must not silently discard valid unsaved edits.

- [ ] Invalid JSON must remain a local editor error. It must not mutate the saved draft or advance its revision.

- [ ] Saving from either view must use the same draft `expectedRevision` conflict protection.

- [ ] Both views must use the same server-side persistence validation, publication validation, evaluation, diff, and publish path.

- [ ] The structured Form must not silently erase supported candidate fields merely because a particular section was not opened or edited.

- [ ] Definition ID remains immutable after draft creation.

- [ ] Keep secret detection and configured provider/tool validation authoritative on the server.

- [ ] Do not turn the Admin candidate editor into an alternate effective-runtime-configuration editor.

### P7.6A acceptance

A maintainer can create/open one draft, change every supported candidate area through the Form or JSON, switch repeatedly between both views without loss, save once, validate/evaluate, review the resulting diff, and publish through the existing immutable publication flow.

---

## P7.6B — Explicit New Definition / New Instance flows

### New Definition

- [ ] Add **`+ New definition`** on Admin home.

- [ ] Creating a new Definition must not require forking an existing version.

- [ ] Preserve the current domain requirement that a persisted `AgentDefinitionCandidate` has a structurally valid required graph.

Do **not** weaken published/domain invariants merely to store null-filled UI drafts.

- [ ] Create a canonical server-owned starter candidate for a new Definition.

Requirements:

- the user supplies a valid new Definition ID;
- the server owns starter defaults rather than the browser hardcoding provider/policy internals;
- the starter is structurally persistence-valid;
- it opens immediately as a normal `SourceKind.New` draft;
- the user still passes the ordinary save/validate/evaluate/diff/publish flow;
- future starter-default changes do not affect already-created drafts.

### New Instance

- [ ] Add **`+ New instance`** on Admin home.

Flow:

```text
choose logical Definition
→ choose published version
→ choose Definition-default persona or Custom persona
→ create durable managed Agent Instance
→ open Instance Admin
```

- [ ] Default version selection should prefer the newest non-deprecated usable publication. Do not invent a stored `defaultVersion` pointer solely for this UI.

- [ ] Deprecated versions may remain explicitly reachable where current lifecycle semantics require it, but must not be the normal/default target for new managed work. Show a warning when explicitly selected.

- [ ] If Custom persona is supplied during creation, persist it **atomically with instance creation** rather than creating a default-persona instance and immediately patching it.

- [ ] Record one coherent Admin lifecycle event/history outcome for custom-persona creation rather than an accidental create-then-edit sequence.

- [ ] Keep `Start managed chat` as a convenience action:

```text
create managed instance
→ create Session
→ open Chat
```

It must not remain the only obvious managed-instance creation path.

- [ ] Keep Instance hard-delete out of P7.6. Lifecycle remains Active/Archived.

### P7.6B acceptance

Admin home independently supports creating a reusable Definition draft and a durable managed Instance. Starting a managed chat remains a shortcut rather than a hidden CRUD mechanism.

---

## P7.6C — Definition/version UX semantics

- [ ] Keep one inventory row per logical Definition.

- [ ] Continue showing drafts/publications/versions hierarchically under that Definition rather than as separate logical Definitions.

- [ ] Clearly distinguish factual version concepts:

```text
latest version
latest active / usable version
deprecated historical version
instance-pinned active version
```

- [ ] Do **not** add a persistent “default version” concept unless an independent runtime/product requirement requires one.

- [ ] When the numerically latest version is deprecated, the UI must not imply that it is the preferred version for new work.

Example:

```text
Customer Support

Latest         v6 · Deprecated
Latest active  v5 · Active
```

- [ ] Deprecated versions remain visible and explicitly forkable.

- [ ] Deprecated versions must not be the default fork source while a non-deprecated version exists.

- [ ] Preserve exact historical version lookup and existing Session/Instance associations.

- [ ] Keep built-in and durable source/status provenance visible where it helps explain lifecycle behavior without making each source a separate Definition.

### P7.6C acceptance

A maintainer can tell, without knowing persistence internals, which logical Definition they are viewing, which version is numerically latest, which version is suitable for new work, what an existing Instance is pinned to, and which historical versions are deprecated.

---

## P7.6D — Make Knowledge Source ↔ Resource binding explicit

The current implicit relationship is:

```text
Knowledge Source identity
  ↓ convention
knowledge/{identity}
```

This becomes too opaque once Admin supports arbitrary file/folder resource ingestion.

- [ ] Extend the Knowledge Source binding with an explicit optional logical resource path.

Conceptually:

```text
Knowledge Source
  identity: refund-policy
  title: Refund Policy
  citation: refund-policy@v3
  resourcePath: knowledge/refund-policy.md
```

- [ ] Use normalized logical path as the Definition-level binding. Do not use draft `ResourceId` as the durable semantic reference.

Rationale:

```text
ResourceId
= storage/lifecycle identity

Logical path
= portable Definition/package reference
```

- [ ] Preserve backward compatibility for existing Definitions that omit `resourcePath`.

Legacy fallback:

```text
resourcePath missing
→ resolve knowledge/{identity}
```

- [ ] Put explicit path resolution/fallback behind one shared authoritative helper so runtime resolution and draft validation cannot drift.

- [ ] Validation must require the resolved path to bind a resource of `Kind = Knowledge`.

- [ ] Keep the existing textual Knowledge media-type rules.

- [ ] Admin Knowledge Source UX should select from currently bound Knowledge resources instead of looking like an unrelated parallel system.

Suggested interaction:

```text
Knowledge resource
[ knowledge/refund-policy.md ▼ ]

Identity
refund-policy

Title
Refund Policy

Citation
refund-policy@v3
```

- [ ] Identity remains the stable semantic/tool-facing identity. Title remains human-readable metadata. Citation remains the citation label. `resourcePath` identifies packaged content.

- [ ] Removing or changing a resource used by a Knowledge Source should produce an obvious draft validation blocker. Do not silently rewrite or delete the semantic Knowledge Source.

- [ ] Published versions freeze the candidate binding and the matching immutable publication resource set together.

### P7.6D acceptance

A maintainer can inspect a Knowledge Source and immediately identify the packaged resource that backs it. Old Definitions using the implicit convention still behave the same.

---

## P7.6E — Multi-file and folder resource ingestion

Keep the existing single-file upload.

Add:

- [ ] multi-file upload;
- [ ] folder selection where the browser supports it;
- [ ] folder drag-and-drop where supported;
- [ ] preservation of safe relative logical paths.

### Kind inference

Infer `Kind` from conventional top-level folders:

```text
knowledge/*   → Knowledge
templates/*   → Template
references/*  → Reference
assets/*      → StaticAsset
eval/*        → EvalFixture
```

This is a convenience inference, not a new runtime storage architecture.

- [ ] Allow per-file Kind correction before binding.

- [ ] Allow per-file logical-path correction before binding.

- [ ] Do not infer a dangerous or ambiguous path silently. Path normalization/traversal checks remain server-authoritative.

### Bulk preview

- [ ] Show a preview before draft mutation.

Example:

```text
Path                              Kind
knowledge/refund-policy.md        Knowledge
templates/reply.md                Template
assets/logo.png                   StaticAsset
```

The preview should surface at least:

- duplicate/colliding logical paths;
- unsupported/invalid paths;
- inferred Kind;
- file size;
- media type when useful;
- known item/aggregate-limit violations.

### Revision and atomicity

Do not implement folder import as many competing resource metadata writes using the same draft revision.

Use a coherent batch-bind operation:

```text
select files/folder
       ↓
build + edit manifest
       ↓
upload/hash content
       ↓
single batch resource bind against expected draft revision
       ↓
one resulting draft revision
```

- [ ] Content payload upload may remain individually hashed/deduplicated.

- [ ] The final draft resource-binding mutation must use one `expectedRevision`.

- [ ] The final batch bind should be atomic for draft resource metadata: either the accepted manifest binds coherently or no metadata subset is committed.

- [ ] One successful batch bind advances the draft revision coherently rather than once per file.

- [ ] Server-side item, byte, aggregate, media-type, path, duplicate, and secret-content checks remain authoritative.

### Failures

- [ ] Report per-file content-upload failures clearly.

- [ ] Allow safe retry of failed content uploads without duplicating already-addressed immutable content.

- [ ] Distinguish content-upload failure from final draft-bind/revision failure.

- [ ] A stale revision on final bind must not leave a half-bound draft manifest.

- [ ] After a successful bind, refresh the draft/revision and resource list from the server.

### P7.6E acceptance

A maintainer can drop/select a small folder tree, see the exact paths and inferred kinds that will enter the Definition, correct mistakes, bind the manifest under one coherent revision, and understand any individual upload or final-bind failure.

---

## P7.6F — Instance Admin polish

Preserve the existing conceptual split:

```text
Instance management
  Persona Form | JSON
  Active version
  Lifecycle

Memory & automation

Effective configuration
```

- [ ] Keep Instance Persona Form and Persona JSON as two views over mutable **instance-owned persona data only**.

- [ ] Do not expose the full persistence record as editable Instance JSON.

- [ ] Keep revisions, timestamps, compatibility flags, memory, trigger registrations, WorkItems, and other lifecycle state outside Persona JSON.

- [ ] Keep Effective Configuration read-only.

- [ ] Effective Configuration remains a resolved, secret-safe projection. It must not become a universal editable runtime object.

- [ ] Keep Active/Archived lifecycle and active-version reassociation explicit and revision-protected.

- [ ] Continue distinguishing Definition identity/default persona from durable Instance persona revisions.

### P7.6F acceptance

Editing instance-owned state cannot accidentally mutate Definition configuration or runtime-derived Effective Configuration, and viewing Effective Configuration cannot grant or change runtime authority.

---

## P7.6G — UX, testing, documentation, and freeze

### UX review

- [ ] Perform a complete Admin UX review after behavior is implemented, using the established Impeccable workflow/skill.

Review at least:

```text
Admin home
new Definition flow
Definition Form
Advanced JSON
Capabilities
Knowledge Source binding
single/multi/folder resource upload
bulk preview
Test & Publish
new Instance flow
Instance Persona Form/JSON
version/lifecycle controls
Memory & automation
Effective Configuration
```

- [ ] Review desktop and narrow/mobile layout, including approximately 390 px width.

- [ ] Review keyboard navigation and visible focus.

- [ ] Review loading, empty, error, retry, stale-revision, invalid-JSON, validation-blocked, dirty-draft, destructive-confirmation, upload-failure, and partial-content-upload states.

- [ ] Do not hide required lifecycle semantics behind hover-only UI.

- [ ] Keep user-visible terminology aligned with the Definition → Version → Instance → Session model.

### Automated verification

- [ ] Add/extend Domain/Application/API tests for:

  - complete candidate round-trip;
  - canonical new-Definition starter;
  - new managed Instance with default persona;
  - new managed Instance with atomic custom persona;
  - deprecated-version selection rules where enforced;
  - explicit Knowledge Source `resourcePath`;
  - legacy implicit Knowledge Source fallback;
  - Knowledge Kind/media validation;
  - batch resource bind atomicity;
  - stale batch revision;
  - duplicate/path/size/count/aggregate-limit rejection.

- [ ] Add/extend frontend tests for:

  - Form ↔ JSON round-trip without data loss;
  - invalid JSON isolation;
  - dirty-state behavior;
  - new Definition flow;
  - new Instance flow;
  - Definition/version labels;
  - Knowledge resource selection;
  - Kind/path inference and correction;
  - bulk preview;
  - upload and bind failure reporting;
  - Persona JSON scope;
  - Effective Configuration read-only behavior.

- [ ] Add meaningful Synthetic Playwright coverage for the whole P7.6 happy path:

```text
New Definition
→ edit Form
→ inspect/edit JSON
→ bind resources from multi/folder flow
→ bind Knowledge Source
→ validate/evaluate/diff
→ publish
→ New Instance with custom persona
→ inspect Effective Configuration
→ create/open managed chat
```

- [ ] Add focused Synthetic E2E for stale/conflict/error states that are practical at the browser boundary.

- [ ] Re-run the complete deterministic solution/frontend test suite.

- [ ] Re-run Compose/SQLite smoke coverage because P7.6 touches durable Admin/resource lifecycle.

- [ ] Keep hosted providers opt-in; P7.6 closure must not depend on a hosted model merely to prove Admin CRUD/resource semantics.

### Documentation and design sync

- [ ] Update `docs/13-frontend-implementation-spec.md` for the final Admin information architecture and interactions.

- [ ] Update architecture/backend/persistence docs where the explicit Knowledge Source resource-path binding or batch resource mutation changes a contract.

Likely owners include:

```text
docs/03-system-architecture.md
docs/04-backend-interfaces.md
docs/13-frontend-implementation-spec.md
docs/15-persistence-and-configuration.md
```

Update `docs/10-technology-decisions.md` only if a real technology/architecture decision changed.

- [ ] Sync `.agents/context/PRODUCT.md` and `.agents/context/DESIGN.md` after the observed UI is final. `/docs` remains authoritative for detailed behavior.

- [ ] Sync the design system/tokens/components after the final Impeccable review rather than before observed interaction patterns settle.

- [ ] Record a bounded `docs/reports/p7.6-freeze-candidate.md`.

The report should identify:

- behavior added;
- small model/API corrections made;
- compatibility behavior retained;
- explicit non-goals/deferred work;
- deterministic test evidence;
- Synthetic E2E evidence;
- Compose evidence;
- hosted CI candidate SHA/workflow.

### P7.6 stop condition

P7.6 is complete when:

```text
the entire supported Definition candidate can be authored through Form or JSON
Form and JSON operate on one revisioned draft without loss
Admin exposes explicit New Definition and New Instance flows
published versions remain immutable
Definition/version semantics are understandable without a new default-version concept
Knowledge Sources visibly and durably bind to Knowledge resources
legacy implicit knowledge bindings still work
single-file, multi-file, and folder resource ingestion share one safe resource model
bulk resource binding is revision-safe and not half-applied
Instance JSON remains persona-scoped
Effective Configuration remains read-only
Admin UX has been reviewed/polished and design context is synchronized
deterministic tests + Synthetic E2E + Compose are green
hosted CI is green on the recorded closure candidate
```

Observed: P7.6 is frozen on `17d89ae`. Hosted workflow [`36427670239`](https://github.com/trannamtrung1st/agent-core/actions/runs/36427670239) is green on that SHA. Synthetic offline gates and Compose smoke both succeeded. Closure report: `docs/reports/p7.6-freeze-candidate.md`. The commit that records this URL is documentation only.

---

## P7.6 non-goals / deferred platform work

Do **not** pull these into P7.6:

- plugins / external provider framework;
- plugin marketplace;
- persistent Agent Instance filesystem;
- ZIP/package import/export format;
- Git repository resource sync;
- remote filesystem browsing;
- remote object-storage browsing;
- live folder sync/watch;
- FTP/SFTP;
- multi-agent communication/orchestration;
- visual workflow builder;
- distributed job/queue infrastructure;
- production PostgreSQL/Redis/Kubernetes migration.

These remain deferred future work. P8 may address only a concrete extensibility need that belongs to its bounded goal; the rest must wait for their own evidence and dependency order.

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

# P7.7 — Operational Diagnosability & Realtime Debuggability

**Status: closed/frozen on `40a1d92` (workflow `36594702224` green).** Closure report: `docs/reports/p7.7-freeze-candidate.md`.

Make failures and runtime activity diagnosable across conversation execution, realtime transport, background work, triggers, tools/providers, and HTTP/Admin operations without leaking sensitive internals or introducing production infrastructure prematurely.

Desired flow:

```text
user-visible failure
        ↓
safe DiagnosticId / CorrelationId
        ↓
structured server logs / Activity trace
        ↓
Session / Response / Trigger / WorkItem / Agent Instance context
        ↓
developer can locate the real failure quickly
```

## Failure identity and correlation

Intended semantics:

```text
DiagnosticId
= one specific failure occurrence that can safely be shown/copied

CorrelationId
= existing logical operation / turn / causal-flow correlation

TraceId
= infrastructure/distributed trace identifier when a real Activity exists
```

Requirements:

- server-owned IDs;
- preserve the existing server-owned correlation authority boundary;
- clients must not invent trusted correlation/diagnostic identity;
- do not fabricate a TraceId when no Activity/trace exists;
- relate IDs where useful without treating them as interchangeable.

A safe diagnostic reference should be available for meaningful failures across conversation/model response execution, realtime errors, trigger/background execution, WorkItem execution, tool/provider execution, and relevant HTTP/Admin failures.

Prefer a first-class safe diagnostic field where appropriate rather than hiding identity inside arbitrary extension metadata.

## Structured failure logging

- [ ] Normalize meaningful unexpected-failure logging so server-side failure logs retain the actual exception object/stack trace internally and include relevant stable identifiers when available, such as:
  ```text
  DiagnosticId
  CorrelationId
  TraceId
  SessionId
  ResponseId
  AgentInstanceId
  TriggerRegistrationId / TriggerOccurrenceId
  WorkItemId
  ErrorCategory
  ErrorCode
  ```
- [ ] Do not require every log event to contain every ID.
- [ ] Close the observed inconsistency: some hosted/background services log only exception type while conversation execution preserves the exception object.
- [ ] Preserve existing safe redaction/content-logging rules.

## Safe failure UX

- [ ] For a failed agent response or other user-visible runtime failure, provide a subtle diagnostic affordance rather than exposing raw internals (for example: response failed, user-safe category/message, optional **Error details** / copy diagnostics).
- [ ] Copy/details may expose only safe fields such as DiagnosticId, CorrelationId, SessionId, ResponseId, error category/code, and relevant WorkItem/Trigger identifiers when useful.
- [ ] Never expose through normal UI: stack traces; raw provider responses/bodies; prompts or hidden reasoning; credentials/tokens/cookies/API keys; filesystem/internal host paths; unrestricted request/response payloads; other sensitive implementation details.
- [ ] Where consistent with the durable response/error model, safe diagnostic metadata remains inspectable after reload/history without turning raw exceptions into durable conversation content.
- [ ] Apply equivalent safe diagnostic behavior to HTTP/Admin `ProblemDetails` where appropriate.
- [ ] After UI behavior settles: review/polish via the existing Impeccable workflow; update frontend implementation/design documentation if interaction rules changed; sync design context/system only after observed UI is final.

## Realtime JSON diagnostic mode

- [ ] Keep SignalR/MessagePack as the canonical/default production-like realtime path.
- [ ] Add development/debug configuration so the frontend connection may use SignalR JSON instead of MessagePack for WebSocket inspection in browser DevTools.
- [ ] Rules: MessagePack = canonical/default; JSON = optional diagnostic transport only.
- [ ] JSON must not become a second semantic protocol, separate hub, separate event contract, or separate lifecycle/reconnect implementation.
- [ ] Both transports must preserve the same DTO/event semantics, protocol version, sequencing, correlation/causation behavior, reconnect/replay behavior, and error model.
- [ ] Keep comprehensive realtime coverage on MessagePack; add a smaller JSON parity/smoke gate for the essential conversation path only (no full duplicate JSON+MessagePack CI matrix).

## Observability foundation

- [ ] Build on existing `RuntimeTelemetry`, `ActivitySource`, `Meter`, `OperationalDiagnostics`, redaction rules, and Observability configuration — no parallel telemetry subsystem.
- [ ] Wire or complete internal tracing/metrics/logging behavior required for diagnostics; production observability infrastructure (Grafana/Tempo/Jaeger/etc., general logging platform) remains deferred.

## Scheduling / background-service stance (unchanged)

P7.7 does **not** replace the current Trigger/Occurrence/WorkItem scheduling architecture:

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

Hosted services remain infrastructure wake-up/execution mechanisms. Do not add Hangfire, Quartz.NET, a distributed scheduler, Redis, a queue broker, or another background-job framework in P7.7.

Architecture direction:

```text
Agent Core owns:
- trigger semantics
- ownership
- authorization/policy
- recurrence meaning
- occurrence admission/dedupe
- WorkItem lifecycle
- approvals
- agent execution semantics

Infrastructure may later own:
- wake-up mechanics
- distributed trigger acquisition
- worker dispatch
- clustering/failover
```

A future Hangfire/Quartz/distributed implementation, if justified, should sit underneath an appropriate infrastructure seam rather than replace Agent Core's Trigger/Occurrence/WorkItem domain model.

Deferred scheduling work (not P7.7):

```text
MisfirePolicy:
- CoalesceLatest (current effective behavior)
- SkipMissed
- CatchUp
```

Introduce only when a concrete workflow requires more than current coalescing behavior.

## P7.7 non-goals

- Hangfire, Quartz.NET, distributed scheduler/workers, Redis/message broker;
- production Grafana/Tempo/Jaeger/etc. deployment or general production logging platform;
- PostgreSQL migration;
- P8 provider/plugin/browser implementation, universal provider abstraction;
- changes to Trigger/Occurrence/WorkItem semantic ownership;
- exposing raw internal exceptions to users;
- full duplicate JSON+MessagePack CI matrix.

## P7.7 verification / acceptance

Observed closure evidence is in `docs/reports/p7.7-freeze-candidate.md`. Hosted workflow `36594702224` is green on `40a1d92`.

- [x] A failed agent response can provide a safe diagnostic reference; copied diagnostics are sufficient to locate the corresponding structured server failure.
- [x] Correlation remains server-owned; clients cannot supply trusted diagnostic/correlation identity.
- [x] Unexpected hosted-service failures preserve exception details server-side (not only exception type).
- [x] Sensitive data is not exposed in user-facing diagnostics or unsafe `ProblemDetails` fields.
- [x] HTTP/Admin errors can carry equivalent safe diagnostic identity where appropriate.
- [x] MessagePack remains the default realtime transport; configurable JSON realtime mode works for the essential conversation flow with equivalent semantic contracts.
- [x] Deterministic automated tests cover new diagnostic contracts and redaction behavior; frontend tests cover failure-detail/copy behavior.
- [x] Meaningful Synthetic E2E covers user-visible failure → diagnostics when practical.
- [x] Full existing deterministic gates remain green; Compose/SQLite smoke remains green if durable error metadata/storage changes.
- [x] Docs/design context synchronized after implementation; bounded P7.7 closure report produced before P8 begins.

## P7.7 stop condition

P7.7 is complete when a developer can start from a user-visible failure and reliably trace it through safe IDs into structured server diagnostics, realtime JSON can be enabled for debugging without changing realtime semantics, sensitive internals remain protected, existing architecture is preserved, and deterministic/hosted closure gates are green.

After P7.7 is frozen, **P8 — harness/platform extensibility** becomes active again with the existing browser/provider-extensibility direction.

---

# P8 — Harness/platform extensibility

**Status: next after P7.7.**

P7.6 is frozen on `17d89ae`. Hosted workflow `36427670239` is green on that SHA.

When P8 begins (after P7.7), start from one concrete provider need.

The Product / Architecture North Star above is not itself a P8 backlog. P8 must remain evidence-driven and narrowly scoped to real extension seams.

**Strong first candidate to evaluate:** bounded browser automation through Playwright, Playwright MCP, or a narrow adapter around Playwright.

Current repository evidence:

- Playwright already exists as frontend E2E infrastructure, and Playwright MCP is configured for development/testing;
- neither is currently an Agent Core runtime capability or evidence that product browser automation is implemented;
- Agent Core already has a trusted tool registry, role allowlists, execution-time policy checks, exact-action approvals, cancellation/time limits, detached-execution restrictions, and durable WorkItem checkpoints;
- runtime tool registration and dispatch remain static/in-process, so a real browser provider would exercise the “second tool host/provider” seam identified by P7.5;
- browser profile ownership, authentication-state isolation, application/domain scope, durable receipts, and live versus detached execution remain unresolved design inputs.

Candidate reasoning:

```text
real product requirement:
agent needs to interact with arbitrary existing applications

        ↓

concrete second capability/provider:
Playwright browser automation

        ↓

exercise only the extension seams actually required

        ↓

learn what the real provider/tool architecture needs
```

This is preferable to inventing a universal plugin or MCP framework first. It does not declare the P8 design or implementation complete.

## Goal

Make the established Agent Core harness extensible without turning every implementation detail into a plugin API.

## Platform/extensibility

- [ ] Use P7.5 findings to identify extension seams backed by real second implementations.

Potential boundaries:

```text
model providers
tool providers
integration providers
browser/computer capability providers
trigger/event sources
sandbox providers
```

Do not force these into one generic provider abstraction.

- [ ] Add external tool-provider/plugin extensibility only when another real provider/integration justifies it.

MCP-like providers may be adapters.

Requirements:

- native Agent Core tools remain first-class;
- external tools still pass through Agent Core policy/authorization;
- MCP remains an adapter option rather than an authority source or universal internal protocol;
- provider credentials remain outside model context;
- provider-specific DTOs do not leak into Domain/Application contracts;
- extension failure does not corrupt durable agent/session state.

- [ ] Define extension identity/versioning/compatibility only to the degree required by actual extensions.

- [ ] Extend P7/P7.6 validation for provider/plugin-specific concerns when a real extension exists:

  - provider availability/capability mismatches;
  - missing provider references;
  - provider-specific permissions/configuration;
  - compatibility/versioning;
  - extension-specific validation/evaluation.

- [ ] When an extension is added, reuse the shared resolution primitives for any decision whose semantics should match. Do not introduce a second policy for that decision, and do not fold the extension into one universal configuration object.

- [ ] Revisit a durable mutable Agent Instance workspace only when a concrete cross-session file workflow requires it.

  Any accepted design must name its scope and lifecycle explicitly; do not generalize `SessionWorkspace` into a universal identity filesystem.

- [ ] Revisit richer reusable evaluation suites when multiple harness/provider implementations make them useful.

## P8 non-goals

Do not automatically turn P8 into:

- a plugin marketplace;
- prepared-worker marketplace or package system;
- visual workflow builder;
- multi-agent orchestration engine;
- unrestricted browser agent or globally shared authenticated browser profile;
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

These items do not block P7.7 or P8.

## Real P3 provider verification

- [ ] Revisit the known Real/OpenRouter historical-image reread gap separately from Admin/platform work.

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
- [x] P7.5 architecture/infrastructure-readiness consolidation frozen on `70a5720`.
- [x] P7.6 Admin usability closure frozen on `17d89ae`.
- [x] P7.7 operational diagnosability frozen on `40a1d92` (workflow `36594702224` green).

---

# Next implementation item

**P7.7 — Operational Diagnosability & Realtime Debuggability** is frozen on `40a1d92` ([workflow `36594702224`](https://github.com/trannamtrung1st/agent-core/actions/runs/36594702224) green). See [P7.7 closure report](docs/reports/p7.7-freeze-candidate.md).

Closed scope:

```text
1. server-owned DiagnosticId / CorrelationId / TraceId semantics and safe user-facing diagnostics
2. structured failure logging with stable IDs and preserved exception detail server-side
3. optional SignalR JSON diagnostic transport with MessagePack remaining canonical
4. observability wiring on existing RuntimeTelemetry/Activity/redaction — no production stack
5. preserve Trigger/Occurrence/WorkItem ownership and current hosted-service scheduling
6. deterministic + frontend tests, closure report, then hand off to P8
```

**P8 — harness/platform extensibility** is next (browser/provider seam; see P8 section).

P0–P7.7 remain frozen. Do not rewrite their closure evidence or treat the Product / Architecture North Star as implemented behavior.

P7.7 is frozen on `40a1d92`. P9 and P10 remain requirement-triggered and must not be pulled forward to support speculative P8 work.

Do **not** begin P8 by replacing SQLite, Docker, local storage, the single-process scheduler, or the modular monolith.
