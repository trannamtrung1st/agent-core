# Tenant terminal cleanup and Skill projection consolidation — 2026-10-24

Preimage: `bf0c1a78`. This module follows the original full IAM proposal. It removes two remaining unscoped application paths and qualifies the canonical replacements; it does not accept the full proposal.

## Change and owning behavior

Runtime terminal persistence previously called `SessionWorkingCleanupService`, which resolved raw Session metadata through a Profile and unowned Run/Sandbox stores before deleting a Session tree. Current tenant cleanup already uses `ITenantWorkingCleanupStore` and exact retained owner/policy/lease/causal-child proofs through `TenantWorkingCleanupService` and the existing startup/five-minute workspace worker. The Profile-based actor, its DI registration, executor dependency/forwarder and Runtime callback are removed together. Terminal state publication/persistence is unchanged. Tenant cleanup remains eventual through its existing technical proof, with no new queue, execution authority, schema or migration.

`EffectiveSkillCatalogResolver` is now only the pure static projection used by canonical owned configuration/admission. Its unused production `ResolveAsync` method loaded an arbitrary Instance's raw Skill snapshot and management policy from `IAgentInstanceStore`; it had no production callers. That loading arrangement is confined to the existing native test-data helper, preserving all fixture assertions about enabled state, procedure budgets, pinning and version changes. No principal, grant or Run authority is fabricated. Current tenant Skill procedures still require current source reads and their existing exact consent rules.

## Focused verification

Expected use case: after actual owned HTTP admission, the pinned retention policy decides cleanup; deletion and child/report accounting defer removal until safe, preserve sibling/child/home/Artifact bytes, survive SQLite host recreation and never admit another model generation. Native purge repeats the exact proof and refuses substituted roots. The existing Synthetic integration scenarios exercise these behaviors rather than treating a build or source-reference count as execution.

| Selection | Qualified distinct cases |
| --- | ---: |
| API `FullyQualifiedName~Tenant_working_cleanup_` | 13: nine retention/restart cases passed initially; four child/report cases passed after correcting their diagnostic assertion |
| Infrastructure `FullyQualifiedName~Tenant_working_cleanup_` | 8 passed; repeated proof, safe native retry, four substituted root levels, exact confirmed lease and SQLite reopen |
| Application `SkillActivationTests`, `InstanceSettingsResolverTests`, `DynamicSkillActivationTests`, `StateIsolationPolicyTests`, `ConversationFailureDiagnosticTests` | 50 passed; pure Skill projection/settings/pins, isolation and terminal failure regressions |
| Serial solution build | Passed, zero warnings/errors, 49.19 s |
| Final API-test build | Passed, zero warnings/errors |

**71 distinct cases qualified, zero final failures/skips.** Tests use `dotnet test tests/<project>/<project>.csproj --no-build --no-restore --nologo --filter '<selection>'`. The Application filter joins each class with `FullyQualifiedName~` and `|`; `SkillActivationTests` also matches `DynamicSkillActivationTests`, counted only once. Build command: `dotnet build AgentCore.sln --no-restore --nologo -m:1 -nodeReuse:false -p:UseSharedCompilation=false`.

The first API run had nine passes and four failures, all at the final historical raw-exception-text assertion, after the cleanup, child-generation and completion-accounting assertions succeeded. The diagnostic privacy successor intentionally withholds original messages. A first corrected assertion still expected only the old projection-stage exception; one complete safe diagnostic showed that current Harness preparation can now refuse the deleted execution lineage earlier with `AccessDeniedException`. The final assertion accepts exactly those two typed authority-refusal stages, requires the safe HResult-bearing classification and rejects the old original message. All functional/no-generation/file-preservation assertions remain. The final four-case recheck supersedes those assertion failures; the nine unaffected passes and 58 other group passes are retained rather than redundantly rerun.

Logs: `/private/tmp/tenant-store-closure-consolidation-build-20261024-final.log`, `/private/tmp/tenant-cleanup-closure-{api,app,infra}-20261024.log`, `/private/tmp/tenant-cleanup-closure-api-final-20261024.log`, `/private/tmp/tenant-cleanup-closure-api-final-build-20261024.log`. Per-project `TestResults/tenant-cleanup-closure-*.trx` retain actual results. Intermediate assertion-only failures remain separately logged. All reported processes are terminal.

## Source closure navigation

The [current source index](tenant-store-method-source-index-2026-10-24.json) binds **93 store interfaces / 545 declared callable slots / 1,557 call or method-reference sites** across Application, Infrastructure and API. It records exact file hashes, declaration/concrete/default implementation sites, enclosing member and receiver type. The aggregate source fingerprint covers all 1,714 C# source files in those projects, excluding bin/obj. All 545 slots have implementation locations, including default interface bodies. **181 slots have no bound production caller** and require explicit retained-contract/data/default-body review; absence of a caller is not acceptance.

The older syntax census counts 548 method declarations: `ITenantRuntimeStore` adds three explicit default implementations of inherited completion slots rather than three new callable contracts. `IBuiltInAgentDefinitionStore` inherits two read-only catalog methods; all current calls to those parent slots bind through the built-in receiver (27 Get and three List sites). Interface existence does not restore an unowned catalog or grant private authority.

This is a temporary read-only Roslyn navigation analysis using local SDK/framework references and the actual built project assemblies. It includes conditional access, method references, concrete implementations and explicit/default interface bodies. Source generators were not run in that analysis: its nine CS8795 diagnostics are missing generated-regex bodies (Application three, Infrastructure five, API one); the real build has none. Reflection/dynamic reachability and authorization are not inferred. The saved index supplies source addresses for the remaining review; it is not a new product tool or authorization framework.

The [requirement matrix](tenant-proposal-acceptance-matrix-2026-10-24.md) maps every original backend/UI condition and final obligation to measured successor evidence. Its remaining method/effect review and final rendered UI/design confirmation keep the original goal active. No frontend source, provider invocation, live OAuth, production database reset, Real-host restart, optional CI, push or PR is part of this backend module.
