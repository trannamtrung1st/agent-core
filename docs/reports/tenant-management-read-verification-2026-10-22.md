# Tenant management observations — 2026-10-22

This module qualifies four canonical conversational model reads on top of `164d00a2`: `management.inspect`, `settings.list`, `settings.inspect` and `automation.preferences.inspect`. The IAM proposal remains active. General Harness/configuration inspection and authoring, resource/Skill management, private Skill loading, typed remediation, approval-default review and final authority/schema consolidation remain open.

## Delivered boundary

The existing Runtime lazily discovers the four tools through contextual `capabilities.load`. Its mailbox and revisioned Run command remain the discovery owner. Capability projection intersects admitted and current selection so later grants cannot widen an old Run. Reads require the actual owned conversational human task, current executor `agents.read`, current claim/requester and both admitted/current Self-Management mode, scope and capability. Wider owner selectors, independent/background/completion origins and missing raw read authority remain unavailable.

The narrow `ITenantManagementReadStore` facet observes the existing owned Instance/Run/security aggregate. Values retain exact Instance/configuration witnesses, Definition/current-Run versions, source/default/constraint information, explicit snapshot and excerpt markers, and next-Run timing. All 12 setting sections are exercised. Sandbox returns only the selected profile reference, withholding host scripts, setup and limits. The unattended read observes only its existing override choices; it does not admit work or refresh the executing model.

Required read Review withholds the observation until an exact `inspect_management` decision through the incumbent approval/receipt/successor machinery. Consent supplies no missing raw permission, configuration change or future read authority. Saved witnesses survive compaction and are rechecked before later model use, protected output and outcome commit. A contiguous same-task completed receipt chain can explain only the three incumbent conversation preference fields; unrelated configuration changes and lost reads/management fail closed. Human source inspection uses current human read authority. Changed sources withhold replacement values while preserving qualified cancellation.

Inbox filtering applies exact Agent read scope before counts/paging. The shared Chat/Inbox inspector renders literal saved values, meaningful field/source labels, explicit read-only confirmation and factual authorization status using the existing Ant Design layout. [Desktop](assets/tenant-management-read-2026-10-22/read-desktop.png) and [narrow](assets/tenant-management-read-2026-10-22/read-narrow.png) evidence records the actual flow. No CSS, transport, execution engine, permission or table is added.

## Persistence and recovery

`20261022070000_TenantManagementReadConsent` extends the closed existing payload and preserves all 112 incumbent trigger definitions and indexes. Two guards bind the exact owned human request root/current Instance and freeze the prepared observation, giving 114 total. Empty upgrade/downgrade/re-upgrade preserves the predecessor guards, current model and foreign keys. Downgrade refuses retained management-read requests or completed read receipts before conversion; historical migrations remain unchanged.

The actual SQLite API host was stopped with a pending exact consent and recreated over its disposable database. A new authenticated session read and approved that saved request. The worker produced exactly one completed successor with retained source, factual receipt, unchanged Instance and no grant. A separately reopened store, supplied the same immutable Definition catalog, validated the completed source. A downgrade attempt with the pending request was refused without losing it.

## Executed verification

**336 distinct focused cases passed, plus four browser journeys.** Counts combine successful case identities across grouped runs and targeted repair rechecks; they are not one full-suite command.

| Check | Result |
| --- | --- |
| .NET solution and final affected API/test-project build | Pass; zero warnings/errors. Serial `dotnet build ... --no-restore --nologo -m:1 -nodeReuse:false -p:UseSharedCompilation=false`. |
| Management HTTP/Runtime/store matrix | **63 passed across qualification/recheck**: lazy discovery, all setting sections, source receipts, withheld Review, approve/reject, source-change/read-revocation/freeze checkpoint boundaries, pending cancellation, strict selectors/missing authority, exact scoped inbox filtering and SQLite host recreation. Both InMemory and SQLite. |
| Adjacent canonical API regressions | **38 passed**: 16 Core procedure, 8 direct preference and 14 approved preference cases. Admission pins, revocation, exact successor and receipt behavior remain intact. |
| Application | **79 passed**: `ManagementReadContractTests`, `ManagementProjectionTests`, `CapabilityProjectionTests`, `CapabilityDiscoveryRuntimeTests`, `TenantApprovalToolContractTests`. |
| Infrastructure | **29 passed**: `TenantManagementReadMigrationTests` and selected existing exact approval, current reviewer/target, inventory, generation, concurrent-root and atomic persona-effect cases. |
| Shared inspector | **127 passed**: `NODE_OPTIONS=--no-experimental-webstorage pnpm exec vitest run src/features/access/TenantApprovals.test.tsx`. Existing Ant Design test-only NaN-height warning remains. |
| Frontend build | `pnpm run build` passed; existing large-bundle warning remains. |
| Native Playwright CLI | **4 passed**: approve/reject at 1440×1000 and 390×844 through real login, Chat, shared inspector and persisted successor. Literal script text stayed escaped, controls stayed in view, no document overflow or captured non-aborted request/page failures, Instance revision unchanged. |
| Playwright MCP | Independently signed in, sent a conversation-policy read, reviewed and approved its saved values, observed **Read authorized** and the factual reply. Sent a fresh setting-list read, inspected its labeled section window and rejected it; observed the refusal reply. Zero console errors; captured failed requests were navigation/refresh aborts. |

API filters selected `Management_read`, `Canonical_management`, `Core_procedure`, `Model_managed_preferences` and `Model_preference_authorization`; the corrected restart case was rerun with the adjacent Core matrix. All .NET execution used `--no-build --no-restore --nologo`, Synthetic adapters and disposable stores. The initial broad Infrastructure class filter unintentionally selected 899 cases and was interrupted; no passing total is claimed for that run. The final 29-case selection above covers this module's affected persistence/approval boundaries. CI and live-provider UAT were not requested or run.

Qualification found a missing Definition dependency in the manually reopened test adapter after the real restarted host had already succeeded; the fixture was corrected and the recovery test passed. The browser review exposed generic saved-field labels; their shared management-read presentation was corrected, then all inspector cases and four native browser journeys passed. An initial new UI assertion expected lowercase `language`; it now checks the existing **Language** label. These repair rechecks do not duplicate the distinct totals above.

## Remaining scope

This is scoped local qualification, not complete Harness/Self-Management or proposal acceptance. `harness.inspect`, `configuration.inspect`, general settings/policy/child writes, binding changes, private Skill/resource loading and typed remediation remain pending canonical integration. The mandatory autonomous approval-default audit, safe dependency/schema consolidation, historical boundary-reference repair, event-review retry investigation and final security/isolation/functional/UI acceptance remain required in the current delivery ledger.
