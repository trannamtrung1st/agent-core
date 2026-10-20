# Tenant Automation admission consent — 2026-10-21

Canonical Schedule/manual occurrences targeting one new Background Session and no completion destination now reserve exact durable consent **before** creating a Session, Activation, Run or provider request. The full original multi-tenant proposal remains open. This report qualifies this admission slice; it does not claim milestone, CI, all Automation ingress/destination or final design/security acceptance.

## Behavior and ownership

- The existing Automation/occurrence, IAM, approval and processed-signal owners atomically advance the source and save one AwaitingDurableWork occurrence (`admission_approval_pending`) plus exact consent. Reserved private identities are immutable witnesses; no execution resource exists yet.
- Own executor permission plus current policy can require Review. Exactly one missing executor permission can become a primary-only exceptional grant; multiple missing permissions refuse without a request. The compound operation requires exact `automations.run`, tenant `sessions.create` and tenant `agent-runs.create`. Reviewer raw Agent/Automation reads, all underlying permissions and current approval/grant-exception authority remain independent. Scheduled requests retain no human requester; manual requests retain the actual invoker.
- Approval settlement repeats current source, configuration/model, requester, executor, reviewer, exact decision and available grant checks. It creates the reserved fresh root once, using actual settlement time for Run creation, or rejects the source without work. Rejection/cancel/expiry/changed authority cannot revive or retarget consent. Existing Runtime execution and tool authorization remain separate.
- Public manual reservation returns 202 with the approval ID and null Run/Session IDs. Source-less approval details show the exact time/revision, literal task, schedule, destination, model, primary permission, compound scope and bounded consent. Pending, decision-recorded, admitted and not-admitted are factual distinct states. No planned identity becomes a conversation link. Current busy hints prevent another Run; reload never repeats an action.
- SQLite keeps the source-first and consent-second saves inside one transaction. Ordinary approval families retain actual Run/Session foreign keys. The frozen migration retains all 43 prior guards (39 unchanged bodies, four explicitly extended source guards), adds three admission guards, preserves legacy-only round trips and refuses incompatible retained-consent downgrade. No user database was reset.

## Focused verification

**202 distinct passing cases** across the selected checks below; overlapping reruns are not added to that total. Backend integration hosts and data roots are disposable Synthetic fixtures with no provider credentials/outbound calls.

| Check | Cases | Actions and observed result |
| --- | ---: | --- |
| API admission consent matrix | 38 | Both stores: manual and scheduled reservation; no premature graph; all four Schedule serialization round trips; exact hash; rejection/cancel/expiry; all three primary grants; multiple missing permissions; no independent reviewer; scoped inbox filtering/counts/detail; pending source busy; archive/cancel/current authority refusal; direct settlement after revocation; exact one-time graph and consumed grant. |
| SQLite application-host recreation | 4 | Scheduled/manual × pending/approved-before-settlement. Recreate the host with persisted consent and no source Run, decide when still pending, settle once and execute the exact Background task to actual Runtime completion. No duplicate Run/provider replay. |
| Existing Automation API regression | 43 | Thirty ownership/current-origin/runtime cases, ten editor/current-access cases and three existing occurrence cold-host cases passed in the grouped module run. Its two new scheduled-count failures were corrected and requalified in the admission matrix; these 43 independent passing cases are retained. |
| Infrastructure schema/approval guards | 6 | Existing approval/root transaction checks and legacy migration down/up; current model parity, all 46 guards, retained legacy rows and foreign-key check. A frozen-source comparison independently confirmed 39 unchanged preceding guard bodies and only four intentional source extensions. |
| Shared frontend tests | 108 | Both shared approval/Automation files passed 107 cases; the subsequently added unsupported-primary-permission case and affected admission previews passed in an eight-case focused confirmation. Tests cover complete literal evidence, null source links, closed preview validation, factual result states, pending acknowledgement, stale drafts, unknown outcome fences and current read retirement. |
| Playwright E2E | 3 | Existing desktop/narrow Schedule create/edit/stale/save/manual-completion/cancel/Activity and scoped-reader revocation passed. The added narrow actual admission-before-work → exact review → confirm → decision → settlement journey passed after the preparation fix. No page errors or legacy authority requests were observed. |

Commands:

```sh
dotnet build tests/AgentCore.Api.Tests/AgentCore.Api.Tests.csproj --no-restore --nologo --disable-build-servers -m:1 -p:UseSharedCompilation=false
dotnet test tests/AgentCore.Api.Tests/AgentCore.Api.Tests.csproj --no-build --no-restore --nologo --filter 'FullyQualifiedName~Automation_admission_'
dotnet build tests/AgentCore.Infrastructure.Tests/AgentCore.Infrastructure.Tests.csproj --no-restore --nologo --disable-build-servers -m:1 -p:UseSharedCompilation=false
dotnet test tests/AgentCore.Infrastructure.Tests/AgentCore.Infrastructure.Tests.csproj --no-build --no-restore --nologo --filter 'FullyQualifiedName~TenantAutomationMigrationTests|FullyQualifiedName~TenantOperationAuthorizationTests.Root|FullyQualifiedName~TenantOperationAuthorizationTests.Approval'
cd web
npx vitest run src/features/access/TenantApprovals.test.tsx src/features/access/TenantAgentAutomations.test.tsx
npx vitest run src/features/access/TenantApprovals.test.tsx -t 'admission|settlement'
npm run build
TENANT_CHAT_API_PORT=5097 TENANT_CHAT_WEB_PORT=5197 npx playwright test --config playwright.tenant-automations.config.ts
TENANT_CHAT_API_PORT=5097 TENANT_CHAT_WEB_PORT=5197 npx playwright test --config playwright.tenant-automations.config.ts --grep 'exact Automation admission'
```

The selected grouped API regression filter was `FullyQualifiedName~Automation_admission_|FullyQualifiedName~Tenant_automations_use_current_human_scope|FullyQualifiedName~Tenant_owned_Automation_occurrence_reopens|FullyQualifiedName~Tenant_automation_editor`. Builds passed with zero .NET warnings/errors. Web TypeScript/Vite builds passed; existing large-chunk warnings remain. CI/full local CI were not requested or run.

## Corrections and bounded browser inspection

Grouped verification corrected reference-identity comparison of model/value witnesses; consent equality after deserialization; source/approval SQL insert ordering; retained frozen guard inventory; strict pending IDs and accepted source graph evidence; reviewer qualification against a not-yet-existing approval; and the scheduled test's race with the actual scheduler. The final tests assert durable outcomes instead of which worker first advances a source.

Playwright MCP on a separate disposable Synthetic API/Vite pair exercised real sign-in, current owned setup, manual Run reservation, exact shared review at 390×844, primary confirmation, decision acknowledgement, explicit refresh and factual admission. It observed no source-conversation link, no invented Activity link while pending and no horizontal document overflow. The final E2E pending review additionally checks exact occurrence time/revision and primary permission; its stable Drawer capture waits for the opening transition to finish. Existing shared dark Ant Design Descriptions, Drawer, footer, confirmation and focus owners remain; there is no new CSS, token, font, raster asset or UI kit. Surface briefs and PRODUCT/DESIGN tooling context reflect the changed flow.

Local browser evidence: `/private/tmp/automation-admission-narrow-settled.png`; E2E pending review/trace under `/var/folders/8f/r5cy7q69413g8xpd_z9v2yt40000gn/T/agent-core-tenant-chat-e2e-H2QDQB/results/`. These local evidence files are not product assets. No new discretionary visual world or repeated polish loop was introduced. Historical design-sidecar fingerprints/pre-existing drift were preserved; the full proposal's final sidecar regeneration and cross-surface polish audit remain open.

## Remaining scope

Event/webhook/watch ingress, existing-Session/arbitrary completion destinations and their UI, voice, System/audit, legacy authority/schema removal, the exhaustive original-proposal inventory and final whole-product design/security audit remain open. Real external providers were not exercised. A successful admission grants no later tool, recipient read or completion authority.
