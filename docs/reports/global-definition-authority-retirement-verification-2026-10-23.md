# Global Definition authoring authority retirement

Date: 2026-10-23. Preimage: `b8b6bed9`. Scoped cutover evidence; the complete proposal remains open.

## Result

Removed six unused global authoring actors and registrations: `AgentDefinitionLifecycleService`, `AgentDefinitionResourceService`, `AgentDefinitionDraftValidationService`, `AgentDefinitionDraftDiffService`, `AgentDefinitionDraftEvaluationService` and `AgentDefinitionDraftPublishService`. Production callers were limited to that chain and DI. Current tenant authoring already uses `TenantDefinitionService`, shared candidate/resource validators, fingerprinting, the isolated Synthetic evaluation runner and the owning tenant store transaction; those current owners remain.

Pruned dead Admin transport maps and global inventory constants/models. The three current maps (`ToInstanceSkill`, `ToAgentInstance`, `ToPersona`) and retained overview models match their preimages byte for byte. The unused global content-upload return model is removed with its actor. Current HTTP shapes, permission/defaults, migrations, protected values and hash-bound historical preparation formats are unchanged.

## Successor qualification and fixture boundary

Added ten real canonical Knowledge publication cases, both stores: inferred `knowledge/{identity}` path, explicit path, missing binding, wrong kind and non-textual content. Valid publications preserve the source path field and exact owned resource digest; global adapters cannot read the owned draft/publication. Invalid publication reports `definition_resources_invalid`, preserves source/draft/security revisions and audit, and creates no publication. The inferred path is a data convention, not an authorization fallback: only actual same-owner bindings are considered.

Retired 32 global actor test methods across the old new-draft/Knowledge/publication/validation/diff/evaluation/Skill-publication suites and the last global Definition lifecycle-concurrency fixture. Original bodies are in the preimage. Their unscoped authoring and diff formatting are not current tenant authority. Current source/template/resource/evaluation/publication transactions and protected HTTP flows provide separate successor evidence.

Preserved the isolated capability/evaluation projection check using the same candidate. Preserved retired Sandbox candidate refusal/explicit repair against shared validation without rewriting the immutable file source. Native inherited-policy/freeze tests now seed a physical unowned publication directly; that fixture helper synthesizes no principal, grant, consent or Run and does not qualify tenant authoring. Canonical HTTP resource upload/evaluate/publish/read/fork and template-copy cases qualify current authoring separately.

## Verification

**66 distinct passing selected cases** in the grouped run:

| Group | Result |
| --- | --- |
| Initial affected cases | 10 canonical Knowledge cases passed in 24s; 5 API/native policy cases passed in 14s. All repeat cases in the groups below. |
| Shared validator and isolated evaluator | 9 passed in 90ms. |
| Canonical Definition/Knowledge/template integration | 42 passed in 1m28s: owned immutable versions and foreign/global fences, verified resources and current evaluation evidence, publication races, account/security/cancellation fences, approved template byte validation and immutable provenance. |
| Protected API/native regression | 15 passed in 27s: current author/publish/instantiate, owned upload/evaluate/publish/read/fork, exact resource scopes, approved template fork, native policy/history and immutable-source repair. |
| Solution build | Initial 54.04s; final 49.92s, both zero warnings/errors. |

The final-build HTTP repeat passed all 3 selected cases in 4s; these repeat the current author/publish/instantiate, protected resource/evaluation/publication/read and template-fork cases above. All build/test handles are terminal. All test commands use `dotnet test --no-build --no-restore --nologo`. Application selects `AdminDefinitionCandidateValidatorTests` and `NativeDefinitionEvaluationProjectionTests`. Infrastructure selects the three main owned Definition/publication/suspension cases, the five template integration families and `Owned_Knowledge_publication_`. API selects current authoring/resources/resource scopes/templates, `SelfManagementAuthorityTests` and `SandboxApiTests`; the final repeat selects only current author/publish/instantiate, upload/evaluation/publication/read and template fork.

Build: `dotnet build AgentCore.sln --no-restore --nologo -m:1 -nodeReuse:false -p:UseSharedCompilation=false`. Logs/preimage/exact-method manifests use `/private/tmp/definition-authority-retirement-*20261023*`. Temporary stores and keys are fixture-owned. No installed-data reset, Real-host restart, provider-key call, deployment, Docker run, CI, push or PR is part of this slice.

## Remaining goal

Remaining global/native configuration, Instance resource/workspace, tool/kernel/store callers still require reconciliation. Safe final schema consolidation and previously recorded canonical long SSO, large-page discovery, reserved-cleanup journey and proposal-wide security/isolation/function/rendered UI acceptance remain open. Historical freezes stay scoped to their original candidates.
