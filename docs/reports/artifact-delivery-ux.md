# Artifact delivery UX

Status: local implementation and required local gates complete; publication to main authorized, including concurrent external UI changes. Exact-behavior-head hosted acceptance remains pending. This enhancement is not closed/frozen until that hosted gate is green.

This bounded post-P9.10 enhancement implements `local/proposals/artifact-delivery-ux-final-proposal-v2.md`. Starting checkout: `8efb8de0`, preserving its Runs-table update. P9.10 stays frozen on `71a9e6fd`; P9.8/P9.9, continuity behavior, and parallel Synthetic CI topology are unchanged. P10/P11 remain unopened.

## Delivered behavior

Chat's shared rich-block renderer now selects `ArtifactView`. The existing owner-protected metadata endpoint supplies canonical filename/type/size; block labels and UUIDs never become filenames. The service retains only public immutable metadata in a 128-entry Session/Artifact-keyed LRU, including pending requests; failures are evicted for Retry. Content is read only on explicit Download using `ownerFetch`. Temporary Blob URLs are revoked after one second, anchors are removed, and unmount aborts pending downloads. No Blob enters Zustand or a persistent cache.

The card reuses file-chip fill/border/radius/insets and Ant Design controls/icons. Loading, metadata unavailable/Retry, downloading/disabled, and body failure/Retry states stay local to the file. Long filenames have ellipsis, full titles and accessible Download names. Live, reconnect, reload, history pagination and ended history share this renderer. Backend routes, ownership, storage, quotas and provider/response grammar are unchanged. A separate stale Admin navigation test/mock was synchronized to the pre-existing `8efb8de0` shared Run details drawer; no production Admin code changed.

## Acceptance map

| Criteria | Evidence |
| --- | --- |
| AC1–AC3 canonical card; no GUID label | ArtifactView component, real Core metadata browser journey, invalid fixture fallback |
| AC4–AC7 exact bytes/name, owner authentication, lazy body | Real-GUID browser download stream and API content/header assertions; render makes no content request |
| AC8–AC9 bounded dedup/cache, temporary URLs | Service tests cover LRU eviction, pending-request bounds, Session keys, fresh body reads, delayed revoke, anchor cleanup and abort |
| AC10, AC17 Session/auth fail closed | API anonymous, cross-Session, nonexistent and non-GUID tests; metadata identity validation; existing unauthorized projection regressions |
| AC11–AC12 durable/live/history parity | Browser reconnect → reload → second download → end → retained download; existing paging renderer |
| AC13–AC14 local failures and Retry | Component and browser metadata/body 503 recovery; surrounding assistant reply stays visible |
| AC15–AC16 response contract | Existing typed, marker-free, compatibility parser and terminal display repair backend regressions; semantic/rich browser suites |
| AC18–AC19 accessibility/responsive | Native keyboard Download/Retry; full accessible long name; visible focus; computed styles and overflow checks at 1440/768/390px |
| AC20 no added architecture/product surface | Frontend service/component/CSS only; no new production endpoint, protocol, store, migration, dependency or workspace product |
| AC21 real download browser proof | `web/e2e/artifact-delivery.spec.ts`: real GUID, canonical suggested filename, exact 127-byte content, lazy requests, reconnect/reload/ended reads |
| AC22 docs | Frontend/protocol docs, product/design tooling context and TODO synchronized |

## Executed scenarios

1. Synthetic Sam/Support publishes `case-note.md` through the existing knowledge → `artifacts.create` workflow. Chat resolves a real GUID to Markdown · 127 B. Before activation there is no Artifact content request. Keyboard activation triggers the browser download with `case-note.md` and exact expected content: `Demo-only order status language. Cite this document as support-order-policy@demo. Do not invent refunds or live account changes`. Reconnect and reload retain the card without reading its body; a second download matches. Ended read-only history hides the composer and still downloads the same bytes.
2. Metadata fails once with 503: File unavailable and Retry replace the loading state while the reply remains intact. Retry resolves metadata. Content fails once with 503: canonical metadata stays visible, Download failed/Retry appears, and keyboard Retry succeeds. The separate layout stress fixture substitutes only a long metadata filename; it is not the real-GUID storage proof. Two delivered cards remain accessible at 1440/768/390px with no horizontal page overflow after responsive layout settles.
3. API upload → materialize → metadata/content read checks canonical display name, content type, byte size, Content-Disposition and exact bytes. Anonymous reads are 401; foreign/nonexistent/fixture IDs are 404. End retains bytes; durable delete makes metadata/content 404.
4. Interactive Playwright MCP checks the running disposable Synthetic app: publish, inspect metadata-only network traffic, keyboard download, inject body 503, Retry and read the downloaded stream. The only observed browser error is the intentional 503. Final long-name captures and computed styles confirm elevated fill, 1px border, 12px radius, 8×12px padding, filename ellipsis and keyboard focus at desktop/tablet/mobile. Evidence is local under `local/artifact-delivery-evidence/`.

## Checks

- Focused Vitest: 38 passed (5 service, 6 card, 27 Conversation).
- Frontend build: passed; existing large-bundle and SignalR annotation warnings remain.
- Focused Artifact/semantic/rich browser regression: 7 passed.
- Full backend: OrderEvents 4; Domain 145; Application 1215 passed / 1 optional skip; Infrastructure 741 passed / 15 environment/opt-in skips; API 373 passed / 2 opt-in skips.
- Compose SQLite volume-survival gate: passed after a transient registry EOF retry; disposable container/network/volume cleaned up.
- Final full frontend: **93 files / 695 tests passed** in one complete Node 22 run. Initial run had two AdminApp timeouts under concurrent local load and one stale Runs mock; both affected files also reran green (41 passed). The corrected mock reflects pre-existing `8efb8de0` shared details while retaining exact Thought/Event source selection assertions; the Artifact work changes no production Admin navigation.
- Final full primary browser gate: **97 passed** in one complete Node 22 run (Synthetic, Browser STT, Browser/Browser). The initial gate caught a new-test session-title collision with the existing catalog scenario; Artifact tests now use distinct titles. A later attempt lost its Vite hosts and produced connection-refused/timeout failures; retained logs distinguish those from the final green run.
- Extended harness/continuity/secretary coverage: **17 passed** across the initial 10 passes and all 7 interrupted cases rerun green. The Artifact work changes no production continuity or cadence code.
- Exact-behavior-head hosted Synthetic/Compose workflow: blocked. Automatic approval review rejected the attempted commit/push to `main`, requiring explicit human authorization for protected/shared branch publication. Nothing was committed or pushed. Do not mark closed until that publication is authorized and the exact-behavior-head workflow is green.

Commands use the existing solution/test runner. Local `pnpm exec` initially selected Node 26, so final frontend/browser gates explicitly prepend the Node 22 and .NET locations to PATH. Final frontend command: `pnpm exec vitest run --maxWorkers=1`. Earlier focused unit runs used `/Users/trungtran/.nvm/versions/node/v22.18.0/bin/node node_modules/vitest/vitest.mjs run --maxWorkers=1`. Final primary browser command: `pnpm exec playwright test --project=synthetic --project=browser-stt --project=browser-browser --reporter=line`. Browser CLI uses disposable ports/SQLite paths, `CI=1`, and existing `web/playwright.config.ts`; focused runs use `PLAYWRIGHT_FAITHFUL_MANUAL=1` to start only the primary host. Full backend uses `dotnet test AgentCore.sln --no-restore --nologo --blame-hang --blame-hang-timeout 5m`. Compose runs `COMPOSE_PROJECT_NAME=artifact-delivery-verify ./scripts/compose-sqlite-volume.sh` without touching existing user containers/volumes.

Initial sandbox .NET attempts failed on named-pipe socket permissions and browser host startup timed out. Authorized runs outside the sandbox succeeded. No provider credentials or live inference were used. The pre-existing Impeccable design sidecar is stale; optional Impeccable `document` can refresh it separately. Its repair is outside this change.


## Follow-up consistency and runtime review

The requested second review found a test-coverage mismatch: the teardown test claimed late-metadata protection while only exercising download cancellation. It now names that behavior accurately, and a separate regression verifies that a delayed previous-Session metadata response cannot replace the current Session's filename or download target. A service regression also verifies that bytes arriving after cancellation create no Blob URL and trigger no download. Both pass against the existing implementation; no production repair was needed. The real browser journey now explicitly asserts that ending the Session succeeds before checking ended history.

Follow-up checks: **40 focused unit tests passed** (6 service, 7 card, 27 Conversation), both Artifact Synthetic E2E journeys passed, the Artifact API integration test passed (1/1), and TypeScript/production build passed. Interactive Playwright MCP published a fresh real Artifact, displayed `case-note.md` / Markdown · 127 B and downloaded the exact expected 127 bytes. The only interactive console error was the existing missing `/favicon.ico` (404), unrelated to Artifact delivery. These focused checks supplement the earlier complete gates; the full suites were not rerun for these test-only changes. The disposable review hosts were stopped and `git diff --check` passed.

## Publication boundary

The implementation is reviewable as the current unstaged working-tree diff. Auto-review rejected the commit/push command before execution, so no index staging, commit, push or hosted workflow was performed. Explicit authorization to commit and push this candidate to `main` is the remaining publication prerequisite. Hosted acceptance must test the resulting exact behavior SHA; a later docs-only closure commit may pin that tested parent, following the existing P9.10 convention.


Local logs and visual evidence are retained under `local/artifact-delivery-evidence/`, including final frontend, primary browser, extended browser, backend, build and Compose results plus initial failure/rerun records. Changed documentation links and fences and `git diff --check` pass. Test hosts and the disposable Compose container/network/volume were cleaned up; existing nopCommerce containers were preserved.


Concurrent workspace edits appeared during verification in `web/src/features/admin/InstanceContinuitySection.tsx`, `web/src/features/chat/BackgroundWorkDrawer.tsx`, and a separate Runs-actions paragraph in `docs/13-frontend-implementation-spec.md`. They were preserved. They belong outside the Artifact publication, including the unrelated documentation hunk; only the Artifact-owned files/hunks and the stale baseline test correction should be committed for this enhancement. The latest complete frontend/browser runs passed, and the current working tree also builds. Hosted exact-commit acceptance remains mandatory because local verification occurred in a shared, changing checkout.


The user subsequently explicitly authorized committing and pushing all current changes to `main`, including the external UI changes above. That authorization supersedes the earlier publication prerequisite and Artifact-only staging scope. Historical verification results remain as recorded; hosted acceptance must verify the combined published behavior.
