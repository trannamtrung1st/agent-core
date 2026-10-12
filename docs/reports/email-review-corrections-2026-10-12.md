# Email review corrections — 2026-10-12

Focused correction of the four findings from the merged account-scoped Email review. This preserves the existing email/credential abstraction and does not open a new milestone. No live Gmail probe, hosted workflow dispatch or CI wait is part of this pass.

## Changes and observed workflows

| Scenario | Expected and observed result |
| --- | --- |
| “Show my latest 10 emails” through an owned Synthetic SessionRuntime | The source Run requests account selection. A fresh Run reads the selected alias from its continuation context, calls the real `email.search` executor with an omitted query and limit 10, receives account-scoped messages, and completes without duplicating the original user entry. |
| Tool-level recent inbox listing | Omitted, empty and whitespace-only string queries return newest-first Synthetic inbox summaries. Limit 10 reaches the account provider unchanged; non-string queries reject safely. Existing query-length and search-limit bounds remain. |
| Two accounts already bound | Selecting the second account persists its ID and actual `secondary` alias despite an input reference of `mail`. Fresh pins include that binding; the model request and search result carry `secondary`. Cancellation, expiry, foreign Session, stale revision, detach and newer input still block continuation. |
| Disabled SMTP, InMemory and temporary SQLite | Reading and Core drafting remain configured; sending is absent from offered tools. Account inventory exposes setup guidance; send-target selection and approval preparation reject. Dispatch with an earlier grant rejects and leaves no durable claim, including after reopening storage. Enabling dispatch permits the same draft with a fresh exact approval and one Synthetic send. Real factory dispatch requires explicit SMTP opt-in; Synthetic remains key-free. |
| IMAP server omits UIDNEXT | Fake TLS IMAP with one or 600 messages returns a message, including a sparse UID of 6007. The fallback fetches UIDs from `1` or `101:600` message positions and searches those UIDs. It never uses a near-uint-max lower bound; the 600-message scan reports truncation. Existing PEEK/UIDVALIDITY and SMTP acceptance/uncertain-delivery tests pass. |

Selection identity is private persisted JSON and safe fresh-Run context, not credential authority. New Run admission checks selected identity against its pinned binding; email operations still recheck live account/binding authorization. Legacy allowed selection receipts without identity require a new request. No database migration is needed.

## Local checks

All test commands ran from the repository root with `--nologo -m:1 -nr:false -p:UseSharedCompilation=false`.

| Command / filter | Result |
| --- | --- |
| `dotnet test tests/AgentCore.Application.Tests/AgentCore.Application.Tests.csproj --filter 'FullyQualifiedName~EmailToolTests\|FullyQualifiedName~ToolRegistryPolicyTests\|FullyQualifiedName~ConfigurationPendingTaskTests'` | 35 passed, 0 failed |
| `dotnet test tests/AgentCore.Infrastructure.Tests/AgentCore.Infrastructure.Tests.csproj --filter 'FullyQualifiedName~ImapSmtpEmailProviderTests\|FullyQualifiedName~EmailAccountIntegrationTests'` | 24 passed, 0 failed |
| `dotnet test tests/AgentCore.Api.Tests/AgentCore.Api.Tests.csproj --filter 'FullyQualifiedName~EmailSelectionRuntimeTests\|FullyQualifiedName~EmailAccountApiTests\|FullyQualifiedName~SelfManagementContinuationTests'` | 20 passed, 0 failed |
| `git diff --check -- <correction paths>` | Passed; whole-checkout check flags a concurrent unrelated extra blank line at `web/src/app.css:2682` |
| `.agents/skills/impeccable/scripts/impeccable doctor --json` | Empty findings after refresh |

The initial sandboxed runner could not bind its local MSBuild sockets. That process was stopped and the offline tests reran with local socket permission. The first fake-server fallback run caught a test assertion expecting parentheses around MailKit's single UID fetch field; the command expectation was corrected and reruns passed. A malformed string literal in the new query test was corrected before the passing tool suite. Final logs are disposable local files `/tmp/email-app-final.log`, `/tmp/email-infra-final.log` and `/tmp/email-api-final.log`.

The local Impeccable sidecar now includes the exact Email Account presentation narrative from `.agents/context/DESIGN.md`. Existing normative tokens, component snippets and `DESIGN.md` remain unchanged. Canonical port, protocol, persistence and test documentation records the corrected behavior.

## Limits

These are focused local functional regressions through the real executor, SessionRuntime, durable stores and fake TLS transports. Full backend/frontend/browser/Compose milestone gates and hosted CI were not rerun for this backend correction pass. No frontend product code changed. This report does not supersede the historical merged implementation's gate evidence or claim new milestone acceptance. Gmail authentication, live IMAP reading, SMTP delivery and final recipient receipt remain unverified. SMTP remains disabled by default.

Concurrent pre-existing/unrelated prompt-isolation, reliability-journey, stylesheet and architecture documentation edits in the shared checkout were preserved.
