# Tenant version-review backend verification — 2026-10-18

This checkpoint supplies the backend prerequisite for reviewed tenant Instance Definition adoption. It does not claim that the tenant version page has shipped or accept the full proposal/milestone. The version UI, Skills/resources, tenant Sandbox and remaining resource/effect families, voice/rich delivery, autonomous origins and obsolete-authority/schema consolidation remain open. The earlier SQLite direct-preference reliability follow-up remains unchanged.

## Change

The canonical agent API now provides exact-source `/versions` paging and `/active-version/preview?version=N` reads through `TenantAgentReadService`. Owned sources require independent exact Definition-read permission. Shipped lookups use only the immutable built-in store; they require no unrelated Instance-creation permission and have no global durable-publication fallback. Version responses contain safe metadata, exact source/current Instance identity, current security witnesses and advisory adoption hints.

Preview and adoption share one private preparation path using the existing settings/model resolver, merged Skill/resource validation and bounded Automation planner. Preview applies no plan or stored Skill state. Adoption retains its lifecycle gate, current original-human authority and atomic tenant/Instance/publication/Automation witnesses. A preview is neither an authorization token nor a replay instruction.

The pure `InstanceVersionPreviewFactory` also serves the existing administrative preview. It now includes management defaults and other Definition defaults omitted by the prior projection, while retaining inherited-item changes. Preserved choices distinguish actual sparse management overrides/Frozen from inherited fields and retain persona, class budgets and unattended preference hints. Tenant projection filters Skill and resource keys independently before constructing child diffs; unreadable/future children have no names, IDs or hidden totals in the result. Child visibility flags describe permission coverage. Compatibility validation errors are generic; non-validation failures/cancellation keep their normal behavior. No schema, provider, public debug hook, new authority engine or frontend component was added.

## Observed workflows

- InMemory and SQLite: publish owned v1, create an Instance with its own persona, publish v2 with changed instructions/management defaults, page its exact source and preview v2. Current version/revisions remain unchanged until explicit adoption. Adoption preserves persona and existing content, and SQLite reopen resolves the saved v2.
- Prepare an Automation reconciliation during preview: registration status/revision stays Active/unchanged. Explicit adoption performs the existing atomic policy reconciliation.
- A reader with only exact parent/child assignments cannot preview an owned source without Definition read. After that grant, only readable removed children appear; a future private Skill remains absent. Revoking source read denies the version catalog. Existing removed-child grants retire on adoption/reopen and do not revive on later version restoration.
- A retained daily-scheduling override conflicts with the target's narrowed policy. Preview returns generic incompatibility and adoption rejects; saved Instance, management choices and security revision remain unchanged. Deprecated versions remain labeled catalog history and cannot be previewed for adoption.
- Public HTTP: passed. The scenario reads a general-assistant preview, changes settings, rejects the old reviewed witnesses, explicitly refreshes/adopts, and verifies retained language. It also checks paging/version errors, no-store, foreign and unauthenticated reads.

## Focused local verification

| Check | Result |
| --- | --- |
| Initial preview→adopt→reopen use case | 2 passed, before broadening coverage |
| Infrastructure read races plus owned adoption, child filtering, incompatible overrides and Automation planning | 68 passed in InMemory/SQLite |
| Final affected projection/use-case cases after management-choice refinement | 8 passed; these repeat cases from the 68, not additional distinct cases |
| HTTP authorization/read races, reviewed adoption, version write fence and existing administrative preview/Run consumer | 32 passed |

Infrastructure command: `dotnet test tests/AgentCore.Infrastructure.Tests/AgentCore.Infrastructure.Tests.csproj --no-restore --nologo --filter 'FullyQualifiedName~Human_agent_reads_reject_captured_configuration|FullyQualifiedName~Tenant_instance_configuration_resolves_owned_resources|FullyQualifiedName~Tenant_adoption_rechecks_publication|FullyQualifiedName~Tenant_child_scopes_resolve_current_publication|FullyQualifiedName~Tenant_version_preview_rejects'`. The final eight-case command uses the same filter without the read-race method. Logs are `/private/tmp/tenant-version-preview-normal.log`, `/private/tmp/tenant-version-preview-infra.log` and `/private/tmp/tenant-version-preview-infra-confirm.log`.

HTTP command: `dotnet test tests/AgentCore.Api.Tests/AgentCore.Api.Tests.csproj --no-restore --nologo --filter 'FullyQualifiedName~TenantAgentAccessTests.Agent_read_does_not_release|FullyQualifiedName~Creation_scoped_inventory_and_archive_restore_use_current_tenant_authority|FullyQualifiedName~Version_catalog_and_preview_are_scoped|(FullyQualifiedName~Every_agent_HTTP_mutation&DisplayName~version)|FullyQualifiedName~ScopedInstanceConfigurationTests.Old_session_new_turn_refreshes_exact_version_settings_resources_and_preserves_old_run'`. Log: `/private/tmp/tenant-version-preview-api.log`.

The first compile found a missing namespace import, corrected before the normal workflow passed. Final management-choice coverage initially compared collection-backed records by reference; the unchanged persisted values were confirmed using serialized value equality instead. These were implementation/test-harness corrections, not unverified acceptance claims.

All execution uses deterministic/Synthetic adapters or temporary InMemory/SQLite stores. The checks build the affected .NET projects. No hosted CI, full CI suite, live provider, installed-data reset or deployment was run. This backend-only change adds no new rendered surface; browser/Impeccable acceptance belongs to the future tenant version page. Existing design-system tokens and the previous controls visual evidence remain unchanged.

Canonical interfaces, backend/protocol/test documentation and the version interaction contract are synchronized. README, TODO, implementation plan and delivery ledger keep the full proposal and tenant version UI explicitly open.

Total distinct focused cases: 100 (68 Infrastructure, 32 API). Repeated normal/final projection checks are not added to that total. Both final test handles completed with exit code 0.
