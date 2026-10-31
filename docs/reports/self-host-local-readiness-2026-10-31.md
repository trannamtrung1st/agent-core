# Local development and self-host readiness — 2026-10-31

Status: **Order 7 complete for the selected local workstation and packaged profiles**,
2026-10-31. Independent review: **Ready**; no remaining actionable P0–P2 finding.
This is bounded local qualification, not owner manual or customer acceptance. Baseline commit:
`e047ebd38380730b08fba83f6737fea48e45a237`. This ledger records new execution,
not acceptance inherited from earlier deliveries.

## Design and coverage gate

Selected workstation: macOS arm64, .NET SDK 10.0.401, Node 24.22.2,
pnpm 9.6.0, Python 3.14, Docker Engine 29.9.1 / Compose 5.6.0.
An installed Sandbox server on loopback 18080 and an unrelated API on 15088
must remain untouched. Use separate application roots, ports and project names.

| Lane / capability | Existing owner / setup | Verification owner / expected result | Boundary |
| --- | --- | --- | --- |
| Native Synthetic / Real API + Vite | `dev-env.py`, `dev-real.sh`; explicit bootstrap then `start --api-only` | Launcher tests; current tenant Playwright; login, completed Chat, persistence and tracked restart | Loopback Development only; Real boot does not configure inference |
| Packaged Synthetic / Real HTTPS | `compose.py`, base + optional real + https; private PFX and offline bootstrap | `canonical_smoke.py`, `verify-compose-identity.py`; cookie/CSRF, reply, exact attachment bytes after recreation | Production, non-root, one SQLite writer; Chromium absent |
| Native Browser | `BrowserOptions`, tenant Browser services / profile configuration | Tenant Browser suite plus native navigation | Requires installed browser; Synthetic surrogate is insufficient |
| Sandbox / Workbench / Office | `opensandbox-compose.py`, protected connection handoff, explicit image catalog and tenant Session selection | Native SDK and tenant file/Office services; real command, publication and byte readback | One server per daemon; never start a competing server or reset installed workloads |
| Tasks / Goals / Evaluation / Workflows | Existing tenant service, Runtime, worker and Core/Elsa owners | Current Task/Goal/Workflow integration fixtures and UI; required gates, result/report and durable resume | No blind effect replay; component proof alone is not integrated native acceptance |
| Spaces / Environment / Secrets / files | Existing IAM/context/credential/file owners | Current tenant isolation and file suites; exact scope and denied read | Membership supplies context, never IAM |
| Automation / events | Existing scheduler, occurrence and delivery owners | Current tenant event/Automation tests; one durable effect | No separate scheduler |
| Recovery | `data-backup.py`, `qualification/production-recovery.py` | Stopped Synthetic snapshot/verify/restore, original keys, retained bytes and Workflow | Native external roots and writers require separately coordinated stopped-state recovery |
| Decision / store / mail / Drive / voice | Existing optional launchers and protected adapters | Selected real operation + outage/recovery if prerequisites available | Disabled/shadow Decision by default; store is an independent project; hardware/provider checks separate |

Source confirms `ConfigurationCutover.ValidateDeployment` refuses Browser or
OpenSandbox outside Development (`unqualified_native_effects`). Therefore the
Operations matrix's HTTPS + app-sandbox rows describe an unsupported combination.
Retain the guard and document native/hybrid as the execution lane; no new local
packaged execution profile is introduced without qualification.

Frozen initial change map (no schema migration or UI change):

- `scripts/dev-real.sh`: accept the existing Decision flag on restart, with a
  lifecycle regression proving preflight/reuse and explicit stop.
- `docs/local-development-quickstart.md`: entrypoint linking canonical owners,
  exact new-install steps, topology boundaries, private configuration and recovery.
- Operations and relevant indexes: correct unsupported packaged Sandbox claims;
  link the quickstart and this source-bound evidence.
- Extend source changes only for an observed failure in the selected journeys.

Verification order: focused launcher/config/Compose tests; disposable native
bootstrap/lifecycle and current tenant browser flows; current-source packaged
HTTPS Synthetic and Real; selected native integrations; stopped Synthetic
recovery; independent implementation review. No hosted CI or full local CI run
is requested. Record SH-01–SH-16 and conditional gates before final handoff.

## Observed integration repairs

- Native `restart --with-decision` reproduced exit 2 (`unknown option`) before
  the fix; the focused regression now passes and preflights/reuses the service.
- Default native startup with the existing trusted server reproduced
  `data_reset_required`: the shared canonical handoff root contains installed
  layout data. No installed data was reset. The launcher now retains an explicit
  `Persistence__WorkspaceRoot` child namespace within the server allowlist and
  derives the corresponding Docker root. Outside roots refuse before API launch.
  A new private namespace starts successfully. Server source/config/service hashes
  matched beforehand; its container identity/start time remained unchanged.
- The old deterministic Sandbox demo calls direct pre-IAM execution tools.
  `synthetic-sandbox:canonical-demo` uses the current exact authorization request,
  accepts only a factual Completed/exit-zero SDK result, and publishes Word/Excel
  originals through incumbent Artifact authority. Failed/unavailable outcomes
  never request replay or claim success. This is an explicit Synthetic fixture,
  not a Real provider fallback.
- Independent review found two P2 documentation defects: first-use commands stopped
  the app before sign-in, and old active packaged Sandbox examples contradicted
  HTTPS Production preflight. Both are corrected; final incremental review follows.

Live-provider authorization: automatic approval initially refused credential egress.
The owner then explicitly approved one OpenRouter metadata check and one short
inference request. The explicit opt-in packaged Real proof passed with one completed
non-Synthetic reply and preserved history/attachment bytes after recreation. No
additional provider calls are authorized or inferred. Credentials/content are not
included in this report.

## Executed qualification

All paths below are repository-relative. Large logs, TRX files, screenshots and
private fixture state are retained under ignored `local/selfhost-20261031/`;
they are local execution evidence, not portable hosted artifacts. No secret is
required to read this ledger. Native Goal TRX is `results/selfhost-native-goals.trx`.

| Gate | Expected → observed | Result / evidence |
| --- | --- | --- |
| SH-01 | Literal private configuration, fail-fast missing/ambiguous inputs, selected tooling → configuration cutover 7/7, Compose wrapper 7/7; four Compose combinations valid and three retired/ambiguous cases refused; no dotenv evaluation | Pass; configuration commands below |
| SH-02 | Deliberate fresh bootstrap then real API/Vite/login/status/stop/restart → native-a Synthetic on 15580/15573, native-b Real on 15581/15574 and native-c Synthetic on 15582/15575 each use independent roots/process records; original cookie/history survive restart | Pass; bootstrap/process logs and actual Playwright MCP actions |
| SH-03 | Packaged HTTPS cookie/CSRF/tenant isolation, real MessagePack, one completed Chat and exact file bytes after recreation → all observed using the disposable trusted CA and independent login/logout | Pass; `packaged-synthetic-final.log` |
| SH-04 | Real HTTPS boots with no hidden default, then deliberate protected model setup → key-free unconfigured-model proof passes; separately authorized live metadata + one inference passes and retained history/attachment bytes survive recreation | Pass for packaged text scope; `packaged-real.log`, `packaged-real-live.log`, final-source no-live proof |
| SH-05 | Roles, tenant isolation, current revocation and Agent authority independent of human → HTTP role/revocation 2/2; model/IAM 3/3; packaged anonymous, wrong-tenant, CSRF and logout refusals; background Chat-approval route correctly 403 | Pass; `selfhost-roles.trx`, `selfhost-iam-models.trx`, packaged/native UI logs |
| SH-06 | Built-in General Assistant, exactly one reply/history; missing Real model actionable → native-a visible composer/reload passes, native-b disabled Send plus provider-setup link verified; live packaged reply is non-Synthetic | Pass; MCP interaction, packaged proof |
| SH-07 | Context does not grant IAM, exact protected resource scope and distinct Session context → Space/Environment isolation, epoch/CAS and SQLite reopen 4/4; native test grants independent worker Space and resource rights explicitly | Pass; `selfhost-context.trx`, native UI test |
| SH-08 | Real SDK execution and canonical files; authorized Office preview/publication/download → native owned-source/command/replay/retirement test 1/1 across both stores; UI runs Python/Node, Excel B1=5, Word “Total: 5”; original hash equals published metadata and workspace bytes | Pass; `selfhost-native-sandbox.trx`, `native-ui.log` and fixture JSON |
| SH-09 | Real native Browser, reviewed current-IAM action and recovery → native profile isolation/reopen/symlink refusal 2/2; current-IAM approval/navigation/readback/recovery 26/26 with native opt-in enabled | Pass; `selfhost-native-browser.trx`, `selfhost-browser-iam.trx`; initial missing-opt-in skips are excluded |
| SH-10 | Independent delegated worker with native effect, denied target, result/report, duplicate/restart safety → nonparticipating target absent; worker Instance/Session distinct; three worker reviews execute command and publish two originals; Task Succeeded and completion report admitted; duplicate submission replays one Task; restart retains same terminal revision/worker and four original hashes | Pass; `native-ui.log`, `native-fixture.json`, `native-cleanup.log`; existing delegated-question/guidance UI spec also passes |
| SH-11 | Required gates cannot be skipped; actual independent proof and durable resume → Workflow/Task suites 49/49; Goal/Evaluation browser journey passes; three native Goal proof pass/fail/timeout cases pass without replay; actual Workflow UI rejects bypass, publishes corrected v1, survives host restart awaiting sign-off, completes only after authorized sign-off | Pass; `selfhost-workflows.trx`, `task-goal-browser.log`, `selfhost-native-goals.trx`; MCP Workflow execution |
| SH-12 | Authenticated owned event/delivery, durable admission and once-only progress → configured Webhook/Core completion and SQLite host-recreated Automation occurrence 8/8 | Pass; `selfhost-events.trx` |
| SH-13 | Lifecycle/refusal retains other owners and uncertain effects → launcher 29/29 including occupied real socket, stale PID, failed startup, unhealthy selected service, namespace refusal and Decision restart; deployment preflight 14/14; native Goal timeout remains Inconclusive; current server identity unchanged | Pass for selected local scope; launcher/preflight/native evidence; no physical Docker-daemon outage injected into the shared server |
| SH-14 | Cold controlled-Synthetic restore retains keys/bytes/bookmark; wrong key/schema/missing bytes/active owner refuse → all seven recovery assertions true, eight files and one protected record verified; Workflow bookmark continued and matching snapshot rollback succeeded | Pass within existing snapshot scope; `recovery.json`; native full cross-root restore remains unqualified |
| SH-15 | Second isolated written first-use setup requires no installed app state → native-b fresh Real bootstrap/login on separate ports/root, no inference configured, honest disabled composer; native-c fresh namespace additionally runs integrated native journeys | Pass; private examples created without overwriting root `.env`; installed shared Sandbox/image are explicit selected dependencies |
| SH-16 | Current navy setup, disabled/denied/approval/conflict/success feedback and narrow layout; consistent docs → real interactions at desktop/390px, provider setup and Workflow sign-off inspected; stale Office authority requires source refresh; no observed product presentation defect required a UI edit | Pass for visited surfaces; quickstart/canonical links updated; Order 8 remains owner review |

### Commands and focused results

Run commands from the repository root, with the selected disposable prerequisite
configuration. These are the executed selections, not a recommendation to rerun
all checks against an installed app.

```sh
python3 -m unittest discover -s scripts/tests -p test_dev_real.py -v
python3 -m unittest discover -s scripts/tests -p test_compose.py -v
python3 -m unittest discover -s scripts/tests -p test_configuration_cutover.py -v
python3 scripts/qualification/configuration-compose.py
pnpm --dir web exec tsc --noEmit
AGENTCORE_COMPOSE_PORT=15443 python3 scripts/verify-compose-identity.py --hub
AGENTCORE_COMPOSE_PORT=15446 python3 scripts/verify-compose-identity.py --profile Real
PYTHONPATH=scripts python3 scripts/qualification/production-recovery.py
```

The live Real run used `--profile Real --live-openrouter-env` pointing at the
existing private `.env`, after the owner's explicit bounded authorization. It
made one metadata test and one short inference. It was not run again after
Synthetic-only fixture changes. The final-source Real run supplies no live flag.

Focused .NET commands used `dotnet test <project> --no-restore --filter <filter>
--logger 'trx;LogFileName=<name>' --results-directory
local/selfhost-20261031/results`. Project/filter/count mapping:

| Project | Exact selected filter (OR is `\|`) | Passed |
| --- | --- | ---: |
| `tests/AgentCore.Api.Tests` | `FullyQualifiedName~DeploymentPreflightTests` | 14 |
| `tests/AgentCore.Api.Tests` | `FullyQualifiedName~Tenant_Browser_model_requests_exact_Review_and_resumes_once_with_durable_bounded_observation\|FullyQualifiedName~Tenant_native_Browser_recovery_preserves_current_authority_and_exact_effects` | 26 |
| `tests/AgentCore.Api.Tests` | `FullyQualifiedName~Configured_Automation_event_completion_keeps_exact_Webhook_or_Core_admission_and_delivery_consent\|FullyQualifiedName~Automation_admission_recreates_a_SQLite_host_before_work_exists_and_executes_only_the_exact_approved_occurrence` | 8 |
| `tests/AgentCore.Api.Tests` | `FullyQualifiedName~DynamicModels_HTTP_setup_preserves_explicit_default_and_safe_tenant_configuration\|FullyQualifiedName~Tenant_background_model_cannot_borrow_the_requesting_Owner_creation_authority` | 3 |
| `tests/AgentCore.Api.Tests` | `FullyQualifiedName~Cookie_csrf_two_tenant_membership_and_revocation_are_enforced_over_http\|FullyQualifiedName~Exact_resource_assignments_require_owned_targets_and_revocation_applies_to_the_next_request` | 2 |
| `tests/AgentCore.Infrastructure.Tests` | `FullyQualifiedName~OpenSandboxScriptTests` | 8 |
| `tests/AgentCore.Infrastructure.Tests` | `FullyQualifiedName~Workflows_\|FullyQualifiedName~Tasks_human_cross_Agent_atomic_admission_replay_conflict_messages_and_reopen` | 49 |
| `tests/AgentCore.Infrastructure.Tests` | `FullyQualifiedName~Concurrent_Sessions_capture_distinct_Spaces_and_Environment_epoch_prevents_silent_reactivation\|FullyQualifiedName~Space_private_provisioning_membership_CAS_tenant_isolation_and_reopen_share_one_owner` | 4 |
| `tests/AgentCore.Infrastructure.Tests` | `FullyQualifiedName~Tenant_Docker_Sandbox_verifies_owned_source_executes_exact_review_once_and_retires_only_its_generation` | 1 |
| `tests/AgentCore.Infrastructure.Tests` | `FullyQualifiedName~Goals_native_` | 3 |

Native Browser ownership checks additionally passed 2/2 with filter
`FullyQualifiedName~TenantBrowserOwnershipTests`. Browser checks used
`AGENTCORE_CHROMIUM_ISOLATION_TESTS=1`. Native Sandbox/Goal checks used
`AGENTCORE_OPENSANDBOX_TESTS=1` plus `AGENTCORE_OSB_PROOF_URL`, `_KEY`, `_MOUNT_ROOT`
and `_IMAGE` populated literally from the existing private handoff, without
logging the key. Each test creates UUID descendants and retires its own generation.
The Goal command wrote its TRX to `results/` rather than the local report folder.

The integrated native UI command was:

```sh
SELFHOST_WEB_URL=http://localhost:15575 \
SELFHOST_WORKBENCH_IMAGE=sha256:985c3477b1bcf54385a33c048e465ed6bf7f590d09e9dd4bbfae06d62af91561 \
AGENTCORE_OPENSANDBOX_TESTS=1 \
TENANT_UI_LOGIN=selfhost-owner@example.test \
TENANT_UI_PASSWORD=<disposable-test-password> \
SELFHOST_RESULTS=../local/selfhost-20261031/native-ui-results \
pnpm --dir web exec playwright test --config playwright.selfhost.config.ts
```

Result: **1 passed**, 1.6 minutes. Existing `session-goals.spec.ts` and
`task-delegation.spec.ts` also passed against native-a: **2 passed**, 38 seconds.
These are authenticated UI interactions, separate from backend component tests.
The final native UI test reloads settled history before preview, so its current
read-authority witness is fresh; it never retries a shell/publication effect.

### Actual failures and bounded corrections

Early fixture runs exposed setup mistakes: shortened image ID, missing exact
Instance verification, insufficient independent workspace delete/mount rights,
publication policy without an effect-applying Review, and missing UI confirmation.
The tests now follow existing contracts. None was bypassed in product authority.
A legitimate stale Office preview read displayed “The file changed”; refreshing
settled source state resolves it. Foreground and delegated success were rerun.
The first launcher run inside the restricted tool sandbox could not bind a socket;
the same 29 selected tests passed with local socket access. This is not reported
as a product failure. Initial native Browser opt-in skips were rerun with the
explicit flag and executed, rather than counted as passing.

The only additional runtime fixture repair was dispatching the explicit
canonical Sandbox scenario before AutomationRunScript's generic idle fallback.
Otherwise the delegated worker returned no meaningful change without running the
selected native command. Focused tests and the real delegated UI journey now
exercise that ordering. Ordinary Synthetic prompts and Real provider routing
retain their existing behavior.

## Conditional qualification and limits

| Capability | Status / remaining meaningful qualification |
| --- | --- |
| OpenRouter protected model | Pass for the one approved metadata + inference and packaged persistence check; no sustained load/provider SLA claim |
| Native Browser/Workbench/Office | Pass on this macOS arm64/Docker Desktop workstation, explicit current IAM and installed immutable image; real effects driven by a deterministic Synthetic model fixture |
| Live-model native Browser/Office orchestration | Not Run in this run; live inference and native authorization/effects were separately exercised. No claim of provider-generated arbitrary shell correctness |
| Packaged Browser / native Sandbox with HTTPS Production | Out of Supported Profile: preflight refuses native effects; Chromium omitted. Use native Development |
| Speech, physical microphone/speakers | Not Run: no selected speech credential/device qualification; Synthetic audio is not physical proof |
| Gmail/Drive/SMTP | Not Run: no selected external account/delivery action; existing private OAuth configuration alone is insufficient |
| Native Kev/MLX/CUDA Decision | Not Run: not selected for this handoff; default-off/shadow preserved. Launcher flag/lifecycle regression is not inference qualification |
| nopCommerce | Not Run: not selected; existing independent store project/data must remain separate |
| Linux, Windows, AMD64/GPU, hostile/public/customer load | Not Run; no inferred cross-platform/customer certification |
| Complete native cross-root disaster recovery | Not Run; stopped-state inventory/procedure documented, native restart and controlled-Synthetic restore separately qualified |
| Hosted CI / full local CI suite | Not Run, not requested; no GitHub Actions dispatch/rerun/wait |

No schema migration, new runtime, authorization bypass, second UI kit or product
UI change was introduced. Goal/Task/Workflow/approval owners and unknown-effect
fences remain incumbent. Order 8 manual validation and Order 9 Group Chat are
outside this delivery.

## Final integration repair and review

Cleanup supplied an additional reproducible blocker after the Office journey:
six idle generations remained `Terminating` with zero cleanup passes. Each had
an immutable completed command receipt, while later publication Reviews had
replaced its Run's current `ApprovalRequestId`. Reconciliation called settlement
again; both stores checked the mutable latest-approval pointer before returning
the already committed receipt. The swallowed mismatch prevented retirement.

`TenantSandboxApprovedOperationService.SettleAsync` now returns an existing
receipt only after matching exact tenant/request/operation/Run/Session/Instance/
executor/action-hash witnesses against retained execution/owner facts. It does
not create a grant, dispatch, recompute an outcome or weaken new transactional
settlement. Existing current-authority/generation checks still control reuse and
remote retirement. No migration or port change is needed.

The new `laterReview` variants reproduce the defect in **both stores**: before
the fix, 12 existing reset variants passed and the two new variants failed with
expected Terminated / actual Terminating (`selfhost-reset-repro.trx`). A prior
attempt failed fixture setup while the command was still Running; it was corrected
to Completed/Ready before later Review, and is not treated as defect evidence.

After starting the repaired native-c host, **all 10 fixture generations are
Terminated**. Docker inventory contains only the preexisting shared Sandbox server.
Four canonical Office originals and four published originals retain their exact
hashes; foreground Workspace API reads also retain both originals. The single
Task remains Succeeded at revision 8 with its original worker identity. No shell,
initialization or publication was replayed. `native-cleanup-final.json` records
these assertions. The initial timeout log is retained as failure evidence.

Independent implementation review covered correctness, architecture/IAM,
configuration/lifecycle, persistence/recovery, executable evidence, operations,
documentation and simplicity. No separately named Standard Implementation Review
Checklist exists in the repository; these concerns were checked directly. The
three P2 findings (first-use command order, stale packaged Sandbox instructions,
and unignored default test output) are fixed. The independent reviewer confirmed
the receipt-readback repair has no additional actionable P0–P2 finding, with
runtime closure contingent on the regression and actual retirement evidence above.
The final selected reset/worker/idle-authority regression run passed **50/50**,
zero skipped (`selfhost-reset-recovery.trx`). Exact filter:

```sh
dotnet test tests/AgentCore.Infrastructure.Tests --no-restore \
  --filter 'FullyQualifiedName~Tenant_Sandbox_human_reset_retires_exact_generation_after_logout_without_replay_or_file_loss|FullyQualifiedName~Tenant_Sandbox_worker_|FullyQualifiedName~Tenant_Sandbox_idle_mounts_retire_' \
  --logger 'trx;LogFileName=selfhost-reset-recovery.trx' \
  --results-directory local/selfhost-20261031/results
```

Static Python/Bash/Node parsing, changed Markdown local targets and `git diff
--check` passed. Product UI source/tokens were not changed after the targeted
Impeccable interaction/render review found no defect requiring repair.

## Retained environment and Order 8 handoff

The disposable **native-c** API/Vite remains running at `http://127.0.0.1:15582`
and `http://localhost:15575`. Private inputs, original keys, SQLite, process logs
and the exact Workspace namespace are retained. The ignored, mode-0600
`local/selfhost-20261031/OWNER-HANDOFF.md` contains the disposable login, direct
Task/Workflow URLs, lifecycle commands and a concise manual checklist. It contains
no installed provider credential. Native-a and native-b tracked processes are
stopped; their independent data is retained for diagnosis.

All packaged fixture containers/networks/volumes are disposed by their own
fixtures. Native SDK/Goal fixtures retire their own generations. The integrated
UI fixture's ten accumulated foreground/worker generations (including early
failed fixture runs) were retired through exact current-authority controls;
canonical files, approvals and immutable receipts are retained. No installed data,
Browser profile, key ring, store project or unrelated workload was reset.

The preexisting shared server remains container
`57a66233ed45fcde015c18be9e5fdea85bb877afc1e712225e8f04b7961cb825`, started
`2026-10-29T02:26:58.277944833Z`, on loopback 18080. Its installed source/config
hash was checked before reuse. The unrelated installed API on 15088 was not
managed or stopped. The selected native image remains
`sha256:985c3477b1bcf54385a33c048e465ed6bf7f590d09e9dd4bbfae06d62af91561`.

Owner manual validation remains **Order 8**, unstarted by this implementation:
inspect retained Chat/Office/Task results, create separate manual scenarios,
exercise desktop/narrow keyboard interactions, and record actual issues before
Group Chat. **Order 9 Group Chat remains unstarted.** External integrations and
hardware qualification remain explicit later scenario work.


## Exact source and Order 7 closure

Final executable/test/build input identity (`python3 scripts/source-fingerprint.py`):

- Baseline commit: `e047ebd38380730b08fba83f6737fea48e45a237`.
- Qualified uncommitted source: `worktree-d55aae53e40b35437f719222461ef04f6b5313fa7a9ed6860a939ba3bc05dd67`, 4,296 inputs. This is not a new committed or CI-accepted SHA.
- Final Synthetic image: `sha256:b12de868f567c2bd152d1e86fd8a434a5f9426550e7dade7f15b43574de9bea0`.
- Final Real image: `sha256:8369c95d9fd91dd9502644f55f21175e819bc2d721ed350636d93043631a879f`.
- Both final health build identities report `1.0.0+worktree-d55aae53e40b35437f719222461ef04f6b5313fa7a9ed6860a939ba3bc05dd67`; provenance accompanies functional checks rather than substituting for them.

The final two packaged commands above were run with `AGENTCORE_SOURCE_REVISION`
set to that exact fingerprint. Synthetic passed actual trusted-CA MessagePack,
Chat/file/recreate/auth checks. Real passed explicit unconfigured-model/login/
settings/recreate/logout checks. Both fixtures exited successfully and disposed
their own data. The earlier approved live-provider result remains separate; it
was not repeated, and later production changes are the exact-receipt readback
repair plus deterministic Synthetic fixtures, not provider routing changes.

The native Debug health build label remains the baseline Git SHA; it does not
encode uncommitted changes. The external fingerprint above and actual repaired
retirement/readback evidence identify this qualified working tree.

The final native host runs the repaired source. Integrated Office/delegated UI
execution preceded the receipt repair; final-source restart, exact file/Task
readback and actual remote generation retirement exercise the changed path.
The 50-case recovery regression and independent review also cover that repair.
No blanket rerun of unrelated historical suites or hosted CI is implied.

SH-01–SH-16 have the executed evidence recorded above for the selected scope.
The native workstation path, current-IAM Browser/Workbench/Office and delegated
native worker, packaged Synthetic and supported Real text, lifecycle/refusal,
controlled-Synthetic restore, reproducibility and documentation gates are closed.
Conditional provider/device/platform/full-native-restore limits remain explicit.
Stop further readiness refinement and hand the running disposable environment
to **Order 8 — Owner Manual UI/UX Validation**. Group Chat remains Order 9 and
unstarted.
