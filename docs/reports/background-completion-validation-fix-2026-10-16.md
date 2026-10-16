# Background completion validation recovery

Date: 2026-10-16. Focused correction after automation authorization wiring; no milestone acceptance or CI requested.

## Reported failure and cause

The supplied diagnostic `5c790c44-b7cc-4650-ab37-2be40ce01c5a` identifies the failed background Run in Session `a278dd25-d10b-429e-9d33-7018480bafd1`. Read-only inspection of its retained checkpoint shows successful public search receipts, one denied public fetch, completion-only finalization, and a `work.complete` call with outcome Response and attentionRequired false. Its summary contains 4,442 characters, exceeding the existing 2,000-character contract. Core rejected those arguments during completion-only recovery and used the same missing-completion diagnostic as a model that never called the tool. The error did not accurately describe the rejected envelope.

The task text and search results are investigation evidence, not new instructions to perform news research. The original Session, Run, results, database and running API were preserved.

## Correction

The completion schema, tool description and finalization instruction state the summary bound explicitly. Validation identifies an invalid summary, outcome or attention flag without echoing model content. An invalid completion envelope in finalization receives one field-specific correction receipt when the remaining admitted step, output and checkpoint budgets permit; the existing hard deadline still applies. Only `work.complete` is offered, so the correction cannot restart substantive work or replay effects. A typed `CompletionCorrectionAttempted` flag persists with the existing checkpoint format and defaults to false for existing checkpoints. Provider retry and SQLite reopen do not restore that allowance.

Repeated invalid completion, or prose after consuming its correction, uses Run code `completion-invalid`, execution diagnostic `CompletionInvalid`, reason `backgroundCompletionInvalid` and channel `backgroundCompletion`. A missing call retains `completion-required`. NoAction-after-mutation and attention consistency remain enforced. No deadline, step, token, summary or retry ceiling increased, and no outcome is inferred from ordinary prose or silently truncated text.

Parent Chat metadata says **Background work result**, covering successful and unsuccessful outcomes without labeling a failed task completed. The existing shared metadata owner and child destination link remain in use; no layout/style changes were needed. Canonical architecture, interfaces, backend, frontend, protocol, checkpoint and operations/testing guidance now agree. The protocol's stale outcome names and description of provider-generated completion reporting were corrected to current behavior.

## Runtime and browser verification

- SQLite/runtime scenarios save one real workspace file and Artifact, then reproduce the 4,442-character envelope. One correction succeeds with the expected bounded summary; repeated invalid completion fails with the new diagnostic. Two additional cases inject transient provider failure after correction preparation, reopen SQLite and either complete or reject the second invalid envelope. Each keeps exactly one write and Artifact, no replay file, and the consumed correction flag. Existing missing-call, prohibited-tool, provider/protocol/authentication and reserve-entry cases remain covered.
- Application checks cover exact summary boundaries (1, 2,000, 2,001 and 4,442 characters), bounded field-specific errors, attention/outcome consistency, NoAction safety, checkpoint round trips, runtime cancellation/finalization and hard deadlines.
- Playwright MCP used a fresh Synthetic API on 5680 and Vite on 5683 with SQLite, workspace, Artifact and attachment roots under `/private/tmp/agent-core-completion-browser-20261016`. Visible Chat submission of the corrected fixture produced one completed background result and one parent report. A second submission produced one truthful invalid-completion report; Background work → View original result displayed `backgroundCompletionInvalid` and retained diagnostic metadata. The supported inspection flow had successful requests and no JavaScript errors. Exploratory direct navigation to an unpromoted child Chat URL produced unrelated repeated `email-account-request` 404s; supported original-result inspection succeeded, and that polling boundary was not changed here.
- The repeatable four-case Playwright suite covers retained original results/continuation, missing completion, corrected oversized completion and repeated invalid completion. It asserts exactly one parent report and visible diagnostics. Its original-result journey retains 1440/768/390 viewport coverage.

## Exact focused checks

Backend commands run from the repository root with prefix `TMPDIR=/private/tmp DOTNET_CLI_USE_MSBUILD_SERVER=0` and flags `--no-restore -m:1 -p:UseSharedCompilation=false --nologo`:

- `dotnet test tests/AgentCore.Application.Tests --filter 'FullyQualifiedName~WorkCompletionRequestTests|FullyQualifiedName~AgentRunToolCallCheckpointTests|FullyQualifiedName~AgentRunDurabilityTests|FullyQualifiedName~TerminalDisplayRepairTests'`: **103 passed**.
- `dotnet test tests/AgentCore.Api.Tests --filter 'FullyQualifiedName~ReliabilityJourneyTests|FullyQualifiedName~AutomationAuthorizationJourneyTests|FullyQualifiedName~ChatAutomationJourneyTests|FullyQualifiedName~ContinuityBoundaryTests'`: **64 passed**. The 12-case completion-recovery subset also passed separately.
- `dotnet test tests/AgentCore.Infrastructure.Tests --filter 'FullyQualifiedName~InMemoryAndScriptedTests|FullyQualifiedName~AutomationAuthorizationProviderTests'`: **13 passed**.
- From `web/`, `pnpm run test --run src/features/chat/diagnosticDetails.test.tsx src/features/chat/BackgroundWorkDrawer.test.tsx`: **51 passed**. An existing asynchronous React `act` warning remains in a diagnostic-copy test.
- From `web/`, `PLAYWRIGHT_API_PORT=5680 PLAYWRIGHT_WEB_PORT=5683 PLAYWRIGHT_FAITHFUL_MANUAL=1 PLAYWRIGHT_SQLITE_PATH=/private/tmp/agent-core-completion-browser-20261016/verification.db pnpm exec playwright test e2e/agent-run-background-session.spec.ts --project=synthetic --workers=1`: **4 passed**, against the known disposable Synthetic hosts.
- `pnpm run build`: **passed**, with existing SignalR annotation/bundle-size advisories. Relative file links in the eight changed canonical documents resolve; `git diff --check` passes.

Verification used no hosted model or public search requests and spent no provider credits. The test runner/browser required local-socket execution permission. The disposable listeners were stopped afterward. The user's running app was not restarted, failed history was not rewritten, and CI was not run. A deployed API must intentionally restart to load the correction; retrying the task creates a new Run. Hosted model judgment in shortening the digest remains unqualified by these deterministic fixtures.
