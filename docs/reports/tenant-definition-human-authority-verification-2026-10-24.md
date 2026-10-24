# Definition lifecycle original-human authority — 2026-10-24

Preimage: `db86dbeb`. The remaining store/effect review reproduced a real create-after-logout HTTP race in Definition authoring. This module treats the complete Definition lifecycle together. The original proposal, other method/effect families and final rendered design gate remain active.

## Reproduction and correction

An actual Synthetic HTTP Owner request prepared a Definition create, then logged out that original login immediately before the owning store transaction. The pre-fix response was **201 Created**, where the regression expected a refusal. Current Account and IAM status alone did not notice retirement of the AuthenticationSession captured by the in-flight HttpContext. The baseline is an observed failure, separate from passing evidence: `/private/tmp/tenant-definition-human-baseline-20261024.log` and `tests/AgentCore.Api.Tests/TestResults/tenant-definition-human-baseline-20261024.trx`.

All current Definition HTTP handlers now derive the original Account/authentication-session/principal through the existing `IdentityHttp.TenantHuman` helper. The Application service reuses `IdentityService` and `TenantHumanSessionAuthority`, without accepting identity proof from request bodies or headers. Fourteen read methods cover root/summary/access, drafts/publications, coherent resource/evaluation state, verified content and approved templates. An outer read witness revalidates the original login and unchanged tenant security revision after the complete source/visibility I/O. Authoring-options permission checks use the same original-human witness before returning the existing safe configured choices.

Thirteen service commands forward that reference through all eleven owning mutation slots: create/template, update/fork, publish/deprecate, resource batch/remove, scenario save/remove, evaluation result and draft/root deletion. Both stores reuse their incumbent original-human helper inside the existing ownership lock/transaction before any change; SQLite repeats it before final commit. Current active actor, security/draft/publication/scenario/source revisions, exact resource grants, deletion references, evaluation fingerprint and safe audit remain independently enforced. Offline evaluation also repeats the original-human/security witness after byte reads and before bounded computation, then validates again at result commit.

Logout/password rotation yields 403 `initiator_unavailable`, withholding captured response data and refusing any authoring graph, security revision or success-audit change. Staging may conservatively retain unreferenced immutable content bytes; it creates no binding or read authority. Computation completed before a final refusal is not claimed to be undone. No permission, approval floor, grant lifetime, schema, DTO, route, tool, provider or frontend behavior is added. Principal-only internal callers retain their current scoped contract; model consent/effects and autonomous Runs keep their independent exact authority rather than acquiring an invented human.

## Runtime verification

**218 distinct cases pass, zero final failures/skips**: 128 new direct HTTP race/recovery journeys (7m54s), six existing HTTP regressions (8 s) and 84 owning-store regressions (4m2s). Each process is terminal with exit code 0. The one-case pre-fix reproduction remains a deliberately failed baseline and is not included in the passing total.

The final solution build passes with zero warnings/errors (52.26 s), using:

```sh
dotnet build AgentCore.sln --no-restore --nologo -m:1 -nodeReuse:false -p:UseSharedCompilation=false
dotnet test tests/AgentCore.Api.Tests/AgentCore.Api.Tests.csproj --no-build --no-restore --nologo --filter 'FullyQualifiedName~Definition_HTTP_commit_rechecks_original_login|FullyQualifiedName~Definition_HTTP_read_withholds_captured_metadata'
```

The new fixture uses real authenticated HTTP, shipped template sources, file-backed immutable content, and actual InMemory/SQLite owners. Each private factory uses a new temporary root, key directory and database. The only decorators schedule logout/password rotation after actual capture or immediately before the owning transaction; they forward the actual original-human reference unchanged. Fresh login recovery explicitly submits the same unchanged request and witnesses, proving that refusal does not destroy authoring work or fabricate an automatic retry.

The 56 mutation journeys cover fourteen operations × both stores × logout/password rotation: create, template fork, update, publication fork, publish, deprecate, resource add/remove, scenario save/remove, evaluate, delete a retained-root draft, delete the last unpublished draft, and delete the complete Definition. They compare saved tenant state, roots/counts, all draft/resource/scenario/result snapshots, immutable publications/resources and audit before/after refusal. The retired cookie returns 401; explicit fresh-login retry succeeds with exactly one security revision advance.

The 72 read journeys cover sixteen surfaces × both stores × both retirement modes, plus late byte capture for draft/publication downloads. Captured root/summary/access/draft/state/resources/publication/scenarios/results/templates/authoring metadata and bytes are refused after original-login retirement. Errors contain no private Definition name/content, inventory or permission projection. A fresh login can read the same unchanged source; all authoring/audit facts remain unchanged.

The six existing HTTP regression journeys pass (8 s), covering owned author → publish → instantiate, resource upload/evaluate/publish/fork/read, approved template copying, revision/reference-aware deletion, mutation-only-role disclosure refusal and item-scoped child filtering. The 84-case owning-store regression group passes (4m2s), covering both-store immutable publications, source isolation, required evaluation/CAS, Account suspension, resource scopes/I/O races, source-preserving no-op preparation, exact deletion references and shipped-template failures/rollback.

The existing API regression filter joins `FullyQualifiedName~` for `Author_publish_and_instantiate_use_owned_publications`, `Owned_resource_upload_evaluate_publish_and_read`, `Shipped_template_http_fork`, `Definition_delete_http`, `Mutation_only_roles_cannot_receive_stored_definition` and `Item_scoped_child_grants_filter`. The Infrastructure filter uses the same form for the following exact prefixes:

```text
Owned_definitions_publish_immutable
Publication_requires_current_resource_and_evaluation_evidence
Account_suspension_fences_prepared_definition
Resource_batches_and_required_evaluation
Evaluation_completion_loses_changed_draft
Resource_batch_conflicts_and_competing_writes
Definition_resource_grants_retire
Definition_resource_scope_grant_delete_races
Definition_resource_download_rechecks
Deleting_last_unpublished_draft
Definition_deletion_uses_all_revision_witnesses
Archived_instances_and_retained_session_activations
Delete_rejects_foreign_targets
Instance_creation_and_definition_retirement
Shipped_template_fork_owns
Template_workspace_and_skill_dependencies
Invalid_approved_template_content
Prepared_template_fork_loses
Initial_template_bindings_verify
Resource_unchanged_commands_preserve
Resource_path_conflicts_fail_preparation
Resource_unchanged_metadata_refuses
```

Build log: `/private/tmp/tenant-definition-human-build-20261024.log`. Test logs: `/private/tmp/tenant-definition-human-{cases,api-regressions,infra-regressions}-20261024.log`, with corresponding `tenant-definition-human-*.trx` in each project’s `TestResults` directory. No product C# changed after the passing build. Final test outcomes and terminal handles are recorded above; source binding analysis is not counted as a runtime test.

## Method and authority reconciliation

All **44 Definition-family callable slots** receive explicit source dispositions in the [current method index](tenant-store-method-source-index-2026-10-24.json): nineteen canonical owner methods, ten native authoring methods, seven native resource methods, six native evaluation methods and two physical byte methods. Eighteen of these have no bound production caller; they remain explicitly reviewed native data/test contracts rather than inferred private endpoints.

Canonical queries resolve actual active tenant roots; allowed IDs filter before inventory totals. Child reads use actual owning drafts/publications. Every canonical mutation shares its existing owning transaction and exact witnesses. Publication consumers outside HTTP authoring retain their own owned Instance/Run/background/source fences; the original-human argument is not a universal Run requirement.

All native authoring/resource/evaluation entry points exclude owned and retired owner markers. InMemory checks actual owned Definition/draft state; SQLite global query filters join the actual owner, including resource/scenario/result children. Native create checks ownership inside its transaction; result insertion explicitly refuses owned drafts. Native exact-source readers cannot resolve an owned durable publication. Canonical owned configuration pins independently eligible bindings instead of falling back to an unscoped durable catalog.

Immutable content storage is a physical mechanism, not tenant authority. It verifies hash/bytes and bounded physical paths; current Definition/Instance human byte delivery uses the independent no-follow verified-source facet. Retained pinned/management-inspection reads verify hash/size under their separate current typed source fences; the raw hash reader itself is neither a no-follow nor an authorization primitive. Binding an HTTP-provided hash without actual upload/already-owned binding remains forbidden.

The regenerated index resolves **92 interfaces, 544 callable slots, 547 syntax bodies (three inherited explicit defaults), 1,550 production call/reference sites and 1,711 C# files**. There are **127 reviewed dispositions and 417 pending**, with **47 of 180 uncalled slots reviewed and 133 pending**. The source SHA-256 is `fd5531a8fdf57f4711c3061d45cc965ed6baa2c94b8c9c44830df9d3e47cc941`. Its nine Roslyn analysis diagnostics are expected generated-regex bodies absent from the read-only compilation; the actual solution build succeeds. Earlier reports preserve their own source counts/hashes.

No rendered UI, live provider/customer effect, production deployment, multi-instance rollout, physical device/audio or CI acceptance is claimed. No installed-data reset, Real-host restart, push, PR or `.agents` edit occurred. The existing Impeccable context remains in place without rerunning its launcher. The [original acceptance matrix](tenant-proposal-acceptance-matrix-2026-10-24.md) retains the complete remaining gate.
