# Browser redesign and main integration verification

Date: 2026-10-09. This review integrates browser/tool-awareness branch `6797248ddfb938e04134ce899030569ee32ba743` with main `bd5fa4bd`, preserving both histories. Local main had no independent commits; it was behind origin/main. Existing milestone freezes remain historical; P10/P11 stay unopened.

## Resolutions and corrections

Eight conflicts were reviewed individually. Frontend documentation keeps both the draft-evidence cancellation contract and main's Session-first Activity navigation. Testing and implementation-plan sections preserve the native semantic browser contract alongside Activity and memory close/forget coverage. Both historical background-result ledgers remain intact. Test conflicts retain main's stronger original-result inspection and exact Session retry targeting; equivalent comments preserve the direct-store-before-runtime arrangement.

The first combined API check reproduced three failures: main's newly added memory-reopen fixture still requested retired General Assistant v17. Its creation/reopen requests and the new Activity/memory browser fixtures now use the shipped v21. Retired definitions were not restored. README built-in filenames and current Activity routes were synchronized. The earlier browser comparison ledger now distinguishes its unexecuted fixture repeat from subsequent authorized Real-profile evidence.

Automatically merged prompt code preserves both bounded Core memory receipts and refreshed eligible-tool metadata. Activity retains independent Runs, owner-scoped Session inspection, failed-read action fences and original background results. Protected credentials remain attached/direct-turn, exact-origin, bound Password sinks; discovery does not grant authority or activate every Skill. Strict zero-argument browser.close remains unchanged.

## Combined runtime evidence

- Focused API command: `dotnet test tests/AgentCore.Api.Tests/AgentCore.Api.Tests.csproj --filter 'FullyQualifiedName~BackgroundSessionJourneyTests|FullyQualifiedName~IdentityMaintenanceJourneyTests|FullyQualifiedName~SessionCatalogApiTests' --nologo`: **31 passed** after correction. Includes original-result stability, owner isolation, catalog ordering, SQLite reopen, retained resolved memory and exact-approved forgetting.
- Isolated Synthetic Playwright: Activity, memory semantics, credential/background labeling, durable results, capability projection and Secretary projects: **11 passed**. API 5380, UI 5373, fixture 5391, disposable `/private/tmp/branch-integration-browser.db`; no Real data reset or provider inference. Activity checks include paging, original-result continuation, failed-read recovery and 1440/768/390px containment. Memory close retains report bytes; deletion requires approval.
- Playwright MCP: isolated in-memory Synthetic API 5480/UI 5473. Created General Assistant v21 through Admin, arranged one named Session through supported owner APIs, opened its zero-run detail from Activity, opened Chat, sent a message, observed the completed `Hello from synthetic.` response, returned through Back to Activity, then inspected the exact Chat turn's Run result. Expected outcomes were observed.
- `python3 scripts/tests/test_dev_real.py`: **5 passed** with process-inspection permission. The first sandboxed attempt was blocked by `ps`, not a product failure.

Local logs: `/tmp/branch-integration-api-recheck.log`, `/tmp/branch-integration-playwright.log`, `/tmp/branch-integration-launcher.log`. Full backend/frontend/build checks and all five exact-candidate hosted jobs are finalization gates; their terminal results are recorded in the PR and delivery message rather than transferred from either predecessor branch's passes.

## Full-gate follow-up

The combined full backend run passed **2,974 tests**: Domain 173, Infrastructure 932, Application 1,470, API 395, OrderEvents 4. Twelve explicit opt-in cases skipped; local Infrastructure exercised the Docker cases omitted on hosted runners. Production TypeScript/Vite build passed with the existing chunk-size warning.

The first local frontend invocation selected Node 26.7 through pnpm despite the shell's Node 22.18. Six files/70 cases failed with unavailable native localStorage warnings. Direct execution of Vitest under Node 22 passed those **70/70**, then the complete **101 files/803 tests**. No storage polyfill, application change or assertion removal was made. Logs: `/tmp/branch-integration-frontend.log`, `/tmp/branch-integration-frontend-runtime-recheck.log`, `/tmp/branch-integration-frontend-node22.log`, `/tmp/branch-integration-build.log`.

The first full local browser core run passed **137/138**. The remaining Event-automation test timed out clicking an off-viewport option in the larger catalog left by earlier journeys; the same file passed **2/2** with a fresh catalog. The selector now searches through the supported Event combobox before selecting the exact Event label/key, retaining ingress, deduplication, outcome and navigation assertions. Both tests passed **2/2** against the same populated disposable database from the failed full run. Logs: `/tmp/branch-integration-full-core.log`, `/tmp/branch-event-reproduction.log`, `/tmp/branch-event-populated-recheck.log`. A fresh complete core run and fresh exact-head hosted gates verify this follow-up before PR finalization.

## Real-model boundary

[Real UAT verification](real-uat-browser-model-verification.md) retains the original outcomes: Luna's final cold-profile complete task passed; DeepSeek completed one earlier warm-profile task, while its final cold-profile run logged out but supplied four nonempty malformed close arguments. Operator cleanup is separate evidence. This merge neither claims both models are consistently error-free nor adds a permissive adapter to conceal the model limitation.
