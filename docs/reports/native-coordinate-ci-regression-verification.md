# Native coordinate CI regression follow-up

## Scope and original failure

The [main workflow on bc3452de](https://github.com/trannamtrung1st/agent-core/actions/runs/38101589357) failed `NativeBrowserJourneyTests.Native_forms_keyboard_visual_target_and_pending_action_cancellation_recover` at its coordinate-click success assertion. Infrastructure reported 1 failed, 1,162 passed and 15 skipped; the other six hosted Synthetic jobs passed. The original failure log has no screenshot admission/freshness diagnostics, so it cannot establish whether authority was withheld initially or expired afterward.

This is a bounded native-browser test correction, not a new milestone or architecture change. Production coordinate validation, screenshot privacy, settlement budgets and replay fencing are unchanged. Concurrent composer changes are excluded from this fix's isolated checkout and commit.

## Confirmed defect and diagnostic boundary

The test checked only screenshot `ErrorCode == null`, then treated its snapshot ID as coordinate authority. A successful image can intentionally carry context only. The test therefore had a missing positive precondition. A native mutation barrier reproduces the same `stale_visual_evidence` result without any mouse effect: screenshot delivery succeeds, settlement succeeds, but the visual state changes during collection and admission is withheld.

Safe observed receipt for the withheld-admission scenario:

```json
{
  "coordinateEvidence": false,
  "observationUnavailable": false,
  "coordinateEvidenceUnavailableReasons": ["visual_state_changed_during_capture"],
  "captureDiagnostics": {
    "settlement": {
      "settled": true,
      "observationAvailable": true,
      "domChangeSamples": 0,
      "peakInflight": 0,
      "lastInflight": 0
    },
    "observerAvailable": true,
    "inflightAtCapture": 0,
    "fontsLoading": false,
    "runningAnimations": 0,
    "pageChanged": false,
    "viewportChanged": false,
    "visualStateChanged": true
  }
}
```

A separate scenario first obtains admitted evidence, then changes layout. Native dispatch rejects the old image with `EffectAttempted=false`; the fixture click count remains zero. A fresh admitted image authorizes exactly one click. Reusing its snapshot ID is rejected and cannot increment the count again. These observations distinguish initial admission denial from later expiry. The historical CI event's precise branch remains unproven: unmodified local repetitions did not spontaneously reproduce it, and its original log omitted the needed diagnostics. Controlled reproduction is not evidence that the CI runner experienced that exact mutation.

## Change

`NativeBrowserJourneyTests.cs` logs each capture's content-free admission/reason/settlement diagnostics and pre-mouse structural freshness information: observer DOM/visual generation counts, fetch/XHR count, fonts/animation state and viewport/scroll values. It logs no page text, URL, resource name, credential, raster bytes or private fingerprint.

The positive journey now uses at most three explicitly logged read-only captures within a shared 20-second cancellation deadline, requiring `coordinateEvidence=true`, a settled matching observation and no unavailable reasons. It calculates the target box after admitted capture. The mouse call is made once and its failure remains visible; it is never retried. Normal and scroll-requiring viewports remain covered. A controlled first-capture mutation exercises reacquisition without granting authority to the rejected image.

Separate coverage verifies withheld admission, post-admission staleness, fresh-capture recovery and consumed-image replay denial using actual Chromium and an observable fixture click counter. No existing privacy or validation assertion was relaxed.

`docs/16-testing-strategy.md` records these bounded acquisition and diagnostic expectations. This report owns verification evidence.

## Review of bc3452de's delayed navigation test

No edit was required. The route waits for top-level DOMContentLoaded before releasing the marker response. The immediate branch requires automatic settlement, zero instrumented in-flight requests and the marker in the returned observation. The held branch requires an unsettled observation, a still-pending request and absence of the marker, then releases the response and verifies the marker plus successful explicit stable observation. The route barriers use 10-second waits; native navigation and stable observation retain their operation/observation deadlines, and the locator wait has Playwright's finite default timeout. Finally releases the barrier, detaches the event handler and closes the Session page. Existing stalled-read and caller-cancellation cases continue to verify deadline behavior.

## Verification ledger

Results are recorded after execution below. No live model/provider calls are used. Hosted seven-job acceptance is a separate gate and is not claimed by local results; the user requested not to wait for CI this turn.

- Baseline: eight native repetition commands passed while a complete Infrastructure run executed in parallel. One final repetition picked up the additional viewport theory; every executed case passed. Logs: `/private/tmp/native-ci-before-repeat-1.log` through `-8.log`. Instrumented baseline full suite: **1,169 passed, 9 opt-in skips, 0 failed; 1,178 total**, in 8.18 minutes (`/private/tmp/native-ci-before-full.log`). The isolated initial run and normal/scrolled/100× CPU-throttled macOS probe also passed.
- Linux baseline reproduction: disposable .NET 10 SDK Debian Linux ARM64 container, constrained to two CPUs, running native Chromium from the committed `bc3452de` source plus diagnostic instrumentation. **3 passed**: 720px viewport, 400px viewport requiring scroll, and 720px viewport with 100× renderer CPU throttling. All three admitted coordinates with empty unavailable reasons and unchanged visual-state diagnostics. This differs from hosted Ubuntu x64 and did not reproduce the spontaneous CI failure. Log: `/private/tmp/native-ci-linux.log`. Source/build isolation and Playwright CLI setup required repair before execution; setup failures are not product failures. The container was automatically removed afterward.
- Initial focused fixture/settlement/deadline run: **19 passed** (`/private/tmp/native-ci-fixed-focused.log`).
- Final repeated focused filter: `FullyQualifiedName~Native_forms_keyboard_visual_target_and_pending_action_cancellation_recover|FullyQualifiedName~Unadmitted_capture_and_expired_admitted_capture|FullyQualifiedName~Navigation_includes_content_that_arrives_after_the_document_loads|FullyQualifiedName~BrowserPageSettleDeadlineTests`. Five sequential `dotnet test tests/AgentCore.Infrastructure.Tests --no-build --no-restore --filter '<filter>' --logger 'console;verbosity=detailed'` executions against `/private/tmp/native-ci-final`: **10 passed per run, 50 passed total**. A separate complete suite ran concurrently. Every controlled withheld-first-capture case logged an unavailable first image followed by admitted capture attempt two and one successful mouse action. No action was retried. Both delayed-navigation branches and failed/malformed polling, stalled-read deadline and cancellation checks passed in all five runs. Logs: `/private/tmp/native-ci-final-repeat-1.log` through `-5.log`.
- The final checkout is a `git archive` of main plus only the changed native test. This excludes unrelated ongoing workspace edits. Its full-suite command is `dotnet test tests/AgentCore.Infrastructure.Tests -m:1 /p:UseSharedCompilation=false --logger 'console;verbosity=detailed'`; native execution requires local build-pipe/browser/socket permissions. **1,172 passed, 9 opt-in skips, 0 failed; 1,181 total**, in 8.76 minutes (`/private/tmp/native-ci-final-full.log`). This includes the complete existing privacy, coordinate freshness, cancellation, deadline and replay suites.

- `git diff --check`, staged diff checks, report fence/JSON validation and tested-source hash comparison: **passed**.

## Hosted acceptance and remaining limitation

The follow-up is committed on main and pushed to start the existing seven-job Synthetic workflow. No hosted green status is claimed in this report: the user explicitly requested not to wait for CI this turn. Required successor gates remain Synthetic backend, both frontend shards, both Playwright core shards, Playwright acceptance and Compose smoke on the final commit. The original CI admission-versus-expiry branch remains unconfirmed; a future failure now carries safe diagnostic evidence to distinguish those mechanisms. This corrects and locally verifies the test contract without claiming a reproduced spontaneous hosted defect or a feature freeze.
