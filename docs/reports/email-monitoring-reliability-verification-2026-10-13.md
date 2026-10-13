# Email monitoring reliability follow-up — 2026-10-13

The four findings in the pasted implementation review are corrected locally on top of `17d6a8b5`. This bounded follow-up does not reopen application milestones or change historical freezes. The owner requested local verification only, reported that the Real profile works, and explicitly waived waiting for CI. No hosted CI or additional real mailbox test was run; the owner report is separate from the Synthetic evidence below.

## Corrections

- Coalesced email evidence retains authorized sources when another account is revoked. Count and causation use the retained sources. InMemory and SQLite occurrence admission validate retained evidence membership/data and current source authority atomically; an entirely revoked bucket is skipped.
- Check now persists a recovery request independently from Paused. Queuing, claiming, reopening and expired-lease recovery do not clear the delivery fence. Failed recovery, including a transient failure after a permanent pause, preserves it. Only successful scan commit clears it.
- Email cursor/Event commit freezes the existing child subscriber identity, revision, filter and dispatch snapshot. Later Automations cannot receive earlier email detections; removed/disabled children still fail current admission checks. Other Core Event source timing is unchanged.
- Instance Credential details reuse the existing Monitoring component, Ant Design Descriptions, detail-label layout and token spacing for read-only account-wide values/status/diagnostics. A canonical link opens global Connections → Credentials for editing. Global monitoring controls retain their existing saved-form behavior.

No schema migration, new event model, provider path or UI kit was introduced. Canonical architecture, ports, Event timing, backend/frontend behavior, persistence, testing and operator guidance were synchronized. Existing unrelated Admin work was preserved.

## Local checks

| Check | Result |
| --- | --- |
| `dotnet test tests/AgentCore.Api.Tests --filter 'FullyQualifiedName~EmailMonitoring|FullyQualifiedName~CoreEvent' --no-restore -v minimal -m:1 /p:UseSharedCompilation=false` | 33 passed, 0 failed/skipped |
| `dotnet test tests/AgentCore.Infrastructure.Tests --filter 'FullyQualifiedName~EmailWatchStoreTests|FullyQualifiedName~CoreEventBucketTests' --no-restore -v minimal -m:1 /p:UseSharedCompilation=false` | 24 passed, 0 failed/skipped |
| In `web`: `pnpm exec vitest run src/features/admin/EmailMonitoringSection.test.tsx src/features/admin/CredentialsSection.test.tsx` | 20 passed |
| In `web`: `pnpm build` | Passed; existing dependency annotation/chunk-size warnings |
| `email-monitoring.spec.ts`, local Playwright runner against isolated API 5095 / Vite 5183 | 4 passed: 1440/768/390/320px |
| Playwright MCP interactions and rendered measurements | Passed at 1440/390px; no console errors or failed monitoring requests |
| `git diff --check` and affected Markdown file-link checks | Passed |

The initial sandboxed .NET attempt could not bind MSBuild IPC sockets. The same local checks passed with approved execution outside that restriction and single-node MSBuild; the stuck original process was stopped. A temporary Playwright configuration reused only the explicitly started disposable Synthetic host and was removed after the run. Both local hosts were stopped. Browser captures are under `local/verification/email-monitoring-reliability/email-monitoring-{1440,768,390,320}.png` (ignored local evidence).

## Observed scenarios

**Mixed accounts:** Detect two account arrivals into one coalesced bucket, revoke either account before the due time, then dispatch. Expected one occurrence containing only the other account: observed in both stores. Revoking both yielded zero occurrences. Submitting the original mixed evidence after revocation was denied by the atomic store fence; dispatcher replay created no duplicate.

**Recovery fence:** Detect a pending notification, induce permanent access failure, queue Check now, dispatch before the watcher, then fail recovery again. Expected zero admitted occurrences and continued Paused authority: observed in both stores. A subsequent transient recovery failure also retained Paused. A blocked successful scan retained the fence while leased and released it only after commit; the already skipped delivery stayed terminal. Store regressions reopened SQLite after queuing/claiming, reclaimed an expired lease and rejected its stale predecessor before a successful recovery.

**Subscription races:** Detect mail with Automation A, then create B before fan-out, optionally cancelling A. Expected only frozen A eligibility (or no admission when A is removed): observed in both stores. A later distinct detection used the updated subscriber set and admitted B.

**Browser ownership:** Create an offline Synthetic Gmail account, enable/save/check/cancel/disable monitoring globally, disconnect and observe Paused, bind the account to an Instance, inspect read-only monitoring and follow contextual Automations/global management links. Expected global controls, no Instance monitoring draft or mutation controls, usable links and no page overflow: observed at all four widths. MCP additionally saved global monitoring, bound a fixture, measured the shared 16px section gap and contained widths at 1440/390, and followed the management link back to editable global controls.

The existing SQLite email-to-Run journey remains among the passing API tests: polling invokes no model, the normal Automation path reads the original Synthetic email through authorized `email.read`, completes its owned Run, suppresses replay and fences later unbound delivery.

## Acceptance boundary

The scoped reliability changes are verified locally. Full repository/Compose suites and hosted gates were not rerun for this small follow-up; no new milestone or hosted acceptance is claimed. Real Gmail/IMAP operation was not independently exercised here, per the owner's instruction.
