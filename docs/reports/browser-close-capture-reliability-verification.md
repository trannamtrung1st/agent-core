# Browser close recovery and screenshot evidence verification

Date: October 10, 2026. Base: `main` at `2d6e7e9f`. Candidate: the commit containing this report.

## Findings and root causes

The failed [Synthetic workflow 38054331705](https://github.com/trannamtrung1st/agent-core/actions/runs/38054331705) recorded `Masked_capture_failure_budget_and_text_only_recovery(vision: True, unavailable: True)` expecting coordinate evidence solely from Vision support. That is an outdated assertion: Vision permits image delivery, semantic observation can fail independently, and coordinates require complete settlement observations plus stable page/tab/viewport and capture consistency.

The unchanged four-case theory passed locally before implementation. The hosted failure log contained no capture metadata, so the exact condition that denied coordinates in that historical capture cannot be established. Controlled post-raster mutation reproduces a legitimate denial, rather than a product safety defect: `settled=true`, settlement `observationAvailable=true`, matching semantic `observationUnavailable=true`, native observer available, no in-flight requests/fonts/animations, unchanged page/viewport, `visualStateChanged=true`, `coordinateEvidence=false`, reason `visual_state_changed_during_capture`. The corresponding stable semantic-failure capture has the same flags except `visualStateChanged=false`, `coordinateEvidence=true` and no unavailable reasons. Neither Vision alone nor semantic availability determines coordinate authority.

Existing native regressions establish the other safety boundary: a transient polling failure followed by quiet successful reads can report settlement completed but `observationAvailable=false`. That capture retains its masked image, denies coordinates and records `state_observation_unavailable`; it does not falsely report a settlement deadline. Mouse actions using both earlier and incomplete-capture evidence are rejected before effects. A fresh complete capture restores authority and verifies the intended button.

The close issue had two independently reproducible code paths. Runtime and executor rejected genuinely absent/blank close arguments while other tools indiscriminately converted blank arguments to `{}`. Responses transport additionally rejected missing/blank arguments before Core could validate them. Separately, `InvalidToolCallRecovery.Exhausted` treated any strategy reaching four failures as global exhaustion. The runtime then removed all tools and failed later calls before validating a corrected `{}` close; initial background Runs also failed immediately. Checkpoint restoration recreated the same global condition. Three new owned-Run cases failed before the correction, leaving the browser open.

The user's AHI observation supplies no original argument payloads or Run identifier. These deterministic reproductions establish product failure paths, not the exact arguments or transport used in that historical AHI Run. No live AHI or paid-model attempt was performed.

## Resulting behavior

- A shared schema-aware argument helper interprets absent/blank payloads as `{}` only for offered zero-property object schemas with no required fields and additional properties forbidden. Unknown/composed schemas are conservatively left unchanged. Explicit JSON null, arrays, malformed values and unexpected fields remain rejected; fields are never removed.
- Chat Completions and Responses preserve arguments, including explicit null versus absence, for Core validation and bounded recovery. Completed malformed calls become invalid tool receipts instead of bypassing Core recovery. Partial/truncated calls retain existing provider rejection rules.
- Equivalent invalid strategies remain blocked after two failed attempts and across checkpoints. Corrected valid calls and unrelated offered capabilities remain executable. Blank zero-parameter calls use their normalized execution shape for strategy comparison, so historical invalid-blank receipts do not block a now-valid close.
- Independently requested authorized cleanup remains eligible under the existing cleanup phase. Step/time/output/checkpoint limits, tool offering, approvals, cancellation, tool-free finalization and uncertain-effect replay fences remain in force. Persistently invalid background calls reach the configured step limit; a tool call during finalization is still rejected.
- Original call IDs, argument payloads and opaque continuation tokens remain durable. A restored pending corrected close obtains one result receipt and is not replayed after completion. Native closure and already-closed results are checked against the actual page/context; neither establishes application logout.
- Screenshot capture/settlement/projection production safety logic is unchanged. Tests log only bounded Core diagnostic flags/counts/reason codes, verify eligible coordinate clicks and pre-effect denial, and retain masking, artifact/image byte equality, text-only behavior and capture/output budgets.

## Changed files

Production: `SessionRuntime.cs`, `InvalidToolCallRecovery.cs`, `SessionToolExecutor.cs`, new `ToolArgumentPayload.cs`, `OpenAICompatibleLanguageModel.cs`, and `OpenAICompatibleLanguageModel.Responses.cs`.

Application tests: `AdaptiveBrowserRuntimeTests.cs`, new `BrowserCloseRecoveryRuntimeTests.cs`, new `ToolArgumentPayloadTests.cs`, `BrowserToolTests.cs`, `InvalidToolCallRecoveryTests.cs`, `BrowserModelCompatibilityTests.cs`, and `CapabilityDiscoveryRuntimeTests.cs`. `ConversationFailureDiagnosticTests.cs` now waits for the interruption mailbox fence before releasing the previous provider; the broad run exposed that asynchronous admission alone did not establish supersession. Production supersession behavior is unchanged.

Infrastructure tests: `OpenAICompatibleLanguageModelTests.cs` and `ResponsesTransportTests.cs`. API tests: `ContinuityBoundaryTests.cs` now verifies scoped blocking, the configured step limit, finalization rejection and unchanged source/Experience/Memory state instead of expecting global strategy termination.

Canonical documentation: backend interfaces, backend implementation, protocol and testing strategy. No UI/design-system contract changed.

## Local verification

All tests use Synthetic adapters, scripted HTTP/model fixtures or disposable native Chromium. Live-provider scenarios remain opt-in and skipped. The existing Real host on port 5080 and its UI on 5173 were left untouched.

| Gate | Result |
| --- | --- |
| Original screenshot theory before changes | 4 passed; hosted failure not locally reproduced |
| New owned close recovery before correction | 3 failed, reproducing global suppression |
| Targeted corrected close/argument/recovery/screenshot checks | Passed |
| Final full Application project | 1,633 passed, 7 opt-in tests skipped |
| Screenshot theory repeated ten times, no retries | 60/60 passed across six cases per run |
| All affected Infrastructure browser/provider tests | 307 passed, 5 live-provider tests skipped |
| Full Domain project | 188 passed |
| Targeted API invalid retrospection/finalization | 4 passed |
| Final full API project | 431 passed, 2 live-provider tests skipped |
| Synthetic record lookup and browser intervention Playwright | 2 passed |
| Dedicated screenshot privacy Playwright | 5 passed |
| Whitespace and changed documentation link checks | Passed |

Commands: `dotnet test tests/AgentCore.Application.Tests/AgentCore.Application.Tests.csproj --no-restore --nologo --blame-hang --blame-hang-timeout 5m`; ten independent `--no-build --no-restore` runs filtered to `Masked_capture_failure_budget_and_text_only_recovery`, with detailed safe output; Infrastructure filtered to `Browser`, `OpenAICompatibleLanguageModelTests` and `ResponsesTransportTests`; full Domain and API projects; API filtered to `Invalid_retrospection_never_changes_source_or_promotes_memory`.

Playwright used the existing configuration with disposable Synthetic API/UI ports 5280/5180, 5281/5181, 5282/5182 and fixtures 5391–5393 for `p9-browser-journey.spec.ts` and `browser-intervention.spec.ts`. Privacy used its dedicated `playwright.browser-privacy.config.ts` and disposable SQLite host. CLI browser acceptance complements the native integrated backend cases; no Playwright MCP or live enterprise acceptance is claimed.

## Remaining acceptance

Hosted CI on this implementation commit has not been awaited or confirmed, per the user's instruction. Local passes do not establish hosted CI green. The precise unavailable-evidence cause in the old hosted screenshot capture remains unknown because that run omitted diagnostics. Actual AHI argument provenance, deployed revision and enterprise acceptance remain outside this offline verification; another live attempt requires renewed authorization.
