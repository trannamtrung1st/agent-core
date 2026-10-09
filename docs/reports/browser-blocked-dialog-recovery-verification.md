# Final native browser reliability and blocked-state recovery

User-requested focused review after `195d53df` on `develop/branch-1`. Concurrent capability-discovery work was preserved and committed separately as `bc0a7e94`. No browser redesign, additional tool, new time budget, model-specific policy or P10/P11 work.

## Historical Run diagnostics

Read-only, sanitized inspection of the existing local SQLite diagnostics found Run `01a11f74-c4e1-7ff1-9afd-822dfd82c879`. Its terminal Run is Cancelled; the corresponding assistant entry is Interrupted with `InterruptReason=userSteer`, without a failure reference. This is not step-limit or timeout evidence.

The five dialog calls were valid `operation=inspect`: the first returned `provider_unavailable` before navigation established a context; the later four returned `dialog_missing`. No accept/dismiss was requested, no dialog-resolution timeout/cancellation was recorded, and no retained tool receipt contained `dialog_pending`. Four later successful finds and seven failed finds match the supplied counts; an additional pre-navigation find returned `provider_unavailable`. Later search errors were one `ambiguous_target` and six `not_found`, interspersed with successful observations/actions. The malformed configuration call returned `invalid` before navigation. No credential values, application content, query values, URLs or raw checkpoints are copied here.

Those records do not prove the reported native blocker or a provider resolution defect. They also do not justify grouping unrelated searches into a global failure limit. Any unrecorded dialog after the final checkpoint remains unverified.

## Reproduced defects and minimal corrections

1. A genuine native confirmation returns `dialog_pending`, but ordinary page tools remained offered and the evidence tracker ignored unsuccessful receipts. Valid repeated searches could continue against an inaccessible page. Existing `BrowserEvidenceProgress` now derives the blocker and equivalent-failure counts from durable receipts, filters projection and refuses valid ordinary browser calls before provider dispatch. It preserves already-authorized dialog, closure and configuration operations. Repeated matching recovery failures suppress that operation before another native dispatch while preserving a different valid recovery action. Three equivalent blocked attempts, or three matching failed recovery operations/refusals, enter existing tool-free finalization with `browserBlocked`. Different operations/errors and malformed calls do not combine. Ordinary unsuccessful exploration without an observed modal is unchanged.
2. Dialog resolution did not participate in the adapter's existing active-action cancellation fence. A delayed native acceptance could survive cancellation. Resolution and its pending originating action now use that fence; cancelled/timed-out pending work closes/replaces its page before later operations can dispatch.
3. The adapter retained an inspectable dialog after its page closed. Closed dialog pages now clear retained modal/action state. Resolving an externally handled modal returns truthful `dialog_missing` rather than retaining a failed strategy indefinitely.
4. An originating action opening a second dialog remained pending while the first resolution waited for it, producing a timeout. Resolution now preserves a new modal and races the originating action against the existing dialog signal. The next decision is returned explicitly; the triggering click is not replayed.

Verified resolution, missing modal or confirmed closed context and confirmed cleanup clear the old condition. An explicit resolution that reveals another modal starts a fresh bound. Same-Run restoration rebuilds state from receipts; independent Runs start clean. Capability/origin/credential checks, approval and generation fences remain unchanged. No modal is automatically accepted.

The existing finalization deadline calculation preserves Standard/unattended deadlines when their reply reserve is zero; interactive finalization still uses at most its existing 30 seconds and never extends the hard deadline. This boundary has dedicated budget coverage. Existing blank-argument close canonicalization also clears the receipt-derived blocker after confirmed closure.

## Previously solved behavior retained

- `27a98478`: required find `by`/`value`, authorized discovery/projection, invalid-call circuit breaker and trusted current/prior execution evidence.
- `ddfdaaa6`: unchanged 300-second interactive budget, 30-second final reply reserve, safe failure facts and durable action receipts.
- `195d53df`: actionable modal guidance, exact already-authorized dialog recovery projection, confirmed closure/profile ownership, 60-second cleanup reserve, independent sign-out verification and terminal-progress fencing.
- Existing `browser.get_config` empty-object validation rejects malformed input before provider execution. New coverage verifies this without a production validation change.

## Verification evidence

Before correction, all three new owned native Run cases failed their projection/completion expectations. Native cancellation and closed-dialog inspection regressions both failed on the baseline. The additional successive-dialog case reproduced a 26-second timeout before its correction. Logs: `/tmp/dialog-before-single.log`, `/tmp/dialog-native-before.log`, `/tmp/dialog-successive-before.log`.

After correction, the owned Run executes a single sign-out trigger; it resolves and independently observes Sign in when authorized, or returns a truthful blocked/unverified partial answer. Repeated ordinary calls are refused and bounded. A separate case suspends through `execution.wait`, resumes the same Run/Response/attempt under a fresh claim, restores the blocker and finishes without replay: the native DOM trigger counter remains one. Native cases exercise stale/external resolution, cancellation, a second prompt after the first confirmation, and closure/reopening. Existing Chromium alert/confirm/prompt, redaction, failed resolution retry and persistent-profile cases remain in the affected suite.

| Check | Result |
| --- | --- |
| Focused owned native Run, checkpoint, malformed configuration and userSteer acceptance | 10 passed (`/tmp/dialog-final-acceptance.log`); final strategy/checkpoint/unchanged-evidence subset 15 passed (`/tmp/dialog-strategy-final.log`) |
| Focused native lifecycle | 4 passed (`/tmp/dialog-native-focused.log`); existing native cleanup/redaction cases also passed in the earlier focused run |
| Full backend | Passed: Domain 173; Application 1,434 / 3 opt-in skipped; Infrastructure 924 / 7 skipped; API 390 / 2 skipped; OrderEvents 4. Final Application 1,436 / 3 skipped; deadline-boundary rerun in progress (`/tmp/browser-blocked-application-deadline-final.log`) |
| Frontend unit | 769 passed / 1 credential-dialog timing failure under two workers; its isolated file passed all 4 cases. Single-worker run: 769 passed / 1 different Admin timeout; that isolated case passed in 4.8 seconds (`/tmp/browser-blocked-admin-recheck.log`). The prior behavior 389f9aa5 full hosted frontend gate passed |
| Frontend production build | Passed; existing bundle-size warning (`/tmp/browser-blocked-web-build.log`) |
| Isolated Synthetic Playwright | 123 passed / 1 unrelated Admin selection failure. Fresh-data diagnostics + Admin rerun: 11 passed (`/tmp/browser-blocked-synthetic-recheck.log`), including the native sign-out/closure/final reply/reload case |
| Exact-behavior hosted CI | Final deadline-boundary behavior commit pending; prior behavior 389f9aa5 backend/frontend/acceptance/Compose gates passed in workflow 37899700812 |

Commands use `dotnet test ... --nologo -m:1 -nr:false -p:UseSharedCompilation=false`. Single-process MSBuild avoids unavailable local build-server pipes. Frontend uses `NODE_OPTIONS=--no-experimental-webstorage pnpm run test --run --maxWorkers=2`; the initial run without that documented override encountered Node/jsdom storage-global failures and is not acceptance evidence. The CLI Synthetic suite uses `/tmp/browser-blocked.playwright.config.mts`, ports 5380/5473, fixture 5391, and disposable `/tmp/browser-blocked-ui` database/workspace/artifact/key roots. It does not reset existing Sessions or profiles. No paid model, AHI authentication or private credential is required.

## Remaining scope

External AHI behavior and its claimed unrecorded modal remain unverified. A stale modal dismissed outside the adapter may still be reported by inspection until a resolution attempt returns `dialog_missing`; the public native dialog interface does not expose a handled-state query. The tested resolution path safely clears that stale reference. Broader agent ergonomics, unrelated valid-but-unsuccessful search strategies, Browser redesign and PR/main reconciliation remain outside this focused correction. Acceptance is not closed until required hosted jobs pass on the final behavior SHA.
