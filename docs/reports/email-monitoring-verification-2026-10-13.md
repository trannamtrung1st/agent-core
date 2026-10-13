# Email monitoring verification — 13 October 2026

Status: non-live scope closed/frozen on behavior `bcfa7a7e7b602f35e635765c262d4f2a35ca6015`, on `develop/branch-1` from main baseline `8fc22203`. All seven [exact-source hosted jobs](https://github.com/trannamtrung1st/agent-core/actions/runs/38359505594) and final local gates passed. Explicit public publication authorization was granted on 2026-10-13. Real Gmail/IMAP UAT remains separately unapproved and unrun.

## Delivered slices

- A `6da22a58`: independent configuration/runtime CAS and generations, typed cursors, additive SQLite watch/authority tables, lease expiry and InMemory parity. Focused store proof passed twice (both stores); no pending model drift.
- B `ee4218bc`: Gmail profile/History feed and shared read-only IMAP UID transport. Four initial offline HTTP/TLS feed cases passed.
- C `363bd110`: one central worker, built-in catalog, atomic cursor/outbox staging and hidden source authority. SQLite watcher → Event → normal owned Automation Run → original authorized email.read → work.complete passed. Polling made zero model calls; unbinding denied a second pending Event.
- D `fa09f8c5` plus verification corrections: owner GET/PUT/202 check contract and existing account-detail AntD controls. No new navigation, provider grants, auto-binding or send approval.
- E `408b1754`, `0a02fa40`, `47f6353f`: transactional/gated source authority, independent paused/re-enable recovery, due-lease fairness and truthful commit telemetry. Gmail handles ID-only History responses with minimal metadata (the final fake-HTTP matrix includes both ID-only and label-bearing entries); older Definitions remain denied. Final model fixture parses account/message references from the actual admitted evidence.
- F `4c262783`: canonical docs/design synchronization and responsive capture evidence; final results are recorded below. Hosted verification was initially blocked by public-publication approval; the owner authorization and final review below supersede that blocker.

## Runtime observations

The API/worker journey used a dedicated Synthetic SQLite host, a fake clock and injected model. It explicitly enabled monitoring, observed an empty baseline, inserted one fresh fixture message, advanced due time, staged one safe Event, dispatched one existing subscribed Automation, drained its owned Run, and observed the exact original email body through authorized `email.read` with `untrusted_email_content` labeling. The Run completed through its normal work.complete contract. A second message followed by unbinding produced zero admitted Runs. No real provider or send call was made.

InMemory/SQLite recovery fixtures exercised temporary transport failure (old cursor retained, no Event), successful retry, two bound eligible Instances sharing one account poll, repeated duplicate provider refs (two durable owner receipts total), SQLite reopen, and turning monitoring Off while the feed was blocked. The old scan could not commit; pending source authority was false and dispatch admitted zero occurrences/Runs. Final extensions passed older Definition denial, permanent paused recovery, interval saves preserving Paused, and source admission fencing. Source admission takes the shared credential lock only for Email sources; generic schedule paths retain their existing lock behavior.

Owner HTTP tests exercised anonymous GET/PUT/check (401), default Off/revision 0, invalid interval (400), independent saved revisions, stale save (409), coalesced checks (202), host-off rejection (409), safe no-store response, explicit Off and deleted-account NotFound. Twenty initial related frontend tests passed; the final Monitoring file contains six passing tests, including delayed status reads preserving draft values and their original CAS revision.

Playwright MCP exercised actual Add Gmail → Synthetic authorize → account details → enable/save → Check now → disable. One batched 1440×900, 768×900, 390×844, 320×900 inspection measured no horizontal page overflow and Monitoring widths of 1342/670/300/230 pixels. The shared table container bounds expanded Credential details; all controls remain reachable by page scroll. Before captures exist at `/tmp/email-monitor-before-dialog.png` and `/tmp/email-monitor-before-account.png`; initial responsive captures at `/tmp/email-monitor-on-{width}.png` and `/tmp/email-monitor-off-320.png`. Final confirmation at all four widths preserved switch focus and zero page overflow. [Desktop](assets/email-monitoring/confirmation-1440.png), [tablet](assets/email-monitoring/confirmation-768.png), [390px](assets/email-monitoring/confirmation-390.png), [320px](assets/email-monitoring/confirmation-320.png) show an actual Synthetic coverage gap after the fixture account authorization changed. [Retrying](assets/email-monitoring/status-Retrying-1440.png), [Gap](assets/email-monitoring/status-Gap-768.png), [host disabled](assets/email-monitoring/status-HostDisabled-390.png) and [Paused](assets/email-monitoring/status-Paused-320.png) are explicitly mocked safe owner-read projections, not evidence of real provider failures. No physical-device or real screen-reader claim is made.

Repeatable `email-monitoring.spec.ts` passed all four widths against the disposable Synthetic SQLite host (1.9 minutes). It exercised keyboard Add/enable, exact interval selection/save, an actual worker baseline, queued-check completion, cancel/disable, Gmail disconnect to Paused, and Events catalog navigation. The initial failures exposed nonvirtual Select accessibility, a stale-read callback variable, and AntD loading icons changing exact button names. The final controls expose real options, fence reads/drafts, and preserve stable Save/Check accessible names. Functional fixes are separate from the bounded discretionary visual cycle.

## Executed checks and pending gates

| Gate | Command / result |
| --- | --- |
| Data/CAS/reopen | `dotnet test tests/AgentCore.Infrastructure.Tests --filter FullyQualifiedName~EmailWatchStoreTests --nologo -m:1` — initial 2 passed |
| Provider feeds | focused Gmail History / IMAP watch tests — initial 4 passed; final matrix passed within the full Infrastructure suite, including truncation/deletion and ID-only History |
| Integrated watcher/API/recovery | `dotnet test tests/AgentCore.Api.Tests --filter FullyQualifiedName~EmailMonitoring --nologo -m:1` — initial 4 passed; final extensions passed, including evidence-derived account/message arguments; 16 affected API rechecks passed before the full green run |
| Full backend | `AGENTCORE_LIVE_PROVIDER_TESTS=0 ... dotnet test AgentCore.sln --no-restore --disable-build-servers --nologo -m:1` — 3,889 passed / 21 explicit opt-in skips / 0 failed. Domain 195; Application 1,802 + 8 skips; Infrastructure 1,325 + 10 skips; API 563 + 3 skips; OrderEvents 4. Log `/tmp/email-monitor-frozen-backend.log` |
| Frontend | Explicit Node 22.18.0: `node node_modules/vitest/vitest.mjs run --maxWorkers=2` — 117 files / 951 passed / 0 failures on frozen behavior. Log `/tmp/email-monitor-frozen-frontend.log`; prior environment/contention failures retained below |
| Build | Explicit Node 22 `tsc --noEmit` and `vite build` — passed; existing large-bundle advisory remains |
| Responsive browser | isolated `pnpm exec playwright test e2e/email-monitoring.spec.ts` — 4 passed; full Node 22 Synthetic/browser-stt/browser-browser regression — 212 passed (23.1 minutes) |
| Existing Compose | Final source: `COMPOSE_PROJECT_NAME=email-monitor-final-20261013 AGENTCORE_COMPOSE_PORT=5494 bash scripts/compose-sqlite-volume.sh` — passed; existing data, Synthetic OAuth and exact key-ring hashes survived recreation |
| Monitoring Compose | Final source: `python3 scripts/verify-email-watch-restart.py --project email-monitor-final-20261013 --port 5494` — passed; cursor, generation, configuration and account authorization unchanged, no historical inbox Events |
| Impeccable | context once, actual shape/audit/polish/Operate references, incumbent UI and before captures read; doctor returned no findings; scoped detector returned no findings. Final confirmation preserved switch focus and zero overflow; canonical targets/anchors and design JSON validated |
| Exact-source hosted workflow | All seven [jobs](https://github.com/trannamtrung1st/agent-core/actions/runs/38359505594) passed on `bcfa7a7e7b602f35e635765c262d4f2a35ca6015`. Initial publication approval rejections were resolved by explicit owner authorization; the public branch was pushed successfully |
| Real Gmail / IMAP | Not run; dedicated mailbox owner opt-in remains a separate UAT gate |

Native/browser ports 5394/5194 and Compose 5494 are disposable task hosts. No user database reset or existing host termination was performed. Sandbox blocked VSTest loopback until the test invocation was escalated; actual checks then ran. Migration tooling 10.0.5 reported its version older than EF runtime 10.0.12, but generated/executed migrations successfully. The SQLite trigger-constraint widening performs an existing EF data-preserving table rebuild; production backup/startup migration rules apply.

## Full-run failures and corrections retained

The first full backend run found the old six-key catalog assertion; it now asserts seven keys and explicitly includes email.message.received. A second broad run under load contained recurring Automation, malformed-bucket recovery and task-evidence assertions, plus two paused-source assertions compiled during source edits. The final isolated affected API recheck passed all 16 cases; the evidence-derived watcher/read/revocation subset then passed 4/4. The frozen-source full rerun passed 3,889 tests with 21 explicit live/paid opt-in skips and zero failures; earlier failed commands remain retained here as diagnostic history.

The first broad alternate-port browser run was stopped after OAuth callbacks used the operator's default 5173 origin and broke the Gmail/Monitoring flows. The test runner now pins PublicAppUrl to each Synthetic test UI. The fresh isolated rerun passed all 212 cases. No assertions or timeouts were weakened, no user database was reset, and no real-provider UAT was performed.

Google's [History API contract](https://developers.google.com/workspace/gmail/api/reference/rest/v1/users.history/list) permits ID/thread-only messages; the final feed fetches minimal labels when labels are absent. This is HTTP contract evidence, not real-mail acceptance.

## Acceptance coverage map

| IDs | Observed coverage / limits |
| --- | --- |
| EM-01–03 | Default Off/no due claims; from-now Gmail/IMAP baselines; strict 60/300/900 intervals and independent configuration/Credential CAS. Store/API/HTTP/TLS fixtures. |
| EM-04 | Two-Instance account sharing has one feed poll; due-store tests verify leased heads do not starve another account. Three-claim/concurrency ceilings are fixed in the single worker implementation. |
| EM-05–08 | Account-scoped Gmail refs, messageAdded-only detection, duplicate/paginated budgets, ID-only responses and expired-history gap. Fake HTTP contracts; Real Gmail remains unverified. |
| EM-09–10 | Loopback verified-TLS EXAMINE/UID-only commands; no BODY/STORE; UIDVALIDITY rollover, missing UIDNEXT/truncation gap and deleted recent mail. |
| EM-11–12 | Two owned deduplicated receipts from one shared poll; monitor-off/unbind/pause/current binding/Instance authority fences. Duplicate Credential binding is already rejected by the existing binding store. |
| EM-13–16 | Shared built-in catalog/filter/Automation path, older Definition denial, evidence-derived normal Run/read, 202/coalesced Check now and non-sending Test access. Actual UI/HTTP and runtime fixtures. |
| EM-17–20 | Failed scan retains old cursor; post-commit duplicate/reopen recovers original receipts; expired/stale leases and disable/re-enable fence commits; permanent failures stay Paused across interval saves, with explicit Check recovery; bounded temporary retry. |
| EM-21–22 | Owner projection omits secrets/cursors/leases/fingerprints/content; Event data omits subject/body; polling has zero model calls and no automatic binding/send. Existing approval/send regressions passed in the complete Synthetic browser suite. |
| EM-23–25 | Dedicated controlled-form tests, conflict/slow-save/deferred-read fixtures, keyboard/mouse four-width click paths, paused/host/gap/retry projections, focus and zero overflow. No physical-device/screen-reader or real transport-failure UI claim. |
| EM-26–27 | Full Synthetic browser suite passed 212 cases including neighboring Gmail/chooser/send, Events and speech. Both stores reopen/retain receipts; isolated Compose verifies existing grants/key-ring and watch cursor/generation preservation. Final complete backend/frontend gates are green, with counts/logs recorded separately. |
| EM-28 | Canonical targets/anchors, design JSON/Impeccable checks and all seven [exact-source hosted jobs](https://github.com/trannamtrung1st/agent-core/actions/runs/38359505594) passed on `bcfa7a7e7b602f35e635765c262d4f2a35ca6015`. Final review and closure below supersede the initial publication blocker. |
| EM-29 | Dedicated owner mailbox opt-in was not requested; Real Gmail/IMAP UAT remains separately unverified. |

## Impeccable audit and delivery

The bounded audit/polish used one batched four-width inspection, one correction batch (75ch copy, accessible real Select options and stable loading-button names), and one confirmation round. Later functional regression fixes were independently tested. The incumbent dark AntD v6 hierarchy and shared token spacing remain; no new kit, palette, navigation or account dialog redesign was introduced. Technical audit: accessibility 3/4 (named controls, keyboard/focus/semantics checked; no actual screen-reader/physical-device acceptance), performance 3/4 (no added dependency; existing large bundle remains), theming 4/4, responsive 4/4, implementation integrity 4/4 — 18/20. Doctor and scoped detector had no findings. This is an observed local audit, not a blanket WCAG certification.

Final local acceptance on behavior candidate `47f6353f`: backend 3,889 passed / 21 explicit opt-in skips / 0 failed; frontend 951 passed in 117 files; Synthetic core + browser speech 212 passed; production build; existing Compose restart and new watch cursor/generation recreation; documentation targets/anchors; design JSON and Impeccable checks. Logs are `/tmp/email-monitor-frozen-backend.log`, `/tmp/email-monitor-frozen-frontend.log`, `/tmp/email-monitor-full-browser-final.log`, `/tmp/email-monitor-build-final.log`, `/tmp/email-monitor-compose-final.log`, `/tmp/email-monitor-compose-watch-final.log`.

## Release acceptance

EM-01–EM-29 are the proposal's acceptance IDs. Passing focused checks and responsive screenshots do not waive full-suite, source-authority race, broader Synthetic, design/document or hosted gates. The owner authorized public push/dispatch on 2026-10-13. Non-live implementation is closed/frozen on `bcfa7a7e7b602f35e635765c262d4f2a35ca6015` after all required local and exact-source hosted gates passed. Live-provider consent, fresh real-mail detection/read, restart/duplicates and revoke behavior remain unverified separately from mandatory offline acceptance.


## Follow-up UI polish (2026-10-13)

The requested frontend/Impeccable refinement preserves the dark Ant Design v6 Operate baseline. The shared `CredentialDetails` composition now owns 16px spacing between metadata, Gmail/Custom connection controls and Monitoring in both global and Instance views. Monitoring has a quiet divider and a 16px title with live semantic status. Header copy and action rows use 8px gaps; saved-form groups use 12px, and section groups use 16px. Reload is a text action, subscriber guidance sits with configuration links, and mobile interval controls/labels have a 40px minimum height alongside existing Admin buttons. Saved feedback clears when a new draft begins or Cancel restores values; Next check distinguishes Off, Paused and operator-unavailable states. No new dependency, token family or navigation was added.

The bounded follow-up used one batched incumbent inspection at 1440/768/390/320, one corrective batch and one confirmation covering those widths and recovery states. Measured detail widths were 1342/670/300/230px; no page overflow. Computed shared detail/section gaps and section inset were 16px, control-row gaps 16px, and mobile interval/label heights 40px. Keyboard traversal produced native visible Switch focus; Save, Cancel, queued Check now, diagnostic disclosure and navigation were exercised. MCP also verified Custom incoming tests globally and outgoing tests through an Instance binding at desktop/mobile, with the Instance-specific Automation link.

Direct browser recovery used an explicitly injected 409 save response: the draft remained, Reload restored authoritative settings, and the isolated fixture was then saved Off. Safe GET projections exercised Retrying, HostDisabled and Paused; Check now stayed disabled for unavailable host/account and remained available for retry. The persisted Synthetic fixture also disclosed its actual coverage gap. These projections do not establish real-provider failure or mailbox acceptance. Console/network inspection found only the intentionally injected 409, with no unexpected application errors on the final Instance journey.

Validation on the follow-up UI source:

- Explicit Node 22 Vitest: `EmailMonitoringSection`, `CredentialsSection` and `EmailAccountDialog` — 21 tests passed in 3 files. The saved-form regression now asserts stale success feedback clears on editing. Log: `/tmp/email-monitor-polish-vitest.log`.
- TypeScript `--noEmit` and Vite production build passed. The existing large-bundle advisory remains. Log: `/tmp/email-monitor-polish-build.log`.
- A fresh isolated Synthetic SQLite host ran `email-monitoring.spec.ts`, `email-accounts.spec.ts` and `system-credentials.spec.ts`: 10 passed in 3.3 minutes. It covered four-width save/queue/off/disconnect/pause, Gmail consent/edit/chooser, consecutive binding revisions and safe Credential CRUD/mobile navigation. Log: `/tmp/email-monitor-polish-fresh-e2e.log`; temporary configuration `/tmp/email-monitor-polish.playwright.config.mts`, ports 6094/6194, database `/tmp/email-monitor-polish-fresh/monitor.db`.
- The first run against the reused isolated database passed 6/8 but timed out waiting for queued Check now to re-enable at 768px and OAuth navigation at 320px. The fresh run above passed unchanged test deadlines and UI source; no assertion or timeout was relaxed. Root cause of those first-run timing failures was not established. Log: `/tmp/email-monitor-polish-e2e.log`.
- Scoped Impeccable detector returned no findings; design JSON parsed and source diff checks passed. The canonical frontend presentation specification, DESIGN context, sidecar and surface brief were synchronized.

Current captures: [desktop](assets/email-monitoring/polish-monitoring-1440.png), [tablet](assets/email-monitoring/polish-monitoring-768.png), [390px](assets/email-monitoring/polish-monitoring-390.png), [320px](assets/email-monitoring/polish-monitoring-320.png), [Synthetic coverage gap at 390px](assets/email-monitoring/polish-gap-390.png).

This follow-up changes frontend presentation and local feedback; it does not repeat or supersede the earlier complete backend, 951-test frontend, 212-case browser or Compose evidence. Hosted acceptance/publication was still pending when this polish was delivered; the later explicit authorization and final review below supersede the publication blocker. Dedicated real-mailbox UAT remains open. Physical-device and actual screen-reader behavior were not tested.


## Final review and authorized publication (2026-10-13)

The owner explicitly approved committing and publishing this branch to the public repository. Review traced watch due/lease/generation transitions, provider cursor budgets and authorization, atomic source receipts/admission, owner projection and frontend asynchronous form state. Corrections within the approved slice:

- A permanently paused watch no longer reports checkPending merely because its internal retry timestamp has passed. This leaves explicit Check now available for an otherwise usable account. The clock-controlled owner HTTP recovery fixture failed in both InMemory and SQLite before the fix, then verified Paused/false pending, no automatic poll and 202 explicit recovery after it.
- Explicit Reload now fences edits made after its request began and retains their original configuration revision. A retired read failure cannot replace a newer successful read. Switching accounts releases an old pending UI action and ignores its completion. The delayed Reload and pending-save account-change regressions both failed before the correction.
- Custom port/TLS defaults are owned by the mounted form fields: new accounts show IMAP 993, SMTP 465 and required implicit TLS. Saved accounts retain their own values. Actual browser reproduction observed all four fields empty before the fix; afterward MCP observed 993/465 and both TLS choices. The new 320px E2E saves from those defaults, deliberately changes SMTP to 587/STARTTLS, tests incoming access in Synthetic and reopens the exact saved values.
- Paused recovery names Test incoming for Custom and Test access for Gmail, matching the visible controls.
- The hosted Compose job now includes the existing isolated watch/cursor/generation recreation script after the SQLite/key-ring smoke. It uses a fresh email-monitor-ci-* project and the built Synthetic image; no user database or real mailbox is involved.

Executed focused checks: 4 EmailMonitoring owner/runtime API cases passed, including SQLite event → normal Run → authorized original email.read; 24 unit cases passed across Monitoring, Credential collections and account dialog before the final small UI refinements; the fresh final Monitoring run passed all 10 cases (`/tmp/email-monitor-review-final-monitor-ui.log`). The focused browser regression passed all 11 cases in 3.4 minutes at ports 6094/6194 using `/tmp/email-monitor-review-browser/monitor.db`; its log is `/tmp/email-monitor-review-browser.log`. Existing test deadlines and assertions were retained.

MCP exercised actual Custom creation defaults and existing-account reopening, and the delayed Reload/edit/Cancel path. The gated read used a harmless subscriber-count projection to observe that the response had rendered before checking the draft, then restored the real projection. Expected and observed: the draft remained checked after the delayed response and Cancel restored persisted Off. A safe Paused projection with an already-past due timestamp and false pending left Check now enabled at 320px without overflow; real owner HTTP admission is established by the clock-controlled two-store fixture. No real provider failure, subscription creation, mail arrival or send is claimed by these UI projections. Console inspection found no application warnings/errors in the final account journey.

Full local backend/frontend and final build gates run against the review source, with logs `/tmp/email-monitor-review-full-backend.log`, `/tmp/email-monitor-review-frozen-frontend.log` and `/tmp/email-monitor-review-build-final.log`. The first full frontend run (`/tmp/email-monitor-review-full-frontend.log`) passed 954 cases but failed the new copy assertion: it had cached the old component before that final source/test addition. A fresh focused run passed; the full gate was restarted with source edits frozen, without relaxing assertions. The hosted run will verify the pushed exact source, including the complete browser projects and both Compose checks. Final counts/SHA/hosted outcome are recorded in the closure addendum after completion. Documentation validation checked 16 files, 678 local targets and 217 anchors with zero missing references; workflow YAML and design JSON parsed; source diff and scoped Impeccable checks passed. Real-mailbox UAT remains separately unapproved and unrun.


## Closure addendum (2026-10-13)

Behavior is frozen on `bcfa7a7e7b602f35e635765c262d4f2a35ca6015`. The owner-authorized public push succeeded; an initial HTTP 400 transport failure was resolved with per-command buffered HTTP/1.1 transfer, without a force push or global Git configuration change. Current main was fetched and has no commits missing from this branch. All seven [hosted gates](https://github.com/trannamtrung1st/agent-core/actions/runs/38359505594) completed successfully on that exact SHA:

| Job | Result |
| --- | --- |
| Synthetic Compose smoke | Passed |
| Synthetic Playwright acceptance | Passed |
| Synthetic Playwright core (1/2) | Passed |
| Synthetic Playwright core (2/2) | Passed |
| Synthetic backend | Passed |
| Synthetic frontend (1/2) | Passed |
| Synthetic frontend (2/2) | Passed |

Final local evidence: full backend **3,889 passed / 21 explicit opt-in skips / zero failures**; full frontend **955 passed in 117 files** (488.12 seconds); strict TypeScript and production build passed. The existing bundle-size advisory remains. Focused Synthetic browser regression passed **11 cases**; fresh final Monitoring unit regression passed **10 cases**, and the direct MCP outcomes are recorded above. All required affected paths were executed; builds alone were not treated as runtime acceptance.

Hosted detail: backend's four configured .NET projects passed **3,879 cases / 27 skips**, plus 3 Python storage-monitoring tests. The six additional skips are existing DockerSandboxFact readiness-probe cases; those six and the four OrderEvents cases ran in the successful local solution gate. No test assertion or timeout was relaxed. Frontend shards passed **547 + 408 = 955** cases. Core/browser-STT/browser-browser shards passed **108 + 105 = 213** cases. Sequential acceptance projects passed **16** cases (Manual-A 1, lifecycle 1, Admin 1, harness 6, continuity 2, maintenance 1, secretary 4). Both Compose steps passed, including persisted watch cursor/generation/configuration/authorization and no historical Events across recreation. Hosted evidence is saved at `/tmp/email-monitor-review-ci-final.json` and `/tmp/email-monitor-review-ci-full.log`.

The documentation follow-up records this accepted behavior and does not change runtime source. EM-01–EM-28 non-live evidence and EM-29's separate opt-in UAT disclosure are complete. No real Gmail/IMAP consent, mail detection/read, delivery, live restart/revoke, physical-device or actual screen-reader acceptance is claimed. P10/P11 and earlier historical freezes remain unchanged. Owned native and test hosts were stopped; no user database reset or unrelated host termination occurred.
