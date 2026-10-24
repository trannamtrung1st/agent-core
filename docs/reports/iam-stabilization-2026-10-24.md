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
