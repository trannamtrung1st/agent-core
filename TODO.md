# TODO

The OpenSandbox cutover is synchronized with the owner-selected main snapshot
`ed943429`. General Assistant v30 preserves main's immutable v29 and combines
its current public-download/Drive/Office authority with sandbox execution. Combined
verification and hosted gate status are tracked in the [OpenSandbox ledger](docs/reports/opensandbox-integration-verification.md#current-main-integration--2026-10-14);
prior acceptance below remains evidence for its original behavior SHA.

Keep this file focused on open work and maintainer ideas. [README](README.md) summarizes the shipped product; the [Implementation Plan](docs/18-implementation-plan.md) owns milestone order and gates. Verification history, exact behavior commits and test results belong in [reports](docs/reports/).

## Current status

State and isolation implementation is locally complete, integrated with the reviewed schema-2 context cutover. [AS-01–28 evidence](docs/reports/agent-state-isolation-verification-2026-10-15.md) covers Synthetic/SQLite/model-context, Admin, realtime and disposable Chromium checks. Live OpenSandbox, hosted models, customer accounts, deployment and optional CI remain separate from this local scope. The [consistency follow-up](docs/reports/agent-state-isolation-review-2026-10-15.md) covers strict admission after policy relaxation and independent ephemeral browser fencing.

The owner-requested three-layer context replacement is locally verified: Core/system, Runtime/developer and Evidence use one typed assembly path, schema-2 `agentInstructions`, frozen field/task provenance and interaction-only format-2 checkpoints. Populated installations require the explicit backed-up offline conversion before startup. [Verification and remaining operator actions](docs/reports/three-layer-context-cutover-verification-2026-10-15.md) distinguish local acceptance from unperformed deployment, CI and live-provider qualification.

The MVP and phases through P9.10 have closed/frozen evidence. Later bounded enhancements have their own acceptance records; an earlier freeze does not establish acceptance for a successor. Reopen closed work for a reproducible regression or an explicit new requirement.

Email accounts, Gmail OAuth and monitoring have accepted non-live scopes. The contextual Admin/email integration also has exact-source hosted acceptance; see [integration verification](docs/reports/email-monitoring-main-integration-2026-10-13.md). Real mailbox acceptance remains separate.

Office + Google Drive v1 implementation is closed after [current-head local qualification](docs/reports/office-drive-cross-feature-fixes-2026-10-14.md#qualification-closure) on `2766d08e`: the historical sparse assertion and post-Sandbox integration qualifications are resolved. Customer-file/live-account acceptance below remains separate; no further mandatory implementation fixes or hosted CI are required for this closure.

P10's scoped OpenSandbox cutover is closed for trusted single-user local Docker
development/demo on behavior `4c8f2507`, with all eight
[exact-source hosted gates](https://github.com/trannamtrung1st/agent-core/actions/runs/38518370016)
passed. [Verification](docs/reports/opensandbox-integration-verification.md)
records OSB-01–18, actual runtime evidence and historical failures. Own execd
loopback and absent writable-layer quota remain disclosed local-demo limits;
production hardening is deferred. P11 remains unopened and requirement-triggered.

## Open work and acceptance gaps

- [ ] **Account / tenant IAM and durable approvals cutover:** identity/store/HTTP foundations and the owner-prioritized [access UI](docs/reports/access-ui-polish-2026-10-16.md) are locally verified. Complete tenant ownership across resources/stores/tools/Hub, trusted execution/root context, durable exact-operation approval/grant continuation, Agent Access and tenant-aware Chat, obsolete owner-authority removal and final canonical schema qualification. The [delivery ledger](docs/reports/multitenant-iam-delivery-2026-10-16.md) is authoritative for remaining gates; no full multi-tenant deployment or milestone acceptance is claimed.

- [ ] **Office/Drive manual and Real acceptance:** implementation and all seven automated Synthetic/Compose gates passed on `a451600a`. Dedicated real Google UAT, desktop Office-client fidelity and actual native browser 200% zoom remain unverified; see the [successor ledger](docs/reports/office-file-preview-drive-verification-2026-10-13.md).

- [ ] **Original Background Work results:** reconcile the remaining exact-source hosted acceptance after follow-up fixture/native-browser corrections. Local checks and earlier green runs are recorded in the [verification ledger](docs/reports/background-original-result-verification.md); preserve the distinction between earlier acceptance and later edits.
- [ ] **Core Events stabilization:** establish and record exact-source hosted acceptance for the stabilization and subsequent recovery/resource-hygiene follow-ups. Preserve the documented SDK fallback and execution-time timeout/cancellation coverage limits. See [verification](docs/reports/core-events-automation-presets-verification.md).
- [ ] **Event recovery operations:** observe deployed exhaustion/backlog trends and set alert baselines using the [operator runbook](docs/17-observability-and-operations.md#observing-event-recovery-pressure). Synthetic load results do not establish production rates.
- [ ] **Agent self-management:** finish full proposal acceptance, reconcile hosted results for the latest corrections and run bounded real tool-capable model UAT when the owner opts in. Automatic fresh-Run continuation is already verified in Synthetic; the [completion follow-up](docs/reports/agent-self-management-completion-followup.md) supersedes the older manual-continuation limitation. See the [proposal acceptance ledger](docs/reports/agent-self-management-verification.md).
- [ ] **Real email UAT:** after operator setup and explicit dedicated-mailbox opt-in, verify Google consent, refresh/restart, read, exact-approved send/recipient delivery, revocation and Gmail/Custom IMAP monitoring. Non-live acceptance is recorded in [account verification](docs/reports/email-account-verification-2026-10-12.md), [OAuth verification](docs/reports/gmail-oauth-verification-2026-10-12.md) and [monitoring verification](docs/reports/email-monitoring-verification-2026-10-13.md).
- [ ] **Hosted voice (HOSTED-04):** complete one non-Synthetic voice smoke on an explicitly selected configuration. Hosted provider checks remain opt-in; see [speech handoff](docs/reports/p1-replaceable-speech-handoff.md).

Other bounded successor verification limits remain in their linked reports under the [Implementation Plan](docs/18-implementation-plan.md). Check those records before claiming a follow-up is closed.

## Future work — start only for a concrete requirement

- **Sandbox beyond approved P10:** pools, Kubernetes, multi-host scheduling and a second execution engine remain deferred. The trusted-local OpenSandbox cutover is accepted above; public/multi-tenant isolation, writable-layer quota and signed attestation remain deferred.
- **P11 — Production:** introduce authentication and tenant ownership for an external pilot; adopt PostgreSQL, object storage or external secret protection when hosting requires them. Add TLS, operational monitoring, backup/restore and rollback evidence for that deployment. Distributed workers, coordination services and orchestration require a measured need.
- **Application participation:** define Application Bindings when a real client application embeds or invokes an identity. Website automation already uses explicit Credential grants and instance-owned Browser state. Broader messaging, shared application/task workspaces and portable worker packages need separate requirements.
- **Extensions and packaging:** add provider seams and reusable evaluations when another concrete implementation needs them. Marketplaces, universal plugins, package import/export, Git/folder sync and remote file browsing remain deferred.
- **Memory and retrieval:** revisit sensitive-personal-data admission policy, optional vector/classifier retrieval and unified Admin search when needed. Learned memory, Experience, instructions, persona and authoritative resources retain separate ownership.
- **Scheduling:** add misfire policies beyond coalesce-to-latest or an external scheduler only for a workflow that needs them; wake-up and dispatch stay beneath Core-owned Automation/Activation/AgentRun semantics.
- **Providers:** investigate the historical Real/OpenRouter image-reread gap when a hosted workflow needs it. Revisit streaming STT or native speech-to-speech only for a measured quality or latency requirement.

## Maintainer notes

Always keep this section. These are personal product ideas to refine, not approved implementation scope.

- [ ] End chat capability/tool.
- [ ] Refactor legacy, compatibility, fallbacks, refactor env names, etc. [TBD]
- [ ] **Validation/Evaluation**: agent run/turn verification, goals (session, environment), verfication/evaluation management ... [TBD]
- [ ] **Environment instructions**: for all agents working in same context? [TBD]
- [ ] **KOL, marketing strategy**: book KOL to use and review, post videos. Create posts across forums and pages [TBD]
- [ ] **Commercial video:** follow a person collaborating with apparent co-workers throughout a workday. At the end, reveal that it was one person working with Agent Core.
- [ ] **Admin assistant:** explore an agent that helps the owner operate and configure Agent Core through the existing authority and approval boundaries.
- [ ] **File creation and processing:** expand practical workflows for PNG images and other file formats.
- [ ] **RAG:** explore retrieval-augmented generation and evaluate Chroma DB if a concrete retrieval requirement justifies revising the specification.
- [ ] **Teamwork:** explore agent communication, delegation, workflows and orchestration, inspired by Grok-style collaboration. Multi-agent features require an explicit specification revision first.
- [ ] **Targeted improvements:** consider additional tools, sandbox enhancements, security, model routing and monitoring based on observed needs.

Completed ideas are covered by existing work: Skill/file mentions in the unified composer, Skill catalogs, bounded agent-assisted configuration, Experience and policy-governed initiative through Automations. Track future improvements against those owners rather than duplicating their completed plans here.

## Maintenance rules

- Keep actionable gaps and future ideas here; keep completed checklists, phase narratives, CI retries and test counts in reports. Preserve the maintainer notes during consolidation.
- Update the canonical document when behavior or a decision changes, then update only necessary summaries. Historical freezes remain evidence of their original versions.
- Preserve the SessionRuntime mailbox as the single conversation-state writer, independently replaceable providers, stale-response fencing and active microphone capture during server audio output. See [AGENTS.md](AGENTS.md) and [Architecture](docs/03-system-architecture.md).
- Keep authority separate from tool discovery, Skills and credentials. Background work uses the same owned Session/AgentRun path and execution-time checks.
- Keep durable Instance `/home`, temporary Session `/working`, resources, attachments and Artifacts distinct. Do not restore retired WorkItem engines, Application Connections or keyword Skill activation from old plans.
- Use Synthetic/offline checks by default; exercise affected runtime behavior for code changes. Prefer deterministic race coverage, run applicable gates and record exact-source hosted evidence where required. Local passes and publication alone do not establish closure.

## Automation destinations and background report-back closure

Retained because the [verification report](docs/reports/automation-targets-background-reportback-verification.md) links here as its closure owner. Closed/frozen on behavior `53c0c2fe9f87f28e866e80a2ff48127b5dbda3e8`, 2026-10-08: all five jobs in [hosted workflow 37781579245](https://github.com/trannamtrung1st/agent-core/actions/runs/37781579245) passed (backend, frontend, Playwright core, Playwright acceptance and Compose smoke). Final local checks passed after the documented retries; details remain in the report.
