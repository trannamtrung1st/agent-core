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

- Scheduling Application regression command: `TMPDIR=/private/tmp DOTNET_CLI_USE_MSBUILD_SERVER=0 dotnet test tests/AgentCore.Application.Tests --no-restore -m:1 -p:UseSharedCompilation=false --nologo --filter 'FullyQualifiedName~Trigger|FullyQualifiedName~ScheduleDraft'`: **192 passed**.
- SQLite/runtime/readiness regression command uses the same build options with `tests/AgentCore.Api.Tests` and filter `FullyQualifiedName~ReliabilityJourneyTests|FullyQualifiedName~SchedulerReadinessTests`: **31 passed**, including nine scheduling, seven denied/draft, four classifier-diagnostic, eight background, two busy-parent and one readiness cases.
- CI must be checked on the pushed candidate's exact SHA. The existing Synthetic workflow supplies backend, frontend, browser core/acceptance and Compose gates; dispatch or a previous green commit does not establish acceptance.
- The earlier outside-session frontend edits already belong to main (`8d036114`, `787508c5`, and the combined `9d664ca6` change). They remain included; the final candidate inherits their regression coverage.
- No API host was reachable on local development ports 5080/5280 at investigation start. Running deployment verification requires reading the loaded assembly's `/health.buildVersion`, comparing its embedded source SHA with the pushed candidate, and exercising the request on that deployment. No hosted URL was supplied; the user subsequently authorized bounded testing with the Real profile. A disposable Synthetic deployment and CI are separate evidence from the originally failing hosted/provider deployment.

The integrated model fixture performs real Core admission, storage, scheduling and execution without provider credentials. It proves deterministic authorization and durable occurrence behavior; it does not qualify a hosted model's ability to generate Automation arguments. Live model qualification remains an explicit opt-in check, separate from key-free CI.
