# Initiative 9 — Task Delegation & Communication

**Status:** Implemented with focused local verification; [delivery evidence and qualification limits](../reports/initiative-9-local-verification-2026-10-27.md)
**Prepared:** 2026-10-27
**Repository:** [`trannamtrung1st/agent-core`](https://github.com/trannamtrung1st/agent-core)
**Reviewed baseline:** `main` at [`0ca5eac3b56d7d9e1788b63d14785a205a55cdfc`](https://github.com/trannamtrung1st/agent-core/tree/0ca5eac3b56d7d9e1788b63d14785a205a55cdfc) (2026-10-27)
**Intended canonical proposal path:** `docs/proposals/initiative-9-task-delegation-communication.md`
**Delivery policy:** Clean, complete cutover; disposable development data may be reset **explicitly**; no legacy API, alias, decoder, dual-write, compatibility shim or runtime fallback.

> **Authorization boundary:** This document specifies the successor design and the documentation changes needed to authorize Initiative 9. It is not an instruction to silently modify production code, databases, existing tenant data or prior verification reports. An implementation is complete only after all acceptance gates in this document pass on the actual implementation source revision.

## 0. Executive decision

Implement **durable, same-tenant agent-to-agent task delegation and one-to-one communication**, using the **existing Session → Activation → AgentRun execution path**, current tenant IAM/Review, Space/Membership authority, Run coordinator, Session Runtime mailbox, Artifact and Evaluation ownership, and existing React/Ant Design v6 experience.

The new **Delegated Task** is a small, durable **coordination/lineage aggregate**, not an alternative execution unit. It links one source task/Run to a worker-owned ordinary Session, the worker's own admitted AgentRun, bounded messages, immutable outcomes and parent reporting. The worker always runs as its **own Agent Instance and executor principal**. The caller's identity and permissions never become the worker's credentials or general operating authority.

Provide two communication forms on one guarded message mechanism:

1. **Task conversation:** messages between the delegating Agent and the assigned Agent, scoped to one task; progress, questions, replies, completion and acknowledgement.
2. **Direct Agent thread:** exactly two tenant Agent Instances, without a delegated task; durable messages with an explicit, opted-in recipient wake policy. No group membership, shared multi-user transcript or group-chat semantics.

**Breaking cutover:** Retire the existing model-facing `background.start`, `background.list`, `background.inspect`, `background.take` and `background.acknowledge` in favor of one canonical `tasks.*` model-facing delegation/inbox contract. Existing immediate **same-Agent** children migrate to ordinary Tasks with `targetInstanceId == sourceInstanceId`; cross-Agent Tasks use exactly the same contract. Keep `work.complete` and `execution.wait` as the underlying established Run completion/suspension mechanisms. Do **not** retire independent Automation/Event/Manual execution or its legitimate existing-conversation delivery semantics; those are separate origins, not a competing child-task API. Migrate any remaining immediate-background report/read logic that is exclusively child-task-specific; if an existing shared function serves Automation reporting, split its explicit semantic ownership rather than retain a hidden legacy task path.

**No new execution engine, scheduler, broker, actor system, distributed workers, autonomous group chat, A2A protocol or arbitrary workflow DSL.** The existing bounded tenant Run coordinator performs task admission/recovery and dispatch. Decision Intelligence may *advise* eligible target selection but may never authorize it.

### 0.1 Product outcomes

A user or authorized Agent can assign a bounded objective to another eligible Agent; inspect its status, correspondence and saved original result; supply clarifications; and receive one truthful, attributable completion or attention notification. The delegate can work independently, invoke only its own authorized tools, and return a result/artifact reference subject to separate source/recipient permissions. A parent Session can track multiple child tasks and can deliberately continue work on their factual outcomes. A one-to-one Agent conversation can occur without creating a task. All of this must recover across SQLite host recreation without duplicate **logical Task/Activation/Run admission** or duplicate externally visible protected effects. A model generation may be re-attempted only under the **existing** bounded AgentRun retry contract; do not promise exactly-once model calls.

### 0.2 Explicit exclusions

- Initiative 10 Group Chat: multiple humans/Agents sharing one room, mentions, implicit participation, shared transcription or collective visibility.
- External A2A/MCP interoperability, remote tenant/organization federation or cross-tenant delegation.
- A workflow builder, agent team templates, executable DAG/DSL, event-bus platform, agent marketplace, pooled compute infrastructure or new distributed scheduler.
- Delegated identity impersonation, copied approvals/grants, automatic cross-Space file sharing, automatic Goal verification or automatic permission expansion.
- Autonomous model-only arbitration of IAM, consent, budget, recipient visibility, task acceptance or execution.
- Replacement of the current Session/AgentRun/Activation model, Speech/Interaction Controller, Automation triggers, Evaluation pipeline, Sandbox or Artifacts.

## 1. Baseline audit and required corrections

Source: reviewed `main` at the SHA stated above; **no full local checkout, tests or live runtime/browser execution are claimed**. Recheck branch HEAD before applying the proposal.

| Current source / contract | Observed fact | Initiative 9 consequence |
| --- | --- | --- |
| `TODO.md`, `docs/08-development-roadmap.md` | Initiative 8 locally closed; Initiative 9 next/unstarted; Initiative 10 Group Chat reserved. | Initiative 9 needs an explicit new spec and cannot borrow an earlier milestone's acceptance. |
| `docs/03-system-architecture.md`, `docs/10-technology-decisions.md` | One mailbox per Session, immutable Activation and Run admissions, existing claims, continuation lineage, bounded completion inbox. | Keep this as sole engine; Task adds coordination, not another mutable conversation writer. |
| `src/AgentCore.Domain/Conversation/SessionOrigin.cs` | Immediate background origin carries parent IDs, remains a same-Agent task concept. | Introduce canonical DelegatedTask origin; separate task provenance from ordinary Automation/Manual origins. |
| `src/AgentCore.Application/Security/TenantBackgroundAdmissionRules.cs` | Child admission explicitly compares `child.Owner == parent.Owner`, same context/Instance/principal and same Space location. | Must **replace**, not loosen indiscriminately: validate two independent owners plus a task-specific, approved cross-Agent bridge. |
| `src/AgentCore.Application/Security/TenantBackgroundCompletion.cs` | Existing completion report builds with the **child's owner** and assumes parent/child same Agent. | New task report must be admitted by the **parent's own Run/Session owner**; child is only a bound source. Never fake a cross-Agent Run owner. |
| `SessionRuntime.BackgroundStart.cs`, `.TenantCompletions.cs`, `.CompletionInbox.cs` | `background.*` starts same-Agent children; list/inspect/take/acknowledge and wait already have durable/fenced mechanisms. | Replace the tool family with `tasks.*`; reuse durable receipt, wait and delivery primitives underneath. |
| `src/AgentCore.Application/Security/PermissionCatalog.cs` | Closed permission catalog, distinct Agent principal/tenant IAM, explicit approvals. | Add explicit permission keys with independent scopes and tests; a new tool name does not itself grant access. |
| `docs/03-system-architecture.md` | `/home` Space-owned; `/private` worker's own Private Space; `/working` Session-private. | Never mount the source Agent's private/home files into a delegate; a context handoff is data, not a mount or membership. |
| `docs/reports/session-goals-delivery-2026-10-27.md` | Goals are independently verified through saved exact Run/Evaluation evidence. | Task `Succeeded` is a factual outcome, **not** Goal `Verified`/`Completed`; bind exact evidence separately. |
| `.agents/context/DESIGN.md`, `web/src/app/antdTheme.ts`, `.impeccable/surfaces/session-goals.md` | Ant Design v6 dark Operate world, 640 px operational drawer and five-width/reduced-motion/zoom review. | Mandatory Impeccable shape **before UI code**, consistent design-system reuse, final bounded polish and doc sync. |

### 1.1 Superseded statements requiring canonical updates

The current architecture/decisions correctly say that a background child is a same-instance Session and that completion reporting is not a delegation system. Those **current-truth statements become historical** for immediate child coordination once this initiative is implemented. Change the canonical owners together; do not leave contradictory paragraphs asserting same-Agent-only children or that “no Task entity” exists. A Task must be described only as **coordination metadata**, never as a replacement for AgentRun. Preserve exact prior SHA/evidence claims in dated reports; do not rewrite those reports as if Initiative 9 had been tested.

### 1.2 Pre-implementation audit gate

Before modifying code, take a fresh HEAD inventory of all `background.*` registered tools, built-in Definitions, prompts, IAM defaults, approval cases, API consumers, SQLite/InMemory stores, Session origins, Activity surfaces, tests and documentation. Produce a **one-to-one removal/replacement map**. Verify whether a former implementation is Task-only or shared by independent Automation destinations. A TODO match or folder name is not proof of safe retirement.

## 2. Canonical language and object ownership

| Concept | Canonical meaning | Owner / source of truth |
| --- | --- | --- |
| **DelegatedTask** | Durable intent, assignment, budget, status, dependency/wait, source, worker result and receipt. **Not** execution. | Tenant task/coordination store; guarded by existing IAM transaction and exact Run data. |
| **Delegation** | One authoritative request to assign a Task to an Agent; accepted at most once per source operation key. | `TenantTaskService` / atomic admission coordinator. |
| **Worker Session** | A normal Session owned by the target Agent Instance; own Space/Environment, transcript, scratch, Runs and artifacts. | Existing tenant Session owner and `TenantSessionRuntimeHost`. |
| **Worker AgentRun** | Actual execution of a Task; result/failure/side-effect/wait and immutable Run pins. | Existing `AgentRun`/`Activation` stores and coordinator. |
| **Task message** | Untrusted, bounded two-party correspondence attached to one Task; durable ordering and delivery/ack receipts. | `AgentMessage` store, separate from Session chat entries. |
| **Direct Agent thread** | Exactly two Instance principals; durable messages not tied to a Task. | Small `AgentThread` record; no group chat. |
| **Task result** | Verified persisted factual terminal Run outcome and selected task-authorized shared projection. | Worker Run + task settlement receipt; never an LLM claim alone. |
| **Task notification** | Durable, deduped signal to eligible parent/recipient; may resume one wait or admit one new Run when explicitly allowed. | Same existing inbox/wait/Activation/Run pipeline. |
| **Task routing** | Deterministic eligibility, target selection and admission of a single chosen Agent. | `TenantTaskRoutingService`; no separate job executor. |
| **Task-linked Goal evidence** | Exact saved Run/Task pointer eligible for independent Session Goal evaluation; no automatic pass. | Current Goal/evidence owner. |

A **parent Session** tracks tasks through a scoped, read-only task projection. It does not host child conversation mutable state or become a second Task store. Each worker can also be a delegator up to bounded depth. For a same-Agent Task, the delegator and worker Agent identities coincide; the two correspondence roles are distinguished by the **source vs worker Session/Run witness**, never by spoofable message-role input. For a human-initiated task, source Run is nullable and a real human task-command origin replaces the missing source Run; no invented source Run is permitted.

## 3. Scope, actor and authorization matrix

### 3.1 Actors

- **Original initiator:** authenticated tenant human when present, otherwise the actual admitted no-human Automation/Event source. Preserve a nullable requester; never invent an operator for unattended work. A direct human Task command is a real, separately authorized source, not a fabricated model Run.
- **Delegating Agent:** for model delegation, the currently executing, claimed AgentRun's actual tenant Agent principal and Instance. For deliberate human delegation from a Session, retain the real authenticated human as the delegator actor and the selected source Agent Instance as context, without asserting it was a speaking/claimed AgentRun.
- **Worker Agent:** independently selected target Instance's tenant Agent principal, own capabilities and execution policy.
- **Reviewer:** current authorized human under existing Review/ExceptionalGrant rules; no delegated authority is created by being a reviewer.
- **Observer:** human or Agent with current scoped read permission for that Task/thread and any individually requested source. A human supervisor may append explicitly attributed `HumanGuidance` to a Task thread under independent mutation permission and current owning Session scope, but may not post into a direct Agent thread while impersonating an Agent.

### 3.2 Proposed closed IAM permission additions

Add in `PermissionCatalog` and named default roles **explicitly**, with proper classifications, scoped resources and denial coverage:

| Proposed key | Class | Rights (in addition to other explicit checks) |
| --- | --- | --- |
| `tasks.read` | ReadOnly | Read only a task in which the actor is an authorized delegator/worker, or a qualified human with both appropriate Session/Agent scopes. |
| `tasks.delegate` | Mutation | Create a task using the current source Run/human identity and an eligible same-tenant target. |
| `tasks.cancel` | Mutation | Request cancellation of an exact readable task by current permitted source/owner/human. |
| `agent-messages.read` | ReadOnly | Read independently authorized task/direct messages for the actual participant/qualified human. |
| `agent-messages.send` | Mutation | Send a bounded message as the current real sender; no caller-supplied speaker identity. |

Task acceptance is a **target Instance policy**, not an implicitly granted `tasks.delegate` permission. The target must be active, explicitly opted in (`cooperation.acceptTasks`/`cooperation.acceptDirectMessages`) and have its own required `sessions.create` and `agent-runs.create` execution authority. Both pinned and current Definition capabilities must permit eligible model tools. Administrative changes to this policy use **existing `agents.update`**, tenant Agent ownership, configuration revisions and saved audit; they are not made by the source Agent unless its normal self-management scope legitimately allows it. Default is **off**, including for shipped built-ins until explicitly configured.

Existing `sessions.create`, `agent-runs.create`, `agents.read`/`agents.chat` (where appropriate), `sessions.read`, `agent-runs.read`, `spaces.read`, membership and resource rules remain independent. **Default grants:** new Agent task/message permissions are absent from pre-existing Agent roles until explicitly assigned; only human tenant Owner/administrators receive new administration abilities through a reviewed explicit role catalog update. Both target opt-ins and direct wake remain off for all Instances by default. No `tasks.*` permission allows an Agent to borrow the user's or target's permissions. Preconfigured ordinary Agent IAM roles must be re-reviewed explicitly; never add new rights to all roles by wildcard or implicit migration.

### 3.3 Admission formula

Task admission requires *all* of these gates:

1. Real tenant and source Session plus **source Run for model calls**, valid current claim + generation for model calls (or a real authenticated human, current `sessions.manage` and `tasks.delegate` rights for human commands); no forged source/initiator/root. Human Task creation never fabricates a source Run.
2. Source Agent's pinned **and current** Definition capability and `tasks.delegate` IAM plus exact source task authority for a claimed model Run. For a human command, check the real human's current task and Session mutation permissions; do not require a fictitious Agent claim. In both cases recheck the actual delegation creation permissions/Review policy and the target's independent execution rights.
3. Target Instance currently active, belongs to same tenant, accepts tasks, has a valid Definition/Model route and its **own** task execution and new-Session permissions.
4. An explicitly authorized target Space chosen from current target Membership and allowed context; source Space is not inherited. If the target is allowed to work in a Shared Space, it still sees only currently permitted sources. Fallback target is the target's protected Private Space, **only if** current target authority is sufficient and it was the agreed requested scope; no silent substitution after a requested Space becomes unavailable.
5. Current target-side capacity, budget, ancestor depth, selected dependency validity, policy and exact immutable request fingerprint.
6. Existing current Approval floor, exceptional grant/independent reviewer restrictions and current source/target revalidation at effect time. A review on delegation does not pre-approve the worker's future operations.

**Core rule:** Delegator authorization permits *requesting an assignment*. Worker execution and every protected file/Browser/Sandbox/credential/account operation are authorized as the **worker's own executor**, with current target policy and applicable exact consent. Delegator/root provenance is attribution and policy input, never a bearer capability. `OriginalInitiatorPrincipalId` affects requester-aware Review; it is not an impersonation of that principal.

### 3.4 Privacy and content sharing

- Parent cannot inspect the worker's full conversation, arbitrary `/home`, `/private`, `/working`, Memory, Credentials, connected accounts, files or private evaluations merely because a Task links them. Parent sees only authorized task summary/messages and **explicitly shareable** outcome references.
- A Task output may include bounded human-readable result and source references; immutable Artifact bytes remain owned by their worker Session and require independent *source publication* and *recipient read* authority. No auto-copy, auto-export or cross-Space mount.
- Any `message.text`, worker result, source excerpt or uploaded artifact is **Evidence**, framed untrusted; neither a parent response nor a Core instruction. Never execute tool instructions contained in task messages as authority.
- A user with `tasks.read` but without the worker's `sessions.read` gets task-safe status only; no guessed hidden Session/Run IDs, counts, task previews, filenames or participant metadata outside its actual policy.
- Permission revocation, Agent archive/decommission, Space membership loss, expired/deleted Sessions and logout instantly retire private UI state. Late network callbacks cannot repopulate it.

## 4. Functional contracts and product journeys

### 4.1 Task delegation

An authorized human or model requests a task with a title, objective, explicit target or eligible-route selector, target Space selection, optional accepted `dependsOnTaskIds`, completion notification choice and bounded budget/deadline. The server checks current authority and stores a closed immutable request. It returns the **same** `taskId` for a true identical retry and rejects the same key with changed arguments.

Default target selection is explicit. The cross-Agent task form must show and save a **concrete target Space**, defaulting to the worker's currently eligible Private Space when not otherwise chosen (never silently switching away from a user-selected Shared Space). An `eligible` selector may choose only among explicit caller-visible candidates authorized at admission. Hard gates run first. The deterministic selector considers current capability, requested Space membership, declared skill/functional fit, configured per-Agent acceptance, model availability, concurrency/budget and stable tie-breaks. Existing Decision Intelligence may produce a **bounded preference ranking** behind the same gate; its output has no IAM standing. The final selected Instance/Space/route version is saved, shown to the user and immutably pinned for that task. If no target is eligible, refuse with a safe precise reason and do not queue a speculative task.

A target can only be changed by **creating a new Task** with a replacement/retry link. Moving an existing task to another Agent would invalidate its execution identity, approvals, files and evidence.

### 4.2 Same-Agent delegated work

The source and worker may be the same Instance but always have **distinct Sessions**, source Task link and separate Run identity. Treat it as exactly the same Task protocol with no special same-owner compatibility branch. This is the canonical replacement for the old immediate `background.start` child facility.

### 4.3 Multi-hop delegation and bounded coordination

Worker Runs may delegate under the same gate, at most **3 delegation edges** from the root, **2 tasks created per claimed source Run** (or 1 per explicit human request), **8 live descendants per root**, and a **configurable absolute time/cost/token limit** not exceeding the root's frozen allowance. These proposed defaults are explicit configuration values; implement enforceable invariant tests before exposing knobs. A worker cannot transfer budget it does not have; child reservations reduce remaining root allowance atomically, and releases only restore unspent reserved allowance. Cancellation/revocation does not reset the root cap.

For bounded **workflow coordination**, allow an immutable dependency list of **up to 8 earlier sibling Task IDs created by the same delegating Session**. Require every dependency to exist, be currently readable and belong to the same parent chain. A Task with dependencies remains `PendingDependencies`; it receives **no worker Session/Run** until all required predecessors have factual `Succeeded` terminal receipts. A failed/cancelled prerequisite makes it `Blocked`, with explicit manual replacement/retry; no speculative automatic retry or runnable workflow DSL. Dependencies only point to earlier committed sibling IDs, so cycles are impossible. The existing Run coordinator reconciles eligible blocked tasks in bounded pages. Do not create a generic DAG worker or custom scheduler.

### 4.4 Progress, questions and answers

A worker can send bounded `progress`, `question`, `answer` or `note` messages to the delegating side within its Task thread; an authorized delegating Agent can reply. A qualified current human supervisor may reply as **Human guidance**, visibly attributed to that actual TenantUser rather than either Agent. The server derives sender kind, `fromAgentInstanceId`/source Session if an Agent, recipient and task relationship; model arguments can contain only content, type, target task/thread and idempotency key. Task threads are two **Agent roles** plus attributable human oversight, not an open multi-participant shared chat. For same-Agent tasks, source/worker Session witness determines which role authored the message.

Messages append atomically with a **monotonic per-thread sequence**, immutable sender provenance, message revision, content classification and one delivery receipt. Sending a message is **not** an Activation/Run creation by default. An eligible active `execution.wait` can resume once for a new bound message/event; otherwise the notification is a durable inbox item that the next claimed Run can inspect. No hidden model spin for every chat message. Worker questioning does not silently suspend its current Run unless it explicitly invokes the existing bounded wait contract.

### 4.5 Direct Agent communication

`AgentThread` contains **exactly two** same-tenant opted-in Agent Instances and no user/human members. A real claimed AgentRun may create/open an eligible thread and send a message. Its independent `agent-messages.send` right and both current sender/recipient policies are required; message contents remain private until separately read.

Default delivery is **inbox-only** and does not start a model Run. The recipient must have current `agent-messages.read` permission as well as `cooperation.acceptDirectMessages`, and any wake must additionally satisfy its own `agent-runs.create`/`sessions.create` and capability policy. A recipient can enable a bounded **auto-wake** policy in its own settings: **at most one queued wake per direct thread**, at most **two message-triggered wake hops per independent root** and **eight auto-wake admissions per pair per hour**, subject to the recipient’s lower existing execution budget and tenant rate limits. The hop count/receipt is durable across restarts; no reconnect or retry resets it. The policy performs the following controlled action: the first eligible new message creates or uses a recipient-owned ordinary background Session, then atomically admits one independent-root message Activation/Run via the existing coordinator. Subsequent messages are serialized by that Session's current admission owner; active work is queued, not entered concurrently into its mailbox. Capacity/revocation failures leave an inspectable undelivered notification, never a fictional delivered reply. Auto-wake is disabled by default, rate/budget/depth bounded, cannot recursively auto-wake between both participants without limits and is not a Group Chat implementation.

### 4.6 Outcome and reporting

A worker signals completion through its **existing `work.complete`** contract. Task reconciliation reads the *actual final task Run chain*, including approved successors, then records a terminal Task settlement once; model prose alone cannot settle it. Store `Succeeded`, `Failed`, `Cancelled` or `NeedsAttention` using explicit, auditable reason/effect semantics. An unconfirmed external effect is `NeedsAttention`, not success and not blindly retried.

The `reportOnCompletion` default is **false (inbox-only)**. If the user or model explicitly sets it true and the policy allows it, create a **single** trusted task-completion notice to the parent Session after revalidating both source and recipient scope. An eligible parent still owns its own Run/Activation, effective config and model. The task's worker principal must **never** own a Run in the parent Session. This fixes the same-Agent assumption in the current completion factory. The notice contains a safe factual task/result projection and source pointers, each separately read-authorized. A processed receipt blocks duplicate notifications after host loss. If parent Session is closed, paused, deleted or loses authority, preserve an inspectable notification in the task inbox (or terminal unavailable reason), without fabricating a parent Run or re-opening the Session.

Parents may `tasks.list`, `tasks.inspect`, `tasks.messages.list`, `tasks.messages.send`, `tasks.cancel` and acknowledge a **specific** completed task notice. `execution.wait` may wait on an exact current set of task IDs with existing time/count caps; it releases claims/resources and resumes exactly once from durable evidence. Acknowledgement means *handled*, not *verified*, *published* or *succeeded*.

### 4.7 Cancellation, error recovery and retries

- Cancellation is a **request**. Persist current actor, reason and revision; cancel pending dependency tasks immediately, signal running work via the existing Run cancellation owner, then derive settlement from actual completion/effect facts. When a non-replayable effect is uncertain, leave `NeedsAttention` and show a recovery action.
- Parent Session deletion/expiry and Agent decommission trigger bounded owned task retirement or cancellation; protect independent task/Artifact retention and active-effect cleanup in existing owners. Ordinary parent Chat completion alone does not silently delete a live Task; use an explicit policy (default: task may finish but cannot inject into a completed parent; notify through retained inbox while authorized). A hard deletion/revocation may terminate it.
- Automatic Run attempts use the **existing** Run attempt/recovery policy. They never create a new Task. A deliberate retry of a truly terminal Task is a **new `taskId`** linked by `retryOfTaskId`; never resurrect a terminal task, never reuse an uncertain external effect receipt.
- A lost HTTP/tool response requires request-key **readback** of the saved Task/Message/receipt; no optimistic double submission or transport-level effect replay.
- Direct-message delivery, task-message acknowledgement and parent report admission have durable unique indexes on their own logical receipt identities.

## 5. State machines and invariants

### 5.1 Task lifecycle

```text
            accepted request
                  |
       +----------+----------+
       |                     |
 PendingDependencies       Queued  --> Running <--> Waiting
       |                     |           |
       +-- prerequisites -->-+           +--> Succeeded
       |                                 +--> Failed
       +-- failed prerequisite --> Blocked
                                         +--> Cancelled
                                         +--> NeedsAttention
```

`PendingDependencies`, `Queued`, `Running`, `Waiting`, `Blocked`, `Succeeded`, `Failed`, `Cancelled`, `NeedsAttention` are closed states. `Succeeded`/`Failed`/`Cancelled` are terminal. `Blocked` is terminal for dependency failure unless manually replaced. `NeedsAttention` is a retained non-runnable terminal-resolution state (operator may explicitly resolve facts, cancel or spawn a new task; no implicit retry). Only the central persisted service/store may update status using current Run/dependency facts and CAS. UI cannot submit arbitrary state transitions. `Running`/`Waiting` projections require consistent actual Run status; store and Run contradiction fails loudly rather than guessing. A task may have **zero worker Run** only while `PendingDependencies` or `Blocked`; after readiness, task + new Session/Activation/Run admission commits together.

### 5.2 Message delivery lifecycle

`Recorded → NotificationPending → DeliveredToAuthorizedInbox → Acknowledged` or `Undeliverable` with a saved reason. An inbox read alone need not acknowledge. Direct auto-wake delivery is independently marked `ActivationAdmitted` only after a committed Session/Activation/Run tuple; `DeliveredToAuthorizedInbox` is not proof of model execution. Reconciliation never changes untrusted text or sender provenance.

### 5.3 Non-negotiable invariants

1. One claimed Run, one owning Session Runtime; no cross-Agent writes into another Session's transcript or mailbox.
2. One Task creation/accepted message per unique `(tenant, trustedSource, idempotencyKey)`; compare its persisted canonical action hash and return 409 if the same key is reused with changed content.
3. Tenant, source, sender, recipient, original initiator, root Activation and target executor come from **stored trusted owners**; clients/models cannot supply authoritative principals or parent lineage.
4. Every worker has its own selected/pinned Instance + Definition/persona + model route + own target Space/Environment + Skills/Resources, with current execution-time narrowing.
5. A Task or thread ID is a **selector**, never read/delegate/write permission; filter before lists/counts/cursors.
6. The same committed worker result, parent report and message may be projected many times, but side effects/notice admissions are logically at most once.
7. Every cross-Agent content boundary is inert Evidence. Approval/grant/sensitive source rights never transit on Task links.
8. Completion is factual Run settlement; independent Goals Evaluation and human acceptance remain separately required.
9. Every pending/unknown/partial/denied outcome is rendered accurately; no silent fallback to another Agent/Space/provider or restored old tool.
10. No task-scope ref is exposed after tenant/Session/Agent access retirement, even from a delayed browser request.

## 6. Detailed admission and execution design

### 6.1 Transaction boundaries

**Immediate runnable Task:** In a single SQLite/IAM-owning transaction (or its InMemory equivalent), validate current security and real model source Run **or authenticated human command**, reserve capacity/root budget, write Task + immutable assignment/lineage + worker Session + initial input + correctly rooted Activation + initial queued AgentRun + notification/outbox index + audit. For model delegation the worker Activation inherits the real parent Run's root/parent witnesses; for human delegation the worker Activation starts a **new independent root** bound by immutable Task source data to the human command and parent Session, without inventing a parent Run. The same request key either finds the exact committed tuple or fails on mismatch. Dispatch is **after commit** through existing AgentRun coordinator; crash before dispatch recovers the queued Run, never repeats admission.

**Dependency Task:** Atomically write Task + dependency edges + budget reservation + audit in `PendingDependencies` without a worker Session. A later bounded, transactional eligibility transition admits the one worker Session/Activation/Run and advances Task `Queued` exactly once. Use a unique `(taskId, initialRun)` row/reference and predecessor receipt checks. The coordinator remains the only execution dispatcher.

**Completion:** Commit factual settlement + result-bound source references + one notification receipt or explicit delivery-pending fact under a shared current-source/recipient transaction. Parent Run admission is separately atomic with its own Session/Activation/Run store and exactly-once receipt, after rechecking current parent config and authority. Failure between settlement and notification is retried **only at the idempotent state transition**, not by re-executing the worker.

**Communication:** Commit thread/message + sequence + normalized recipient/projection + delivery receipt and audit atomically; recipient Activation is an independent guarded admission. Lost acknowledgement is resolved from saved fact rather than new model/tool work.

### 6.2 Trusted lineage and run intent

Extend closed enums/contracts deliberately:

- `SessionOriginKind.DelegatedTask`, `SessionOriginKind.DirectAgentMessage` (direct-message worker Session only when wake enabled).
- `ActivationKind.DelegatedTask`, `ActivationKind.TaskCompletion`, `ActivationKind.AgentMessage`; add corresponding `ExecutionOrigin` values and strict admission validation. In a delegated Task, source Session/Run, root and parent Activation witnesses are inherited only from the saved real source Run. A direct message is a separately admitted independent root unless a specific task-chain wake contract binds it to an existing root.
- `AgentRunTaskSource.DelegatedTask` with exact saved task ID/revision (or a dedicated typed immutable Task witness), not arbitrary stringified context. `AgentRunOutputContract.BackgroundOutcome` can continue for a worker's underlying `work.complete`; do not add a second result engine.
- Each record binds `TenantId, TaskId, SourceSessionId, SourceAgentRunId?, SourceActivationId?, RootActivationId, SourceActorKind/PrincipalId, HumanCommandId?, DelegatorInstanceId, WorkerInstanceId/PrincipalId, WorkerSpaceId/EnvironmentId, OriginalInitiatorPrincipalId?, InitialWorkerSessionId?, InitialWorkerRunId?, ParentTaskId?, Depth, immutableConfigHash/assignmentHash`. Model-initiated tasks require non-null `SourceAgentRunId`/`SourceActivationId`; direct human commands require a genuine authenticated human and unique `HumanCommandId` and have **no** invented parent Run. For human work, `RootActivationId` is the worker's independently admitted root.

A Task's multi-hop source/worker branch is verified with **actual persisted provenance**, not `Run.Owner` equality across agents. A subsequent TaskCompletion into the human-selected parent Session is a guarded continuation from the **actual worker Run/root**, or is held as a notification if current parent authority does not permit admission; it never claims a non-existent human source Run. The existing same-owner `TenantBackgroundLineage`, creation/approval and completion methods must be replaced/rewired where task-specific. Keep ordinary approval-successor lineage strict for each **individual** worker Session/Agent.

### 6.3 Existing-owner interface changes

Implement bounded application ports, not a generic repository framework:

- `ITenantTaskStore` for atomic task admission, exact request lookup, claimed reconciliation, immutable terminal settlement, cancel request, task-page read and recipient projection; implementations in current InMemory and SQLite IAM/persistence owners.
- `ITenantAgentMessageStore` for closed two-party thread, append, ordered page, read permission/ack and pending recipient delivery receipts.
- `TenantTaskService` for request validation, deterministic target selection, IAM/Review integration and safe projections; `TenantTaskCoordinationService` for existing-coordinator reconciliation (no new hosted process).
- `TenantTaskToolContract` and `TenantAgentMessageToolContract` integrated into the **existing** `ToolCatalog`, capability discovery, `TenantToolAuthorization` and `SessionRuntime` mailbox tool admission.
- Extend `TenantRunConfigurationResolver` for **worker-own** current target Space/Membership, model and config resolution; never clone the parent's `ResolvedAgentRunConfiguration` into a different Agent.
- Reuse `ITenantRunStore`, `IAgentRunStore`, `IAgentRunDispatcher`, `TenantSessionRuntimeHost`, `TenantRunExecution`, existing approval/operation receipt owners, source-access services, audit, Event delivery and Run capacity.

No external actor runtime, separate queue database, Redis, Kafka, MediatR, generic repositories, additional EF DbContext owner or second mutable Session writer.

### 6.4 Routing correctness

The route preview is advisory and must be revalidated on submit. Inputs: bounded objective, optional required capability labels, explicit current allowed candidate set and requested target Space. Default `explicit` route is recommended in UI. `eligible` selection must record **why this target was selected** as safe non-secret factual metadata. Deterministic ordering: eligible first, declared fit second, remaining capacity third, stable Instance ID tie-break. A Decision Intelligence adapter may later reorder *already-eligible* candidates within a bounded attempt; absent classifier, timeout or invalid ranking restores the stable deterministic ordering **for ranking only**, never weakens admission or permission. This is not a compatibility fallback path. Record candidate list version, actual selected target and model-pin readback; refuse if current target/Space drift invalidates it. Use existing tenant Auto model routing **only for the worker's own selection policy**, not to grant access or change parent pins.

## 7. API and model-tool contract

### 7.1 Single canonical model tool family

| Model tool | Canonical accepted shape and behavior |
| --- | --- |
| `tasks.delegate` | `{objective,title?,target:{instanceId}|{mode:"eligible",candidateInstanceIds:[...]},spaceId?,environmentId?,dependsOnTaskIds?,reportOnCompletion?,deadlineAt?,retryOfTaskId?,requestKey}`. All unrecognized properties rejected. Model source/actor derived from current claim; human HTTP command uses its authenticated human identity instead. |
| `tasks.list` | `{scope:"created"|"assigned"|"pending",cursor?,limit?}`. Filter by current rights **before** pagination/count. |
| `tasks.inspect` | `{taskId}`; safe Task state, assignment, factual worker outcome, notices, independently allowed links. |
| `tasks.messages.list` | `{taskId,cursor?,limit?}`; exact membership/read checks; ordered bounded page. |
| `tasks.messages.send` | `{taskId,kind:"progress"|"question"|"answer"|"note",text,requestKey}`. Sender/recipient derived; inert content. |
| `tasks.cancel` | `{taskId,expectedRevision,requestKey,reason?}`; one exact cancellation request. |
| `tasks.acknowledge` | `{taskId,notificationId,expectedRevision,requestKey}`; mark a specific saved notification handled. |
| `agent-messages.send` | `{recipientInstanceId,threadId?,text,requestKey}`; create/open an authorized two-party thread; never a group. |
| `agent-messages.list` | `{threadId?,cursor?,limit?}`; scoped thread metadata/messages, private counts filtered before paging. |

A human Task or Task-guidance operation may use the equivalent authenticated HTTP application command; only the owning server can supply the actor. **Direct Agent-thread creation and posting are exclusively claimed Agent tool actions**, not HTTP operations by a human pretending to be an Agent. HTTP direct-thread reads are only independently authorized oversight. The client/model may supply **selector IDs**, not tenant/principal/root/Run lineage or system timestamps. Model tool descriptors are only offered when both pinned/current capabilities and current policy allow them; the server still rechecks actual claim, IAM, source and budget. Retain `work.complete` and `execution.wait` with an exact task-target wait mode. No `background.*` compatibility registration, alias in prompt templates or legacy parser on startup.

**Default closed limits** (revisit only through an explicit budget spec): objective max 4,000 characters; title max 80; message max 2,000 UTF-8 characters; task preview/result projection max 2,400 characters; message page max 50; list page max 100; up to 8 sibling dependencies; at most 2 new child tasks per source Run, 8 live descendants per root, 3 delegation edges; direct thread precisely 2 Agents; finite absolute task deadline no later than 24 h from admission unless explicitly approved by tenant governance. At the deadline, prevent new claims and request cancellation of running work; a confirmed no-effect expiration settles `Failed` with an exact deadline reason, while uncertain external effects remain `NeedsAttention`. Use UTF-8 byte checks and normalized canonical JSON hashing; reject pathological nesting/array duplicates and oversized receipt payloads.

### 7.2 Authenticated tenant HTTP additions

All routes are under `/api/v2/tenants/{tenantId}` and reuse current Account cookie, CSRF, tenant owner, security revision, operation audit, safe reason codes and uniform error semantics. The listed routes are for authenticated **human tasks, human Task guidance, inspection and notifications**; direct Agent messages are created only by the current trusted model-tool/Runtime boundary, not by a human impersonation HTTP route:

```text
GET    /sessions/{sessionId}/tasks?scope=created|assigned&cursor=&limit=
POST   /sessions/{sessionId}/tasks
GET    /tasks/{taskId}
POST   /tasks/{taskId}/cancel
GET    /tasks/{taskId}/messages?cursor=&limit=
POST   /tasks/{taskId}/messages
GET    /tasks/{taskId}/notifications?cursor=&limit=
POST   /tasks/{taskId}/notifications/{notificationId}/acknowledge
GET    /agents/{instanceId}/tasks?scope=assigned&cursor=&limit=
GET    /agent-threads?instanceId=&cursor=&limit=
GET    /agent-threads/{threadId}/messages?cursor=&limit=
```

Extend the **existing** Agent Instance settings/read/update contracts for `cooperation` opt-ins and current limits. Do not build a second settings service/UI. `GET` responds with independently scoped `{items,nextCursor,hasMore,securityRevision}`. Command envelopes require `requestKey`, `expectedSecurityRevision` and task/thread revision where applicable. HTTP 400 malformed; 401 unauthenticated; 403/404 privacy-safe denied/missing; 409 revision/key mismatch; 410 expired/retired; 429 actual capacity/rate limit; 503 transient unavailable. Denial responses and paginated counts disclose no hidden Agent identities or Task existence. Safe detailed diagnostics remain server-owned.

**Example (illustrative, not a statement about a presently implemented route):**

```json
{
  "requestKey": "b3cda4f4-3f99-4831-8e8c-4d6a8d8eb493",
  "expectedSecurityRevision": 42,
  "objective": "Compare the two approved migration approaches and return the risks.",
  "title": "Review migration approaches",
  "target": { "instanceId": "11111111-1111-4111-8111-111111111111" },
  "spaceId": "22222222-2222-4222-8222-222222222222",
  "dependsOnTaskIds": [],
  "reportOnCompletion": true
}
```

**Successful response fields:** `taskId`, `revision`, `state`, `selectedTarget` (safe name/id only if independently readable), `workerSessionId`/`initialRunId` (nullable and gated), `created`, `replayed`, `expiresAt?`, `canInspect/canCancel/canSend` server projections and `securityRevision`. These booleans aid UI, never replace command reauthorization.

### 7.3 Realtime delivery

Do **not** create another Hub. Extend existing tenant event/subscription/projection conventions with server-sourced, ordered **task/message changed** hints carrying only tenant-authorized IDs, revision, sequence and safe unread/attention indicators. The browser must refetch under current HTTP rights; a realtime hint is **never** trusted task/message content, recipient permission or a sign that model work completed. Reconnect uses saved cursor/revision; lost deltas cause independent fact reload. Keep existing voice/response/entry sequence handling unchanged.

## 8. Persistence and clean cutover

### 8.1 Canonical tables and indexes

Add one EF migration **after** `20261027040000_SessionGoals`; generate its actual migration ID from the final source tree and update the model snapshot/contract SQL. Proposed logical tables (actual names follow repo conventions):

- `TenantDelegatedTasks`: tenant, task ID, source/session/run/root and optional parent task, assigned Instance/principal, worker Session/initial Run nullable until readiness, selected Space/Environment, immutable normalized request hash and snapshot, status/revision/timestamps, deadline, bounded budget reservation, exact terminal outcome and reason, retry linkage.
- `TenantTaskDependencies`: exact immutable earlier sibling edges, unique `(tenant, taskId, prerequisiteId)`.
- `TenantAgentThreads`: tenant/thread ID, **two canonical sorted Instance IDs for direct threads (distinct)**, optional task ID; unique direct pair `(tenant, firstInstanceId, secondInstanceId)` (one retained direct thread per pair); task-linked thread uniquely keyed by Task and may have equal Agent IDs with **distinct source/worker Session roles**.
- `TenantAgentMessages`: tenant/thread/message IDs, immutable sender principal/Instance, kind/content/sequence, source Run witness/request hash, immutable recipient snapshot, created time.
- `TenantTaskNotifications` (or explicit reuse of existing completion inbox rows): exact unique logical `(tenant, taskId, terminalRunId, recipientSessionId, kind)`, delivery/ack revision and saved result source references.
- `TenantTaskCommands` or incumbent operation receipt rows for idempotency; use the existing receipt owner where its semantics fit, never two independent receipt authorities.

Use FKs/checks for tenant-consistent ownership, nonempty IDs, distinct two-party identities **for direct threads only**, closed status values, immutable parent/dependency links, unique message `(tenant, threadId, sequence)`, unique source request key, indexed bounded recipient queues and task subtree scans. Close all EF/InMemory parity gaps, including time precision and atomic CAS race winners. Do not persist whole worker transcript, raw secrets or unsupported copies of Artifact bytes inside Task rows.

### 8.2 Migration policy: new-only, deliberate reset

1. Freeze writes and inventory existing Task-equivalent background Sessions/Runs, approvals, completion inbox, Automation destinations, Goals, Retention and artifact references. Take a recoverable **full database plus Workspace/Artifact/Attachment/secret-key-root backup** before destructive work.
2. **Disposable development/Synthetic**: operator explicitly selects the target database and protected data roots, confirms inventory and may reset/rebootstrap execution data or the entire isolated fixture as required. Never infer reset from a connection string and never delete persistent `/home`, Credentials or unrelated files automatically.
3. **Persisted installations that cannot be safely converted**: fail startup with a named `task_cutover_reset_required`/offline-migration diagnostic; no silent schema fallback, auto-convert-on-read or data destruction. Back up and deliberately rebuild via the documented operator procedure. This user authorizes a destructive development cutover, not unapproved deletion of actual customer state.
4. Add a **single canonical new model**. Remove obsolete same-Agent child admission factories, model tool names/definitions, approval previews, parser aliases, old UI actions and tests *where exclusively task-bound*. Keep independent Automation destination semantics through their already-authoritative mechanisms, not legacy task compatibility.
5. Replace all model/instance built-in capability lists, General Assistant definitions, cap-load descriptors, Synthetic fixtures, tests and documentation. Old `background.*` calls fail explicitly as unsupported; they never map, translate or dual-write to `tasks.*`.
6. Validate fresh SQLite schema, InMemory parity, true predecessor migration tests, empty down/up where supported and deliberate downgrade refusal if Task data exists. Exercise real host restart and installed data gate. Record the exact schema/migration SHA, reset scope, backups and results; **never** report completion from generating a migration alone.

### 8.3 Retention and cleanup

Task/message retention follows actual Session retention and tenant privacy lifetimes. Saved tasks survive ordinary Chat archiving while readable; Temporary task content expires no later than its owning sensitive scope; deletion removes public visibility **before** physical cleanup. Source/worker Session hard-deletion triggers a bounded retained-reference check, Task tombstone/redacted receipt and approved physical cleanup as appropriate. Never cascade-delete immutable independent Evaluation evidence or Artifact bytes while still owned/referenced elsewhere; use their owners. Cleanup jobs are idempotent, scoped and safe after restart.

## 9. UI/UX: Impeccable-first design contract

### 9.1 Mandatory order — **design before code**

1. Read `.agents/skills/impeccable/SKILL.md`, its **shape/new-work/Operate** references, `.agents/context/PRODUCT.md`, `.agents/context/DESIGN.md`, `.impeccable/design.json`, `web/src/app/antdTheme.ts`, `web/src/app/AppShell.tsx`, `web/src/app.css` and existing Chat/Activity/Goals/Background drawers. Attempt the project's normal Impeccable context command in a runnable checkout. If unavailable, document that fact and read the committed context directly instead; no invented tool results.
2. Create **before any UI implementation** `.impeccable/surfaces/task-delegation.md` and `.impeccable/surfaces/agent-communication.md`: thesis, user/job, reference incumbent, view hierarchy, keyboard/viewport contract, copy, full state table, reuse owners and observed no-go conditions. Produce annotated layout sketches or working design-lab states if practical. Finalize interaction and information architecture as a reviewable design gate.
3. Review that contract against this proposal and current `/docs/13` and `/docs/19`. **Resolve UX defects before writing UI code**, including same-Agent vs cross-Agent labels, who can view a task vs worker conversation, unverified results, two-party direct-thread scope and long-running status.
4. Implement using current Ant Design v6 components and the **existing** `AppShell`, `ChatHeader`, `BackgroundWorkDrawer`, `SessionGoalsDrawer`, `SessionSandboxFrame`, `AgentRunDetails`, Activity, approvals and layout spacing owners. Prefer product composition extraction at repeat consumers; no generic AntD wrapper layer, second UI kit, new global token system or new messenger app shell.
5. After backend/UI integration, conduct **one batched Impeccable visual + behavioral critique** across representative screens, states and viewport sizes, fix findings in one bounded pass, then one confirmation round if needed. Do not claim “polished” from a screenshot alone. Follow the project's documented **finish-review** and record its actual disposition, remaining defects and provenance.
6. Run the **design-documentation synchronization pass**: reconcile actual `antdTheme.ts`/`app.css`/shared product compositions and screenshots with `.agents/context/DESIGN.md`, the two new surface briefs, `.agents/context/PRODUCT.md` where product context needs updating, and `.impeccable/design.json` as supported by installed tooling. **Change global DESIGN tokens only for a real shared system change**. If unchanged, document exact reuse and preserve the canonical theme; refresh stale sidecar metadata only against actual captured source evidence.

### 9.2 Visual North Star

**Mode: Operate.** “Work assigned to a trusted specialist, with visible ownership and honest outcomes, without losing the conversation.” Quiet Chat remains the primary reading surface. Task activity is a **clear operational layer**, not a stream of extra chat bubbles or an independent multi-pane workplace.

Reuse incumbent design roles: AntD v6 dark resolved surfaces (container `#141414`, elevated `#1f1f1f`, border `#303030`), readable primary/link/focus token paths from `antdTheme.ts`, native system typography (14 px body, 16 px section titles) and 8/12/16 px spacing rhythm. The existing **640 px operational drawer**, capped to the viewport and full-width narrow view, is the first-choice presentation. **Do not hard-code token colors in new components**; the listed values are documented visual reference, not a replacement theme.

No avatars invented for agents, arbitrary status colors, decorative dashboards, custom drag-drop orchestration canvases, unreadable timeline spaghetti or autonomous chat messages masquerading as a human. Status must have text and a meaningful accessible name.

### 9.3 Navigation and surfaces

| Surface | Placement / incumbent owner | First-viewport content and actions |
| --- | --- | --- |
| **Session Tasks** | Chat header or existing operational action cluster → `TaskCenterDrawer` (compose `SessionSandboxFrame`/`BackgroundWorkDrawer` patterns) | “Tasks”, created/assigned tabs only where authorized; current actionable count; cards with agent identity, concise title, real status, last update, deadline/attention; **Delegate task** for eligible human; task detail. |
| **Task detail** | Inside Task drawer, not independent Chat transcript | Factual status + assignee + source owner; objective; requested Space (safe metadata); latest Run result/attention; “Messages” section; independently authorized Run/Artifact/Evaluation links; Cancel/Reply/Acknowledge per current rights. |
| **Agent Inbox** | Existing Agent Activity/Admin detail scoped to an Instance | Assigned tasks, actionable questions, message requests, completed task reports; per-item unread/handled facts; no generic Group Chat nav entry. |
| **Direct Agent threads** | Scoped “Communication” section/tab of Agent Activity, separate from task cards | Exactly two Agents, inbox-first message history, current opt-in, optional wake status, failed delivery/readback. No human participant composer. |
| **Agent cooperation settings** | Existing Agent configuration panel (not separate Admin system) | `Accept delegated tasks` default off; `Accept direct messages` default off; `Allow direct-message wake` default off; bounded per-Agent concurrency/budget/deadline and allowed Spaces/capabilities. Explain policy and operating cost. |
| **Review** | Existing tenant/Chat approval inspector | Exact source → destination Agent/Space → objective → budget/capability → requested operation; approved/not approved and receipt, not promised completion. |
| **Activity / Runs** | Existing Activity, `AgentRunDetails`, `SessionGoalsDrawer` deep links | Actual source↔Task↔worker Run chain; separate “Task succeeded” and “Goal verified”; no leaked private worker source. |

On a narrow screen: single full-width Drawer, no nested permanently open sidebars, action row wraps, task details use disclosure/tabs rather than horizontal table scrolling, messages wrap and preserve reading order. On desktop, a compact list/detail layout may be used **inside** the established drawer only if focus/scroll owners remain accessible; avoid another overlapping Drawer for every item. Deep links resolve within the selected current tenant and explicit scope, never infer Agent identity from a URL alone.

### 9.4 First-pass layout and interaction blueprint (design input, not shipped UI)

**Desktop / wide operational drawer — same hierarchy for created and assigned work:**

```text
Main Chat (unchanged)                    TASKS · current Session
┌──────────────────────────────┐  ┌──────────────────────────────────┐
│ Header · Agent · Goals · Tasks│  │ Tasks                   [Close] │
│                              │  │ [Created] [Assigned]            │
│ Conversation                 │  │ [Delegate task]  [Reload]       │
│                              │  │                                  │
│                              │  │ Search/filter only if authorized│
│                              │  │ Task title                      │
│                              │  │ Assigned: Agent · Working       │
│                              │  │ Updated … · Needs no action     │
│                              │  │                                  │
│                              │  │ ── Selected task ───────────────│
│                              │  │ Objective · status · ownership  │
│                              │  │ Evidence / original Run        │
│ Composer / voice (unchanged) │  │ [Messages] [Activity]          │
└──────────────────────────────┘  │ Message history · Reply        │
                                  │ [Cancel task]  [Acknowledge]    │
                                  └──────────────────────────────────┘
```

**320–390 px mobile drawer — one reading column, one owning scroll region:**

```text
┌──────────────────────────┐
│ Tasks            [Close] │
│ [Created] [Assigned]     │
│ [+ Delegate]    [Reload] │
│                          │
│ Selected task            │
│ Worker · Status · Updated│
│ Objective (full on open) │
│                          │
│ [Messages] [Activity]    │
│ Message / source detail  │
│                          │
│ [Reply / guidance]       │
│ [Other permitted actions]│
└──────────────────────────┘
```

These are **information-architecture sketches**, not pixel-accurate mockups. The design gate must validate the same reading hierarchy against actual incumbent `ChatHeader`, `SessionGoalsDrawer`, `BackgroundWorkDrawer` and Agent Activity instead of implementing two independent task experiences. Source vs worker context is textually labeled, particularly when the **same Agent** is both. A Task notice appears as a compact **operational status** in the existing Chat affordance—not a fabricated human or assistant message. A human guidance reply shows a labeled human actor; direct Agent threads have no human message composer.

The proposed primary path is **Tasks → Delegate → choose eligible Agent and concrete Space → review exact scope/cost/notification → confirm → inspect saved Task → question/answer → factual result → acknowledge or independently verify evidence**. Noneligible candidates should be omitted or shown disabled with a safe explanation, never as optimistic selectable targets. Submission remains locked while committing; an uncertain response offers **Read saved task** using the same request key.

### 9.5 Required complete screen-state matrix

| State | Visible language | Available recovery/action |
| --- | --- | --- |
| Empty, not opted-in | “No tasks yet”; reason collaboration is disabled | Inspect permission/policy or enable in Agent settings when allowed. |
| No eligible delegate | “No available Agent can accept this task” | Show only authorized compatible candidates / correct scope. |
| Queued / running | “Queued” / “Working” with assignee and last factual update | Inspect current task; don't show synthetic progress. |
| Dependencies waiting / blocked | “Waiting for prerequisite” / “Blocked by failed prerequisite” | Inspect prerequisite if separately readable; create replacement task deliberately. |
| Question / unread message | Sender, time, body clearly marked as Agent message | Read, reply or wait; no invisible auto-run. |
| Waiting / approval | “Waiting for response” / “Approval required” | Link existing exact Approval when readable. |
| Task succeeded / Goal unverified | “Task finished; Goal evidence not verified” | Inspect exact worker Run and independently verify Goal criteria. |
| Failed / cancelled / uncertain | Factual terminal/attention reason without confident claims | Inspect saved result; new deliberate retry with new Task ID only. |
| Parent ended / result undeliverable | “Task finished; notification couldn't be delivered to parent conversation” | Inspect retained inbox with current rights; no session resurrection. |
| Lost reply / stale revision | “Check saved task before retrying” | Explicit reload using request key; preserve drafts, no auto-resubmit. |
| Denied/expired/logged out | Clear private task, message, Run and draft projections immediately | Return to authorized scope; no stale callbacks re-open content. |
| Long title/objective/message, no attachments, loading, reduced motion | Clamped labels only in summary; complete detail accessible on demand | Sticky reachable footer, visible focus, screen-reader labels, skeleton/Empty/Alert. |

### 9.6 Accessibility, responsive and craft acceptance

Verify **320, 390, 768, 1024 and 1440 CSS px**, plus **200% browser/CSS layout zoom** and reduced motion. Keyboard-only flows: open task actions, select current authorized Agent/Space, create/review task, inspect messages and results, reply, Cancel, Escape, return focus to opener. The new drawer must not steal active composer/voice keyboard behavior; Chat text/voice remains the same Session. Use native `Drawer`, `Flex`, `Space`, `Form`, `Select`, `Descriptions`, `Tabs`, `List`, `Tag`, `Alert`, `Empty`, `Spin`, `Typography`, `Button` and existing confirm pattern as appropriate. Announce new task and message status via an accessible live status region, without flooding on each poll. Reject color-only statuses; ensure semantic errors, meaningful buttons, inert untrusted Markdown/text (sanitize), bounded truncation with full detail on demand, safe link preview, no horizontal page overflow, dark-theme contrast and touch-safe actions. For new shared UI compositions, compare the other consumers at the same widths before shipping.

## 10. Integration contract with existing initiatives

| Existing initiative / owner | Required consistency |
| --- | --- |
| 1 — IAM | Worker owns its own principal; independent scoped checks; explicit Review/ExceptionalGrant and current requester rules; no ambient delegation of executor permissions. |
| 2 — Space/Membership/Environment | Worker explicitly selects its own eligible Space; model/run context pinned; `/home`, `/private`, `/working` never copied/mounted from delegator. |
| 3 / 6 — Dynamic Model / Decision Intelligence | Worker owns model/default/Auto preference, current route validation and cost budget; smart router can rank eligibility but not authorize. |
| 4 — Self-Management | Cooperation opt-in lives in existing settings; worker cannot mutate another Agent just because it was delegated work. |
| 5 — Message/Execution simplification | `Session → Activation → AgentRun`, saved/temporary retention and chat completion remain intact. Task messages are **not** normal user chat input or a revived Turn entity. |
| 7 — Unified Evaluation | Evaluations run via existing exact Run/Suite evidence and scripts; task receipt does not imply a passed evaluation. |
| 8 — Session Goals | Task Run can be a referenced work source only after current scope/source checks; independent exact Evidence/Verification and final human acceptance remain mandatory. |
| 10 — Group Chat | None of this grants many-to-many conversation visibility or participation; the two-Agent/thread model is designed to admit a future separate group specification, not to imitate it. |
| Automation/Events | Existing independent root/origin and delivery ownership remain. Automation Runs can delegate through the new canonical Task facility when authorized; no new schedule/event subsystem. |

## 11. Implementation sequence and atomic quality gates

**Do not start UI implementation before Gate D1.** Each slice lands only after its focused proof; no milestone is accepted from compilation or mock-only tests.

| Gate | Deliverable / acceptance evidence |
| --- | --- |
| **D0 — fresh-source contract audit** | Compare HEAD and all background/approval/Run/UI references; produce exact former→new contract removal map; resolve direct-thread wake and Automation coexistence on paper; note any branch drift. |
| **D1 — Impeccable shape** | Two approved `.impeccable/surfaces/*.md` design contracts; hierarchy, flows, status/error/permission matrix, reuse/spacing and five-width scenarios signed off **before code**. |
| **B1 — Domain and IAM** | Closed Task/Thread/Message/Notification models, state transitions/limits, typed lineage, new permission catalog and policy, strict negative tests, no enabled-by-default Agent delegation. |
| **B2 — atomic persistence and migration** | SQLite + InMemory parity, EF migration and snapshot, CAS/idempotency/unique indexes, restart/host recreation, explicit reset gate, safe retention and strict retirement of obsolete task rows. |
| **B3 — task execution and routing** | Actual cross-Agent worker Session+Activation+Run on existing coordinator; root/child bounds, exact Space/identity/config/model pins; deterministic eligible routing; same-Agent Tasks use same path. |
| **B4 — direct and task communication** | Secure two-party/thread/message writes, bounded inbox/wait/optional direct wake with real Runtime; delivery/ack receipts and host-restart recovery; no group or cross-tenant leakage. |
| **B5 — outcome, approval and source fidelity** | Actual worker terminal settlement, parent-owned report admission, exact receipt non-replay, cancellation/unknown-effects, current Review, separate file/publication/read and Goal verification. |
| **F1 — integrated UI** | Chat Task drawer, task details, Agent Activity Inbox/direct threads, Cooperation settings, existing Approval/Run/Goal deep links; real Synthetic journeys, not static preview only. |
| **F2 — Impeccable polish / system sync** | Batched desktop/mobile/error/recovery visual review; corrections and one confirmation; honest finish-review disposition; `.agents/context/DESIGN.md`, sidecar/surface briefs and `/docs/13` match source UI and token owners. |
| **R — migration removal and docs consistency** | No registered old `background.*` child tools, aliases, dual-write, fallback or old UI shortcuts; updated built-ins/tests/docs; strict old command failure. Rerun affected focused checks. |
| **A — final acceptance** | Real both-store/restart/HTTP/model/Synthetic native browser matrix passing on exact current SHA; migration proof; documented limits; optional CI only if explicitly requested. |

Do not mark B1–A as complete merely because this design has been written. The `main` snapshot is a reference, **not** a proof that a future branch matches it.

## 12. Mandatory test and evidence matrix

The following tests must run against the **actual production service/HTTP/model owner**, both `InMemory` and actual `SQLite` where storage semantics matter. Native Playwright against a running Synthetic API/SPA is required for UI behavior, in addition to Vitest/RTL/unit tests. Record the exact code SHA/fixture profile and which gates were actually run.

| ID | Scenario / negative boundary | Required assertion |
| --- | --- | --- |
| T01 | Same-Agent Task from a real claimed Run | New child Session and Run exactly once; no old background path. |
| T02 | Cross-Agent Task, approved target | Worker Run owns **target** Agent principal, Definition, Space and model. |
| T03 | Model supplies a forged tenant/principal/parent/root | Closed input rejects before mutation; no impersonation. |
| T04 | Target inactive/archived/decommissioned, opts out, no Membership | No Task admission; no fallback Agent or Space. |
| T05 | Source capability removed or source claim generation stale | Execution denied despite previously offered descriptor. |
| T06 | Target current/pinned capability or model route changes | Exact admissibility check; never silently substitute route. |
| T07 | Missing target `sessions.create`/`agent-runs.create` / source `tasks.delegate` | Refusal even with UI button or readable target. |
| T08 | Executor IAM vs requester-aware Review / no-human Automation | Independent policy correct; no fictitious human requester. |
| T09 | Exact approval approve/reject/expire/revoke/grant exception | One saved action; no entire-Run permission or downstream privilege. |
| T10 | Same request key, same hash after lost response | Same Task and Run, same receipt; no duplicate effect. |
| T11 | Same key, different body or target | 409 conflict; no changed task silently created. |
| T12 | Two concurrent requests/claims, SQLite race | Unique CAS winner; one Session/Activation/Run. |
| T13 | Crash before/after task+Run admission commit | One logical Task/Activation/Run; execution may retry the **same** Run under its established bounded claim/attempt policy. |
| T14 | Parallel subtasks, fan-out/depth/budget limit | Caps enforced, root allowance not reset by continuation/restart. |
| T15 | Dependency previous sibling succeeded vs failed | Exact ready/blocked settlement; no phantom Run. |
| T16 | Task messages from both real Agent principals | Durable ordering, actual sender, independent read filters. |
| T17 | Direct two-Agent thread; third Agent/foreign tenant probing IDs | Zero thread text/name/count/notification leak. |
| T18 | Duplicate message/wake signals across reconnect/restart | One stored message and max one admitted target Activation. |
| T19 | Recipient opted out / busy / rate-limited | Durable pending/undeliverable fact; no unauthorized wake. |
| T20 | Parent `execution.wait`, message/completion vs timeout races | At most one successor and correct reused durable wait semantics. |
| T21 | Worker `work.complete` and approval-successor chain | Task terminal based on real final Run, not model prose or initial Run only. |
| T22 | Parent report for **different** target Agent | Parent-owned Run/Session, safe untrusted worker result, one report. |
| T23 | Parent ended/deleted/paused or loses current authority | No injected Run; inspectable withheld/failed notice when permitted. |
| T24 | Parent can read Task but not worker private Session/files | Only safe metadata; result/Artifact bytes separately denied. |
| T25 | Attempt to treat task success as Goal accepted | Goal remains unverified until exact required evidence + acceptance. |
| T26 | Exact Artifact publication/source read/review by delegate | Independent worker/source/recipient authorization; no auto-sharing. |
| T27 | Cancel queued/running/effect-indeterminate task | Truthful request/terminal/NeedsAttention; no blind re-execute. |
| T28 | Human retry of a terminal Task | New Task ID, explicit `retryOf`, original terminal facts retained. |
| T29 | SQLite host disposal/recreation with admitted tasks/messages | All facts/receipts/queues survive; no duplicate logical operation. |
| T30 | Expiry/retention/deletion and late async scoped HTTP reply | Private UI projection cleared; old callback cannot reopen. |
| T31 | Old `background.*` model calls and old version/payload | Explicit unsupported/invalid, **no alias or compatibility**. |
| T32 | Independent Automation/Event/Manual roots and destination reports | Continue functioning via incumbent ownership, not child-task fallback. |
| T33 | Capacity, fairness and many-task pages with hidden targets | Bounded pagination after access filtering; no leak via count/cursor. |
| T34 | Production-like native protected effects with task actor | Worker own IAM/Review, effect fencing, bounded cancellation and honest unknown status. |
| T35 | Real Playwright delegate→worker→message→completion→inspect | End-to-end ownership, visible saved result, qualified task UI. |
| T36 | Playwright 320/390/768/1024/1440, 200% zoom, reduced motion | No clipping or unreachable action; native focus/escape/keyboard. |
| T37 | UI denied refresh with delayed task/message/Run fetch | Scope epoch retired, all private content gone, no stale reappearance. |
| T38 | New UI vs existing Chat/Goals/Background/Activity consumers | No regressed conversation, voice, history, approval or drawer spacing. |
| T39 | Migration from real predecessor and fresh reset | Strict startup gate; backup, conversion/refusal and schema parity recorded. |
| T40 | Docs and Impeccable finish-review | Contract/API/status names/copy match source; verified final visual verdict; no invented passing evidence. |

**Required local commands (select affected groups, do not presume success):** focused .NET domain/Application/Infrastructure/API tests for `Task`, `Message`, `Background`, `Goal`, `Activation`, IAM/Review and real SQLite restart; `pnpm exec vitest run ...` for the changed frontend services/components; `pnpm exec playwright test --config <new-task-delegation-config>` on real Synthetic; `pnpm run build`; `git diff --check`; docs-consistency/link and example parsing checks; the project's Impeccable context/review/finish commands when installed. Physical devices, hosted real models, hosted CI, native production isolation and deployment remain separately unverified until specifically tested.

## 13. Operational observability and failure recovery

Extend existing `AgentCore.Runtime` tracing/audit, with **TaskId, SourceSessionId, WorkerSessionId, SourceRunId, WorkerRunId, TenantId, source/target Instance IDs, root Activation and generation, permission decision, receipt ID and diagnostic ID**, as permitted safe metadata. Metrics: accepted/refused tasks by safe reason; route ineligible/capacity; queued→claimed latency; Task terminal and `NeedsAttention` counts; pending dependency, message backlog, notification lag, delivery/ack failures; budget/depth throttles; task cleanup; report-admission refusals. Do **not** log model messages, objectives, unredacted result text, secrets, credentials, private file contents or entire prompt payloads. Respect current diagnostic content-redaction policies.

Add bounded host-restart/task reconciliation to existing tenant Run hosted execution (no second runner), with capped batch scans, fairness and per-tenant limits. Expose enough operator facts to diagnose *queued but not dispatched*, *task Run waiting*, *message delivered but not admitted*, *notification pending*, *source authorization lost*, *uncertain external effect*, *expired parent*, and *retention cleanup*. Document safe controls to cancel, readback, reinstate legitimate target policy or create a **new** Task. No silent remediation that could repeat an irreversible effect.

## 14. Documentation synchronization — mandatory implementation checklist

**Canonical change owner first, summaries second.** This proposal is a standalone source for implementation planning; on start, move/adopt it into `docs/proposals/` and integrate the approved semantics in the canonical documents below. Do not leave this proposal as a competing authoritative spec after shipping.

| Repository document / asset | Required synchronized update |
| --- | --- |
| `TODO.md` | Initiative 9 scope, decision/status and resolved “parent tracks sub-agents” note; do not mark complete before acceptance. |
| `docs/01-product-vision.md`, `docs/02-mvp-scope.md` | Authorized post-MVP collaboration framing and exclusions; no group/A2A or new engine. |
| `docs/03-system-architecture.md` | Add **Task Delegation & Agent Communication** owner/sequence/security diagram, two independent Agent principals, parent/worker Session boundaries, reporting, direct threads, current ownership invariants. Supersede same-owner-only child claim. |
| `docs/04-backend-interfaces.md` | New typed Task/Message ports, admission CAS, task review, routing, delivery/receipt, continuation/wait and independent protected source reads. |
| `docs/07-event-model.md` | New semantic task/message status hints and Activity lineage without new shared chat voice event model. |
| `docs/08-development-roadmap.md` | Mark Initiative 9 **planned/in implementation/verified** based on real evidence; Initiative 10 remains next and unstarted. |
| `docs/10-technology-decisions.md` | Record explicit breaking successor: Task metadata only, unique worker-own Run, cross-Agent identity + Space, two-party communication, new-only migration, no backward compatibility. Clearly supersede historical same-Agent child contract without changing past evidence. |
| `docs/11-repository-structure.md` | Place canonical domain/application/store/API/UI owners and removal-map paths; no extra projects/engine. |
| `docs/12-backend-implementation-spec.md` | Task state machine, transactional sequence, permission/revision/receipt/contracts, limits and lifecycle. |
| `docs/13-frontend-implementation-spec.md` | Session Tasks, Task detail, Agent inbox, direct two-Agent threads, cooperation settings, approval and full UX-state matrix; exact design system reuse. |
| `docs/14-api-and-realtime-protocol.md` | Single canonical `tasks.*` tool and tenant HTTP/event wire shapes, strict errors/idempotency/read rights and retired `background.*` names. |
| `docs/15-persistence-and-configuration.md` | EF migration, indexes, InMemory parity, default-off policy, strict reset/no fallback and retention/cleanup. |
| `docs/16-testing-strategy.md` | T01–T40 regression/acceptance mapping, Synthetic/SQLite/Playwright, negative and race cases. |
| `docs/17-observability-and-operations.md` | Startup reset/refusal, backup/restore, bounded hosted-pass reconciliation, counters, controls and deferred external qualification. |
| `docs/18-implementation-plan.md` | D0/D1/B1–B5/F1/F2/R/A gates, acceptance scope, dependency order and real run commands; no completion claims. |
| `docs/19-multi-tenant-iam-ux.md` | New scopes, cooperation policy, independent agent approval, recipient visibility and Review UX. |
| `README.md`, `AGENTS.md` | Update only overview/invariants if needed; the pre-existing “no multi-agent without spec revision” is satisfied by this authorized initiative spec, not deleted casually. |
| `.impeccable/surfaces/task-delegation.md`, `.impeccable/surfaces/agent-communication.md` | Create **before frontend implementation**; document post-implementation reused components, finish review and evidence. |
| `.agents/context/DESIGN.md`, `.agents/context/PRODUCT.md`, `.impeccable/design.json` | Compare actual rendered component/token state and update only factually; use project's supported sync process; no new second product spec. |
| Definition JSONs / Tool Registry / UI copy / Synthetic fixtures | Rename to canonical `tasks.*` and `agent-messages.*`, drop old background-only paths and update tests. |
| New dated `docs/reports/initiative-9-*-verification-2026-*.md` | Exact-source gate results, Impeccable screenshots/detector/finish verdict, doc consistency, migration/operating limits and explicit unverified areas. |

### 14.1 Documentation consistency review rubric

Before implementation readiness sign-off and again at delivery, search for `background.start`, `background.list`, `background.take`, `background.acknowledge`, `ImmediateBackground`, `BackgroundCompleted`, `CompletionReport`, `AgentRunOwner`, `Task`, `AgentMessage`, `SessionOrigin`, `Goal`, `SpaceContext`, old API paths, stale Impeccable tokens and scope names. **Classify every hit** as current code, retired code to delete, legitimate independent-Automation use, or historical report. Verify named anchors and relative links, closed JSON shapes, tool descriptor examples, current permissions, status nomenclature, parent/worker Run ownership and UI action copy. No mechanical string replacement across distinct semantics. Do not edit historical reports to imply new acceptance. Restore a consistent “implemented” claim only after passing actual behavior gates.

## 15. Design and security decisions that are now resolved

1. **Canonical Task entity:** yes, **coordination aggregate only**; no second execution primitive.
2. **Parent tracks children:** yes, via durable Task projection and guarded subscriptions/inbox; not mutable child state in parent Session.
3. **Cross-Agent execution:** target principal always owns its worker Session/Run; source never impersonates.
4. **Same-Agent background:** migrated to the *same* Task contract; old `background.*` model tools removed entirely.
5. **Independent Automation:** current Automation roots and actual destinations remain separate; no broken backward-compatible child engine kept.
6. **Agent messaging:** task thread and direct exactly-two-Agent thread; no group participation. Wake is opt-in, default off, admitted through existing Session/Run path.
7. **Routing:** deterministic eligible selection with optional bounded Decision Intelligence advisory; current auth is final.
8. **Workflow:** shallow hierarchy and immutable earlier-sibling prerequisites only; no DSL/engine.
9. **Workspace:** target Space/Membership selected and pinned independently; no implicit parent file/credential sharing.
10. **Outcome:** actual Run/receipt truth; Task success is not independent Evaluation or Goal verification.
11. **UI:** Impeccable shape before coding, established AntD Operate world, final bounded polish and design-documentation sync.
12. **Migration:** new-only, deliberate disposable reset permitted, strict startup refusal for unmappable populated stores, no runtime legacy parser/fallback/alias.

## 16. Release/acceptance definition

Implementation is **ready to declare locally complete** only when all of the following are true:

- D0/D1 design audit and documented Impeccable-first surface contracts were completed before UI code.
- All B1–B5 backend and F1/F2 UI gates were exercised on the **exact final source**. At least one **real cross-Agent** task/worker Run, one task conversation, one direct Agent thread and one bounded parent completion were observed in actual Synthetic host/runtime, plus InMemory and SQLite restart qualification.
- Security negatives prove absence of target impersonation, cross-tenant/Space disclosure, stale approval continuation, source-witness forging, duplicate effects and hidden data in counts/cursors; current IAM/Review is unchanged outside explicit new permissions.
- Old child-specific `background.*` tools/APIs/Definition capabilities and runtimes are gone; independent Automation reporting, existing `work.complete`, `execution.wait`, Chat/Voice, Evaluation and Goals remain passing.
- Intentional reset/migration and startup refusal are proven on the true preceding schema; no silent deletion and no compatibility branch.
- Native Playwright includes the listed user workflows and recovery, viewport/zoom/reduced-motion/keyboard checks, plus delayed-private-response regression. Impeccable produces an actual final polish verdict and evidence, not a speculative pass.
- Canonical architecture/API/persistence/frontend/testing/operations/decisions docs, design-system context and report pointers match current code. All new links/examples pass consistency review. No status in TODO/Roadmap claims more than the measured tests.
- Any hosted real-provider, deployment, physical-device, native Sandbox or optional CI checks not performed are **listed as unverified** rather than silently treated as complete.

**Final product principle:** Two agents cooperate as separate accountable actors, linked by a durable task and a small, well-defined communication boundary. The platform proves *who assigned work, who performed it, what was actually observed, who may read it, and what was delivered*—without claiming that an LLM message or an approval alone is a completed result.

### Adopted execution allowance

The selected enforceable budget dimension is steps plus active execution time. Each child reserves both from its source's remaining root allowance; the absolute deadline is an additional bound, not a replacement for active-time accounting. A requested completion report reserves eight steps and 60 seconds from the same allowance before admission. Its reservation remains held conservatively after worker settlement. A completion-report Run may not delegate another Task. Provider-token pricing and monetary accounting are outside this implementation's claimed enforcement; any future financial dimension requires its own authoritative accounting contract and verification.
