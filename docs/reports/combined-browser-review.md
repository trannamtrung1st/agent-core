# Combined browser hardening and adaptive assistance review

Date: 2026-10-10. Candidate branch: `develop/branch-1`; original review base: `b7a9329e9c0c24a9aeb47001d3dc7ab5d0258c0c`; integrated concurrent hardening base: `aecfc2abd5792e72768166e2d857700a3eb83791`.

The user authorized review/correction of all staged, unstaged and new working-tree changes, followed by commit and push on the existing branch. This report belongs to that combined candidate; Git identifies its exact commit. Hosted CI is not awaited, and historical milestone acceptance is not renewed. [Adaptive scenarios and initial evidence](adaptive-visual-browser-verification.md) remain the detailed browser-journey ledger.

## Review and corrections

Reviewed the entire current diff: native browser setup/use/release, persistent-profile cancellation cleanup, origin/DNS/redirect enforcement, document/attachment transport, screenshot masking and freshness, immutable host limits, tool admission/projection/artifact ownership, Admin HTTP mapping/display, canonical documentation, existing test-consumer updates and new deterministic/live-opt-in fixtures. The native provider and accepted Session/AgentRun ownership remain in place. No production JavaScript tool or new browser/analytics subsystem was introduced.

| Finding | Correction and verification |
| --- | --- |
| Non-GET document downloads still buffered through SDK fetch and could bypass a lowered host byte cap | The integrated concurrent hardening commit supplies one bounded stream for all native request methods, preserving original form body, content headers and browser cookies. Retained its bounded request/response bodies, DNS and cancellation fences. Native Chromium POST tests prove one submission, authenticated body preservation, accepted 256-byte CSV and rejected chunked 2048-byte CSV under a 1024-byte cap. No probe/replay. The original review repaired the GET-only path; integration keeps the broader concurrently completed transport fix. |
| Application download projection trusted provider bytes against the absolute cap | Effective host cap is checked before artifact storage and allowance consumption. Regression rejects a 2048-byte provider download, then accepts a small CSV in the same one-download scope. |
| Singular capture allowances said “1 downloads” | Existing Ant Design Descriptions copy now uses singular/plural nouns. The component assertion and live one-screenshot case verify it. |
| Canonical hardening report link had no target, and adaptive delivery prose still prohibited commit/push | The concurrent hardening commit provides its original report; retained it and added this separate combined review report. Updated initial-versus-follow-up delivery wording. Canonical local links and adaptive anchors are checked. |
| Design preview sidecar predated the Browser budget disclosure rule | Preserved established DESIGN.md tokens, colors, component snippets and layout; refreshed the sidecar with the source-backed Browser Descriptions rule. |

The concurrent hardening implementation already resolved null screenshot MIME handling, meaningless text verification, duplicate attachment requests, metadata/DNS rebinding denial, partial-context/profile cleanup, empty versus inherited lease origins, capture privacy failures, fresh screenshot coordinate IDs and bounded receipt evidence. Existing protected-target, signed-wheel, frame-origin and DNS policy assertions were retained while fixture setup and new token consumers were updated. Integrated the already-pushed hardening commit by fast-forwarding the same branch, then reconciled the saved working changes. Kept its proxy connection cancellation, post-DNS authority check, bounded streamed resource path, recovery-call cleanup, native input limits, lease/budget tests and loopback TCP alias fixture. No remote history was overwritten.

## Runtime evidence

Focused native hardening/freshness: **30 passed**. Focused Application browser/host-budget/runtime: **82 passed**. Admin effective-configuration component before final copy refinement: **one passed**; final checks recorded below.

Playwright MCP uses a disposable Synthetic SQLite host at API 15080/UI 15173 with fixture 15091 and isolated data under `/private/tmp/agent-core-review/`. Through visible controls: open Admin, Instances, create a disposable approval-demo Instance, open Effective configuration. The Browser provider section shows actual lowered Snapshot=512 bytes, Download=1024 bytes and one screenshot per scope, host restart applicability, unchanged output/deadline units and independent provider readiness. Initial 1200px and 390px measurements show no viewport overflow; shared 192px labels become stacked 330px rows on mobile. At 1440px and 768px, shared labels stay 192px and all values wrap without document overflow. At 390px rows stack without overflow. Stopping only the disposable host produced the expected effective-config error/Retry; restarting with explicit isolated Restricted/EphemeralSession browser settings and pressing Retry restored the same owned Instance and updated effective values. Initial flow has zero console errors and all affected API calls succeeded; the intentional outage produced expected proxy 500 errors, then the retry returned 200. Screenshots are in `.playwright-mcp/review-browser-limits-{390,768}.png`. No paid vision inference ran.

## Broader checks and delivery

The pre-integration full backend run passed all 3,222 default cases (17 live-provider skips). The final integrated full solution run passed Domain 185, Application 1,580 (six skips), Infrastructure 1,049 (nine skips), and OrderEvents four. API passed 406, skipped two and failed the existing webhook-coalescing assertion (`Assert.Single` found no pending occurrence). Its unchanged isolated recheck passed in three seconds. This failure is retained as broad-run evidence; the complete API rerun without parallel frontend/CLI load passed 407, skipped two and failed zero. Final per-project coverage totals 3,225 passed and 17 explicit live-provider skips; the earlier combined command itself still has the recorded failure.

| Final check | Result | Evidence |
| --- | --- | --- |
| Backend solution | Domain/Application/Infrastructure/OrderEvents passed; API broad-run failure described above | `integrated-backend/*.trx`, `integrated-backend.log` |
| Complete API rerun | **407 passed, two skipped, zero failed** | `api-final-full.trx`, `api-final-full.log` |
| Frontend unit suite | **837 passed, 105 files**, including all 42 AdminApp tests; no assertions/timeouts changed | `frontend-full.json`, `frontend-full.log` |
| Frontend TypeScript/Vite build | Passed | `frontend-final-build.log` |
| Final Synthetic CLI E2E | **Three passed**: chat → Admin effective config → chat/new turn, narrow Admin view, AC-1042 browser lookup with one application message/answer | `cli-final/results.json`, `cli-final-e2e.log` |
| Documentation/sidecar | Final local links/anchors valid; sidecar parses; only timestamp and source-backed narrative changed | Local validation plus Git diff |
| Whitespace/conflicts | `git diff --check` passed; zero unmerged index entries | Local Git checks |

Commands:

```sh
dotnet test AgentCore.sln --no-restore --nologo -m:1 -nr:false -p:UseSharedCompilation=false --logger trx --results-directory /private/tmp/agent-core-review/integrated-backend
dotnet test tests/AgentCore.Api.Tests/AgentCore.Api.Tests.csproj --no-build --no-restore --nologo --filter FullyQualifiedName~Webhook_true_false_and_coalescing_preserve_receipt_replay_and_owner_projection --logger 'trx;LogFileName=webhook-recheck.trx' --results-directory /private/tmp/agent-core-review
dotnet test tests/AgentCore.Api.Tests/AgentCore.Api.Tests.csproj --no-build --no-restore --nologo --logger 'trx;LogFileName=api-final-full.trx' --results-directory /private/tmp/agent-core-review
pnpm -C web run test --run --maxWorkers=2 --reporter=default --reporter=json --outputFile=/private/tmp/agent-core-review/frontend-full.json
pnpm -C web run build
pnpm -C web exec playwright test --config=/private/tmp/agent-core-review/playwright.config.ts admin-shell.spec.ts p9-browser-journey.spec.ts
```

The temporary CLI configuration keeps the existing Synthetic project and first API/Vite servers, uses free isolated 16080/16173/16091 ports, explicit EphemeralSession profiles and fresh SQLite/workspace/artifact/key roots under `cli-final/`. `CI=1` prevents server reuse; no unknown/user catalog is reset. Its first setup attempt outside the ESM package failed before any tests (`import.meta` module loading); marking the disposable configuration directory as ESM fixed setup. CLI server shutdown produced a proxy `ECONNRESET` message in the first run; all three scenario assertions passed, and the final integrated run passed all three again. Playwright MCP additionally exercised the lowered-budget and outage/retry flow above. Only hosts started by this review were stopped.

Logs and reports are under `/private/tmp/agent-core-review/`; earlier evidence is under `/private/tmp/agent-core-adaptive-results/`. Paid vision/provider tests remain explicit opt-in and were not enabled. These checks prove runtime/admission/transport mechanics; scripted models do not prove autonomous multimodal understanding. Hosted CI is intentionally unawaited. The reviewed delivery preserves the concurrent remote commit and adds the integrated changes on `develop/branch-1`; its Git identity and remote confirmation are reported to the user after push.
