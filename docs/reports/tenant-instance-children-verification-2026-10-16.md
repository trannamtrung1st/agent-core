# Tenant Instance Skills and resources verification — 2026-10-16

This continues `667ac3d3` toward the full tenant/IAM/approval proposal. Canonical owned Instance child authoring now uses independent typed permissions and the existing atomic parent/security/audit transaction. The full goal remains active; this checkpoint does not accept the full tenant runtime, resource IAM or UI cutover.

## Implemented boundary

Every child operation resolves the actual tenant-owned Instance and requires `agents.read` at that exact parent. Skill content requires tenant `skills.read`; writes additionally require `skills.manage`. Resource metadata/bytes require tenant `resources.read`; writes require `resources.manage`, or `resources.delete` for deletion. Parent update permission does not substitute for these typed permissions. Per-item Skill/resource scope resolution and grants remain pending; this checkpoint implements the tenant-wide typed grant path only.

Canonical Skill commands share preparation with the incumbent service: bounded create/update/delete, inherited enabled choice/reset and atomic customization with source disable plus independent local content/provenance. Local readable IDs retain their owner-local namespace. Definition-bound resource dependencies reject unsupported customization. Built-in procedure content stays immutable; only the existing enable choices can change. Skill inspection resolves Instance overrides rather than reporting Definition defaults.

Resource upload, metadata-only edit, inherited copy, enabled choice/reset, local deletion and protected download use the actual owned manifest and private immutable bytes. New bindings require bytes; there is no caller-supplied hash authority. Metadata-only edits require an existing local binding and preserve its enabled choice when omitted. Paths, media, quotas, secret checks and active Skill dependencies reuse existing policies. Copy creates independent identity and source provenance. Downloads verify bytes/hash/length and recheck current permission plus parent revision after I/O. Deleted bindings do not delete shared immutable blobs.

Server-resolved principal kind controls agent restrictions. An agent writer needs its current Skills/Resources self-management area as well as its own typed IAM; policy enablement is not a role grant. Freeze or revoked IAM blocks subsequent operations. No requester privilege or client actor label is merged into the executing agent's authority.

Child, parent and tenant security revisions commit with audit under the same SQLite transaction or shared InMemory locks. Malformed nested ownership/commands, stale child/parent/security witnesses and cancelled attempts do not partially update the graph. SQLite and InMemory share preparation/staging, with equivalent missing-child handling. Private byte staging can leave an unbound immutable blob after a lost CAS; no public hash read exposes it.

Global Instance find/list/maintenance exclude tenant-owned roots, and public Skill/resource read/write ports reject them. Internal canonical backing helpers reuse the same actual records. Existing ownership/deletion tests now inspect canonical ports so absence assertions distinguish a missing owned root from a merely hidden global view. This closes these legacy paths for the converted data; other resource/effect/runtime owner paths still need conversion.

Effective configuration includes explicit `childAccess` flags and redacts denied Skill/procedure content, resource manifests and knowledge-source projections. Parent inspection remains usable without falsely presenting a denied child collection as an authoritative empty collection. No frontend source changed in this checkpoint.

## Observed workflows

| Use case | Expected and observed outcome |
| --- | --- |
| Owned Definition → publication → Instance → customize | Local copy retains procedure/source provenance and atomically disables the inherited source; stale edit/delete cannot overwrite it |
| Active resource dependency | Disabling a required inherited resource fails without changing parent/security revisions or audit; disabling the dependent Skill permits the choice/reset |
| Actual file lifecycle | Upload/download preserves exact bytes; inherited copy gets a fresh key/provenance; metadata-only rename retains a disabled local choice; SQLite recreation preserves content and choices |
| Parent-only Reader → independent child read grant → revoke | Effective configuration redacts children with explicit access flags; direct reads deny until the separate read grant; read grant permits content but cannot mutate; revocation denies again |
| Revocation during private byte read | A deterministic gate pauses byte I/O, revokes the reader grant, then releases I/O; no bytes return, while the still-authorized owner can read |
| Agent IAM versus management | Contributor IAM alone cannot write while management is disabled; human-enabled Skills area permits Agent-attributed creation; missing Resources area, freeze and role revocation deny their corresponding actions |
| Atomicity and isolation | Malformed child ownership/mixed commands and cancellation leave no child/audit; two same-witness writes produce one child, one parent/security increment and one success audit; old global ports cannot inspect/mutate owned data, including HTTP reads/deletion with a valid legacy owner capability |
| HTTP rejection/recovery | Forged hash-only upload, traversal path, foreign tenant targets and Built-in deletion reject; resource downloads are no-store attachments; deleted content bindings deny |

## Focused verification

Commands use `dotnet test <project> --filter '<selection>' --no-restore --nologo --disable-build-servers -p:UseSharedCompilation=false -m:1 -v minimal` with approved local execution. No provider credentials or outbound provider calls are needed.

| Project / selection | Result |
| --- | --- |
| API `Owned_instance_skills_and_resources` and `Owned_instance_inherited_resource_copy` | 2 passed, zero skipped; the representative HTTP journey ran before broader regression coverage; a final overlapping rerun also passed the valid-legacy-owner HTTP denials |
| API `TenantDefinitionAccessTests`, `TenantAgentAccessTests`, `AgentInstanceSkillTests`, `SelfManagementAuthorityTests`, `SelfManagementOperationTests`, `StateIsolationDefinitionInheritanceTests`, `HarnessManagementRecoveryTests`, `SqliteHostRecoveryTests` | 45 passed, zero skipped after the fixture repair; includes the two child HTTP journeys and actual recovery/error paths |
| Infrastructure `Tenant_instance_child_transactions_reject` final assertions | 2 passed on both stores; overlapping rerun additionally rejects empty/mixed resource commands and unbacked hashes with no partial child/audit |
| Infrastructure `Tenant_instance_child` | Initial 6 passed; the final selected family below includes all 8 cases after adding the gated revocation pair |
| Infrastructure `TenantDefinitionIntegrationTests`, `IamIdentityIntegrationTests`, `AgentInstanceStoreContractTests`, `AdminInstanceDefinitionVersionHistoryTests`, `AdminInstancePersonaHistoryTests`, `AdminInstanceLifecycleHistoryTests`, `AgentDefinitionResourceAdminStoreContractTests`, `InstanceSkillsStartupTests` | 168 passed, zero skipped; includes both stores, child cases, publication/deletion dependencies, shared store/history consumers and SQLite reopen |

Projects are `tests/AgentCore.Api.Tests/AgentCore.Api.Tests.csproj` and `tests/AgentCore.Infrastructure.Tests/AgentCore.Infrastructure.Tests.csproj`. Each filter term is prefixed `FullyQualifiedName~` and joined with `|`. A fixture compile error used `DraftRevision` instead of the actual batch `Revision`; the corrected tests passed. All affected source compiled in the executed suites.

The broader API regression initially passed 40 cases and failed five before their scenarios reached application behavior: older gated/failing SQLite hosts lacked the required explicit identity bootstrap. Those five fixture factories now call the existing `TestSqliteIdentity.Configure` helper with their own disposable database/key root. Production startup remains strict and receives no automatic-bootstrap exemption. The complete selected API rerun passed 45/45, including actual recovery/error scenarios. Changed Markdown local links/fragments, fenced blocks, complete JSON examples and whitespace were checked before commit.

## Remaining scope

Per-item Skill/resource scopes and grants, tenant Sandbox selection/decommission, complete Instance/editor UI and rich preview/access panels, other owning resource/effect stores, Session/Run/Hub/tool/background tenancy, trusted executor/requester/root lineage, durable approval execution, obsolete global authority removal and final schema/reset consolidation remain required. Configuration authoring evidence does not prove protected tenant execution. The [full delivery ledger](multitenant-iam-delivery-2026-10-16.md) retains these obligations.

Verification used disposable Synthetic HTTP, InMemory and temporary SQLite fixtures. No new frontend/browser/visual acceptance is claimed; existing user data, Real hosts and credentials were preserved. No deployment, restart, user-data reset or CI was performed.
