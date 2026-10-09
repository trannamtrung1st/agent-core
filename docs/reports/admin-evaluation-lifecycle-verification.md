# Admin evaluation request lifecycle verification

Date: 2026-10-09. Branch: `develop/branch-1`. Baseline: `17ced42bc4dff669df0a45fc4cd69edbb7b9802b`. Status: closed/verified on behavior `345e8f7a1b31a336964e78b00be7fce8d88f9e4b`.

## Failure and reproduction

[Native hardening CI on `ab118790`](https://github.com/trannamtrung1st/agent-core/actions/runs/37872793696) passed backend, frontend, acceptance and Compose, but core Playwright finished 125/126. The Skills authoring journey received 404 on both draft evaluation endpoints after draft publication. That run blocked final migration acceptance despite the passing configured-model native journey; the corrected-SHA results below close the gate.

The scenario-save path starts an explicit evidence reload and updates the active draft revision, which also starts a reload. Generation counters suppress stale React updates but did not cancel the earlier network reads. A newer pair can settle and make publication eligible while the older pair remains pending. The strengthened existing `web/e2e/p8-skill-journey.spec.ts` holds the first pair after scenario save, allows the later evidence to settle, publishes, and only then releases the held requests. Before the fix, both held reads produced HTTP errors for the consumed draft and zero reads were canceled. The controlled run failed its cancellation assertion. The original CI log identifies the same two endpoints' 404 status. The route timing proves requests can reach the server after publication; this is a request lifecycle defect rather than a native browser provider failure.

## Correction and boundaries

Evaluation list API helpers accept an AbortSignal and pass it through the existing owner-capability fetch. The panel aborts superseded loads and invalidates their generation; revision changes and unmount cleanup cancel old reads. The parent editor has a synchronous pause barrier before publish or active-draft deletion, so cancellation precedes the consuming POST/DELETE rather than relying on a later React effect. Editor closure also cancels immediately. A failed consuming action resumes fresh reads without clearing its diagnostic.

No backend endpoint, publish validation, expected revision, required Synthetic evidence or native browser code changed. Deleted drafts retain 404. The E2E console-error assertion is unchanged. The request-failure collector distinguishes only `net::ERR_ABORTED` evaluation GETs from unexpected failures; it explicitly requires cancellation of both deliberately held requests. No HTTP 404 is ignored or converted to success.

## Runtime and local evidence

- Controlled `p8-skill-journey.spec.ts --project=synthetic`: passed after correction (33.6 seconds). Both held reads canceled; authoring/publication, published Skills, exactly one Synthetic response through reload, durable Always-only Skill pins and archival all passed with no unexpected console errors.
- Existing affected Admin unit filter: 45 passed. Final panel cancellation/recovery filter: three passed, including draft replacement, late completions, synchronous pause/resume and unmount.
- Final production frontend build passed. The first full local core run completed 121/126; five existing Admin failure collectors reported intentional evaluation aborts. The shared narrow classifier now covers these consumers, and ten focused authoring/publication cases passed. The final complete core run passed 126/126 in 15.0 minutes on behavior `345e8f7a1b31a336964e78b00be7fce8d88f9e4b` with fresh disposable hosts/data.
- The broad local frontend run encountered Event creation and version-inspection timing failures while overlapping browser runs and was stopped; no unrelated product code, assertions or test deadline changed. A subsequent isolated run passed all nine cases across EventsSection, DefinitionVersionsTable and the final publish-gate cancellation/recovery test. Fresh Synthetic CI subsequently passed the complete frontend suite: 764 tests across 99 files, plus the production build.
- Playwright MCP on separate disposable Synthetic hosts (API 6580, UI 6573): created `admin-evidence-mcp`, saved instructions, created a required ToolNotOffered scenario, validated current revision, ran Synthetic, observed publish eligibility, published v1 and returned to Versions with the editor closed. All API responses succeeded. Only the existing Ant Design List deprecation console notice appeared. Both test-owned MCP hosts were stopped afterward; existing user hosts/data were untouched.

Local reproduction used isolated ports 6480/6473 and fixture 6491 with SQLite and workspace roots under `/tmp/admin-evidence-repro`; corrected focused data used `/tmp/admin-evidence-fixed`. The first core run used API ports 6480–6482, UI 6473–6475, fixtures 6491–6493 and `/tmp/admin-evidence-core` roots. The final run used API 6180–6182, UI 6173–6175, fixtures 6191–6193 and `/tmp/admin-evidence-core-final` roots. The exact runner commands are `pnpm exec playwright test e2e/p8-skill-journey.spec.ts --project=synthetic`, then `pnpm exec playwright test --project=synthetic --project=browser-stt --project=browser-browser`, with CI and these isolated environment overrides. Logs: `/tmp/admin-evidence-repro.log`, `/tmp/admin-evidence-fixed.log`, `/tmp/admin-evidence-core.log`, `/tmp/admin-evidence-frontend.log` and `/tmp/admin-evidence-build.log`.

The configured Real-model journey remains passed against unchanged native browser behavior; this UI correction made no additional paid-provider requests. All five Synthetic jobs passed on corrected behavior `345e8f7a1b31a336964e78b00be7fce8d88f9e4b`; the native cutover acceptance gates are closed. PR #4's merge conflicts with main remain separate integration work.

## Exact-SHA hosted acceptance

[Workflow 37876708714](https://github.com/trannamtrung1st/agent-core/actions/runs/37876708714) completed successfully on `345e8f7a1b31a336964e78b00be7fce8d88f9e4b` in its first attempt on 2026-10-09. All five jobs passed:

| Gate | Result |
| --- | --- |
| Backend | 2,833 passed: Domain 173, Infrastructure 910, Application 1,360, API 390; 17 explicit opt-in skips |
| Frontend | 764 tests across 99 files and production build passed |
| Playwright core | 126/126 passed in 21.6 minutes; one worker, zero retries |
| Playwright acceptance | All 16 cases passed across seven phase journeys |
| Compose | Owner-capability path and SQLite volume survival passed through recreation |

The final local core log is `/tmp/admin-evidence-core-final.log`; hosted evidence is `/tmp/admin-evidence-ci-final.log`. Closure bookkeeping changes documentation only and does not move the verified behavior SHA. The configured-model journey uses unchanged native/backend code from `ab118790`; no additional paid calls were made for this Admin correction. PR #4 remains unmerged with separate main-branch conflicts.
