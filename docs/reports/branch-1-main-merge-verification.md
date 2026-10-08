# develop/branch-1 and main merge verification

Reviewed and merged on 2026-10-08. First parent: `ac1cba0b` (`develop/branch-1`); incoming main: `4c35ba0f` (`origin/main`, including Activation/AgentRun behavior `8cec78c5`). The local branch was clean before merging. The merge preserves both histories; it does not reset either branch's work.

## Integrated behavior

- Main's immutable Activation, unified AgentRun, real background Sessions, continuation, conditional reporting, persistence migrations and Session-first UI remain the execution owners. Retired WorkItem and ConversationTurnExecution production engines remain deleted.
- Branch Browser v2 retains `IBrowser`, focused tools, native accessibility/ref handling, provider feature negotiation, environment/security controls and capability-aware built-ins. General Assistant v17 combines those capabilities with main's `background.start`; no v16 rollback or legacy browser aliases.
- Browser profile binding, unattended acquisition and release use the actual Session identity. Persistent background leases retain the 32-step/240-second budget; direct interactive browser turns retain their separate budget.
- AgentRun recovery recognizes focused browser mutations. A fresh snapshot clears observation uncertainty but retains the exact blocked action hash; the same mutation is still refused, while a different current ref can execute. Read-only navigation and PDF generation are not classified as recoverable mutations. The Domain recovery vocabulary matches all 27 focused mutation descriptors.
- Screenshot checkpoints use Session-owned artifacts. Recovery checks ownership, size and hash; vision models receive bytes, text-only models retain the artifact receipt. Tool admission includes the current model's vision capability.
- Harness policy limits derive from the finite registered catalog, preserving per-tool authorization/configuration checks. This permits freeze/reconfigure when v17 plus contextual attachment tools exceeds the previous fixed 64-tool ceiling.
- The background browser fixture uses v17 capability selection rather than simultaneously declaring an obsolete tool allowlist. The opt-in Browser v2 model fixture uses the AgentRun-aware runtime fixture.

Canonical docs preserve the execution cutover closure and Browser v2 verification history separately. TODO now agrees with the existing Journey L evidence; historical freezes remain unchanged.

## Local verification

All runs used Synthetic/offline providers, isolated temporary data and dedicated ports. Existing user hosts and databases were left intact. No hosted model request was authorized or made by this merge verification.

| Check | Result | Local log |
| --- | --- | --- |
| Backend build, `dotnet build AgentCore.sln --no-restore --nologo` | Passed, zero warnings/errors | `/tmp/agent-core-merge-build-offline.log` |
| Final full backend, command below | 2,697 passed, 14 opt-in skipped; final Domain/Application reruns also passed | `/tmp/agent-core-merge-final-backend.log` |
| Full frontend, `NODE_OPTIONS=--no-experimental-webstorage pnpm run test --run --maxWorkers=1` | 98 files, 745 tests passed | `/tmp/agent-core-merge-vitest.log` |
| Frontend build, `NODE_OPTIONS=--no-experimental-webstorage pnpm run build` | Passed; existing bundle-size warning | `/tmp/agent-core-merge-web-build.log` |
| Core browser projects, command below | 118 tests passed | `/tmp/agent-core-merge-core-browser.log` |
| Seven isolated acceptance projects, command below | 16 tests passed across all seven projects | `/tmp/agent-core-merge-acceptance-results.json` |
| Docker build and SQLite volume recreation, `bash scripts/compose-sqlite-volume.sh` | Passed with final source | `/tmp/agent-core-merge-compose-final.log` |
| Canonical Markdown links/anchors/fences, agent JSON, conflict markers and whitespace | Passed (22 canonical Markdown files, 7 agent JSON files) | `/tmp/agent-core-merge-doc-check.py` |

Backend command (repository root):

```sh
DOTNET_PROCESSOR_COUNT=4 AGENTCORE_LIVE_PROVIDER_TESTS=0 AGENTCORE_BROWSER_V2_LIVE=0 \
  dotnet test AgentCore.sln --no-build --no-restore --nologo \
  --blame-hang --blame-hang-timeout 90s
```

Core browser command (`web/`):

```sh
CI=1 pnpm exec playwright test \
  --project=synthetic --project=browser-stt --project=browser-browser --workers=1
```

Core ports were API/web `5880/5873`, browser STT `5881/5874`, Browser/Browser `5882/5875`, fixtures `5891–5893`, SQLite `/tmp/agent-core-merge-core.db`, with separate `/tmp/agent-core-merge-core-*` workspace/artifact/attachment/key roots.

Acceptance ran `CI=1 PLAYWRIGHT_FAITHFUL_MANUAL=1 pnpm exec playwright test --project=<project> --workers=1` separately for `faithful-manual`, `admin-lifecycle`, `p76-admin`, `p97-harness`, `p9899-continuity`, `p910-continuity-maintenance` and `secretary-demo`. Each project used its own `/tmp/agent-core-merge-acceptance-<project>.db` and storage roots, API/web `5920/5923`, fixture `5931`. `/tmp/agent-core-merge-acceptance.py` records exact environment and exit codes.

Compose used `COMPOSE_PROJECT_NAME=agent-core-merge-verification`, `AGENTCORE_COMPOSE_PORT=5889` and the normal Compose file plus an override selecting image `agent-core:merge-verification`. Its disposable volume was recreated before final-source verification. The journey verified durable Sessions, AgentRuns/checkpoints, approvals, workspace/definition resources and credential key-ring survival after container recreation.

## Exercised outcomes

Playwright MCP against the running SQLite Synthetic app (API/web `5888/5878`, fixture `5898`): created a General Assistant v17 Instance through Admin; sent `[test:background-start]`; observed the independent child complete with an attention outcome in Background Work; used **Continue in chat** and sent `Check B too.`; observed completion in the same child Session `98206160-da23-4480-8073-4dca9caf356e`. Restarted only this disposable host, reattached the same Session and verified retained original prompt, outcome and continuation. With explicit Restricted fixture browser policy, sent `Please look up record AC-1042.` and observed the intermediate progress message followed by `AC-1042 is In review.` and Ready. A fresh navigation after restart had no console errors; the earlier intentionally interrupted host produced transient transport/request errors. Relevant post-restart requests returned success. The final retained-history/record snapshot is `.playwright-mcp/branch-1-main-merge-final.yml`.

Focused Application integration also verified an expired in-flight click, durable recovery, fresh snapshot, same-action refusal and one different-ref execution; a persistent browser background run executed 26 changing snapshots then `work.complete` (27 recorded steps). Four screenshot recovery cases verified vision/text-only mapping and missing/foreign artifact refusal. Domain theories exercised eight focused mutation types and distinct navigation/PDF recovery. API background journeys crossed admission, child completion, report-back and continuation boundaries. CLI browser coverage exercised both Browser v2 and background Sessions through the merged UI.

## Failed gates and corrections

A final catalog comparison caught read-only PDF generation in the recovery mutation vocabulary; it was removed, navigation/PDF recovery assertions were rerun, and the full Domain (168) and Application (1,321; two opt-in skips) suites passed afterward (`/tmp/agent-core-merge-final-domain.log`, `/tmp/agent-core-merge-final-application-r2.log`).

The first build exposed a live fixture still passing the deleted ConversationTurnExecution store. The first focused recovery run exposed navigation being treated as a mutation. The initial background UI fixture received HTTP 400 from conflicting capability/allowlist declarations. Full API regression exposed the fixed 64-tool Harness ceiling. Each was corrected and its focused workflow rerun successfully, followed by complete affected suites. Test setup errors from an undersized fake ref were corrected before recording the recovery result.

The first acceptance host could not bind port 5900, already occupied by another process; it ran no tests. That process was left untouched. The acceptance projects were rerun on checked unused ports. An initial restricted-shell build stalled; the practical fallback used permitted local process execution with restore disabled. These setup attempts are not passing checks.

## Remaining verification boundary

This is local merge verification, not a new milestone freeze. Hosted Synthetic/Compose jobs have not run on the resulting merge commit. Live-provider, real-store and manual headset/device checks were not rerun; their existing historical evidence does not certify this merged SHA. The final backend skips are nine Infrastructure, two Application and three API opt-in provider/store probes; all six Docker sandbox cases passed once the fixture image was available. The branch is committed locally; no remote push is part of this request.
