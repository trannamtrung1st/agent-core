# Credential lifecycle original-human authority — 2026-10-24

Preimage: `a7011a21`. The remaining store/effect review reproduced a real HTTP login-retirement race. This module closes the basic Credential and binding lifecycle together; the original proposal and remaining method/effect/UI gates stay active.

## Reproduced behavior and correction

The actual Synthetic HTTP journey authenticated the Owner, created an existing owned Credential/Agent, then logged out that same login immediately before a second Credential create reached the owning store. The pre-fix request returned **201 Created**, where the new regression required a refusal. The captured original request remained authenticated in its HttpContext; checking only current Account/IAM state did not notice the retired AuthenticationSession. `/private/tmp/tenant-credential-human-baseline-20261024.log` and its TRX retain the one-case expected baseline failure. This is an observed commit race, not an inferred missing guard.

All seven current Credential handlers (list, item, access, create, metadata update, protected-value replacement and delete) now derive the original Account/authentication-session/principal from the authenticated tenant context. Bind/unbind do the same. `TenantCredentialService` reuses the existing `IdentityService`/`TenantHumanSessionAuthority` for human reads and preparation; final list/item/access read fences revalidate that login and the same tenant security revision after source/visibility I/O. Captured metadata or permissions are withheld if authority retires.

Credential and binding commands carry the original human into their existing owning-store transactions. Both stores reuse the incumbent `RequireAgentHuman` helper, which already validates actor/reference equality and the live Account/session/TenantUser/principal. InMemory checks inside the shared ownership lock before any change; SQLite checks before staging and again before final commit. Logical/secret/Instance/binding/security revision checks, actual source ownership, Agent Connections policy, role independence and safe audit/projection contracts remain separate and unchanged. A refused command returns 403 `initiator_unavailable` without partial Credential, association, Instance, IAM revision or audit changes.

The original human argument is server-owned, is never accepted from a body/header and is not persisted as a new authority token. Trusted principal-only internal commands retain their current scoped IAM/policy contract; canonical model consent/effects keep their existing exact operation owners. An original human is not invented for autonomous tasks. No permission classification, mandatory review floor, grant lifetime, schema/migration, HTTP DTO, model tool, frontend or provider flow changes.

## Direct runtime evidence

The complete **36-case new HTTP group passed** on both InMemory and SQLite, zero failed/skipped (2m13s):

- 24 mutation journeys: six operations (create, update, replace, delete, bind, unbind) × both stores × logout/password rotation at the final pre-transaction boundary. Each case compares the complete saved tenant state, protected Credential metadata/bytes, association/Instance and audit facts before/after refusal. The retired cookie then returns 401. A fresh login explicitly submits the same unchanged request; it succeeds and advances the security revision exactly once without changing executor role assignments.
- 12 read journeys: list, exact item and access affordances × both stores × logout/password rotation after source capture. Each returns 403 without private metadata, inventory or permission projection. A fresh login reads successfully; security revision remains unchanged.

Commands:

```sh
dotnet build AgentCore.sln --no-restore --nologo -m:1 -nodeReuse:false -p:UseSharedCompilation=false
dotnet test tests/AgentCore.Api.Tests/AgentCore.Api.Tests.csproj --no-build --no-restore --nologo --filter 'FullyQualifiedName~Credential_HTTP_commit_rechecks_original_login|FullyQualifiedName~Credential_HTTP_read_withholds_captured_metadata'
```

The final solution build passes with zero warnings/errors in 1m1.24s. Its log is `/private/tmp/tenant-credential-human-build-20261024-r3.log`; the direct group is `/private/tmp/tenant-credential-human-cases-20261024.log` with `tests/AgentCore.Api.Tests/TestResults/tenant-credential-human-cases-20261024.trx`. Initial build corrections preserved the existing shared helper name and OAuth's existing independent session-validation calls; those failed build attempts are not passing evidence. Test decorators now forward the supplied original proof unchanged; existing read-only probes still refuse writes. Four native fixture constructors provide their already-existing IdentityService dependency. No fixture principal or grant was manufactured.

## Focused regressions and qualified result

**114 distinct cases passed, zero final failures/skips**: the 36 direct race/recovery cases plus 78 focused regressions. The baseline failure is intentionally separate and is superseded by the corrected direct case; it is not counted as a pass.

| Group | Result / exercised boundary |
| --- | --- |
| Existing HTTP lifecycle/grants/binding/OAuth | 12 passed, 26 s; independent scoped reads/manage/use, redaction, foreign/old-owner refusal, ordinary CRUD, binding lifecycle, captured binding read retirement and Synthetic OAuth proof/connect/disconnect |
| Owning-store boundary group | 56 passed, 3m19s; logical/secret/security CAS, global Account suspension, exact grants, independent parent/item rights, Agent Connections/freeze, captured read and original-human retirement, owned raw-path refusal, OAuth transaction/uniqueness/expiry and private source rotation |
| Actual model binding | 4 passed, 35 s; both-store exact ExceptionalGrant binding and disabled-Credential removal through the existing Runtime, consent and association owner |
| Durable Credential checkpoint | 6 passed, 26 s; both-store exact successor checkpoint, mismatched arguments withheld, waiting excluded from execution budget, provider time charged and once-only receipt/grant outcomes |

The HTTP regression filter is `FullyQualifiedName~Owned_HTTP_credential_lifecycle|FullyQualifiedName~Exact_HTTP_credential_grants|FullyQualifiedName~Typed_HTTP_profiles_create|FullyQualifiedName~Binding_HTTP_joins_actual_owners|FullyQualifiedName~Binding_HTTP_withholds_captured_metadata|FullyQualifiedName~Tenant_OAuth_HTTP_` on `AgentCore.Api.Tests`.

The Infrastructure filter joins `FullyQualifiedName~` for these exact prefixes: `Owned_credential_lifecycle`, `Exact_credential_grants_filter`, `Credential_transactions_fence`, `Binding_metadata_and_mutations`, `Prepared_binding_writes_fence`, `Agent_binding_changes_require`, `Binding_inventory_never_returns`, `Human_binding_inventory_fences`, `OAuth_store_`, `OAuth_identity_uniqueness`, `OAuth_expiry_worker`, `Legacy_credential_binding_paths_exclude_owned_instances`, `Tenant_Credential_discovery_and_retained_metadata_require`, `Tenant_Sandbox_private_source_rotation_invalidates`.

The model API filter is `FullyQualifiedName~Canonical_existing_credential_bindings_use_independent_rights_and_incumbent_association_owner&(DisplayName~binding_grant|DisplayName~remove_disabled)`. The additional Infrastructure filter is `FullyQualifiedName~Credential_successor_consumes_only_the_exact_checkpoint_and_charges_provider_time_without_human_wait`. All use the same `dotnet test <project> --no-build --no-restore --nologo --filter '<selection>'` form.

Logs: `/private/tmp/tenant-credential-human-{api-regressions,infra-regressions,model,continuation}-20261024.log`; each corresponding test project retains `TestResults/tenant-credential-human-*.trx`. These exact assemblies passed all five disjoint groups. No application source changed after the passing solution build. Every reported handle is terminal. The model/checkpoint cases establish their stated exact-operation behavior, not a new live-customer or every-effect-after-logout claim.

## Method reconciliation and remaining gate

The current source index adds explicit dispositions for sixteen Credential/root/binding slots: nine raw Credential/binding data methods and seven canonical owned lifecycle methods. Raw inventories/get/rotation/writes exclude owned or retired markers; unscoped association methods exclude/reject owned Instances and Credentials. Their remaining production Get/ListBindings calls belong only to the retained unowned EmailWatch mechanism, whose claim enumeration excludes tenant rows. They do not resolve owned protected values. Current human HTTP paths all pass the original live reference; exact model secret use remains with its typed operation owner.

These dispositions supplement the previous 67: 83 slots are reviewed and 461 remain pending; 29 of 180 uncalled slots are reviewed, leaving 151. Account read/effect/check/OAuth and other families still require their own final reconciliation. The index records concrete declarations, implementation/caller locations and current source hashes, not a new permission engine or universal authorization proof. Definition-source review began in the same audit but is not relabeled complete by this Credential correction.

No frontend source changed; actual native HTTP journeys supply functional verification. No rendered UI, live Google/OAuth/customer effect, production deployment, multi-instance rollout, physical device/audio, CI, installed-data reset, Real-host restart, push or PR acceptance is claimed. The prior Impeccable context is preserved without rerunning its launcher or editing `.agents`.
