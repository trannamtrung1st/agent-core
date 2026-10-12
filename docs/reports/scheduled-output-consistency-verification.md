# Scheduled output consistency verification

Date: 2026-10-12. Baseline: main `6acfc8bd9bf1115d08199c8a770ac9f6c1ea625a`. This is a bounded unified-Automation prompt correction; historical freezes and P10/P11 remain unchanged. CI waiting is explicitly waived for this request.

## Finding and correction

The existing-session Automation path already appends persisted instructions as the last task. However, prior chat is also replayed as native User/Assistant messages. The historical Real result in the [greeting report](greeting-scheduling-authorization-verification.md) demonstrates the model answering intervening negative chat instead of greeting. Five pre-fix prompt regressions reproduced the missing history boundary; they are structural evidence, not a claim that every model deterministically fails.

Authored Schedule, Event, Core Event and manual occurrences now receive selected chat as one serialized historical-evidence block, with a fixed instruction that history cannot replace or suppress the admitted task. The persisted instructions remain the current task. Summary boundaries, delivered/heard prefixes, newest-20 selection and the existing 24,000-character history budget are preserved, including serialization overhead. Current Chat and native initiative retain their original message roles. No owner, permission, trigger, approval, timeout or retry boundary changed. Legitimate edits and cancellation continue through existing authorized Automation commands.

## Deterministic verification

Initial prompt regression: **17 passed** after five failures before correction. The SQLite task-isolation journey: **1 passed**; creates/persists a greeting Automation, reopens storage, executes three slots with contradictory and unrelated Chat between them, observes two actual greetings and then a welcome after an authorized owner edit, and observes no fourth execution after disable. Repeated scans do not create duplicate occurrences. The model fixture reproduces historical-instruction dominance when the contradictory turns appear as native conversation messages; live acceptance separately checks actual provider behavior.

Broader regressions on the isolated baseline: full Application **1,741 passed / 8 opt-in skips**; API reliability/destination/continuity/readiness filter **76 passed**. Bounded Real-profile results are recorded in the delivery follow-up after execution.

## Verification isolation

Concurrent unrelated Email edits arrived in the shared checkout and temporarily failed compilation (`EmailAccountService` symbol resolution and an Email test syntax error). They were left intact. A managed isolated worktree at the committed baseline plus only this fix supplies the runtime evidence. The scheduling commit excludes those outside edits.


## Completed delivery and bounded Real acceptance

Behavior commits: `03b182bcceac41639d1ca669d942433a8ad5d2ba` (history evidence boundary) and `846e00ad1c21e075f3279591cd26bae8a5e0e000` (task scope/format/length). The first Real attempt still greeted but added unwanted commentary about earlier rejected cancellation; it is retained as partial evidence, not acceptance. The final scope instruction explicitly forbids answering historical requests or adding unsolicited administration/past-action commentary while retaining honest reporting of current blockers.

Final local checks on the isolated source: full Application **1,741 passed / 8 opt-in skips**; API reliability/destination/continuity/readiness **76 passed**; prompt matrix **17 passed**; exact API build **0 warnings / 0 errors**. Commands use `TMPDIR=/private/tmp DOTNET_CLI_USE_MSBUILD_SERVER=0`, `dotnet test tests/AgentCore.Application.Tests --no-build --no-restore -m:1 --nologo`, and `dotnet test tests/AgentCore.Api.Tests --no-restore -m:1 -p:UseSharedCompilation=false --nologo --filter 'FullyQualifiedName~ReliabilityJourneyTests|FullyQualifiedName~AutomationDestinationJourneyTests|FullyQualifiedName~ContinuityBoundaryTests|FullyQualifiedName~SchedulerReadinessTests'`. The initial focused API task-isolation case passed separately before broadening coverage. All 153 relative file links in the affected specification set resolved.

The approved Real-profile run used General Assistant v24, configured OpenRouter `deepseek-v41-flash`/low, disposable SQLite and the committed UI in the managed isolated worktree. Before live actions, `/health` reported Real, Scheduler Ready and `1.0.0+846e00ad1c21e075f3279591cd26bae8a5e0e000`. No provider timeout, retry limit or generation budget changed. The credential was supplied only through the child process environment without printing it.

Playwright MCP submitted exactly `greet me every 60 seconds`. The model's first tool call had an invalid background-only reporting flag; Core rejected it, and the model corrected it within the existing limits. Automation `01a12dcb-df7a-7d1a-9cc1-7382889c63fc` targeted original Session `e6f60860-80b1-4e04-a0e7-fd72feefaac8`. The test owner bounded it to three firings and matched its Instructions to the initial attempt before any admission: `Send a brief, friendly greeting to the user in the current session.`

After the first greeting, MCP submitted `don't greet me every 60 seconds`. The attempted foreground disable remained blocked by the unchanged authorization path; persisted instructions and active status remained intact. The next scheduled response was a brief greeting, without the former cancellation explanation. The third capped firing happened before the unrelated message was submitted. MCP then sent `The kettle is blue. For this chat response, say only noted.`; the foreground Chat replied `noted`. The owner re-enabled one additional eligible schedule slot with the same Instructions, maxOccurrences=4 and a 70-second end bound. That occurrence greeted rather than answering the historical Chat instruction. The inactive interval was not backfilled.

Observed scheduled results, confirmed in UI and persisted completed Run results:

1. `Hey there — hope your day's going well! 👋`
2. `Hi again — just checking in with a quick hello! 👋`
3. `Hello again! 👋 Hope you're having a good one.`
4. `Hey! 👋 Just popping in with a quick hello — hope things are going smoothly.`

All four have distinct occurrence IDs and due slots. The first three due slots are exactly 60 seconds apart; the final slot is after owner reactivation. Each result fulfills the greeting task and omits prior-action/status commentary. The fixture reached Completed/disabled, and only the review-owned API/Vite processes were stopped. The successful journey had zero console errors after reload; relevant Automation reads and edits returned HTTP 200. Initial stale-capability bootstrap on the replacement disposable deployment recovered before the journey.

Safe evidence is retained in `/private/tmp/automation-output-final-live.json`; the partial first attempt is `/private/tmp/automation-output-initial-live.json`. This qualifies the scoped greeting consistency case for the configured model, not all providers or generic model behavior. The separate read-only `authorization_classifier_invalid_output` follow-up remains fail-closed and is not changed here. CI is triggered by the push but deliberately not awaited, per the user's explicit instruction. No code changed after the successful Real acceptance; the final follow-up commit records documentation/evidence only.
