# Explicit greeting scheduling authorization verification

Date: 2026-10-12. Baseline: main `9d664ca6398b3c250f88f6fd992da9342c2ee243`. Focused successor on unified Automation; historical freezes and P10/P11 are unchanged.

## Findings and correction

The old `TimedImperative` and `RecurringIntervalImperative` vocabularies omitted `greet` and handled compact numeric versus implicit singular timing differently. An exact request therefore reached the model classifier and failed on unavailable providers. The pre-fix throwing-classifier regression reproduced six failures among thirteen cases, including greeting, capability-question and historical forms.

`TimedScheduleIntent` replaces the duplicated timed patterns with one shared action set and bounded timing grammar. Timing must occur at a command edge, positive numeric values must fit an integer, and the text must contain an affirmative action with an operand. Polite prefixes/suffixes and either action order work. Conditional, quoted or oversized text does not gain deterministic authority from this recognizer. Existing nonnumeric scheduling, contextual continuations and genuinely ambiguous requests retain their existing classification path. The agent-directed recognizer is anchored so historical mentions cannot authorize through a substring. A recognized timed phrase has one authorization gate: conditional or quoted variants cannot fall through to the older broader directive pattern (two pre-fix regressions reproduced this bypass).

Explicit negation, capability questions and historical/informational prefixes deny before a classifier invocation. Read-only list questions retain their prior behavior. Authorization consumes current user text and does not infer permission from generated arguments. Owner, trigger-origin, capability, schedule policy and minimum-interval checks remain independent.

Classifier provider failures remain `authorization_classifier_unavailable`. Invalid output now yields `authorization_classifier_invalid_output`, including wrong JSON shape/token, a normalized InvalidResponse failure, oversized output and missing/truncated completion. Partial JSON followed by provider failure cannot authorize. Diagnostics contain fixed safe codes and messages, never provider bodies. No timeout or retry limit changed.

## Runtime evidence

Four new SQLite integration journeys submit the exact requested phrases through Session Runtime and ordinary Automation creation with a classifier that throws if invoked. Each persists a 60-second recurring Automation in the original Session, reopens SQLite, schedules and executes two consecutive due slots, and observes one completed occurrence and greeting per slot. Repeated scans/intake/execution at each slot produce no duplicate. The classifier invocation count stays zero.

Runtime negative cases submit negation, the capability question, historical mentions and unrelated text despite model-proposed creation arguments, and observe zero Automations. Four ambiguous cases expose distinct unavailable, invalid, ambiguous and denied tool results and also create nothing. The existing draft, policy, owner, continuation and background completion/restart cases remain exercised.

## Verification and delivery boundaries

- Scheduling Application regression command: `TMPDIR=/private/tmp DOTNET_CLI_USE_MSBUILD_SERVER=0 dotnet test tests/AgentCore.Application.Tests --no-restore -m:1 -p:UseSharedCompilation=false --nologo --filter 'FullyQualifiedName~Trigger|FullyQualifiedName~ScheduleDraft'`: **195 passed**.
- SQLite/runtime/readiness regression command uses the same build options with `tests/AgentCore.Api.Tests` and filter `FullyQualifiedName~ReliabilityJourneyTests|FullyQualifiedName~SchedulerReadinessTests`: **31 passed**, including nine scheduling, seven denied/draft, four classifier-diagnostic, eight background, two busy-parent and one readiness cases. A final 13-case scheduling/classifier rerun covers the later bounded grammar corrections.
- CI must be checked on the pushed candidate's exact SHA. The existing Synthetic workflow supplies backend, frontend, browser core/acceptance and Compose gates; dispatch or a previous green commit does not establish acceptance.
- The earlier outside-session frontend edits already belong to main (`8d036114`, `787508c5`, and the combined `9d664ca6` change). They remain included; the final candidate inherits their regression coverage.
- No API host was reachable on local development ports 5080/5280 at investigation start. Running deployment verification requires reading the loaded assembly's `/health.buildVersion`, comparing its embedded source SHA with the pushed candidate, and exercising the request on that deployment. No hosted URL was supplied; the user subsequently authorized bounded testing with the Real profile. A disposable Synthetic deployment and CI are separate evidence from the originally failing hosted/provider deployment.

The integrated model fixture performs real Core admission, storage, scheduling and execution without provider credentials. It proves deterministic authorization and durable occurrence behavior; it does not qualify a hosted model's ability to generate Automation arguments. Live model qualification remains an explicit opt-in check, separate from key-free CI.


## Authorized Real-profile acceptance

The user explicitly approved Real-profile testing after the initial local pass. The configured OpenRouter credential was available in repo-local `.env` (it was not in process environment or user-secrets); it was loaded without printing it. A separate Real API on 5380 and Vite on 5383 used disposable SQLite/workspace/artifact roots under `/private/tmp/agent-core-greeting-real-f1757384`. Before live actions, `/health` confirmed Real, Scheduler Ready and loaded behavior `1.0.0+f1757384cd279795e81b0b494fda7f7d29f5309d`. This verifies the new local deployment, not an unspecified external host.

Playwright MCP provisioned General Assistant v24, selected configured `deepseek-v41-flash`/low and submitted exactly `greet me every 60 seconds`. One persisted Active Automation had CurrentUserTurn authorization, a 60-second fixed interval and the original Session destination. The UI confirmed creation and later displayed the first scheduled greeting. SQLite inspection observed two distinct completed occurrence Runs at due slots exactly 60 seconds apart, one Run per slot. The second Run honored the intervening negative message by explaining the still-active schedule instead of greeting; it was not a duplicate occurrence. The capability question and negated message created no second Automation. Admin disabled the single disposable Automation with expected revision, preserving evidence and preventing further scheduled provider calls.

The capability question's attempted read-only schedule verification returned `authorization_classifier_invalid_output`; the model reported the classification failure instead of inventing a successful inspection. This remains a hosted classifier-format limitation, separate from the successfully deterministic create path. MCP showed zero console errors on the Real journey and inspected Automation HTTP reads/update returned 200. No hosted model timeout/retry settings changed. This bounded test qualifies the configured model for the exact create-and-fire journey, not the full catalog or all ambiguous classifiers.

## CI regression correction

The first pushed candidate `da1f38c3` failed the Faithful Manual-A browser gate: `check the oven in 1 minute for me` rendered a generic reply rather than creating its reminder. The new edge-timing grammar had omitted trailing recipient wording. The correction accepts bounded `for me`/`to me` suffixes alongside `please`, preserves classification for conditional/quoted/oversized variants, and adds both oven word-order regressions. The full local wall-clock journey passed in 1.1 minutes without increasing its deadlines: create/persist, end the foreground Session, wait for exactly one completed durable occurrence and reopen its original result. Final CI results must come from the later pushed candidate; the failed run is retained as evidence, not acceptance.


The initial backend job also failed `Scripted_language_model_keeps_schedule_scratch_isolated_across_conversations`: its fixture put the opaque `thread-alpha` marker before `remind me tomorrow`, so the anchored recognizer no longer treated it as an imperative. The marker now follows the explicit request; both distinct conversation seeds and the cross-conversation cancellation-isolation assertions remain. Authorization matching was not widened to satisfy the fixture. The six-case `InMemoryAndScriptedTests` regression is rerun alongside the affected acceptance journey.


## Completed baseline and consistency review (2026-10-12)

All seven Synthetic jobs passed on `10b156bb49b62f78ea6266109394f825d9c03923` in [workflow 38164724985](https://github.com/trannamtrung1st/agent-core/actions/runs/38164724985): 3,619 backend tests passed with 27 opt-in/environment skips, 925 frontend tests passed, 195 core browser tests passed, and the acceptance and Compose smoke jobs succeeded. A second bounded Real/DeepSeek journey verified that exact loaded build before submission: `greet me every 60 seconds` created Automation `01a12c45-4556-79a6-8f31-7eeb8d9ed983`; two distinct completed occurrences greeted the user at due slots 60 seconds apart. Admin disabled it afterward. This evidence supersedes the earlier pending CI statement for that baseline only; it does not qualify a later candidate.

The requested consistency review began from a clean main checkout. Outside-session composer layout, focus and built-in Skill changes are already included through `8d036114`, `787508c5` and `9d664ca6`; no uncommitted frontend edits were omitted. The review found an event-Automation branch that bypassed bounded timed admission for conditional or historical mentions, plus JSON parsing that selected the last of conflicting decision fields. Six new unit regressions reproduced these failures before correction. Creation now goes through the shared qualification/timing gate, event requests require an anchored directive, and duplicate decision fields fail as invalid output. Conditional or uncertain wording retains model classification. Clear event directives and the exact greeting requests still authorize with zero classifier calls. Owner, policy, permission, trigger and interval restrictions remain independent; no timeout, retry or provider budget changed.

Current review checks:

- Application scheduling/draft regression filter: **204 passed**.
- API reliability/readiness integration filter: **37 passed**, including the six added conditional/historical/conflicting-output cases. They produce the expected safe error and zero persisted Automations; the four greeting journeys still persist and execute once per slot after reopen.
- Frontend TypeScript/Vite build: **passed** (existing bundle-size advisory remains).
- Synthetic structured/built-in composer browser regressions: **27 passed**, including 1440/768/390 viewport checks, disabled-Skill recovery, chip/history preservation, keyboard editing and pagination.
- Playwright MCP separately selected the built-in Harness authoring Skill through the visible picker, submitted it as the only first-turn content and observed `/Harness authoring` in user history plus `Hello from synthetic.`. The 1200px picker has 40px rows, 8px gaps/padding, one row per choice and no page overflow. An initial stale-capability model-catalog read returned 401 when switching disposable deployments; bootstrap recovered, and the subsequent reload and submitted journey showed zero console errors and HTTP 200 for the resulting Session read.

The full frontend run and final candidate CI are checked after these focused checks; delivery results must identify the actual pushed SHA. Historical freezes and P10/P11 remain unchanged. Real evidence above covers the earlier exact baseline; any new Real run must verify its loaded build separately. The prior generic classifier-format limitation and full live-catalog qualification remain outside this bounded evidence.
