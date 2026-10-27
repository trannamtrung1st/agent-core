# Unified evaluation stabilization — 2026-10-27

Verified implementation source: [`75b02977e9189ff6f3e12253d531be86653b53fc`](https://github.com/trannamtrung1st/agent-core/commit/75b02977e9189ff6f3e12253d531be86653b53fc). The following report-only commit records this immutable source identity; application and test sources are unchanged.

This bounded follow-up addresses the seven findings against `c36dfabe7d7cbbcba479a98c5fc8a47cf390f528` and `20726166a769779ee38e61f4ada8b10d3817f6ed`. The [original delivery report](unified-scripts-evaluation-2026-10-27.md) remains historical evidence for that source. The unified evaluation foundation is implemented; independently authorized, isolated executable Sandbox Scripts remain deferred. This report does not declare the original end-to-end Initiative 7 scope complete or accept a new application milestone.

## Disposition

| Review finding | Result |
| --- | --- |
| Public callers could claim automatic provenance | Public terminal AgentRun evaluations require Manual. Internal lifecycle observations capture immutable Run/revision, executor and original initiator. Both stores validate these against the actual Run; summaries additionally require the exact runtime deduplication key. A separate human’s Manual result cannot verify an automatic hook. |
| Impossible required BeforeRun bindings | Save/admission/observation validate a nonempty required set. Required Manual, completed/output and offline Definition assertions are rejected. Referenced Scripts must be exact InputPresent Assertions eligible for BeforeRun. Unavailable model judgment remains Inconclusive and blocks required generation. Advisory human assessment may coexist with passing required evidence. |
| Native Sandbox execution incomplete | Explicitly deferred. Bodies remain immutable catalog content and evaluate Inconclusive with `sandbox_evaluation_authority_unavailable`. No host shell, old Run claim, Profile setup path or synthetic-success substitute was added. |
| Inapplicable OnFailure counted | Summary uses factual hook applicability. Completed excludes OnFailure; Failed/Cancelled include it. Factual status stays unchanged. |
| Results recovery and truncated pickers | Definite 4xx plus absent durable reservation restores editing. Unknown outcomes retain the exact request and expose GET-only recovery without a permanent command lock. Results state survives catalog tab switches. Suite/Script pickers read every authorized page, reject partial loads and offer catalog reload. |
| Missing Instance configuration | Existing sparse settings owner adds `evaluationPolicy.bindings`. An Instance can disable optional RecordOnly bindings from its adopted Definition. Exact Suite version/hook/policy, non-RecordOnly policies, Publication ownership and organization defaults remain enforced. Save/reset/reopen and future versus existing Run pins are covered. Existing delegated configuration tools retain typed array schema and their incumbent exact-consent rules. |
| Repeated denied terminal scans | Bounded process-local backoff excludes suppressed Run IDs before store candidate paging/payload reads. Permission denials stop after five unchanged-authority observations and reconsider relevant authority changes. No attempt or failed/passed finding is fabricated for an unperformed evaluation. |

## Ownership and recovery

No new runtime, scheduler, settings owner, provider port, migration or UI kit was introduced. Session Runtime and existing stores retain execution and mutable state ownership. The automatic origin and sparse selection extend existing JSON records. Existing automatic attempts lacking the new trusted marker remain historical; their durable keys suppress replay rather than acquiring trust retroactively. Fresh trusted lifecycle evidence requires a fresh Run.

Instance selections are exact canonical `suiteId:version:hook:policy` keys, at most eight distinct entries. Null override/clear inherits. A saved empty selection disables only optional Definition runtime bindings. Organization defaults merge independently. Existing Runs retain their captured configurations. Current authenticated management/CAS and Suite access checks remain required.

Denied observation waits start at 60 seconds and cap at 300 seconds. Permission-only denials stop after five unchanged-authority observations. At most 512 suppression records are retained; saturation evicts an old entry and pauses scanning for 60 seconds. Authority fingerprints refresh at most every 30 seconds and include tenant/account status, login stamp, principals, membership, roles and assignments. Catalog-unavailable cases also track tenant revision. Retryable conflict/capacity cases continue at bounded cadence. Restart clears only metadata suppression; accepted durable attempts never replay. Summary metadata `automatic_not_performed` and optional retry time remain distinct from an evaluation result.

The new actor/tenant-scoped GET `/evaluation-attempts/recovery?dedupKey=...` returns stored evidence or null with current source/catalog checks. It performs no reservation, evaluation or tenant revision change. Unknown outcomes are never automatically reposted. Pending UI recovery remains in mounted page state; tab switches preserve it and leaving/reloading is guarded. A browser/process loss does not invent a new request or replay the previous one; authorized evidence remains inspectable from its subject.

## Executed local verification

All work used disposable Synthetic/offline inputs. No hosted CI was dispatched or awaited, and the full CI suite was not run.

| Check | Observed result |
| --- | --- |
| Infrastructure filter `FullyQualifiedName~Unified_\|FullyQualifiedName~Tenant_instance_sparse_settings` | 42 passed across InMemory/SQLite: real Synthetic runtime generation/blocking, provenance, backoff/regrant, summary applicability, catalogs, publication, durable reservation/reopen, captured model fixture HTTP and installed-data conversion. |
| Final infrastructure filter `FullyQualifiedName~Unified_failure_hook_summary_follows_actual_terminal_state\|FullyQualifiedName~Unified_instance_selections_use_sparse_settings` | 8 passed. Final confirmation includes Instance future-admission pins, unchanged existing Run pins and both-store save/reset/reopen. |
| Final infrastructure filter `FullyQualifiedName~Unified_actual_Runtime_blocks_first_generation_or_records_separate_terminal_verification` | 12 passed. Runtime preconditions, trusted terminal observations, five unchanged-authority denial attempts followed by suppression, no fabricated evaluation and reconsideration after a real permission grant. |
| Application filter `FullyQualifiedName~EvaluationBindingValidationTests\|FullyQualifiedName~InstanceSettingsResolverTests\|FullyQualifiedName~ManagementMutationContractTests` | 37 passed, including actual referenced Script eligibility, exact Definition ceilings, inheritance, typed configuration array shape/bounds and adjacent mutation contracts. |
| API filter `FullyQualifiedName~Owned_resource_upload_evaluate_publish\|FullyQualifiedName~Owned_instance_sparse_settings` | 2 passed. Authenticated HTTP recovery found/absent reads preserve tenant revision; existing sparse settings HTTP remains valid. |
| `pnpm --dir web exec vitest run src/features/access/useEvaluationLaunch.test.tsx src/services/tenantEvaluations.test.ts` | 8 passed. Definite rejection unlocks, lost reply recovers by GET only, reserved conflict does not repost, entry 101 is loaded and later-page denial exposes no partial catalog. |
| `pnpm --dir web build` | Passed TypeScript and production build. Existing large-chunk warning remains. |
| API build | Passed with zero warnings/errors. |

Logs are task-local `/tmp/eval-stabilize-all-focused.log`, `/tmp/eval-stabilize-instance-pins-final.log`, `/tmp/eval-stabilize-backoff-final.log`, `/tmp/eval-stabilize-app-final2.log`, `/tmp/eval-stabilize-http.log`, `/tmp/eval-stabilize-web-tests-final.log`, `/tmp/eval-stabilize-web-build-final.log` and `/tmp/eval-stabilize-build.log`; they are not repository artifacts. Earlier test-fixture errors (invalid direct Complete transition, a mock cleanup return, a fixture missing configured provider aliases and a test helper-name typo) were corrected and the relevant cases rerun green. They are not unresolved product failures. No unrelated Sandbox deletion tests were rerun or reclassified in this pass.

## Synthetic browser journeys

Playwright MCP used a fresh disposable SQLite Synthetic host on API 5097 and Vite 5197, with Sandbox/Browser providers off and no provider credentials. The test created its own organization, reusable Suite, Definition publication and Instance. Task-owned hosts were stopped and their disposable data removed after verification.

1. Inject a definite POST 400; the real read-only recovery returns null. Observed editable selectors, preserved subject/Suite and native error feedback.
2. Allow a real evaluation POST to commit, then drop its reply and fail the first recovery GET. Observed locked subject/Suite, explicit uncertain-state guidance and enabled status recovery. Switch Suites → Results; the same pending request survives. Recover on a 390×844 viewport. Observed **one POST (backend 200), two recovery GETs**, the original Completed/Passed attempt `01a17b57-5e6c-7add-ab00-360a3b61a6e0`, and no second evaluation dispatch. This exercise caught a parent Tabs destruction override during self-review; the final scenario passed after moving destruction to catalog panes only.
3. Publish an optional exact OnFailure RecordOnly binding, create an Instance, disable that binding in Evaluation hooks and Save. Observed Instance revision 2 and no effective hooks. Reset the section and reload. Observed revision 3, restored exact inherited binding and readable Suite/source in the effective view.
4. Inspect desktop and narrow error/recovery, evidence Drawer and Instance selection/reset states. At 390×844 the Drawer fits the viewport, identifiers wrap, recovery remains reachable and document width does not overflow. Existing shared Ant Design spacing, labels, controls and Drawer ownership remain intact.

Batched images inspected: `/tmp/eval-stabilize-uncertain-desktop.png`, `/tmp/eval-stabilize-uncertain-mobile.png`, `/tmp/eval-stabilize-recovered-mobile.png`, `/tmp/eval-stabilize-instance-disabled-desktop.png`, `/tmp/eval-stabilize-instance-restored-mobile-field.png`. Final Drawer capture was taken after transition with animations disabled. Console errors corresponded to the initial stale-cookie 401, deliberate 400/transport faults and an explicit unreadable subject 403 probe; no unexpected JavaScript exception or warning remained.

This is Chromium viewport emulation and synthesized mouse/keyboard interaction, not physical-device, actual browser zoom, screen-reader, hosted-provider quality or native Sandbox isolation qualification. Impeccable harden/polish was applied to the existing flow; automatic design-hook scan found no deterministic issues. Unrelated stale design sidecar and two orphaned legacy surface briefs remain outside this bounded change.

## Self-review and remaining scope

Self-review traced public/internal admission, stored origin immutability, summary selection, save/admission compatibility, current authority changes, sparse settings inheritance, existing tool schema, unknown-outcome UI state and documented claims. It removed blocking sync-over-async from the InMemory validator, corrected tab-state retention and preserved exact subject kind/hook checks in recovered acknowledgements. Canonical port/backend/frontend/protocol/persistence/testing/operations owners and README/roadmap/plan/TODO summaries were synchronized. All 1,666 local Markdown links/fragments in the 12 changed Markdown files resolve, fences are balanced, and `git diff --check` passes. The original report and historical milestone freezes remain unchanged.

Independent approved Sandbox dispatch/isolation and production/native/provider quality remain open. Backoff metadata intentionally resets on restart; it is not durable observation evidence. Optional hosted CI and milestone-wide gates remain unrun by request. The foundation supplies immutable evaluation references for future Session Goals without claiming completion of executable Sandbox Scripts.
