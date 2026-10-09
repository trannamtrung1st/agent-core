# Browser cleanup lifecycle verification

Authorized Pump 002 review follow-up on `develop/branch-1`, after reply finalization `ddfdaaa6`. The user requested all listed enhancements and explicitly deferred CI. This does not reopen the native migration freeze or P10/P11.

## Implemented behavior

- `dialog_pending` explains the native decision and directs explicit inspect/accept/dismiss without replaying the triggering action. Core projects only the already-authorized eligible `browser.dialog` capability through the fenced Run mailbox. This exact recovery projection does not consume or bypass authority through model discovery; ordinary discovery still has its eight-call cap. Dialog messages remain wholly redacted. Unknown/unrelated confirmations are not automatically accepted.
- Explicit `browser.close` requires native context-close confirmation. `close_failed` or `close_uncertain` preserves the owned live context and profile lease until actual native closure. Best-effort shutdown remains separate; profile reset refuses to delete a context with unconfirmed closure. Caller cancellation remains cancellation; an internal native close timeout returns truthful uncertainty, with observed late close completion releasing ownership.
- At 210 active seconds, the work model call yields to a checkpointed cleanup phase inside the same 300-second budget. Sixty seconds precede the existing final 30-second reply reserve. Only authorized find/snapshot/dialog/click/tabs/close operations remain available, with non-cleanup pending calls durably refused. Per-tool bounds can end cleanup earlier to preserve reply headroom. Approval waits pause all active clocks; generation, cancellation and side-effect fences remain intact.
- Core guidance requires a post-sign-out application observation independently of closure. A persistent profile can still authenticate after a confirmed close. Safe action receipts never claim authentication or sign-out from a click or window closure. Dialog decisions and application-specific sign-out evidence remain model-guided; Core does not invent a generic authentication detector.
- Runtime terminal state rejects active progress after the response pointer clears. The client also rejects late progress for completed/failed/interrupted history restored without tombstones, preserving the separate ephemeral progress surface and terminal transcript.

## Runtime evidence

Actual Chromium cases verified pending confirmation blocks snapshot, dialog type/message redaction, failed resolution retaining the dialog, dismissal retaining authentication, acceptance exposing the sign-in control, confirmed native closure and repeated already-closed. Persistent close failure and uncertainty retain the same context and prevent reset. Reopening after close without sign-out still exposes authenticated state. A stalled close returns bounded uncertainty, then late native confirmation releases the context.

Owned SessionRuntime cases performed sign-out, observed the sign-in control before closure, and returned a completed result near the deadline. Authorized dialog capability was projected without a discovery call; a Definition without that grant returned a truthful partial result and could close without claiming sign-out. Separate deterministic cases exercised the 210-second held-provider cutoff, exhausted discovery budget and generation rejection. This is local native/browser/runtime evidence, not a replay of the external AHI workflow.

## Checks

- Full Application suite: **1,407 passed / 3 opt-in skipped** after cleanup cutoff and Synthetic journey changes.
- Full Infrastructure suite: **919 passed / 7 opt-in skipped**. Final native cleanup, dialog redaction and native journey subset: **14 passed**, including the subsequently added stalled-close case (920 current Infrastructure cases exercised across these runs).
- Full Domain suite: **173 passed**. Finalization/coordinator focused boundary suite: **53 passed**.
- Frontend state suite: **52 passed**; production build passed.
- Isolated Synthetic Playwright `diagnostic-details.spec.ts`: **9 passed**, including native sign-in, sign-out confirmation, verified login control, close, final reply and reload.
- Playwright MCP independently sent the cleanup marker through Riley v20, observed verified sign-out text and the separate `Browser closed` receipt, and reloaded terminal history. No failed reply or late Still working text was shown; console errors were zero. Safe click receipt continued to say submission was unconfirmed, independently of the observed sign-out outcome.

## Verification setup correction and remaining limits

The first UI host used the wrong SQLite option name (`SqlitePath` instead of `ConnectionString`) and opened the repository database. The attempt produced seven passing and two failing UI cases, which are not counted as isolated acceptance. Precisely its five new synthetic test Sessions were deleted through the normal lifecycle API (five HTTP 204 results); no catalog/database reset was performed. The host was stopped and restarted with `Persistence:ConnectionString=Data Source=/tmp/browser-cleanup-ui/synthetic.db` and isolated workspace/attachment/artifact roots. The new database was verified to have zero Sessions before the passing rerun.

Hosted CI and PR/main conflict reconciliation remain deferred as requested. No additional paid-provider calls or external AHI sign-out/mutations were performed. The supplied review does not include the diagnostic trace for its particular close failure or late progress ordering, so those specific historical causes remain unconfirmed. Existing immutable Definitions, credential protection, origins, native `IBrowser`, and Session ownership are preserved.
