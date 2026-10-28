# Formal Workflows architecture

Status: implemented for focused local qualification, 2026-10-29. The [delivery ledger](reports/formal-workflows-delivery-2026-10-28.md) owns measured checks, final source and external limits. This document owns the contract.

## Ownership

The [technology decision](10-technology-decisions.md#decision-bounded-embedded-formal-workflows) permits embedded Elsa for graph traversal and durable bookmarks only. Core owns tenant catalog, immutable published graph/hash, source admission, step operation intents, exact evidence, procedural decisions, audit and safe public projections. Core does not maintain a second authoritative program counter. Agent execution remains Task → worker Session → Activation → AgentRun. Goal completion, Evaluation truth and protected-operation approval remain independently authoritative.

Only Core compiles its closed validated graph. No public Elsa endpoints, Studio, Identity, HTTP/schedule triggers, expression engines, arbitrary activities, imported vendor JSON or host-code execution are enabled. All vendor types remain in Infrastructure. V1 is a bounded DAG with one actionable node, no loops, parallel joins or subflows.

## Source and participant contract

Manual starts bind the authenticated human command; claimed-agent starts bind the actual current Run; Automation starts bind the incumbent accepted occurrence. No-human sources keep a null original human. Every start pins the published version, input digest, invocation origin, request key, actual coordinator, participants and authorized context. Conflicting key reuse refuses; exact retries read back the same binding.

An agent-containing version declares explicit coordinator/target eligibility. The coordinator is an existing active Agent Instance/principal, not a new workflow identity. Each dispatch rechecks current source, coordinator `tasks.delegate`, target cooperation, IAM, Space membership, model readiness and bounded budget. Workflow start permission grants no operational authority.

The incumbent `TaskActor` still requires a real authenticated human or claimed Run. The successor adds an explicit `TaskSource.WorkflowContinuation` variant with a null source Session/Run/human-command and an exact saved workflow/step/version/operation witness. Its dedicated transactional admission facet preserves the public human/claimed-Run contract and never invents a source Session. Both stores revalidate the witness within their ordinary IAM transaction, reserve the source budget and admit the worker Task/Session/Run atomically. Worker execution and settlement recheck that source. A workflow Task owns its actual independent worker Activation root; original invocation/process lineage remains in the immutable workflow witness. The process budget sums durable Task reservations across those roots, while each worker retains incumbent descendant limits. No previous Session/Run is fabricated to manufacture an Activation parent. Workflow-aware source visibility and retention handle this source variant without assuming that every Task has a source Session. Focused InMemory/SQLite integration exercises this contract through real Synthetic workers; the delivery ledger distinguishes that evidence from feature acceptance.

## Existing Automation destination

`AutomationExecutionTargetKind.StartWorkflow` pins a tenant Workflow ID, immutable version/hash and canonical typed constant inputs. It has no source model preference, Session destination, completion-report destination or pinned Space/Environment: worker models and context come from the graph and current Task admission. The existing scheduler/event processor qualifies the current executor and actual manual/scheduled/matched or coalesced occurrence. A real manual invoker is retained; independent events never acquire one.

The owning Core transaction inserts the pending occurrence, reserves the exact workflow binding, links `AcceptedWorkflowExecutionId`, advances the registration and settles its event receipt/bucket. It creates no source Session/Activation/Run. The explicit flush order lets physical guards inspect the accepted link while preserving one Core transaction. Existing Session/Run occurrence links remain unchanged. Original event roots, depth and visited Automation IDs pass through the process witness into actual worker events; there is no extra workflow trigger loop. Retirement prevents future starts, while accepted occurrences retain their original version/inputs. Current executor capabilities, IAM, factual source access and participant authority remain live.

Current human Automation authoring supports this destination. Legacy model Automation mutation/invocation tools refuse Workflow destinations with `automation_workflow_edit_in_designer`; the dedicated `workflows.*` tools own model workflow selection.

## Durable bridge

Core first commits a unique start intent and stable Elsa instance correlation. Elsa create/readback follows outside that transaction. A failure between writes is reconciled using the original correlation, never by allocating another logical instance. Each waiting activity exposes an instance/node/attempt bookmark. Core reserves a unique step operation before calling its existing owner, records its receipt/result, then signals only that bookmark. Saved terminal evidence plus consumed-signal receipts support duplicate/late recovery. Unknown external effects require inspection and never receive a fresh retry key.

Elsa alone owns traversal. Core proof guards derive required global and selected-branch steps from the same validated graph and immutable receipts. Task success, agent assertions, procedural approval and Elsa terminal state do not substitute for positive verification or Goal acceptance. Revoked/expired/inconclusive/unknown facts cannot prove success.

Cancellation commits immediately, prevents new admission, asks incumbent owners to cancel admitted work and reconciles uncertain effects. Deleted/expired source Sessions and disabled source identities/tenants produce a separately qualified, immutable technical source-closure receipt; this is not a human cancellation or decision. Recovery cancels the actual Elsa bookmark and stops existing Tasks, retaining any uncertain effect for inspection. Retirement prevents new starts while preserving pinned executions. Recovery is a bounded pump alongside existing hosted recovery, with cancellation, paging and no Session-mailbox lock. Initial support is single-host SQLite.

## Persistence qualification

Use Core's existing SQLite file with separate Elsa EF contexts/tables and history identifiers, explicit offline migrations and backups. Do not assume cross-context atomic writes. Focused local qualification covers EF 10 compatibility, separate Core/Elsa transactions, WAL/busy timeout, suspend/reopen/resume and the bridge fault boundaries; deployment still follows the coordinated runbook. Missing/unhealthy durable persistence refuses starts; it never silently falls back to memory. InMemory Synthetic uses the same closed activities and bridge semantics.

## Implementation gates

0. Pinned minimal Elsa proof and reviewed design direction.
1. Closed graph validation, immutable catalog, CAS/dedupe and tenant authority.
2. Trusted continuation, one Synthetic worker and dual-store restart recovery.
3. Exact verification, procedural decision, branching and final proof.
4. Claimed-Run tools and existing Automation destination.
5. Tenant designer/outline, inspector and compact Chat entry.
6. Security, concurrency, lifecycle and failure qualification.
7. Bounded visual polish, canonical synchronization and source-based delivery ledger.

No gate is accepted merely by this document. [Implementation Plan](18-implementation-plan.md#formal-workflows) owns status.

### Source-content retirement

The incumbent deleted-Session Run/Task cleanup also minimizes terminal source-bound Workflow inputs and process-decision rationales after all corresponding Tasks and Run roots are quiescent. Technical source/version/input hashes, budget charges, request fingerprints and proof facts remain; a `ContentRetiredAt` marker permits only the one-way `{}` input tombstone. Workflow Tasks use their actual original Session witness, or their own worker Session when no original Session exists, as their content-retention source. Unconfirmed effects prevent minimization. Elsa stores closed node/bookmark mechanics and shared immutable graph definitions, never invocation inputs or worker output; its retained technical cursor is not a second protected-content owner. Native Evaluation operation Review and Manual Evaluation assessment project distinct reasons: only a protected operation uses WaitingForOperationApproval; Manual assessment stays WaitingForVerification.
