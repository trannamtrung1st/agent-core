# Compound management inspection — 2026-10-22

This module extends `0ea392a3` with canonical `harness.inspect` and `configuration.inspect` through the existing management-read, exact approval, source-retention and receipt owners. The original IAM goal remains active. Harness authoring, resource/Skill read/write tools, private Skill loading, remedy execution, approval-default review, final consolidation and proposal-wide acceptance remain open.

## Delivered boundary

Harness inspection retains its approved trusted Harness delegation independently of Definition tool selection. Configuration inspection requires its Definition capability. Both require an actual conversational human root, admitted/current management intersections, current raw Agent read and independently readable child sources. Each delegated area controls its own disclosure. Harness knowledge includes only Knowledge resources. Definition capability ceilings, current Agent selections and admitted Run versions remain separate facts; an observation never widens execution authority.

Existing owned Skill/resource views and credential/binding snapshots supply bounded metadata. Procedures, resource bytes, credential metadata bodies, profiles and secrets are excluded. Active credentials of every purpose expose only independently readable identity/name/kind/purpose/revision metadata. Current raw rights filter before each metadata window; unreadable names/counts are absent. Child witnesses bind the exact owner scope, stable key and observed metadata fingerprint. Fingerprints exclude unshown private procedures and resource dependencies.

Each delivered child participates in current read policy. Child Review can withhold the whole observation even when Agent read itself needs no review. Exact consent grants no raw permission, write or future read. Current human child scopes filter Inbox and conversation visibility before counts/paging. Raw read is rechecked before revealing a changed-source notice. Source changes/revocation withhold captured observations before model reentry and protected output. Canonical optional witnesses survive checkpoints; incumbent simple-read serialization and hashes remain unchanged when witnesses are absent.

Targeted diagnosis omits unrelated inventories and distinguishes unsupported goals/targets, absent delegation, Definition denial, host setup, Instance selection, current Run pins and readable binding absence. Returned preference/capability options contain exact complete replacement values, expected revision and stable option hash. Oversized options are withheld rather than clipped. Options do not execute. Canonical Agent-settings destinations remain available for human setup. Diagnosis wording normalizes to the same target for refusal correlation while exact action hashes still bind full arguments and observations.

No table, permission, execution engine or second approval framework is added. The projection uses the incumbent tool-configuration gate through a deferred store dependency, avoiding an eager IAM/provider configuration cycle.

## Persistence and recovery

`20261022090000_TenantManagementInspections` extends the existing closed approval payload and preserves all **117** incumbent trigger definitions, indexes and foreign keys. Existing immutable-read guards freeze child witnesses. Downgrade refuses retained compound requests and checkpoint references before lossy conversion, while tolerating opaque older checkpoint payloads. A real SQLite host recreation retains pending exact consent and its child witnesses; the reopened host decides and completes it through the normal Runtime.

The Infrastructure assembly reached the .NET user-string heap limit during qualification. One historical generated target-model constraint, `20261021214545_TenantExperienceConsent.Designer.cs`, now loads its **byte-identical** frozen SQL resource. Historical Up/Down execution statements are unchanged. The migration-model test pins the original 143,301 UTF-8 bytes to SHA-256 `2814491da6b894a90c3411ac3b1096906001588dd2370957d9bc59f88a2e3d05`. This changes metadata representation only; it does not remove schema history or convert installed data.

## Presentation and observed journeys

The existing shared Ant Design inspector labels versions, areas, instruction excerpts, individual settings and sources, child metadata, diagnosis and exact options. Script-like strings remain literal text. No product TSX/CSS or UI kit was added. Captured [desktop Harness](assets/tenant-management-inspection-2026-10-22/harness-desktop.png), [phone Harness](assets/tenant-management-inspection-2026-10-22/harness-narrow.png) and [phone configuration](assets/tenant-management-inspection-2026-10-22/configuration-narrow.png) show the exercised presentation.

Native browser journeys use actual Synthetic login, lazy tool discovery, Chat, exact saved read and approve/reject confirmation for both tools at 1440×1000 and 390×844. Expected and observed: approval completes a factual read receipt, rejection reports that the read was not authorized, private sentinels remain absent, script text is escaped, the footer remains reachable, no horizontal overflow occurs and Agent revision stays **3**. No page or non-aborted request failure was recorded.

Playwright MCP independently sent both Synthetic commands through the visible composer. Harness approval returned **Read authorized** and “The exact saved Agent configuration was inspected. No settings were changed.” Configuration rejection returned “The Agent configuration read was not authorized. No settings were changed.” Canonical readback remained revision **3**. The session recorded zero console errors and zero non-aborted failed/API error requests. These are real integrated Synthetic flows, with no hosted provider calls.

## Executed checks

**214 distinct focused cases passed, plus eight native CLI browser journeys.** Successful identities combine grouped runs and focused repair rechecks; repeated identities are not counted twice.

| Check | Result |
| --- | --- |
| Serial solution build and final affected API-project build | Passed, zero warnings/errors, using `--no-restore --nologo -m:1 -nodeReuse:false -p:UseSharedCompilation=false`. |
| Compound API/Runtime/store module | **47 distinct passed** across both stores: 8 metadata/authority cases, 12 exact consent decisions, 8 source races, 14 diagnosis cases, 2 scoped visibility cases, 1 actual SQLite host recreation and 2 refusal-root cases. The grouped run passed 45 and failed 2; both repaired refusal identities then passed. |
| Adjacent API/Runtime regression | **100 passed**: canonical management reads, exact read Review, retained sources, visibility/refusal, immutable Core procedures, preference authorization and management-mutation recovery. |
| Application contracts/projections | **65 passed**: `ManagementInspectionContractTests`, `ManagementReadContractTests`, `ManagementProjectionTests`, `ManagementMutationContractTests`. |
| Infrastructure schema | **2 passed**: `TenantManagementInspectionMigrationTests`, including preserved guards/indexes/FKs, model roundtrip and historical target-model byte hash. |
| Frontend build | `pnpm run build` passed; existing large-bundle warning remains. No production frontend code changed, so the previously qualified shared-inspector Vitest suite was not repeated. |
| Native Synthetic CLI | **8 passed**, 42.4 seconds, no retries: both tools × approve/reject × desktop/phone. |
| Playwright MCP | Harness approval and configuration rejection completed, current Agent revision unchanged, no console or non-aborted API/network failures. |
| Documentation | Changed local links, Markdown fences and new JSON examples checked; delivery ledger retains remaining work. |

The compound API filter is `FullyQualifiedName~Canonical_management_inspection`. The affected API regression filter includes `Canonical_management_reads_`, `Canonical_management_read_Review`, `Management_read_`, `Tenant_Core_procedures_use_claimed`, `Model_managed_preferences_use_current`, `Model_preference_authorization_persists` and `Management_mutation_pending`. Tests use `dotnet test <project> --no-build --no-restore --nologo --filter <filter>` against disposable Synthetic stores.

The retained browser command is `pnpm exec playwright test -c playwright.tenant-chat.config.ts tenant-management-inspections.spec.ts` from `web/`. Qualification used a temporary single-worker configuration against owned Synthetic ports 5096/5186. The temporary configuration was removed, MCP closed and only the owned listeners stopped.

## Repairs and remaining qualification

Qualification repaired explicit production/test store projection factories, trusted Harness eligibility, deferred DI resolution, exact remedy clipping, SQLite semantic refusal lookup and downgrade handling of opaque checkpoints. SQLite's persisted fingerprint column contains the exact action hash; semantic refusal lookup now compares captured targeted form/target rather than mistaking that column for the normalized intent. Existing offering assertions account for the new tool family. The historical mutation-recovery fixture restores the current schema after a deliberately refused downgrade traverses the newer migration. Browser setup uses one actual login session and the actual Knowledge enum value; neither login limits nor product checks were bypassed.

This is focused local module qualification. CI, live-provider UAT, full proposal/milestone acceptance and new remedy execution were not run. Remaining work is recorded in the [delivery ledger](multitenant-iam-delivery-2026-10-16.md); scoped passing evidence does not establish whole-proposal completion.
