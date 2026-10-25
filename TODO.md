# TODO

[Current status](#current-status) · [Open gaps](#open-work-and-acceptance-gaps) · [Roadmap](#consolidated-implementation-roadmap) · [Dependencies](#milestones-and-dependencies)

## Current status

The IAM implementation and local Synthetic proposal acceptance are complete in the [final delivery](docs/reports/tenant-final-delivery-2026-10-24.md). The [stabilization follow-up](docs/reports/iam-stabilization-2026-10-24.md) tracks the bounded launcher, Compose, default Playwright and approval-owner reconciliation. Earlier module reports retain their own source versions and limits. Live-provider/customer UAT, physical devices, deployment and optional hosted CI remain distinct from local IAM completion.

### Existing foundations

| Foundation | Verified scope and remaining boundary |
| --- | --- |
| Context Architecture | Core/system, Runtime/developer and Evidence share one typed assembly path, schema-2 `agentInstructions`, frozen field/task provenance and interaction-only format-2 checkpoints. Locally verified; populated installations still require explicit backed-up offline conversion. [Evidence and operator actions](docs/reports/three-layer-context-cutover-verification-2026-10-15.md). |
| Agent State Architecture | State awareness, resource isolation and editable Persistent, Session-isolated and Stateless configuration presets are locally complete and integrated with the context cutover. These are field presets, not competing runtimes. [AS-01–28 evidence](docs/reports/agent-state-isolation-verification-2026-10-15.md) and [strict-boundary/browser follow-up](docs/reports/agent-state-isolation-review-2026-10-15.md). Live OpenSandbox, hosted models, customer accounts, deployment and optional CI remain separate from this local scope. |
| Workspace & Sandbox Foundation | The earlier Instance-home filesystem implementation is historical; the Space initiative below replaces home ownership. Temporary Session `/working`, canonical filesystem integration and direct mounts retain the mechanisms locally qualified in the [filesystem cutover ledger](docs/reports/canonical-workspace-cutover-verification-2026-10-16.md). Scoped P10 OpenSandbox is closed for trusted single-user local Docker development/demo on `4c8f2507`, with all eight [exact-source hosted gates](https://github.com/trannamtrung1st/agent-core/actions/runs/38518370016) passed; [OSB-01–18 verification](docs/reports/opensandbox-integration-verification.md) preserves historical failures and actual runtime evidence. Own execd loopback and absent writable-layer quota remain disclosed local-demo limits; production isolation/hardening is unfinished. |
| Multi-Tenant & IAM | Locally complete: canonical identity, scoped IAM, owned resources, trusted execution, exact approvals and tenant UI. [Final delivery](docs/reports/tenant-final-delivery-2026-10-24.md) owns proposal acceptance; [stabilization](docs/reports/iam-stabilization-2026-10-24.md) owns repository launch/test integration. Deployment and live UAT remain separate. |

The MVP and phases through P9.10 have closed/frozen evidence. Later bounded enhancements have their own records; an earlier freeze does not accept a successor. P11 remains unopened and requirement-triggered. Reopen closed work for a reproducible regression or an explicit new requirement.

Email accounts, Gmail OAuth and monitoring have accepted non-live scopes. Contextual Admin/email integration also has exact-source hosted acceptance; see [integration verification](docs/reports/email-monitoring-main-integration-2026-10-13.md). Real mailbox acceptance remains separate.

Office + Google Drive v1 implementation is closed after [current-head local qualification](docs/reports/office-drive-cross-feature-fixes-2026-10-14.md#qualification-closure) on `2766d08e`: historical sparse-assertion and post-Sandbox integration qualifications are resolved. Customer-file/live-account acceptance remains separate; no further mandatory implementation fixes or hosted CI are required for this closure.

### Tenant integration evidence

- **Background execution:** [Protected background file effects](docs/reports/tenant-background-file-effects-verification-2026-10-20.md) now cover Workspace/Office mutation, exact inline/source publication, independent decisions, same-task successors and no-replay recovery through the existing owners. [Tenant runtime core/report-back](docs/reports/tenant-background-runtime-verification-2026-10-20.md) and [canonical backend Activity/Continue](docs/reports/tenant-background-activity-verification-2026-10-20.md) now preserve trusted lineage, scoped visibility, initial results and both active/quiescent transitions. The [tenant Activity/Background UI](docs/reports/tenant-background-activity-ui-verification-2026-10-20.md) now supplies exact scoped Sessions/Runs, original-result inspection and explicit same-Session continuation with no-replay recovery. [Background file observation/read consent](docs/reports/tenant-background-file-observation-verification-2026-10-20.md) now preserves task identity across approval successors and reports only terminal results. [Live queued human input](docs/reports/tenant-live-input-queue-verification-2026-10-20.md) now qualifies active text/file binding, fresh human roots, ordered dispatch, current-task isolation, Stop/restart survival and positive capable-definition bound-Attachment background reads. Interrupt/Steer, voice arbitration, frontend live-send interaction, file mutation/publication, native effects, creation Review, inbox/wait tools, scheduled/event/initiative origins and full background acceptance remain open.

- **Human Attachments:** Canonical upload/read/download/Office preview, pending abort and expiry/deleted-Session cleanup use actual owned Attachment/IAM/file stores. [Focused evidence](docs/reports/tenant-attachment-storage-verification-2026-10-19.md). [Internal binding/history](docs/reports/tenant-attachment-binding-verification-2026-10-19.md) and [scoped runtime input context](docs/reports/tenant-attachment-context-verification-2026-10-19.md) have focused qualification. [Allowed-policy model reads](docs/reports/tenant-attachment-read-verification-2026-10-19.md) preserve guarded media, historical reads and source fences; [exact read Review](docs/reports/tenant-attachment-read-review-verification-2026-10-19.md) now uses the existing consent path; [public file messages and composer](docs/reports/tenant-attachment-message-verification-2026-10-19.md) now use canonical admission, exact submitted selectors, tenant preview/download and explicit upload recovery. [Exact bound-original publication](docs/reports/tenant-attachment-artifact-verification-2026-10-19.md) is locally qualified through the existing source/Review/Artifact owners. [Exact virtual Workspace/Office file reads](docs/reports/tenant-attachment-virtual-read-verification-2026-10-19.md) now reuse canonical source/consent/recovery. [Attachment namespace list/search](docs/reports/tenant-attachment-namespace-verification-2026-10-19.md) now uses the canonical Workspace observation/Review and checkpoint owners. [Human materialization backend](docs/reports/tenant-attachment-materialization-verification-2026-10-19.md) now preserves exclusive working copies plus immutable human-owned Artifacts, compound current authority and partial/unknown recovery. [Shared frontend materialization](docs/reports/tenant-attachment-materialization-ui-verification-2026-10-19.md) now provides current source inspection, confirmation, canonical Session file collections and deliberate partial/unknown recovery. The remaining proposal families stay open.

These grouped checkpoints replace overlapping progress narratives. Later linked successors supersede earlier pending statements only within their measured scope; the IAM delivery ledger remains the implementation tracker. Controlled SDK, store-reopen, actual application-host recreation, native browser and live Docker evidence retain the distinctions recorded in each report. No new checks or acceptance are claimed by this consolidation.

- **Workspace reads and human files:** Canonical human browse/search/preview/download/upload/replacement/nonrecursive delete and model read/list/search with exact durable Review are locally verified. Evidence: [human mutations](docs/reports/tenant-workspace-mutations-verification-2026-10-19.md), [human UI](docs/reports/tenant-workspace-ui-verification-2026-10-19.md), [model reads](docs/reports/tenant-workspace-model-read-verification-2026-10-19.md), [read Review](docs/reports/tenant-workspace-read-review-verification-2026-10-19.md), [search](docs/reports/tenant-workspace-search-verification-2026-10-19.md).

- **Workspace effects and Office:** Guarded direct mutations, exact mutation Review/grants, retained factual recovery and DOCX/XLSX inspection/create/edit are locally qualified through the existing filesystem/Run/consent owners. Evidence: [primitives](docs/reports/tenant-workspace-primitives-verification-2026-10-19.md), [direct effects](docs/reports/tenant-workspace-effects-verification-2026-10-19.md), [model/shared Review](docs/reports/tenant-workspace-mutation-review-verification-2026-10-19.md), [controlled abandoned-effect host recovery](docs/reports/tenant-workspace-approved-recovery-verification-2026-10-19.md), [Office inspection](docs/reports/tenant-office-inspection-verification-2026-10-19.md), [Office mutation](docs/reports/tenant-office-mutation-verification-2026-10-19.md).

- **Artifact storage and publication:** Session/Run-owned immutable storage, bounded inline publication and exact publication Review/exceptional grants use the incumbent atomic effect/receipt owners. Evidence: [storage](docs/reports/tenant-artifact-storage-verification-2026-10-18.md), [model publication](docs/reports/tenant-artifact-publication-verification-2026-10-18.md), [publication Review](docs/reports/tenant-artifact-review-verification-2026-10-18.md).

- **Artifact delivery and sources:** Independently scoped rich delivery/preview/download, metadata-only `artifacts.verify` Review, deleted-Session byte cleanup and exact Workspace source publication have bounded evidence; source and recipient reads remain independent. Evidence: [rich delivery](docs/reports/tenant-artifact-delivery-verification-2026-10-19.md), [metadata Review](docs/reports/tenant-artifact-metadata-verification-2026-10-19.md), [cleanup/restart](docs/reports/tenant-artifact-cleanup-verification-2026-10-19.md), [Workspace publication/model/HTTP/browser](docs/reports/tenant-workspace-artifact-model-verification-2026-10-19.md).

- **Resource sources and retrieval:** Pinned/current immutable sources now support exact text/Office reads and consent, namespace list/search, `knowledge.retrieve` and exact resource export. Older source-prerequisite pending notes are superseded only for these converted paths. Evidence: [source prerequisite](docs/reports/tenant-resource-source-verification-2026-10-19.md), [leaf reads/Review](docs/reports/tenant-resource-read-consent-verification-2026-10-19.md), [namespace](docs/reports/tenant-resource-namespace-verification-2026-10-19.md), [knowledge reads](docs/reports/tenant-knowledge-read-verification-2026-10-19.md), [resource export](docs/reports/tenant-resource-artifact-verification-2026-10-19.md).

- **Browser and Credentials:** [Canonical Agent/profile decommission backend](docs/reports/tenant-agent-decommission-verification-2026-10-20.md) now retires exact archived ownership and supervises scoped physical cleanup; the [canonical Lifecycle UI](docs/reports/tenant-agent-lifecycle-ui-verification-2026-10-20.md) now supplies archive/restore/decommission and deliberate recovery. [Canonical System privacy administration](docs/reports/system-browser-privacy-verification-2026-10-20.md) now uses current platform-human authority, policy/audit CAS and explicit UI recovery. Canonical human persistent-profile reset now has [backend/native/HTTP qualification](docs/reports/tenant-browser-profile-reset-verification-2026-10-20.md), with durable pending/unknown facts and no physical replay; the [shared frontend control and deliberate recovery](docs/reports/tenant-browser-profile-reset-ui-verification-2026-10-20.md) are now qualified. Canonical lifecycle/Agent archive now have [owned native retirement qualification](docs/reports/tenant-browser-retirement-verification-2026-10-20.md), with retry and retained profile state. Canonical Artifact-backed drops now have [exact source/native/model qualification](docs/reports/tenant-browser-drop-verification-2026-10-20.md). Capture and explicitly opted-in downloads now have [canonical publication/model qualification](docs/reports/tenant-browser-artifacts-verification-2026-10-20.md) and [shared publication result UI qualification](docs/reports/tenant-browser-files-ui-verification-2026-10-20.md).  Canonical Session Artifact uploads now have [exact source/consent/native qualification](docs/reports/tenant-browser-upload-verification-2026-10-19.md), with independent file reads and origin/claim fences. Owned bindings/exact targets, typed Review, at-most-once dispatch, converted model reads/commands and retained results across SQLite host recreation have focused evidence. Protected-fill Review, exact Credential-use grants and metadata-only Review are supported. Evidence: [ownership](docs/reports/tenant-browser-ownership-verification-2026-10-18.md), [target witness](docs/reports/tenant-browser-target-verification-2026-10-18.md), [Review](docs/reports/tenant-browser-review-verification-2026-10-18.md), [effect/recovery](docs/reports/tenant-browser-effect-verification-2026-10-18.md), [model commands](docs/reports/tenant-browser-model-verification-2026-10-18.md), [model reads](docs/reports/tenant-browser-read-verification-2026-10-18.md), [host recreation](docs/reports/tenant-browser-restart-verification-2026-10-18.md), [Credential-use grants](docs/reports/tenant-browser-credential-grant-verification-2026-10-18.md), [Credential metadata Review](docs/reports/tenant-credential-metadata-review-verification-2026-10-18.md).

- **Sandbox ownership and dispatch:** Canonical lease/receipt storage, current execution authority, typed command consent and existing-generation SDK dispatch have bounded evidence. Saved setup verification and disposable verification retirement reuse existing owners. Evidence: [runtime ownership](docs/reports/tenant-sandbox-runtime-ownership-verification-2026-10-18.md), [current policy](docs/reports/tenant-current-execution-policy-verification-2026-10-18.md), [command Review](docs/reports/tenant-sandbox-command-approval-verification-2026-10-18.md), [SDK dispatch](docs/reports/tenant-sandbox-sdk-execution-verification-2026-10-18.md), [Instance verification](docs/reports/tenant-instance-sandbox-verification-2026-10-18.md), [verification retirement](docs/reports/tenant-sandbox-verification-retirement-verification-2026-10-18.md).

- **Sandbox lifecycle and model controls:** Review-bound provisioning, owned recovery, human Stop/reset/profile switching and model command/inspection/Stop/reset/read Review are verified at their reported controlled SDK/store/HTTP/model boundaries. This is separate from live Docker qualification. Evidence: [provisioning](docs/reports/tenant-sandbox-provisioning-verification-2026-10-18.md), [recovery](docs/reports/tenant-sandbox-recovery-verification-2026-10-18.md), [human Stop](docs/reports/tenant-sandbox-human-stop-verification-2026-10-18.md), [human reset](docs/reports/tenant-sandbox-human-reset-verification-2026-10-18.md), [profile switching](docs/reports/tenant-sandbox-profile-switch-verification-2026-10-18.md), [model command](docs/reports/tenant-sandbox-model-command-verification-2026-10-18.md), [model inspection](docs/reports/tenant-sandbox-model-inspection-verification-2026-10-18.md), [model Stop/reset](docs/reports/tenant-sandbox-model-controls-verification-2026-10-18.md), [read Review](docs/reports/tenant-sandbox-read-review-verification-2026-10-18.md).

- **Tenant UI and Governance:** Sandbox catalog/Instance/Session surfaces, scoped control reads, Instance Resource management/rich files and witnessed single-permission operation-review policy editing have focused local browser/HTTP evidence. Evidence: [Sandbox catalog](docs/reports/tenant-sandbox-catalog-ui-verification-2026-10-18.md), [Instance Sandbox](docs/reports/tenant-instance-sandbox-ui-verification-2026-10-18.md), [Session control reads](docs/reports/tenant-sandbox-controls-read-verification-2026-10-18.md), [Session Sandbox](docs/reports/tenant-session-sandbox-ui-verification-2026-10-18.md), [Instance Resources](docs/reports/tenant-instance-resources-ui-verification-2026-10-18.md), [Governance](docs/reports/tenant-governance-ui-verification-2026-10-18.md).

- **Policy-read recovery:** One revalidated fresh policy read handles a raced security snapshot; model calls/effects are never replayed. The historical SQLite provider-unknown/preference reliability cause remains unproven. Evidence: [focused recovery evidence](docs/reports/tenant-policy-read-recovery-verification-2026-10-18.md).

Remaining IAM work is the current integration and acceptance list in the [delivery ledger](docs/reports/multitenant-iam-delivery-2026-10-16.md); historical module checkpoints do not reopen already qualified successors.

The [OpenSandbox main-integration ledger](docs/reports/opensandbox-integration-verification.md#current-main-integration--2026-10-14) preserves the `ed943429` synchronization checkpoint and General Assistant v30 composition with immutable v29 public-download/Drive/Office authority; later integration status belongs to that ledger, while prior acceptance remains evidence for its original behavior SHA.

## Open work and acceptance gaps

- [ ] **IAM deployment and external qualification:** when explicitly requested, qualify the deployment's TLS/certificate and backup/restore arrangements, dedicated live-provider/customer UAT and physical devices. Optional hosted CI is unrun unless requested; it does not reopen completed local proposal acceptance. See [final delivery](docs/reports/tenant-final-delivery-2026-10-24.md) and [stabilization](docs/reports/iam-stabilization-2026-10-24.md).

- [ ] **Office/Drive manual and Real acceptance:** implementation and all seven automated Synthetic/Compose gates passed on `a451600a`. Dedicated real Google UAT, desktop Office-client fidelity and actual native browser 200% zoom remain unverified; see the [successor ledger](docs/reports/office-file-preview-drive-verification-2026-10-13.md).

- [ ] **Original Background Work results:** reconcile the remaining exact-source hosted acceptance after follow-up fixture/native-browser corrections. Local checks and earlier green runs are recorded in the [verification ledger](docs/reports/background-original-result-verification.md); preserve the distinction between earlier acceptance and later edits.
- [ ] **Core Events stabilization:** establish and record exact-source hosted acceptance for the stabilization and subsequent recovery/resource-hygiene follow-ups. Preserve the documented SDK fallback and execution-time timeout/cancellation coverage limits. See [verification](docs/reports/core-events-automation-presets-verification.md).
- [ ] **Event recovery operations:** observe deployed exhaustion/backlog trends and set alert baselines using the [operator runbook](docs/17-observability-and-operations.md#observing-event-recovery-pressure). Synthetic load results do not establish production rates.
- [ ] **Agent self-management live UAT:** canonical local Harness/Self-Management, scoped configuration and automatic fresh-Run continuation are included in the IAM final delivery. When explicitly requested, run bounded dedicated real tool-capable model UAT. Earlier proposal/hosted pending statements in the [historical ledger](docs/reports/agent-self-management-verification.md) do not reopen that local acceptance.
- [ ] **Real email UAT:** after operator setup and explicit dedicated-mailbox opt-in, verify Google consent, refresh/restart, read, exact-approved send/recipient delivery, revocation and Gmail/Custom IMAP monitoring. Non-live acceptance is recorded in [account verification](docs/reports/email-account-verification-2026-10-12.md), [OAuth verification](docs/reports/gmail-oauth-verification-2026-10-12.md) and [monitoring verification](docs/reports/email-monitoring-verification-2026-10-13.md).
- [ ] **Optional Workbench browser requalification:** the pre-IAM combined `opensandbox-local.py --ui` owner-token fixture is historical and excluded from current workflow selection. Real SDK/tenant-command/native-filesystem coverage is retained; qualify an equivalent full real Workbench/browser/Office journey through canonical tenant authority when that broader environment check is requested. See [stabilization limits](docs/reports/iam-stabilization-2026-10-24.md).
- [ ] **Hosted voice (HOSTED-04):** complete one non-Synthetic voice smoke on an explicitly selected configuration. Hosted provider checks remain opt-in; see [speech handoff](docs/reports/p1-replaceable-speech-handoff.md).

Other bounded successor verification limits remain in their linked reports under the [Implementation Plan](docs/18-implementation-plan.md). Check those records before claiming a follow-up is closed.

## Consolidated implementation roadmap

One canonical entry per initiative follows. Existing subsystem acceptance gaps above remain distinct from future extensions. This numbered order is the intended implementation sequence; earlier initiatives need not be fully deployed before later design work begins. No dates, completion percentages or new acceptance gates are assigned here.

### 1. Multi-Tenant & IAM — locally complete

Canonical tenant/resource ownership, Agent Access, restricted agent principals, scoped RBAC, exact approval/grant continuation, isolation and obsolete-authority retirement have completed local proposal acceptance. The [final delivery](docs/reports/tenant-final-delivery-2026-10-24.md) and [stabilization follow-up](docs/reports/iam-stabilization-2026-10-24.md) preserve the evidence and operating limits. Continue with initiative 2 when requested; the [remaining external obligations](#open-work-and-acceptance-gaps) are deployment/UAT work.

### 2. Space + Membership + optional Environment + Secrets — locally implemented

Implement the approved coherent cutover: Space owns situational instructions and persistent `/home`; Membership owns contextual resource availability; optional Space Environments own typed variables and metadata only. Tenant Secrets reuse the existing protection/IAM/approval foundation. Each Instance has one protected Private Space; each Session selects its Space and optional Environment, captured immutably by each Run. Workspace/sandbox, protected consumers, automations/events/children and the complete UI must use these owners together. Definition execution policy is separately named `DefinitionExecutionPolicy` / `executionPolicy`. [Implementation evidence](docs/reports/space-environment-implementation-2026-10-25.md) records complete local qualification and the ordered main/PR integration. Live matching Sandbox contract-5 reverification is a deployment gate; no installed state is reset or deployment/CI acceptance implied.

### 3. Dynamic Model & Provider Management

Extend the existing provider abstraction and primarily file-based configuration into system/tenant model catalogs: built-in system defaults, admin-managed model/provider configurations, custom OpenAI-compatible endpoints, local LLMs, OpenRouter, enable/disable and tenant availability controls. Capture capabilities and metadata suitable for later intelligent routing. Existing model selection is a foundation; Decision Intelligence remains initiative 6.

### 4. Self-Management & Admin Assistant

Consolidate existing Harness/Self-Management with IAM through current tools, workflows and authority owners: permission-controlled inspection/configuration, agent-assisted configuration and policy management, exact approvals for protected actions, and Managed/Frozen behavior. Admin Assistant is an authorized agent principal using those capabilities. Keep Continuity independent. Complete the [existing successor acceptance gap](#open-work-and-acceptance-gaps) without treating the implemented subsystem as new.

### 5. Message, Execution & Chat Simplification

Review Messages, Activations, Agent Runs and Responses for simpler execution/conversation semantics. Evaluate removing any remaining redundant Turn concept only after dependency analysis proves execution, history, checkpoints, permissions and reporting remain intact; this is an architectural proposal requiring specification revision. The accepted `Session → Activation → AgentRun` cutover already retired the old execution engines.

Include explicit end-chat capability/tool, retention policies and temporary chats, long composer content represented as attachments when appropriate, and cleaner context handling. Build on existing terminal lifecycle and composer/attachment owners rather than resurrecting completed migrations.

### 6. Self-Hosted Decision Intelligence

Explore an optional lightweight, provider-agnostic decision service, potentially a Jev-like open-source local model compatible with existing Docker Compose infrastructure. Prioritize: (1) Smart Model Routing and Auto Mode; (2) Tool/Skill relevance; (3) Context/Memory relevance; (4) initiative intelligence; (5) automatic trigger filtering; (6) configuration assistance; (7) work prioritization. Start with the foundation and useful early decisions; defer later Evaluation, Delegation and Group Chat integrations.

Keep it independently disableable with the existing runtime functional without it. It is neither a separate Agent Instance nor an execution engine, and cannot replace deterministic IAM/authorization. Avoid unnecessary infrastructure; measure usefulness, quality, latency and operating cost.

### 7. Unified Scripts & Evaluation

Consolidate lifecycle scripts/hooks, structured results/evidence, Script/Agent/Manual evaluation, Agent Run and execution verification, evaluation configuration/management and Action Policies. Outcomes should be usable by Goals and Delegation through existing execution owners. Extend current evaluation facilities where appropriate. Automated evaluation remains separate from future human organizational review roles.

### 8. Session Goals

Introduce persistent explicit objectives associated with Sessions/Spaces (a future specification; Environments remain variable profiles), success criteria/checkpoints, agent-driven progress updates, verification-based completion and bounded continuation/recovery. Retain compatibility with Sessions, Activations and Agent Runs and their existing execution/conversation ownership.

### 9. Task Delegation & Communication

Build internal cooperation on existing execution and authorization: durable delegated tasks, agent-to-agent communication, scoped authorization/identity propagation, independent execution with reporting back, task state/outcomes/recovery, delegation routing and workflow coordination. External A2A interoperability may follow internal foundations. Reuse existing owners without another general-purpose execution engine or redundant orchestration infrastructure; multi-agent implementation requires an explicit specification revision.

### 10. Group Chat

Keep this as the final major initiative: multiple humans/agents, shared participation/message visibility rules, mentions/explicit targeting, intelligent participation/initiative and cooperative delegated work. Preserve tenant, IAM, Session and communication boundaries; reuse prior execution, authorization and decision foundations.

## Milestones and dependencies

These four strategic groupings do not renumber or accept the Implementation Plan's milestones.

| Roadmap milestone | Intended sequence |
| --- | --- |
| Production Foundation | 1. IAM → 2. Space/Membership, optional Environments and Secrets → 3. Dynamic Model Management |
| Intelligent Runtime | 4. Self-Management → 5. Execution Simplification → 6. Decision Intelligence |
| Autonomous Execution | 7. Scripts & Evaluation → 8. Session Goals → 9. Delegation & Communication |
| Collaborative Platform | 10. Group Chat |

Self-Management and Message/Execution Simplification may proceed independently after IAM contracts stabilize. Decision Intelligence foundation can proceed alongside them when relevant contracts are available; Dynamic Model Management should supply configuration metadata for intelligent routing. External A2A must not block internal Delegation. Supporting improvements and production hardening can proceed when required.

## Supporting product improvements

These requirement-driven improvements are not sequential roadmap blockers. Each entry retains its existing deferred scope; only relevant canonical specification changes authorize expansion.

- **Application Identity & Integration:** client application identity, per-app account authentication, Application Bindings and embedded Agent Core usage. Website automation already uses explicit Credential grants and Instance-owned Browser state. Broader messaging, shared application/task workspaces and portable worker packages need separate requirements.
- **Files & Processing:** PNG/image workflows and broader file creation/transformation, building on existing Office, preview, attachment and Artifact owners.
- **RAG & Retrieval:** improve knowledge retrieval when justified; evaluate Chroma or similar vector/classifier retrieval only if a concrete need warrants revising the specification. Revisit sensitive-personal-data admission and unified Admin search as needed. Memory, Experience, Resources, instructions and persona retain distinct ownership.
- **Architecture Cleanup:** remove obsolete legacy paths, compatibility layers, fallbacks and inconsistent configuration/environment naming during relevant migrations; preserve current execution and authority owners.
- **Reliability & Infrastructure:** requirement-driven tools, sandbox, security, observability and provider improvements. Beyond accepted local P10, public/multi-tenant sandbox isolation, writable-layer quota and signed attestation remain deferred; pools, Kubernetes, multi-host scheduling and a second execution engine remain outside approved scope. Investigate the historical Real/OpenRouter image-reread gap when a hosted workflow requires it; revisit streaming STT/native speech-to-speech only for measured quality/latency needs. Add provider seams when another implementation needs them; marketplaces, universal plugins, package import/export, Git/folder sync and remote file browsing remain deferred. Scheduling beyond coalesce-to-latest or an external scheduler requires a concrete workflow; wake-up/dispatch remains beneath Core Automation/Activation/AgentRun semantics.
- **Production Operations:** deployment hardening, TLS, security/isolation, backups/restore, recovery/rollback, monitoring and customer-ready operations. P11 remains unopened; retain the completed IAM model when qualifying a deployment. Adopt PostgreSQL, object storage or external secret protection when hosting requires them; distributed workers/coordination/orchestration require measured need.

## Future organizational & business strategy

These are strategy ideas, not software features or implementation commitments.

- **Human AI Reviewer / Auditor:** a potential human role reviewing agent work, system quality, safety and operational efficiency, including whether AI reduces operating cost. This is not a new AI Auditor feature and is separate from automated Evaluation.
- **Marketing & Commercialization:** KOL/influencer reviews and partnerships; community/forum posts, demonstrations, tutorials and social media. Preserve the commercial video concept: one person appears to work alongside several coworkers throughout a day; the ending reveals collaboration with Agent Core agents. Explore AI employees as a possible future offering, and subscriptions, BYOK, bundled provider usage, pricing and billing as business-model options.

## Maintainer notes

Always keep this section. These personal ideas are future direction, not approved implementation scope. The mapping accounts for all 16 original notes in the canonical entries above; additional maintainer directions follow below.

| Original idea | Consolidated destination |
| --- | --- |
| 1. Client app identity / per-app account auth | Supporting: Application Identity & Integration |
| 2. Smart routing / Jev-like policy layer | Initiative 6: Decision Intelligence; deterministic authorization stays in IAM |
| 3. Retention / temporary chat | Initiative 5: Message, Execution & Chat Simplification |
| 4. AI auditor/reviewer / operating cost | Strategy: human Reviewer / Auditor role |
| 5. End chat capability/tool | Initiative 5 |
| 6. Long composer text as attachment | Initiative 5 |
| 7. Legacy / compatibility / fallback / environment naming cleanup | Supporting: Architecture Cleanup |
| 8. Run/turn verification, evaluation management, Session/Space goals | Initiatives 7: Scripts & Evaluation and 8: Session Goals |
| 9. Shared situational context and bindings | Initiative 2: Space + Membership; Environments hold typed variables |
| 10. KOL reviews, videos, forums/pages | Strategy: Marketing & Commercialization |
| 11. Commercial video / apparent coworkers reveal | Strategy: Marketing & Commercialization |
| 12. Admin assistant | Initiative 4: Self-Management & Admin Assistant |
| 13. PNG and other file creation/processing | Supporting: Files & Processing |
| 14. RAG / Chroma DB | Supporting: RAG & Retrieval |
| 15. Teamwork / communication / delegation / workflows / Grok-style inspiration | Initiative 9: Task Delegation & Communication |
| 16. Tools / sandbox / security / routing / monitoring improvements | Supporting: Reliability & Infrastructure; routing in initiative 6 |

### Additional maintainer directions

- **Built-in workflows (to be discussed):** explore built-in workflows that can be triggered by an event or manually by a user or agent, with input data supplied to the workflow. Discuss the design and relationship to existing Automations before specification or implementation.
- **MarkItDown evaluation:** evaluate how MarkItDown could be used in Agent Core and whether it fits the product's file-processing needs and architecture before considering adoption.
- **Built-in general-purpose assistant:** add a full-capability, ChatGPT-like assistant that users can optionally use alongside their own agents. This built-in assistant can only be enabled or disabled; it cannot be deleted. Record this as future product direction for specification before implementation.
- **Unopinionated Core/system context:** later, refine Core instructions to explain how an agent uses and interacts with Agent Core, without prescribing its personality, behavioral style or discretionary choices. Leave those choices to the agent's judgment, influenced by its persona and authorized configuration. Preserve required platform contracts, context trust boundaries and deterministic IAM/approval enforcement. This is a future context-policy review, not a change to the current Core contract.
- **Decision engine abstraction** (within [initiative 6](#6-self-hosted-decision-intelligence)): later, place intelligent decision logic behind a shared abstraction so the current LLM-based approach and a future Jev-like engine can coexist as interchangeable implementations. Keep callers independent of the selected engine; backend selection and any fallback policy remain design decisions. Deterministic authorization stays outside this abstraction. Record this direction for future design without introducing another execution engine.

Completed ideas remain covered by existing work: Skill/file mentions in the unified composer, Skill catalogs, bounded agent-assisted configuration, Experience and policy-governed initiative through Automations. Track future improvements against those owners rather than duplicating completed plans.

- **Sub-agents management**: parent session to keep track of sub-agents? [TBD].

**Unresolved design decisions:** Space/Membership/Environment ownership and switching are specified by the approved initiative 2 cutover; remaining future decisions are safe removal of any remaining Turn concept; Decision Intelligence model choice and measured value; retrieval infrastructure only if justified. Resolve these through the owning specifications before implementation; this consolidation chooses no new architecture.

## Maintenance rules

- Keep actionable gaps and future ideas here; keep completed checklists, phase narratives, CI retries and test counts in reports. Preserve the maintainer notes during consolidation.
- Update the canonical document when behavior or a decision changes, then update only necessary summaries. Historical freezes remain evidence of their original versions.
- Preserve the SessionRuntime mailbox as the single conversation-state writer, independently replaceable providers, stale-response fencing and active microphone capture during server audio output. See [AGENTS.md](AGENTS.md) and [Architecture](docs/03-system-architecture.md).
- Keep authority separate from tool discovery, Skills and credentials. Background work uses the same owned Session/AgentRun path and execution-time checks.
- The Space cutover replaces Instance-owned `/home` with Space-owned `/home`; keep temporary Session `/working`, resources, attachments and Artifacts distinct. Do not restore retired WorkItem engines, Application Connections or keyword Skill activation from old plans.
- Use Synthetic/offline checks by default; exercise affected runtime behavior for code changes. Prefer deterministic race coverage, run applicable gates and record exact-source hosted evidence where required. Local passes and publication alone do not establish closure.

## Automation destinations and background report-back closure

Retained because the [verification report](docs/reports/automation-targets-background-reportback-verification.md) links here as its closure owner. Closed/frozen on behavior `53c0c2fe9f87f28e866e80a2ff48127b5dbda3e8`, 2026-10-08: all five jobs in [hosted workflow 37781579245](https://github.com/trannamtrung1st/agent-core/actions/runs/37781579245) passed (backend, frontend, Playwright core, Playwright acceptance and Compose smoke). Final local checks passed after the documented retries; details remain in the report.
