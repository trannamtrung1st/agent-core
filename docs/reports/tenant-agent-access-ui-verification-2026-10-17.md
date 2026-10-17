# Tenant Instances and Agent Access verification — 2026-10-17

Baseline `e4fae65e`, branch main. This checkpoint advances the proposal's Phase 3/7 through canonical Instance inventory/creation and the Agent Access job. It does not complete the Instance configuration editor, other resource panels or the full tenant cutover. The full goal remains active; no milestone acceptance is claimed.

## Delivered behavior

- `/t/{tenantId}/admin/agents` uses the existing filtered, paged tenant inventory, loaded-page search and exact shipped-template creation. Creation gets a current security revision and retains the existing template/configuration boundary; the template grants no IAM authority. Cancel restores New instance focus; a selected draft can Keep editing or explicitly discard.
- `/t/{tenantId}/admin/agents/{instanceId}/access` separates Who can chat from What can this agent do. The actual human/session must have exact owned-agent read and IAM-read. Safe labels, direct/inherited source, active chat status, independently authorized revoke hints and bounded pages replace raw identity/assignment guessing. Archived targets do not claim Can chat.
- Chat assignment targets the chosen principal at the exact agent resource. Organization execution assignment targets the independent agent Principal, requiring actual organization inspection/assignment authority and an agent-eligible role. Existing commands still enforce human-only restrictions, source scope, last Owner and revision CAS. Confirmations name the role, assignee, agent and organization; inherited revocation explicitly warns of organization-wide impact.
- Effective permission results come from the one Core Authorizer at This agent or separately authorized Organization scope. They are not a UI union of role labels. Human-only permissions remain People only. Capability eligibility, management gates and approval policy stay independent. Reading a powerful agent's roles does not confer them on its chat audience.
- Principal choices require assignment authority on the exact agent. Active human choices expose display name/reference only; candidate agent labels require independent agent read. No login email, account hash/stamp, invitation proof, credential or prepared operation is returned. The scoped page needs no tenant-wide Members inventory. Execution assignments include only scopes the viewer may inspect, without widening resource grants.
- Scoped queries clear former data and abort retired requests. Shared commands guard duplicates/retirement. Stale failures retain choices and require a fresh read; no mutation is automatically retried. Permission-loss reads remove private sections. Technical IDs stay collapsed. Archive/restore uses exact security/Instance witnesses and retains identity/history.

`TenantAgentAccessService` is a read composition over existing security/agent stores, with final current human/session, tenant-security and Instance witness checks. No schema, second IAM engine, new execution authority or Hub event is added. The [protocol owner](../14-api-and-realtime-protocol.md#canonical-agent-access-view) specifies the exact query/DTO contract.

## Executed checks

All execution used fresh disposable Synthetic roots. No installed user data, provider credentials or external provider calls were used.

| Command | Result |
| --- | --- |
| `dotnet test tests/AgentCore.Infrastructure.Tests --no-restore --filter 'FullyQualifiedName~Agent_access_separates_chat\|FullyQualifiedName~Scoped_agent_access_reader' --logger 'console;verbosity=minimal' --nologo` | **4 passed**, InMemory/SQLite; `/private/tmp/tenant-agent-access-infrastructure-final.log` |
| `dotnet test tests/AgentCore.Api.Tests --no-restore --filter 'FullyQualifiedName~TenantAgentAccessTests' --logger 'console;verbosity=minimal' --nologo` | **2 passed**, authenticated HTTP plus existing scoped inventory/lifecycle workflow; `/private/tmp/tenant-agent-access-http-final.log` |
| Focused Vitest command below, from `web` | **19 passed / 4 files**; `/private/tmp/tenant-agent-access-web-current.log` |
| `cd web && TENANT_CHAT_API_PORT=61287 TENANT_CHAT_WEB_PORT=61288 pnpm run test:e2e:tenant-agent-access` | **2 passed**, final reviewer-corrected source, 20.2s; `/private/tmp/tenant-agent-access-e2e-reviewed.log`; root suffix `agent-core-tenant-chat-e2e-eBpXJh` |
| `cd web && pnpm run build` | TypeScript and production build passed; existing dependency/chunk-size warnings remain; `/private/tmp/tenant-agent-access-web-build-reviewed.log` |
| Changed-document links/fragments/fences and `git diff --check` | Passed after synchronization |

```sh
NODE_OPTIONS=--no-experimental-webstorage pnpm exec vitest run \
  src/features/access/TenantAgentAccess.test.tsx \
  src/features/access/accessRoute.test.ts \
  src/features/access/useTenantCommand.test.tsx \
  src/features/admin/AdminInstanceNavigation.test.tsx
```

Both-store cases exercise separate chat/executor permissions, safe choices and query pages, independent human-only denial, exact scoped readers, no organization inspection escalation, revocation, foreign targets and logout. HTTP checks derive the actual cookie human, validate bounds/search/scope and no-store, reject anonymous/foreign access, and verify archived chat availability. The existing HTTP lifecycle case covers resource-only inventory, read/update separation, archive/restore and stale revisions.

The 320px browser owner journey creates an examiner through visible controls after testing Cancel focus and Keep editing, grants exact agent Chat to Avery, grants credential-metadata read to the independent executor, verifies Core results without granting the member credential access, forces a real concurrent-security-change 409 on revoke, reloads and revokes the exact assignment, archives/restores and reloads. The second browser signs in as a scoped reader, observes absent edit/organization controls, then loses actual access and verifies private sections disappear. The owner journey records no page errors or legacy owner/admin requests.

Playwright MCP independently used loopback ports 61285/61286 and roots under `/private/tmp/agent-core-agent-access-20261017-leh049wb`: sign in, create exact template, grant Avery Chat, grant independent executor credential-metadata read, inspect Organization → Credentials → Read Allowed, archive/restore, verify creation close focus and inspect the corrected revoke confirmation without submitting it. Public authenticated APIs supplied member/role fixtures only. This manual host preceded the final archived-status correction; the final native CLI and HTTP cases above verify that correction against current backend source.

Initial verification found and corrected test-analyzer assertion forms, a confirmation transition assertion, a hidden option matching the selected label, and a test-only unsupported matcher option. The first browser fixture used nonexistent `events.read`; the API correctly denied it, and the fixture now uses a real catalog key for the concurrent change. Product fixes were the Select value/option label mismatch, archived Chat status, stretched tags, shared narrow-header area-label wrapping and lost creation-close focus. No assertions were weakened to hide those failures. Node 26's experimental native storage is disabled for jsdom as in earlier checks; no product storage workaround was added.

## Design and documentation evidence

The [Agent Access interaction contract](../19-multi-tenant-iam-ux.md#agent-inventory-and-access-interaction-contract) and surface brief preceded UI code. The extension reuses direct Ant Design v6 controls, AdminShell, collection toolbar/pagination, existing form-section/heading spacing, confirmations, draft guard and scoped query/command owners. No palette, typography, token system, UI kit or raster asset is introduced.

Two root visual rounds covered inventory, creation, chat audience and execution authority at 1440×900, 1200×647, 1024×768, 768×1024, 390×844 and 320×740. The shared mobile area label is hidden while the organization breadcrumb and actions remain. Account/Definition consumers at 390/320px have bounded headers and no document overflow. Confirmation measured tenant header 105px and Account header 87px on mobile, with no document/main horizontal overflow at all six feature widths. The long member label wraps, status tags retain natural width and the creation footer stays contained.

The one detector run returned `[]` (`/private/tmp/tenant-agent-access-detector.json`). A fresh finish reviewer validated all 29 initial captures and requested one material correction: direct chat-role revocation must name the target agent. The correction preserves the inherited organization-wide warning. The original capture matrix was recaptured and validated, with two additional 390/320px exact-revoke captures; evidence remains local under `.impeccable/review/agent-access/`. The verdict pass scored that fix **resolved**, with **disposition: ship** at the scope of the scored confirmation fix, retaining the earlier full-review findings. It confirmed valid recaptures and contained decision actions; it did not rerun runtime tests. No second detector or further discretionary root polish was run.

Canonical protocol, ports, backend/frontend/testing, UX, README and delivery-plan owners are synchronized. The incumbent DESIGN.md/sidecar remain the presentation baseline; pre-existing Audit narrative and historical pending-scope drift are separate from this ordinary extension.

A fresh finish documenter checked PRODUCT.md, DESIGN.md, the sidecar, direction contract, theme/CSS and access components. It confirmed the incumbent palette/type, 48rem reading/form width, 8/12/16px spacing and shared controls apply, and preserved those system files. Its scoped record is `/private/tmp/tenant-agent-access-design-documentation.md`. Existing narrative/provenance drift was reported without silently changing the system.

## Remaining scope

Complete Instance configuration/persona/Skills/resources/credential-binding UI, other resource Access panels, governance/platform administration and the remaining resource/effect/authority cutover remain open. Tenant voice/rich delivery, live mailbox input arbitration, autonomous origins and final obsolete-authority/schema removal are still required. The earlier intermittent SQLite preference provider-unknown follow-up remains unresolved. This is local Synthetic evidence, not live-provider, performance, full SQLite-browser, full proposal or milestone acceptance. CI was not requested or run.
