# IAM repository stabilization — 2026-10-24

This bounded follow-up starts from `7c6da288d86dac46abd10671951f1b9f41a09236`. It preserves the IAM model and the [final delivery](tenant-final-delivery-2026-10-24.md)'s original source fingerprints. This report owns subsequent launch/test integration evidence; it is not a new application milestone, hosted CI, production deployment or live-provider acceptance.

## Changes and coverage

| Review concern | Disposition |
| --- | --- |
| Compose smoke uses retired authority | Replaced with fresh explicit bootstrap, HTTPS cookie authentication, CSRF, canonical tenant operations, completed Synthetic text and original Attachment bytes, container recreation, retained history/cookies and independent logout revocation. No compatibility endpoint. |
| Email-watch restart uses retired authority | Uses canonical tenant Credential and monitoring APIs, exact security/Credential/watch witnesses, a from-now worker baseline, stopped SQLite snapshots, persistent cursor/generation/authorization and anonymous denial. |
| Default Playwright selects pre-IAM contracts | Default runner uses canonical tenant text/lifecycle/management/approval/background/email/voice journeys and a fresh SQLite bootstrap host. Dedicated acceptance configs preserve feature-specific hosts. Browser STT/TTS retain their device/queue/Stop/barge-in coverage through tenant setup. |
| Fresh native startup is incomplete | Loopback launch profiles/launcher explicitly select the supported HTTP exception. Missing identity prints the precise explicit setup command. `dev-real.sh bootstrap-identity` loads the same environment/data roots, accepts operator-supplied values and exits without launching workers/infrastructure. Custom roots resolve absolutely and override launch-profile defaults consistently. |
| Approval service coupling | Mandatory effect persistence replaces silent metadata-only fallback; one redundant optional Run-store dependency is removed. Memory, Continuity and Experience owners now own saved-operation review/reobservation. Shared lifecycle, human checks, decisions, grants and continuation remain centralized. Existing typed execution owners are retained; no dependency bag, workflow framework or second authorization engine. |
| Current/historical status conflicts | TODO, architecture, interfaces, operations, README, testing and the delivery index distinguish completed local IAM from external qualification. Historical reports/fingerprints remain unchanged. Retired owner/API procedures are explicitly historical. |

The constructor still has multiple typed feature dependencies. Their existing partial-fixture composition and fail-closed unavailable-feature paths are retained; this pass does not disguise them behind a service locator or speculative generic dispatcher.

## Canonical test selection

`web/playwright.config.ts` explicitly selects current tests instead of broadly discovering historical `*.spec.ts` files. The old `text-conversation.spec.ts` and `admin-lifecycle.spec.ts` entry points now delegate to their canonical successors. Other pre-IAM specs remain historical source, excluded from the default runner and current workflow selections.

| Historical coverage | Current owner |
| --- | --- |
| Text, queue/steer, receipt, reconnect, attachment and conversation lifecycle | `tenant-chat.spec.ts` through the text entry point |
| Archive/restore/decommission, references, stale/lost replies and scoped readers | `tenant-agent-lifecycle.spec.ts` through the lifecycle entry point; current Instance/Definition configs cover configuration/publication |
| Harness/continuity/memory/secretary-style background journeys | Current management, Memory, Automation, file, Credential and acceptance suites, with independent execution and review authority |
| Browser STT and Browser TTS | Migrated `browser-stt.spec.ts` and `browser-browser.spec.ts`, each with its own bootstrapped SQLite host |
| PCM capture, playback, mute and fresh-click reconnect | `tenant-voice.spec.ts` at desktop and phone widths |
| Real Docker Sandbox/native workspace proof | Existing `opensandbox-local.py accept --build-workbench` and canonical tenant command/native filesystem tests |

The workflow stays manual-only. It selects the current default projects and dedicated canonical configurations, preserves their separate host lifecycles and collects temporary-root failure results. The pre-IAM `web/opensandbox-e2e`/`--ui` owner-token wrapper is retired from current selection; the original combined real Workbench/browser/Office journey has not been requalified for IAM. The physical SDK/tenant command gate is retained, alongside current browser configuration/resource qualification. No hosted workflow was dispatched, rerun or awaited.

The new shared `e2e/support/tenant-auth.ts` handles only explicit authentication throttling and coherent-read conflicts. A rejected login/registration may wait one limiter window and retry once, extending only that test/hook. Only a GET returning `409 stale_revision` can be reread, at most twice. Resource mutations, decisions and uncertain effects are never automatically replayed. Production rate limits and authorization are unchanged.

## Executed checks

The commands below ran locally with Synthetic/offline providers and disposable storage. Counts from separate rows can overlap and must not be added into a milestone total.

| Check | Result |
| --- | --- |
| `python3 -m unittest scripts.tests.test_dev_real -v` | 24 passed, including explicit-bootstrap root selection and missing-identity guidance without private log disclosure. |
| Infrastructure approval/context/account/background selection | 55 passed, zero failed/skipped, both stores; 2m22s. |
| API identity/restart/approval selection | 8 passed, zero failed/skipped; 20s. |
| Revised default Playwright selection | 52 passed in the combined run; its one setup timeout/23 blocked inspection cases were corrected and all 24 passed in the focused rerun, alongside the new real-limiter regression (25 passed, 3.7m). All 77 current default scenarios have passing executions across these partitions. The full combined command was not rerun after that final helper correction. |
| `playwright.browser-stt.config.ts` | 1 passed, 36.3s: tenant recognition listens without microphone PCM and retains composer. |
| `playwright.browser-browser.config.ts` | 5 passed, 42.5s: client STT/TTS, cold voice, Stop retains queue, completed playback dispatches once, speech barge-in. |
| `playwright.tenant-acceptance.config.ts` | 3 passed, 4.1m: membership/invitation refusals, actual organization/role/member paging/keyboard controls, expired approval and independent fresh review. |
| `./scripts/compose-sqlite-volume.sh` | Passed after rebuilding the final Application source; repeat with original Attachment bytes also passed. Anonymous Attachment content reads are denied after recreation. |
| `verify-email-watch-restart.py --project email-monitor-iam-stabilization-20261024 --port 5420` | Passed baseline/cursor/generation/authorization survival and anonymous denial; no historical Events after recreation. |
| MCP against isolated native Synthetic API/Vite, ports 5444/5445 | Actual `dev-real.sh bootstrap-identity` initialized fresh roots. Visible sign-in → organization → create template Instance → Chat → send → completed reply → reload retained exactly one reply. Sign out and reopen protected Session returned to sign-in with zero old reply content. |
| Native shutdown | Only owned process groups stopped, both listeners closed and API logged graceful shutdown. No user host/catalog was reset or terminated. |
| Compilation/static checks | Infrastructure/API builds passed with zero warnings/errors; frontend typecheck and explicit new E2E-helper/config typecheck passed. Python compilation, Bash syntax, local Markdown link/fence checks, workflow YAML/manual-trigger and all 21 dedicated config-path checks, launch-settings JSON and `git diff --check` passed. Compose image builds also compile the SPA. |

Infrastructure command:

```sh
dotnet test tests/AgentCore.Infrastructure.Tests/AgentCore.Infrastructure.Tests.csproj --no-build --no-restore --nologo --filter 'FullyQualifiedName~Durable_approval_retains_exact_action|FullyQualifiedName~Tenant_memory_operation_exact_consent|FullyQualifiedName~Tenant_memory_exact_read_consent|FullyQualifiedName~Tenant_continuity_read_Review|FullyQualifiedName~Tenant_experience_source_Review|FullyQualifiedName~Tenant_experience_continuity_returns|FullyQualifiedName~Tenant_experience_record_requires|FullyQualifiedName~Account_actions_use_owned|FullyQualifiedName~Tenant_report_creation' --logger 'trx;LogFileName=iam-stabilization.trx'
```

The observed flows retained exact consent across reopen/continuation, denied rejected/changed authority, applied effects once, returned factual account receipts and preserved background report sources. Both actual Memory/Continuity/Experience review and shared lifecycle remain covered.

API command:

```sh
dotnet test tests/AgentCore.Api.Tests/AgentCore.Api.Tests.csproj --no-restore --nologo --filter 'FullyQualifiedName~IdentityAndTenantAccessTests|FullyQualifiedName~IdentitySqliteRestartTests|FullyQualifiedName~Canonical_durable_approval_HTTP' --logger 'trx;LogFileName=iam-stabilization.trx' -m:1 -p:UseSharedCompilation=false
```

Browser commands run from `web/`, with distinct disposable ports:

```sh
TENANT_CHAT_API_PORT=5478 TENANT_CHAT_WEB_PORT=5479 pnpm exec playwright test
TENANT_CHAT_API_PORT=5418 TENANT_CHAT_WEB_PORT=5419 pnpm exec playwright test --config playwright.browser-browser.config.ts
TENANT_CHAT_API_PORT=5438 TENANT_CHAT_WEB_PORT=5439 pnpm exec playwright test --config playwright.browser-stt.config.ts
TENANT_CHAT_API_PORT=5488 TENANT_CHAT_WEB_PORT=5489 pnpm exec playwright test --project=synthetic e2e/tenant-auth-recovery.spec.ts e2e/tenant-management-inspections.spec.ts
TENANT_ACCEPTANCE_API_PORT=5468 TENANT_ACCEPTANCE_WEB_PORT=5469 pnpm exec playwright test --config playwright.tenant-acceptance.config.ts
```

MCP console inspection found zero errors during the completed chat/reload flow. Network inspection showed successful canonical requests and expected navigation/unmount cancellations (`ERR_ABORTED`), without a failed authorized operation. Logout then deliberately produced authentication refusal. The CLI voice tests use fake devices; they do not certify physical acoustics.

## Reproduced fixture failures and limits

The first packaged poll encountered a legitimate concurrent `409 stale_revision`; only its read helper changed. All write assertions remain exact. The initial Automation run exposed an old accessible checkbox label and missing stale-read handling; after correction, all six background/report-back journeys passed (52.9s), including exact admission Review, stale destination recovery, one report delivery and same-Session continuation. A combined management run then exhausted the actual IP login limiter: eight passed, five failed in authentication setup and 39 were not run. This led to the shared explicit-backoff helper, not a production security change. Sandbox-restricted local attempts could not bind sockets or access Docker; the same focused checks were rerun with the required local process/network permissions. These setup attempts are not product failures or passing checks. The subsequent 76-case combined run passed 52, but its inspection `beforeAll` still timed out while retrying the same rate-limited form; 23 inspection cases were consequently blocked. The shared helper now reloads a fresh form/CSRF before the deliberate second attempt, matching the existing acceptance journey. The added real-limiter regression first receives 401 on absent-account attempts, reaches 429, observes one UI refusal, waits the actual window and signs in successfully. Its focused run also re-exercises every blocked inspection case. This records the failure and correction rather than presenting the earlier combined command as green.

Earlier passing focused default selections covered 18 text/lifecycle/email/voice cases (2.7m) and six Automation cases; the final combined default run supersedes these overlapping counts. Two MCP locator attempts used an incorrect role/name or Ant Design's hidden semantic option and timed out; reading the current rendered controls resolved them. Their timeouts are not successful product actions.

HTTPS Compose requires an operator-supplied trusted PFX/password and explicit bootstrap for a new volume. Older Compose Attachment/Artifact originals outside the named volume must be preserved before authorized container replacement; the smoke does not migrate operator data. The smoke uses a fresh one-day certificate trusted only by its client, random credentials and an owned fresh project. Existing projects/volumes are refused. It removes only its own containers/volume. No remote HTTP exception, default administrator/password or compatibility endpoint is added. A base HTTP health response alone is not authenticated packaged-app qualification.

Full hosted/local CI, the entire dedicated browser matrix, the retired combined Workbench UI, Real model/provider/customer UAT, physical devices and public/multi-instance deployment are unrun. Historical proposal acceptance remains complete within its recorded local scope; external obligations do not imply those checks ran. This follow-up does not advance a historical milestone or begin Environments.

## Finalization

The bounded stabilization scope is locally complete. The 25-case focused rerun passed after the final fresh-form correction, including the actual production limiter recovery and all 24 previously blocked inspection scenarios. The combined predecessor remains recorded as 52 passed / one failed setup / 23 blocked (11.9m), not retrospectively green. No repeat of the entire default selection or dedicated matrix was needed after the directly reproduced failure and its focused regressions passed.

The final rebuilt image passed both canonical Compose checks; the last text smoke also uploaded and bound `restart.txt`, completed the Synthetic reply, recreated the container and retrieved byte-for-byte original content while an anonymous client was denied. The final email repeat used project `email-monitor-iam-stabilization-final-20261024` on port 5420 and passed. Owned Compose fixtures removed their containers and volumes; native MCP listeners closed with graceful API shutdown.

Final changed Application source fingerprint: `72aafb58bceed1df87b7d6bc3a59cb95f854f9860cb5cdf2f2edd4a92f6971e7` across the eight changed Application files, hashing sorted repository path + NUL + file bytes + NUL. No source commit, push, PR, customer deployment or new roadmap initiative was performed. Current local IAM completion stands; external and optional qualification remains explicitly listed above and in TODO.

## Subsequent native dotenv and smoke assertion follow-up

The native launcher now parses the selected private `.env` before resolving ports/state/data and starts with `--no-launch-profile`. Its dotenv data parser supports hyphenated .NET keys without shell execution; explicit file assignments override inherited values and profile defaults. The expanded example includes the current bindable application option shapes, explicit lookups and collection/logging/native/Compose controls. Backend credentials and bootstrap settings are excluded from the Vite environment. [Current setup and precedence](../17-observability-and-operations.md#running-after-implementation) own the operating instructions. The private `.env` remains gitignored and is not part of the commit.

All advertised launch/build examples in both main Compose files now include the HTTPS overlay and describe the private PFX/password, container readability and explicit fresh-volume bootstrap prerequisites. The smoke independently asserts a well-formed request without CSRF yields `invalid_csrf`, then a malformed request with valid CSRF and a current security revision yields `invalid_request`; neither may create an Agent. This prevents a CSRF failure or stale-revision conflict from satisfying the input-validation assertion.

Focused local verification for this follow-up: `python3 -m unittest scripts.tests.test_dev_real -v` passed all 26 cases; `./scripts/compose-sqlite-volume.sh` passed the distinct refusals plus tenant chat, original Attachment bytes, cookie/history persistence through recreation and independent logout revocation. Both advertised Synthetic and Real overlay configurations parsed with one loopback HTTPS publish and their expected profile, using configuration-only placeholders without starting Real providers. Python compilation, Bash syntax, documentation links/fences and `git diff --check` passed. The native dotenv change was also exercised through actual disposable Synthetic startup: missing-bootstrap refusal, explicit initialization, configured `scripted-beta` selection, authenticated completed text through Vite, restart/cookie/history survival and logout denial with user-secrets disabled. No full local or hosted CI, live-provider calls, new milestone acceptance or roadmap advancement was requested or performed.

## Explicitly requested hosted CI follow-up

The owner subsequently requested the full manual Synthetic workflow. The operational examples in `docs/17-observability-and-operations.md` now use the authenticated native launcher and HTTPS Compose overlays, including servicing and JSON-protocol examples.

[The first hosted run](https://github.com/trannamtrung1st/agent-core/actions/runs/40082976086) tested `1e59debeb187fca1f9e59b20f6da422401446b1c`: 19 jobs succeeded and nine failed. Both core Playwright shards, the real Docker/OpenSandbox gate and the HTTPS Compose smoke passed. That run is a failed qualification, not a passing receipt.

Its concrete failures identified test-fixture drift: Linux `/bin/sh` discarded the fake native executable's hyphenated environment names; one resource-import spec still used retired owner-token routes/controls; navigation unit fixtures used retired unscoped Chat URLs; live-model-mutation unit expectations contradicted the canonical read-only saved-model contract; privacy expectations assumed InMemory despite the SQLite host; two browser locators were ambiguous; and an invitation test raced the sign-out redirect. The corrections retain the affected file bytes, retry boundaries, stale-save protection, current authorization and revoked-content assertions. The fake native executable now runs Python directly, with an additional shell-valid provider key verifying Vite exclusion. Existing-conversation model tests assert no state change or network mutation, while new-conversation selection remains covered.

Two multi-step Ant Design Automation unit cases took 33.5s and 35.1s on the hosted worker and exceeded their 30-second deadline. Only those cases receive a 60-second rendering allowance; their assertions and the global timeout remain unchanged. All 18 Automation cases passed locally, alongside five current model-selection cases. Additional focused local runs passed 26 launcher cases, nine route-navigation cases and ten browser scenarios covering resource imports/inherited resources, scoped credential connections/revocation, SQLite privacy recovery, Browser STT and invitation membership/refusals. Frontend and changed E2E TypeScript checks passed. The browser reruns used isolated Synthetic roots and did not reset user data.

These are bounded fixture and documentation corrections. No production IAM implementation, authorization rule, workflow selection, retry policy or architecture was changed. The [manual workflow's hosted receipts](https://github.com/trannamtrung1st/agent-core/actions/workflows/synthetic.yml) identify each tested source SHA; local passes and the failed predecessor do not substitute for a successful complete latest-main run. Live-provider/customer outcomes, physical devices and deployment remain separate qualifications. This follow-up does not begin Environments.

[The next hosted run](https://github.com/trannamtrung1st/agent-core/actions/runs/40084829027) tested `f4173604cd87077e1aab8038295e3dfdb3c7c9fa` and passed the corrected Linux launcher, resource-import, connection, privacy, STT and invitation groups. Its Events polling encountered an explicit GET `409 stale_revision` while a worker committed security state. The Events spec now reuses the existing `canonicalFetch` helper, which allows at most two fresh reads for that exact conflict and never replays resource writes. All three Events scenarios then passed locally, including typed subscription identity, current receipt reads, exact revocation and separate approval/Run admission. This correction changes only that test's use of the established helper.

That run also exposed two timing boundaries in previously passing fixtures. The first Audit assertion exceeded RTL's one-second default during a cold Ant Design table/breakpoint render (1.24s); only that initial query now allows five seconds. All 18 Audit cases and the focused initial-row rerun passed locally. The Chat cancellation-authority scenario collected security witnesses before the first response was admitted, producing a definitive stale-state refusal before its permission assertion. It now waits for the held Synthetic response's visible prefix before collecting witnesses. The focused browser rerun passed with one interruption request, the required 403, a still-running original Run, disabled Steer/Stop and the retained local queue. No request replay or production authorization change was introduced.

The final frontend shard passed the revised model-selection and Automation cases. Its Profile assertion found the form briefly absent after the save notice, while the canonical post-save read was loading. The assertion now awaits the field's return before checking its persisted value. The focused rerun also exposed eight late Ant Design delay-state callbacks after jsdom teardown; a fixture-owned advancing clock now drains pending callbacks before restoring real timers. All five Profile cases then passed with zero unhandled errors, retaining deliberate-field/revision checks, stale drafts, late-organization exclusion and no automatic replay after unreadable acknowledgements.

The second full run finished with 23 successful jobs, four failed jobs and a backend job cancelled at its 30-minute deadline during Infrastructure; Application and API suites were not reached. Its backend log identified three stale migration-fixture expectations, all reproduced locally. Retirement now compares the table set immediately after its exact migration; Automation invocation guards/indexes are checked at the exact 112-guard checkpoint before separately upgrading current; frozen-metadata coverage checks the 135-guard download checkpoint, its two documented tool-allowlist extensions and the current 138-guard HTTP successor. The SQL-digest, rollback, current-model and foreign-key checks remain. All 42 focused migration cases passed after correction; no migration or database implementation changed.

The backend job receives a 60-minute total budget after the observed cutoff, while Infrastructure gains the same bounded five-minute hang diagnostic used for Application, with no dump generation and precise TRX/sequence receipts retained on failure. Suite selection and assertions remain unchanged. A separate key-free local Infrastructure execution with detailed progress and the bounded hang collector diagnoses the unfinished suite; it is not counted as passed merely because it started.

The third run, [source `07aba217`](https://github.com/trannamtrung1st/agent-core/actions/runs/40087399645), passed both frontend shards, all 21 dedicated browser groups, Compose, OpenSandbox and core shard 2. Core shard 1 exposed a keyboard Select opening-lifecycle race: the fixture now waits for the observed popup motion to finish and moves the pointer away before using the original Arrow/Enter interaction. Both Instance-controls scenarios passed locally with their saved model, stale-draft, freeze, reset and revocation assertions retained. The remaining serial backend was cancelled when superseded by the complete partitioned gate below; this predecessor is not a green full-workflow receipt.

The broad local Infrastructure diagnostic progressed through 1,105 passing cases and exposed two additional historical 83-versus-138 guard-count expectations in completion/conversation migration fixtures. It was explicitly stopped before completion to rebuild those fixtures; it is not a passing full-suite run. Both fixtures now assert their exact historical conversation-destination checkpoint before separately upgrading to current, preserving legacy values, rollback and foreign-key checks.

Actual Infrastructure discovery contains 4,754 parameterized cases across 1,782 methods. Serial execution continued making progress, demonstrating a capacity problem for the single-runner gate. The manual workflow now shares one source-bound discovery plan across 16 Infrastructure jobs, each containing 297–298 cases for this source. Each method and all its parameter cases occur in exactly one partition. Every successful partition must account for its complete planned TRX total, including explicit skips, and retain a source/plan receipt. Domain, Application and API checks remain selected in the backend job. Together with the incumbent browser/frontend/Compose/OpenSandbox groups and the discovery job, the full workflow has 45 jobs. No production IAM code or test assertion was removed to partition execution.

Five planner/CLI regressions passed: parameter-case retention, deterministic complete partitioning, invalid discovery/assignment/source rejection, exact native filter construction, incomplete-TRX refusal and test-process failure propagation. The real complete catalog produced the 16 non-overlapping partitions above. A separate native 39-case metadata/theory probe passed and its actual TRX total matched the plan, verifying the VSTest filtering/accounting boundary. These checks qualify the partition mechanism; the full hosted outcome still requires every configured job on the final source to succeed.

The targeted migration-family preflight passed 84 cases and found one additional stale assumption in the populated Credential-approval fixture: its current Run contains a retained public-download reference, so the documented deeper downgrade guard correctly refuses. The fixture now successfully reaches the safe public-download predecessor, requires the deeper refusal to preserve that checkpoint, upgrades current, compares the exact request/grant and continues actual dispatch and credential review. The focused integrated rerun passed; no downgrade guard was relaxed. The combined preflight remains recorded as 84 passed / one failed, rather than retrospectively green.
