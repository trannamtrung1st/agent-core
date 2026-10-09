# Browser discovery recovery verification

Date: 2026-10-09. Branch: `develop/branch-1`. Baseline: `a69c2aa9`. Status: correction implemented; broad local and hosted verification pending.

## Evidence and scope

Read-only inspection of Riley's persisted AHI runs showed GPT-5.6 Luna Medium successfully loading `browser.fill_credential`, then repeatedly populating mutually exclusive `browser.find` modes and empty optional scope/frame refs. One run made 34 invalid finds without reaching protected filling. An earlier DeepSeek run filled the protected password and clicked Sign In; that is not proof of successful authentication. The immediate failure is malformed model arguments; Core's generic reference repair message and loss of prior find errors weaken recovery. Provider rewriting of optional fields is not established by the retained trace.

The correction keeps strict validation, typed native discovery, opaque refs, ownership, exact origin policy and protected credential boundaries. No application settings, user Definition/profile/data or running host was changed. No raw trace, credential value, username or authentication URL query is copied into this report. No paid-provider or external AHI requests were made during this correction.

## Changes

- Validate the semantic query combination before optional reference validity. Reject overloaded modes with `invalid`, examples of one placeholder or role/name query and instructions to omit unused fields. Invalid scope refs still fail closed and explain how to perform an unscoped search or obtain a container ref. Empty snapshot target refs explain page observation versus subtree targeting. Tool guidance uses the same query examples.
- Preserve the eight most recent successful find results, including refs/state, across read-only snapshots. Older successes become compact status/count receipts with rediscovery guidance. Failed and ambiguous discoveries keep their original evidence; existing tool/output/step budgets bound the execution. Page observation compaction retains its existing one-full-observation bound. Provider generation and authority checks still decide whether a retained ref is usable.

## Verification

Three focused regressions failed before the correction: overloaded query feedback, recent discovery/error retention, and bounded older discovery receipts. After correction, 15 focused Application cases passed, including native Chromium through SessionRuntime, Activation and AgentRun with an offline scripted model. The integrated dense tree and custom-caption scenarios submit the malformed query, inspect the page, assert the original repair error remains, issue a correct query, inspect again, use the retained target ref, load advanced form filling, update Summary, verify actual DOM state and close across separate user turns. No model credentials are needed.

Commands/logs:

- Before: `dotnet test tests/AgentCore.Application.Tests/AgentCore.Application.Tests.csproj --filter 'FullyQualifiedName~Malformed_discovery|FullyQualifiedName~New_snapshot_preserves|FullyQualifiedName~Older_discoveries' -m:1 -p:UseSharedCompilation=false`; three failed. `/tmp/ahi-recovery-before.log`.
- After: same project with filter `NativeBrowserContractTests|BrowserSnapshotCompactionTests|BrowserReliabilityRuntimeTests`; 15 passed. `/tmp/ahi-recovery-focused.log`.
- Full backend: `dotnet test AgentCore.sln --nologo -m:1 -p:UseSharedCompilation=false`; currently running. `/tmp/ahi-recovery-backend.log`.

Fresh hosted Synthetic verification is pending. Actual Luna recovery and external AHI authentication remain unrun; this change does not claim either outcome. Historical native cutover freeze `345e8f7a` and its prior real-model SPA evidence remain unchanged. PR #4's conflicts with main remain separate integration work.
