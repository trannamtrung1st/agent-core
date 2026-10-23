# Global lifecycle deletion retirement — 2026-10-24

Status: this lifecycle authority slice is locally qualified. The original IAM goal remains active.

## Retired authority

Removed `IAdminLifecycleDeletion`, both registrations/implementations, caller-built `AdminInstanceDeleteCommand` / `AdminDefinitionDeleteCommand` with LocalOwner defaults, unused deletion messages, the global Definition purge/witness implementation and the old deletion-event receipt workspace sweep. The port had no production consumers. Canonical Agent decommission and Definition retirement already use separate scoped IAM/store transactions. The old Experience test host no longer registers the removed port.

Removed unused in-memory Instance remove/restore helpers behind that path. The identity integration test retains canonical creation/inventory/archive/restore witnesses, rejected unscoped mutation and orphan/reparenting checks. SQLite still rejects physical deletion through its FK guard. InMemory retains the backing identity without an old physical delete/restore helper.

Canonical SQLite callers retain their shared read-only blocker queries in internal `TenantDeletionReferenceQueries`. Instance queries require an explicit tenant instead of an optional unowned default. Current reference counts, cancelled-Automation rules and retired-identity exclusion remain. These helpers have no mutation, cleanup or registration authority. The canonical transaction and native cleanup owners remain unchanged.

Pure blocker-count records and Definition draft/publication revision witnesses remain because current protocols use them. Historical Admin event summaries remain readable. No migration, stored event rewrite, consent serialization or hash change is introduced; inert Harness preparation is untouched.

## Historical fixture reconciliation

Original bodies remain in commit `1f0c4a60`.

| Removed fixture | Current qualification |
| --- | --- |
| Seven global lifecycle actor methods, 18 declared cases across both stores | Canonical Definition deletion covers complete revision witnesses, stale/foreign/suspended authority, retained Instance/Session/Run/Activation blockers, child/grant retirement and creation/deletion races. Canonical Agent decommission covers archived-only eligibility, actual human authority, immutable identity retirement and factual cleanup. |
| Three `AgentWorkspaceDeletionTests` methods, ten declared cases | Current login/password/security refusal preserves exact bytes and Archived identity without a retirement audit. Owned workspace purge failure retains committed retirement and original bytes, then recovers through exact retry or cold sweep. Active siblings and mismatched executors cannot inherit the cleanup mandate. |
| Skill identity case coupled to the old actor fixture | Extracted the same storage identity/CAS behavior into two `AgentSkillOwnerIdentityTests` cases: cross-owner ID reuse, same-owner duplicate refusal and revision-checked edit/delete preserving the sibling. This is native storage qualification, not unowned actor authorization. |

The old workspace fixture recreated a physically deleted global Instance ID. Current decommission retains an immutable identity tombstone and forbids revival; continuity/SQLite guard cases and native active-sibling preservation qualify the current requirement. Historical deletion receipts do not supply a new owner's cleanup mandate.

## Local evidence

**65 distinct focused cases pass; none are skipped.**

- Infrastructure: 43 cases. The initial group passed 37, including both actual Chromium decommission/sibling-isolation cases, canonical Definition deletion, DI and native Skill identity. Four new workspace recovery cases failed during setup because sentinel files preceded canonical layout initialization. Initializing the fresh disposable layout first corrected the fixture; no layout guard or reset policy was weakened. The final six cases pass: four recovery cases plus both identity-retention integration cases after removing the unused mutators.
- API: four cases pass: both-store human decommission, actual SQLite application-host cleanup recovery and Definition deletion revision/reference/current-authority behavior.
- Application: 18 historical Admin event summary/privacy cases pass.
- Both serial solution builds pass with zero warnings/errors; the final build took 41.69 seconds.

Observed results: rejected owning commits preserve exact sentinel bytes, Archived metadata and no retirement audit. A controlled workspace purge IOException retains the committed Removed identity and original bytes. Retry/cold sweep deletes only the exact retired owner's files, preserves an active sibling and records one retirement audit. Actual Chromium closes the owned persistent profile after Session deletion while its live sibling remains usable. HTTP flows retain current archived-only and human-authority controls; application recreation resumes committed cleanup. Historical summaries and per-owner Skill identity/CAS remain intact.

Commands use `dotnet test <project> --no-build --no-restore --nologo --filter <selection>`, prefixing each test term below with `FullyQualifiedName~`:

```text
Build: dotnet build AgentCore.sln --no-restore --nologo -m:1 -nodeReuse:false -p:UseSharedCompilation=false
Infrastructure initial (AGENTCORE_CHROMIUM_ISOLATION_TESTS=1): TenantAgentDecommissionTests|AgentSkillOwnerIdentityTests|Decommission_closes_actual_owned_native_profile|DefinitionResourceInfrastructureDiTests|Deleting_last_unpublished_draft|Definition_deletion_uses_all_revision_witnesses|Archived_instances_and_retained_session_activations_block_definition_deletion|Delete_rejects_foreign_targets_stale_security|Instance_creation_and_definition_retirement_share_one_atomic_security_witness
Infrastructure final: Decommission_workspace_purge_failure_recovers_exact_retirement|Actual_instances_have_atomic_identity_scoped_inventory_and_archive_revocation
API: Tenant_Agent_decommission_|Definition_delete_http_preserves_revision_and_reference_recovery
Application: AdminEventSummaryPolicyTests
```

Logs are `/private/tmp/tenant-lifecycle-retirement-{build,final-build,store,store-final,api,application}.log` (brace notation names separate files). C# scans find no reference to the retired port, executors, command types, global purge, deletion sweep or in-memory remove helper. Changed document links/fences and staged whitespace are checked separately.

Fixtures use disposable roots and synthetic credentials. No user database, installed data, Real host, live provider, CI run, push or deployment is involved. This is backend/native evidence; no new rendered frontend journey is claimed.

## Remaining goal

The full proposal remains open: caller-by-caller physical store/native authority inventory, broader approval dependency and safe schema consolidation, coordinated inert Harness retirement, Composer reference-scope reconciliation and final proposal-wide security/isolation/functional/rendered UI acceptance. Historical milestones retain their original boundaries.
