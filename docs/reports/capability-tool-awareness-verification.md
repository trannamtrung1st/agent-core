# Capability tool-awareness verification

Date: 2026-10-09. This is a focused correction to existing capability discovery and protected browser fill, not a new milestone or authorization mechanism.

## Problem and correction

The reported sign-in failure classified a non-password target as `unsupported_operation` before attempting an effect. A model could interpret that generic receipt as a missing protected-fill implementation. Existing prompts listed tool names but did not clearly orient the model to each eligible on-demand interface or keep the environment's readiness wording fresh after loads.

- `CapabilityUsageGuidance` provides up to 8192 characters of registry-derived metadata: eligible families, exact tool names and brief purposes. It uses existing authorization/configuration/context eligibility and the executable browser/dialog/cleanup phase filters. It loads neither schemas nor Skill procedures.
- Prompts distinguish Definition authorization from schemas offered now. Capability-aware continuations refresh this environment after projection and phase restrictions. Offered tools can be used directly; a missing interface is discovered through the existing `capabilities.load`. Skills are optional procedural help. Target/argument errors, query misses, execution restrictions and provider denials have distinct implications.
- Password/login/credential intent tags identify the protected sink. `credential_target_invalid` rejects a non-password target before secret resolution or dispatch. Its receipt states target scope, supported capability, no attempted effect and safe observation/retargeting guidance. Password material remains confined to the existing sink, exact origin and active owner's binding.

No grants, automatic Skill activation, bulk schema projection, retry engine, credential bypass, migration or provider-specific prompt branch was added.

## Exercised behavior

1. The existing native Chromium credential fixture reproduced the old generic unsupported receipt. After correction, a Username target returns `credential_target_invalid`; the secret resolver is not called. The real executor receipt supplies the safe next step. Ordinary password typing and detached use remain denied. The correct password target securely fills, reflected secrets remain redacted, and the fixture independently confirms login.
2. `BrowserModelCompatibilityTests.Credential_discovery_and_wrong_target_recovery_sign_in_without_loading_any_skills` uses an owned SessionRuntime/AgentRun and local two-origin SSO fixture with synthetic credentials and **no Skills**. It navigates, enters Email, discovers protected fill from “sign in with saved password”, receives its real schema on the next request, intentionally targets Email, checks the target-scoped failure/no-effect facts, observes again, fills Password, signs in and reads the record. Independent DOM/fixture checks confirm exactly one accepted login and authenticated record AC-1042 “Ready for inspection”. No Skill load or active procedure appears and no protected value enters model messages/arguments.
3. Metadata tests cover initial and post-load readiness, no schema/procedure flood, broad-registry bounds, denied/configuration/vision/detached/tool-less restrictions and natural credential intents. Owned native dialog cases verify that blocked browser.find/click disappear from both executable schemas and the guide, including same-Run continuation. Existing dialog, cleanup, finalization and ordinary form-discovery behavior remains covered.

## Local checks

- `AGENTCORE_BROWSER_BENCHMARK_LIVE=0 dotnet test AgentCore.sln --no-restore --nologo`: all suites passed (Domain 173; Infrastructure 926 / 7 opt-in skips; Application 1453 / 3 opt-in skips; API 390 / 2 opt-in skips; order-event plugin 4). This broad run preceded the final guide phase-filter refinement.
- Final complete Application suite: **1454 passed / 3 opt-in skipped**, no failures. Together with the unchanged Domain/Infrastructure/API/plugin results this exercises 2947 passing backend cases, without double-counting focused reruns.
- Final guide/skill-free credential scenario: 8 passed. Owned dialog/cleanup and ordinary form discovery: 7 passed. Strengthened actual-dialog guide assertions: 4 passed. Credential sink workflow: 1 passed. These are focused reruns, not additional unique case totals.
- `git diff --check` and relative links in changed canonical documents/report passed.
- Logs: `/tmp/capability-awareness-backend.log`, `/tmp/capability-awareness-application-final.log`, `/tmp/capability-awareness-focused-final.log`, `/tmp/capability-awareness-phases.log`, `/tmp/capability-awareness-dialog-final.log`.

## Hosted verification

All five key-free [Synthetic jobs](https://github.com/trannamtrung1st/agent-core/actions/runs/37924238048) passed on implementation commit `4d10bd229e75e1901b639dd2885bf89560669bff`:

- Backend: Domain 173; Infrastructure 920 / 13 skipped; Application 1454 / 3 skipped; API 390 / 2 skipped. No failures. Six Docker sandbox cases skipped in hosted Infrastructure were exercised in the local 926-case pass; hosted Compose ran separately.
- Frontend: 99 files / 770 tests and production build passed.
- Core Playwright: 130 passed.
- Acceptance Playwright: all seven projects passed, 16 cases.
- Compose: owner-capability path and SQLite volume-survival smoke passed.

The completed workflow log is `/tmp/capability-awareness-ci-final.log`. The subsequent evidence-recording commit changes this report only; the tested implementation is the exact commit above.

## Acceptance limits

The new workflow evidence uses a deterministic scripted model and actual Chromium, not a live model or private UAT site. No additional paid inference trials were performed. The earlier native-browser proposal's live-model comparison acceptance remains separate and incomplete. Existing running backend processes must use the rebuilt implementation before exhibiting this correction; Definition authority and active credential bindings are unchanged.
