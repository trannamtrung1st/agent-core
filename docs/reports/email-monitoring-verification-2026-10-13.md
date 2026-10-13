# Email monitoring verification — 13 October 2026

Status: implementation/verification in progress on `develop/branch-1`, reconciled with main baseline `8fc22203`. Non-live closure is not yet claimed. Real Gmail/IMAP UAT is separate and has not been authorized or run against an owner's mailbox.

## Delivered slices

- A `6da22a58`: independent configuration/runtime CAS and generations, typed cursors, additive SQLite watch/authority tables, lease expiry and InMemory parity. Focused store proof passed twice (both stores); no pending model drift.
- B `ee4218bc`: Gmail profile/History feed and shared read-only IMAP UID transport. Four initial offline HTTP/TLS feed cases passed.
- C `363bd110`: one central worker, built-in catalog, atomic cursor/outbox staging and hidden source authority. SQLite watcher → Event → normal owned Automation Run → original authorized email.read → work.complete passed. Polling made zero model calls; unbinding denied a second pending Event.
- D `fa09f8c5` plus verification corrections: owner GET/PUT/202 check contract and existing account-detail AntD controls. No new navigation, provider grants, auto-binding or send approval.
- E/F: final source fencing, recovery/security, browser regressions, Compose and documentation checks are being completed; source/hosted revisions and full-suite outcomes will be recorded after execution.

## Runtime observations

The API/worker journey used a dedicated Synthetic SQLite host, a fake clock and injected model. It explicitly enabled monitoring, observed an empty baseline, inserted one fresh fixture message, advanced due time, staged one safe Event, dispatched one existing subscribed Automation, drained its owned Run, and observed the exact original email body through authorized `email.read` with `untrusted_email_content` labeling. The Run completed through its normal work.complete contract. A second message followed by unbinding produced zero admitted Runs. No real provider or send call was made.

InMemory/SQLite recovery fixtures exercised temporary transport failure (old cursor retained, no Event), successful retry, two bound eligible Instances sharing one account poll, repeated duplicate provider refs (two durable owner receipts total), SQLite reopen, and turning monitoring Off while the feed was blocked. The old scan could not commit; pending source authority was false and dispatch admitted zero occurrences/Runs. Final extensions cover older Definition denial and permanent paused recovery.

Owner HTTP tests exercised anonymous GET/PUT/check (401), default Off/revision 0, invalid interval (400), independent saved revisions, stale save (409), coalesced checks (202), host-off rejection (409), safe no-store response, explicit Off and deleted-account NotFound. Twenty initial related frontend tests passed; five dedicated Monitoring tests passed after callback/name corrections.

Playwright MCP exercised actual Add Gmail → Synthetic authorize → account details → enable/save → Check now → disable. One batched 1440×900, 768×900, 390×844, 320×900 inspection measured no horizontal page overflow and Monitoring widths of 1342/670/300/230 pixels. The shared table container bounds expanded Credential details; all controls remain reachable by page scroll. Before captures exist at `/tmp/email-monitor-before-dialog.png` and `/tmp/email-monitor-before-account.png`; initial responsive captures at `/tmp/email-monitor-on-{width}.png` and `/tmp/email-monitor-off-320.png`. Final confirmation at all four widths preserved switch focus and zero page overflow. [Desktop](assets/email-monitoring/confirmation-1440.png), [tablet](assets/email-monitoring/confirmation-768.png), [390px](assets/email-monitoring/confirmation-390.png), [320px](assets/email-monitoring/confirmation-320.png) show an actual Synthetic coverage gap after the fixture account authorization changed. [Retrying](assets/email-monitoring/status-Retrying-1440.png), [Gap](assets/email-monitoring/status-Gap-768.png), [host disabled](assets/email-monitoring/status-HostDisabled-390.png) and [Paused](assets/email-monitoring/status-Paused-320.png) are explicitly mocked safe owner-read projections, not evidence of real provider failures. No physical-device or real screen-reader claim is made.

Repeatable `email-monitoring.spec.ts` passed all four widths against the disposable Synthetic SQLite host (1.9 minutes). It exercised keyboard Add/enable, exact interval selection/save, an actual worker baseline, queued-check completion, cancel/disable, Gmail disconnect to Paused, and Events catalog navigation. The initial failures exposed nonvirtual Select accessibility, a stale-read callback variable, and AntD loading icons changing exact button names. The final controls expose real options, fence reads/drafts, and preserve stable Save/Check accessible names. Functional fixes are separate from the bounded discretionary visual cycle.

## Executed checks and pending gates

| Gate | Command / result |
| --- | --- |
| Data/CAS/reopen | `dotnet test tests/AgentCore.Infrastructure.Tests --filter FullyQualifiedName~EmailWatchStoreTests --nologo -m:1` — initial 2 passed |
| Provider feeds | focused Gmail History / IMAP watch tests — initial 4 passed; final truncation/deletion case pending |
| Integrated watcher/API/recovery | `dotnet test tests/AgentCore.Api.Tests --filter FullyQualifiedName~EmailMonitoring --nologo -m:1` — initial 4 passed; final extensions pending |
| Full backend | `dotnet test AgentCore.sln --no-restore --disable-build-servers --nologo -m:1` — in progress; Domain 195 passed, Application 1802 passed / 8 opt-in skips, Infrastructure 1321 passed / 10 opt-in skips; API revealed catalog count 6→7 assertion requiring updated explicit coverage |
| Frontend | Initial pnpm-selected Node 26 run: 871 passed / 79 failed (77 storage-global failures plus two Monitoring tests collected before callback correction). Explicit Node 22 full rerun is in progress; initial focused 20 passed, final dedicated 5 passed |
| Build | Node 22 `pnpm run build` — passed; existing large-bundle advisory remains |
| Responsive browser | isolated `pnpm exec playwright test e2e/email-monitoring.spec.ts` — 4 passed |
| Existing Compose | `COMPOSE_PROJECT_NAME=email-monitor-20261013 AGENTCORE_COMPOSE_PORT=5494 bash scripts/compose-sqlite-volume.sh` — passed; existing data, Synthetic OAuth and exact key-ring hashes survived recreation |
| Monitoring Compose | `python3 scripts/verify-email-watch-restart.py --project email-monitor-20261013 --port 5494` — passed; cursor, generation, configuration and account authorization unchanged, no historical inbox Events |
| Impeccable | context once, actual shape/audit/polish/Operate references, incumbent UI and before captures read; doctor returned no findings; scoped detector returned no findings. Final docs/design/confirmation pending |
| Exact-source hosted workflow | Pending final-source push/dispatch and all seven required jobs |
| Real Gmail / IMAP | Not run; dedicated mailbox owner opt-in remains a separate UAT gate |

Native/browser ports 5394/5194 and Compose 5494 are disposable task hosts. No user database reset or existing host termination was performed. Sandbox blocked VSTest loopback until the test invocation was escalated; actual checks then ran. Migration tooling 10.0.5 reported its version older than EF runtime 10.0.12, but generated/executed migrations successfully. The SQLite trigger-constraint widening performs an existing EF data-preserving table rebuild; production backup/startup migration rules apply.

## Release acceptance

EM-01–EM-29 are the proposal's acceptance IDs. Passing focused checks and responsive screenshots do not waive full-suite, source-authority race, broader Synthetic, design/document or hosted gates. Complete final coverage/outcomes and retain failures/skips before changing this report to closed. Live-provider consent, fresh real-mail detection/read, restart/duplicates and revoke behavior remain unverified separately from mandatory offline acceptance.
