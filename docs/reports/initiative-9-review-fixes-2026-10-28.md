# Initiative 9 — targeted review corrections — 2026-10-28

Both findings from the implementation review of `188626d7113ecfe2a1ff73a5955a6b13f5271a9d` are corrected. The focused local regression and browser evidence below qualify this follow-up. The original [delivery report](initiative-9-local-verification-2026-10-27.md) retains its original source and external qualification limits. No migration, Task model redesign, UI framework change or Group Chat implementation is included.

## Discovery correction

`TenantTaskAccessService.TargetsAsync` no longer requires the requester to read a worker's Private Space before considering Shared Spaces. It filters Space authority before paging, walks authorized Space pages in batches of 100, and checks actual worker membership plus both actors' read permissions. It prefers an eligible readable Private Space; otherwise it returns the first eligible Shared Space as a concrete default. A worker with no mutually usable Space is omitted. Task admission independently validates the explicit saved selection as before.

The four HTTP cases cover InMemory/SQLite × one/103 authorized Shared Spaces. A restricted authenticated requester receives 403 for the worker's Private Space but sees the eligible worker and only its usable Shared Space. The eligible Space is beyond the first 100 authorized rows in the larger fixture. Actual Task admission captures that Shared Space. Revoking the requester's exact selected-Space grant removes the target; no Private fallback appears.

## Hint collection correction

The collector takes a dedicated non-overlap guard per connection and briefly copies the current subscription/cursors under the delivery gate. Task, message, IAM and database reads occur outside the delivery gate. It repeats recipient authorization and verifies security/Session witnesses, then attempts immediate gate acquisition for current-subscription validation and ordered publication. A busy live-delivery gate discards the best-effort batch without advancing cursors. Closed/replaced subscriptions and changed/denied authority receive no stale batch. Read denial cannot retire valid Chat/voice delivery; any hint-cursor reset is restricted to the same captured subscription.

Connection collection is limited to four concurrent operations, with the existing per-connection deadline and bounded pages. The existing `AgentRunHostedService` now separately awaits its Run and hint cadence loops, so a slow observer does not postpone Run dispatch. Shutdown cancels and drains both loops. This adds no execution owner, queue, hosted service or protocol event family.

Six deterministic native Kestrel/SignalR cases cover both stores and active, reattached and logged-out recipients. A real authorized saved Task query is held behind a barrier while normal Chat admission and terminal live delivery complete. The fixture owns its exact poll/dispatch pass to avoid racing the automatic collector. Reattachment discards old-attachment hints; real authentication logout suppresses stale hints. A fresh authorized pass still emits ordered hints. A separate fake-time hosted-service test proves that another Run pass executes while hint work remains blocked, hint passes do not overlap, and shutdown cancels/drains the blocked observer.

## Results

- Final focused backend regression: **22 passed, 0 failed, 0 skipped** (`/private/tmp/i9-review-regression-final.log`). This includes both findings, Task HTTP/receipts/privacy, actual JavaScript MessagePack Task/report/reconnect, terminal Chat receipt/publication, safe diagnostics and runtime ownership. The final explicit-pass fixture and fake-time cadence recheck also passed **7/7** (`/private/tmp/i9-review-deterministic-final.log`); these overlap the 22-case regression and are not an additional disjoint total.
- Task Center, Background Work, Activity and Chat service tests: **45 passed** in four files (`/private/tmp/i9-review-ui-regression-final.log`).
- API build passed with zero warnings/errors. Whitespace and changed-document relative-link/anchor/fence/complete-JSON checks passed.

The first integration runs exposed fixture omissions, not changes to production permissions: the restricted delegator needed its explicit creation grants to avoid Review, and Chat executors needed current source-Space read access. A negative discovery assertion initially attempted to remove membership during active work; the incumbent quiescence guard correctly refused it. The test now revokes the exact human Space grant. The test proxy was corrected to preserve InMemory's synchronous access-denied exception rather than wrap it. A preexisting Chat unit assertion expected the retired top-level `offeringId`; it now asserts the current fixed `modelPreference`. The subsequent focused runs above pass. No authority gate was weakened.

## Actual Synthetic browser journey

Playwright MCP operated the owned disposable SQLite Synthetic API/SPA. The restricted Human `shared-review-1793150891784@example.test` had read access to `Review Shared Space` (`544e84eb-b113-4c97-bbdf-d2acbe7282b7`) and received **403** for Riley's Private Space. The real picker still offered Riley, automatically selected that Shared Space, and admitted Task `01a18021-e6ea-7d93-9f2e-5290c528807c` through visible controls. The worker completed under its own identity in the selected Shared Space, emitted its progress message and saved its result. The user inspected and acknowledged it; the saved notification has revision two and a real acknowledgment timestamp, with no parent report admitted for the inbox-only request.

The same user then sent a normal Chat message through the real SignalR composer and received the completed `Hello from synthetic.` reply. Background Work showed the appropriate scoped empty source inventory. Returning to the owner account, the worker's populated Background Work drawer showed the new delegated result alongside independent Automation results with Reported and Not reported statuses. No new console error was observed during that owner consumer check. Intentional Private-Space 403 probes are expected denials, not clean-console evidence.

Representative captures: [Shared default](assets/initiative-9/shared-only-default-fix.png), [saved/acknowledged Task](assets/initiative-9/shared-only-review-fix.png), [Background Work regression](assets/initiative-9/review-background-regression.png). UI markup and design tokens are unchanged; no new visual-design acceptance is claimed.

## Scope and closure

This follow-up addresses both supplied findings with current service, HTTP, persistence, wire and browser evidence. Production load/latency was not benchmarked; the deterministic tests establish absence of the identified gate/cadence blocking dependency, not a latency SLA. Live providers, real containers, physical audio/devices, manual accessibility certification and optional CI were not run. Existing monetary/token-accounting and native browser-menu zoom limits remain those of the original delivery.

No new material finding remains in the two corrected paths. Stop Initiative 9 refinement at this local scope. Initiative 10 — Group Chat remains unstarted and requires its own authorized specification/implementation work.

The owned Synthetic verification hosts were stopped after the browser checks. Disposable evidence roots were retained; no user database was reset.
