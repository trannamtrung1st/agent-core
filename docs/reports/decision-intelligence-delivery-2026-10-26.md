# Decision Intelligence delivery — 26 October 2026

## Overall verdict

The S1 foundation and S2 canonical preference/routing cutover are delivered and locally verified. **Production Auto and full proposal v1 acceptance remain unqualified**: this machine has no NVIDIA Docker runtime, so no real Kev checkpoint inference, held-out routing quality/calibration, warm latency or GPU footprint was measured. Host inference remains disabled/shadow and tenant Auto remains off by default. No application milestone acceptance or CI acceptance is claimed.

Exact implementation code SHA: **33d2e8857b2b2fbec4a661f1b9d96c2a8d75307a**. This report is a subsequent documentation-only commit. Baseline: `aab065eaabe8be27d5fd0480e1e8cdccae76b468` on `main`.

The approved [proposal](../proposals/self-hosted-decision-intelligence.md) permits S3–S5 as independently verified subsequent slices. They are explicitly deferred below; they are not implemented consumers hidden behind flags.

## Slice ledger

| Slice | Delivered state | Remaining qualification or owning blocker |
| --- | --- | --- |
| S1 foundation | Typed bounded choice/yes-no/score interface, strict result validation, LocalKev HTTP adapter, authorized Run-only LLM adapter, explicit Synthetic backend, bounded concurrency/cancellation, readiness and metrics; optional pinned sidecar deployment and launcher | LocalKev schema/transport is fixture-tested against the inspected pinned upstream contract. Real container build, checkpoint smoke, CUDA loading, crash/restart and resource qualification remain unrun on this host. |
| S2 routing and Auto | Shared foreground/unattended resolver; canonical inherit/fixed/auto model and independent reasoning intent; Definition/Instance/Automation/Session cutover; immutable decision/route pins; migration, HTTP/MessagePack, administration, Chat and Run inspection | Local functional acceptance passes. Production acceptance requires held-out baseline comparison, calibration, stated cost/quality tradeoff and an operator-approved latency/footprint budget. The 0.7 probability threshold and 500 ms timeout are controls, not measured quality/performance. |
| S3 tool/Skill relevance | Deferred | EffectiveSkillCatalogResolver and tool gates retain eligibility, explicit/always selections and budgets. No purpose-specific bounded ranking request/baseline has been qualified. |
| S3 context/memory relevance | Deferred | Prompt/continuity/memory owners retain authorized evidence, source freshness and budgets. A bounded candidate contract and held-out relevance fixture must precede replacement; no new retrieval store is introduced. |
| S4 initiative/event relevance | Deferred | Existing initiative eligibility, generative plan and voice limits remain; event expression/durable recovery remain authoritative. Purpose-specific evaluation, policy opt-in and durable verdict evidence need their own verified slice. |
| S5 configuration advice | Deferred | Existing assisted authoring and approval channels remain. No measured benefit or bounded advice projection has been qualified; intelligence does not write settings. |
| Work priority | Deferred | Initiatives 7–9 must supply the authorized pending-work owner and data. No Work table, additional scheduler or orchestration path was added. |

## Change inventory

- Application decision contracts accept only bounded caller-projected candidates/signals. Infrastructure owns HTTP/provider details; provider DTOs remain outside Domain/Application/Contracts. Synthetic requires no hosted credentials.
- LocalKev verifies authenticated `/v1/models` identity before `/v1/systemone`, pins the exact checkpoint, rejects redirects/proxy use/unapproved addresses, caps JSON/response size and validates complete finite normalized probabilities. Saturation refuses immediately; cancellation/deadline never creates a retry or a second dispatcher.
- The LLM backend requires an admitted trusted invocation and current model authority; it cannot perform pre-admission Auto routing. Authentication loss returns a stale-authority outcome without an alternate-provider fallback.
- Routing filters current offering/connection/credential eligibility, hierarchy, transport, tools, vision and fixed reasoning constraints before ranking. Fixed model choice bypasses ranking; Auto effort stays within the chosen offering. Uncertain/unavailable/shadow results use only an explicitly allowed eligible configured default or refuse with repair guidance.
- Saved intent is separate from the actual Run pin. Definition/Instance sparse inheritance, unattended precedence and Automation overrides share the canonical representation. New foreground, manual/scheduled Automation, direct background, completion-report, Experience-review and configuration/context continuation admissions use the existing owners. Approved work reuses its exact captured pin.
- Provider-round observations are bounded, process-local and tenant/route scoped; they exclude decision/classifier/speech calls. Their p50/p95 scope includes cold/retry overhead and is not advertised as warm latency or quality. Unknown model cost/context/performance remains unknown.
- UI extends Ant Design v6 directly: tenant AI Models → Routing, shared execution model fields, compact Chat picker, last actual Run observation and safe Run decision evidence. Operator-only readiness/help lives in System model catalog. CAS conflict recovery preserves drafts.
- The optional decision image pins Kev source and upstream lockfile, uses a separate immutable artifact download, non-root/read-only execution, private networking and bounded resources/queue/connections. Native Real startup remains independent; there is no CUDA/Python addition to the application image.
- Canonical architecture, backend/frontend/wire/persistence/operations/testing documents, host configuration, README and TODO now describe this delivery and its qualification limits. The [operator runbook](../decision-intelligence-runbook.md) owns setup, backup/refusal/restore and deliberate Auto qualification.

## High-value findings resolved

| Finding and source | Effect and correction |
| --- | --- |
| Prepared background creation model comparison — `TenantBackgroundCreationRules.cs:84`; report comparison — `TenantBackgroundReportRules.cs:120` | Comparing JSON of two different pin types made property order reject identical approved selections after decision evidence was added. Both sides now serialize the same concrete pin type. Actual creation/report approvals pass on both stores, including SQLite restart; report approval makes no extra decision call. |
| Independent inheritance — `InstanceSettingsResolver.cs:276` and `SessionModelBinder.BindPreference` | An inherit preference with its own reasoning intent must retain the lower model while applying the explicit reasoning override. Canonical merge/binding now preserves that distinction, including explicit model-default versus absent inherited reasoning. |
| Observed versus configured model — `SessionEventMapper.cs:291`, `sessionStore.ts:352` and `ChatApp.tsx` | Preference edits previously hid some prior fixed/default Run observations, and live admission could leave an old effort visible. A safe `observedFromRun` flag distinguishes actual Run facts from pre-admission previews; current response-start updates the observation while stale/tombstoned events cannot resurrect it. |
| Manual Experience review context — `TenantExperienceReviewService.RequestAsync` and `TenantExperienceReviewRules.Build` | This manual background entry point had no captured Space. It now resolves the Instance private Space through current requester/executor authorization, persists the matching Session context and uses the shared unattended model resolver. Existing deterministic replay returns the captured Run before reranking. The normal record-through-tools flow passes on both stores. |
| Offline cutover — `20261026100000_DecisionIntelligencePreferences.cs:10` | Immutable legacy configuration/approval evidence cannot be silently reserialized. Preflight refuses unsupported predecessors before schema mutation; supported mutable intent converts without inventing Auto. |

No known material issue remains in the locally verified implementation scope. This statement does not close the production qualification gates above.

## Focused local verification

.NET commands used `--no-restore --nologo -m:1 -p:UseSharedCompilation=false`. No GitHub Actions was dispatched, rerun or awaited; no full local CI suite ran.

| Check | Result and exercised behavior |
| --- | --- |
| Application: AutoModelRouter, SessionModelBinder, ExecutionModelPolicy, InstanceSettingsResolver, ModelRoundObservations, ManagementMutationContract and TenantAutomationAuthoring | **75 passed.** Fixed bypass, candidate/effort constraints, explicit inherited reasoning, shadow/default/refusal, routing revision evidence, bounded scoped observations and canonical authoring. |
| Domain: AgentDefinitionValidator and AutomationSpacePins | **18 passed.** Canonical preference validation and preservation of saved/occurrence context pins through lifecycle copies. Retired test-call/message assumptions were updated to the canonical contract. |
| Infrastructure: DecisionEngine, preference migration, actual Auto admission/provider invocation, model references and sparse Instance settings | **35 passed** in the focused 37-case run. Its two Experience cases initially failed; both were corrected and independently rerun below. Strict Kev fixtures include typed yes-no/score, malformed/adverse results, missing/auth/version/timeout/saturation paths. Actual store tests cover replay, cancel, voice admission, stale credential revocation and reopen with immutable pins. Native provider wire checks retain admitted effort and authority; observations exclude decision/classifier rounds. |
| Infrastructure: manual Experience review | **2 passed** after the context correction. New shared-resolver admission, replay, private context capture, source inspection and recording through normal tools pass on InMemory/SQLite. |
| API: canonical decision routing endpoint journey | **1 passed** on final source. Authentication/permission-safe status, Routing CAS, Auto chat admission, persisted intent/observed flag and manual Automation admission through existing owners. |
| API: background approval/restart | **3 passed** after pin-comparison correction. Auto creation/report approval on both stores, no report reranking, and pending SQLite report consent survives host recreation. |
| API: direct background execution | **4 passed.** Fixed and Auto on both stores: parent → detached child → one completion report, expected decision count and immutable evidence, one admission/report audit each. |
| API: surrounding focused configuration/management coverage | **21 passed** earlier in the delivery: decision HTTP plus affected configuration mutation/approval, model setup, draft and owned Instance paths. These overlap other rows and are not an additional unique-test total. |
| Web component/helper checks | **7 files / 110 passed** for Routing/model administration, System readiness, shared fields, picker, preferences, controls and Session state. **60 Session-store tests passed again** after the final observation change. Earlier focused Definition/Automation editor checks also passed; these sets overlap. |
| Web build | `pnpm --dir web build` **passed** (TypeScript and production Vite build). Existing large-chunk warnings remain; no lint script is configured. |
| Isolated Synthetic browser | `pnpm --dir web exec playwright test --config=playwright.decision.config.ts`: **1 passed, 19.1 s test / 38.5 s total.** Enable Auto, select fixed high reasoning, send/reload and retain intent/pin; image input selects the vision-capable offering and updates the live chip; disabled policy refuses while preserving draft; explicit fixed selection recovers; Instance-inherited Auto admits through the same route. Development proxy sockets reset during test teardown. |
| Playwright MCP against running Synthetic app | Actual sends/replies, reload, model-default versus prior high effort, fixed-model Auto reasoning (actual high), and switching back to Auto while retaining the last Run observation were exercised. Final Routing/System/Chat captures at 1440/768/390/320 CSS px have root width equal to viewport. |
| Deployment/configuration scripts | **17 focused Python tests passed** for launcher/configuration/Compose/preflight; **5 decision-specific tests passed again**. Shell syntax, Python compilation and optional Compose `config --quiet` passed. Fixtures use fake private keys. |
| UI finish and documentation handoffs | Independent reviewers reopened all **16 final captures**, returned **SHIP**, and recorded source/image fingerprints. [Finish review](assets/decision-intelligence/finish-review.md), [documentation comparison](assets/decision-intelligence/documentation-review.md) and adjacent PNGs are retained. This is representative responsive evidence, not physical-device or 200% browser-zoom certification. |

Relevant filters and scenario definitions are retained in `tests/AgentCore.*.Tests/*Decision*`, `AutoModelRouterTests`, `ModelRoundObservationsTests`, the affected model/settings/approval tests and `web/e2e/decision-intelligence.spec.ts`. Passing checks require no external provider credentials. Intermediate failures described above were repaired and retested; they are not counted as passing runs.

## Migration outcome

Only disposable predecessor/fresh fixture databases were migrated. No installed user database, existing application volume or customer data was reset or migrated.

The supported predecessor test verifies mutable fixed/inherited conversion, removal of retired columns, unchanged immutable Run evidence, foreign-key integrity, canonical approval persistence, restart reopening and no pending EF model changes. Frozen forward metadata updates the preference-bearing approval check and two insert triggers while preserving the other 188 trigger definitions; historical migrations/resources are unchanged. Unsupported legacy Definition configuration, prepared Management/Automation approval evidence and orphan effort intent refuse before mutation. Downgrade deliberately refuses in favor of a matched binary/database/key backup restore.

The read-only preflight checks a distinct existing backup and critical schema/inventory/integrity without printing content. Matching inventory alone is not a restore test. An operator must still perform the runbook backup/restore and migration against the actual deployment before rollout.

## Measured evidence, assumptions and remaining gates

- Real hardware preflight was attempted with a fixture-only private key. It returned: the optional CUDA service requires Docker NVIDIA runtime and a suitable GPU; native Apple Silicon serving is a separate operator setup. **No image build, weight download or service start was attempted.**
- Inspected/pinned source: Kev `6b719c3c3f367295f6ef336f4f751cf5ff970abc`; artifact: `jaredpalmer/kev-4b@6cfce5c2fa4b4bd64026336ab649c5ca78857d52`. HTTP behavior was verified with pinned-contract fixtures, not real GPU inference. Upstream benchmark numbers are not Agent Core quality measurements.
- Routing sends bounded capability/availability descriptions and coarse task signals (input size/mode, image requirement and attachment count), not raw conversation, workspace content or credentials. Their decision-quality value is **unmeasured**. No semantic routing superiority, cost saving, calibrated correctness or non-inferiority claim follows from deterministic Synthetic choices.
- No warm p50/p95 added latency, cold checkpoint start, saturation throughput, GPU/CPU/RAM footprint, held-out multilingual/ambiguous/code/tool/vision quality distribution, option-order robustness or cost tradeoff was measured. Production budgets remain unset. Provider-round operational samples are a different metric and are not used as routing quality evidence.
- Live providers, hosted model/speech calls, real Kev inference, customer migration/restore, production deployment, physical devices, full duplex audio performance and full proposal/milestone acceptance remain unrun. Existing voice lifecycle is preserved; affected speech admission was tested with Synthetic fixtures.

Smallest final recommendation: retain the disabled/shadow defaults, qualify the pinned service and a representative held-out distribution on suitable operator hardware, record the quality/error analysis and approved resource/latency budget, then deliberately enable effective Auto for an opted-in tenant. Fixed selection and disabling tenant Auto remain the existing rollback controls; admitted Runs keep their pins.
