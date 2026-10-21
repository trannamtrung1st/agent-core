# Tenant Automation configured completion verification — 2026-10-21

This module converts optional completion delivery for canonical independent tenant Automations. It does not accept Existing-Session execution, remaining voice/System/audit work, final legacy authority/schema removal, the complete original proposal or an application milestone.

## Behavior and ownership

The existing Automation editor saves None or one exact active UserChat conversation owned by the same tenant, Agent Instance and executor. Authoring requires the actual human's current raw destination read and Agent Chat access. The shared conversation picker uses explicit canonical tenant collection/selected-item reads; its default consumer remains supported. Denied/stale commands retain drafts, block replay and require explicit successful current reload before a deliberate corrected save.

An accepted durable occurrence freezes its destination. The initial independent Run, actual Automation Session origin and owning-store occurrence prove the same IDs; arbitrary caller destinations, missing occurrences and invented conversational parents fail. Registration destination edits affect later occurrences only. The independent task does not require `background.start`; immediate-background children retain their own capability/causal requirements. Scheduled roots have no human requester; manual roots retain the actual manual initiator.

The incumbent completion coordinator reuses separate exact source-read consent and report-creation consent, primary-only exceptional grants, inbox receipts and the Runtime's atomic output owner. Current raw source/destination reads, Agent Chat permission, lifecycle, current definition and reviewer qualification repeat at admission, execution and final publication. A changed communication Review policy before output blocks unconsented publication. Finished child revisions remain unchanged. Core formats the bounded completion receipt directly, without another provider/model call. Work completion, consent, report admission and actual delivery remain separate facts.

Closed prepared-admission schema 3 binds the configured destination in Current, Next and occurrence. Versionless one-Schedule and schema-2 no-report event serialization remain unchanged. The migration rebuilds only the existing approval owner check, restores all prior guards and adds four exact destination/source guards (79 total). Rollback refuses retained tenant destinations or schema-3 admission consent. No new queue, worker, table or execution owner is added.

## Corrections found during execution

- A real Synthetic admission Review returned HTTP 500 because the closed consent codec prohibited configured destinations. Schema 3 and matching persistence checks now retain the exact destination; focused integration and browser admission checks cover the repaired path.
- SQLite millisecond UTC persistence made an unchanged higher-precision schedule appear semantically changed on a destination-only update. Canonical authoring now normalizes UTC instants to milliseconds and local times to seconds before child-revision comparison. One-shot and fixed-interval destination edits retain the accepted occurrence's original report target.
- The new controlled fixture initially raced the hosted coordinator during approved cold recovery. It now removes only that hosted descriptor and explicitly drives the incumbent coordinator; other hosts remain. The completion fixture uses a valid attention flag, actual fixed-interval wire fields and serialized occurrence comparisons. These fixture corrections do not weaken product checks.

## Focused checks

No CI, full local CI suite, live OAuth/mailbox/provider, deployment or production data was used.

| Check | Result and observed behavior |
| --- | --- |
| API module group | 103 passed: 39 configured-report cases plus 64 affected admission and immediate-child report cases; another 4 Webhook/Core cases passed in both stores (107 API cases total). The event cases exercise actual receipt/production, schema-3 admission consent and exactly one delivered report, preserving a null human requester and the child revision, with only the work model invocation. Configured independent/manual/scheduled execution, exact consent, primary-only grant, rejection/revocation/archive/quiet behavior, frozen destinations, forged proof rejection, final-output fences and SQLite recreation; affected incumbent admission and immediate-child report consent/recovery included. |
| Application closed-consent contracts | 12 passed: maximum event payloads, schema-3 destination/hash round trip, unchanged historical schema-2 bytes and rejection of unknown/duplicate/missing/null/empty/conflicting destination fields. |
| SQLite migration group | 3 passed: upgrade/rollback/rebuild, retained legacy destinations, 79/75/79 guards, no pending model changes and foreign-key integrity. |
| Focused frontend tests | 120 passed across TenantAgentAutomations, AdminSessionPicker and TenantApprovals. |
| Builds | API tests, Application tests and Infrastructure tests built with zero warnings/errors; frontend build passed with the incumbent bundle-size warning. |
| Browser | 2 passed on the repaired binary. The configured flow selects the shared picker destination, exercises an actual 409 retained draft/reload/save, runs the saved Automation and observes exactly one delivered assistant report. The separate admission flow observes no Session/Run before decision, the exact destination UUID in the inspector and factual admission after approval. |

Commands used (run from the repository root, except browser commands from `web/`):

```sh
dotnet build tests/AgentCore.Api.Tests/AgentCore.Api.Tests.csproj --no-restore --nologo --disable-build-servers -m:1 -p:UseSharedCompilation=false
dotnet build tests/AgentCore.Application.Tests/AgentCore.Application.Tests.csproj --no-restore --nologo --disable-build-servers -m:1 -p:UseSharedCompilation=false
dotnet build tests/AgentCore.Infrastructure.Tests/AgentCore.Infrastructure.Tests.csproj --no-restore --nologo --disable-build-servers -m:1 -p:UseSharedCompilation=false
dotnet test tests/AgentCore.Api.Tests/AgentCore.Api.Tests.csproj --no-build --no-restore --nologo --filter 'FullyQualifiedName~Configured_Automation_completion|FullyQualifiedName~Automation_admission_consent_reserves|FullyQualifiedName~Automation_admission_recreates|FullyQualifiedName~Tenant_background_report_creation_consent|FullyQualifiedName~Tenant_background_report_pending_consent|FullyQualifiedName~Tenant_background_report_source_read_consent'
dotnet test tests/AgentCore.Api.Tests/AgentCore.Api.Tests.csproj --no-build --no-restore --nologo --filter FullyQualifiedName~Configured_Automation_event_completion
dotnet test tests/AgentCore.Application.Tests/AgentCore.Application.Tests.csproj --no-build --no-restore --nologo --filter FullyQualifiedName~TenantEventAdmissionContractTests
dotnet test tests/AgentCore.Infrastructure.Tests/AgentCore.Infrastructure.Tests.csproj --no-build --no-restore --nologo --filter 'FullyQualifiedName~TenantAutomationCompletionMigrationTests|FullyQualifiedName~TenantAutomationMigrationTests|FullyQualifiedName~TenantEventMigrationTests'
pnpm run test --run src/features/access/TenantAgentAutomations.test.tsx src/features/admin/AdminSessionPicker.test.tsx src/features/access/TenantApprovals.test.tsx
pnpm run build
CI=1 TENANT_CHAT_API_PORT=5099 TENANT_CHAT_WEB_PORT=5199 TENANT_CHAT_TEST_RUN_ROOT=/private/tmp/tenant-completion-browser-final pnpm exec playwright test --config playwright.tenant-completion.config.ts --grep 'configured completion|exact Automation admission'
```

The .NET runner required its local-socket sandbox exception; the initial sandboxed contract invocation aborted before tests and then passed with that exception. API and browser hosts were disposable Synthetic instances. The unrelated API on port 5080 was untouched.

Independent Playwright MCP additionally exercised real native sign-in, picker selection/save (201), a foreign-Instance destination save (403), retained blocked draft, explicit reload, correction and save (200). It reproduced the admission codec 500 before the fix; the final integration/browser admission checks establish the corrected result. Expected denial responses and aborted retired reads are distinguished from JavaScript failures. The old MCP host is not evidence of running the repaired binary.

## Bounded presentation finish

Five genuine scrolled Completion-section captures establish configured-state presentation at [1440px](assets/tenant-automation-completion-2026-10-21/configured-report-1440.png), [1024px](assets/tenant-automation-completion-2026-10-21/configured-report-1024.png), [768px](assets/tenant-automation-completion-2026-10-21/configured-report-768.png), [390px](assets/tenant-automation-completion-2026-10-21/configured-report-390.png) and [320px](assets/tenant-automation-completion-2026-10-21/configured-report-320.png). CLI checked no horizontal document overflow at each width. MCP measured the existing 16px body gap, native Select height 32px and manual-ID button height 40px at narrow width; this is not a claim that every control is 40px.

The one [detector result](assets/tenant-automation-completion-2026-10-21/detector.json) is `[]`. A fresh finish reviewer returned SHIP with no material fixes for this section and adjacent timing/footer; a separate documenter updated existing DESIGN/surface context. [Recorded finish](assets/tenant-automation-completion-2026-10-21/finish-review.md). Native Ant Design v6, shared controls/spacing and footer owners remain; no CSS, tokens or second kit was introduced. Historical sidecar hash `8bdc3659a25b706454ac8cc0394323c5e0e60ea1ade616756b180efbaecef49b` and pre-existing fingerprint drift remain unchanged.

Loading, hover, empty, denial/stale/access-loss states and measured contrast have no dedicated screenshot acceptance. Functional tests cover their exercised behavior only. Existing-Session execution, remaining proposal families, exhaustive route/schema/security removal and final cross-product design acceptance remain open.
