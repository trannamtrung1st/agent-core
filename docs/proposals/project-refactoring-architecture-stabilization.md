# Project refactoring and architecture stabilization

Authorized 2026-10-29 against `e9994fed2cc33c277d4cda114c95a59e8fec71b7`. This is the design and decision record for the owner’s local proposal. [Delivery evidence](../reports/project-refactoring-delivery-2026-10-29.md) owns observed checks and remaining gates. Production Hardening follows this initiative; historical milestones remain unchanged.

## Ownership and move map

| Current source | Accepted destination | Surface / authority |
| --- | --- | --- |
| API root `AgentRunHostedService`, `EmailWatchHostedService`, `OpenSandboxHostedService`, `SessionShutdownHostedService`, `TenantRequestIsolationHostedService`, `TriggerSchedulerHostedService`, `WorkspaceCleanupHostedService`, `SchedulerReadiness` | `src/AgentCore.Api/Hosting/`, same filenames | Preserve public types/namespaces, logger categories, DI lifetimes, registration order and readiness semantics. |
| API root `FilePreviewOptions.cs` | `src/AgentCore.Api/Configuration/FilePreviewOptions.cs` | Preserve namespace, option keys, defaults and validation. |
| Application root `EffectiveConfigurationComposer.cs` | `src/AgentCore.Application/Admin/EffectiveConfigurationComposer.cs` | Only caller is already Admin. Move its namespace to Admin; keep its private-use memory default with this projection. No universal configuration service. |
| `SessionRuntime.cs` browser continuation transformations | `Application/Tools/BrowserContinuationPolicy.cs` | Explicit existing request, origins and result inputs. The pump retains local origins and lifecycle; no I/O or new asynchronous owner. |
| `PumpModelCoreAsync` round-phase request projection | `Application/Sessions/ModelRoundRequestPolicy.cs` | Pure request transformation; retain order of repair, completion, background, capacity, cleanup and finalization restrictions. No execution permission granted. |
| `web/src/services/realtime.ts` binary decoding | `web/src/services/realtime/bytes.ts` | Stateless; preserve facade export `bytesOf`, typed-array offsets, base64 validation and all accepted forms. Connection/epoch/queues remain in the facade. |
| Current-schema test setup | `tests/Shared/CurrentSqliteTestDatabase.cs`, linked into existing API/Infrastructure test projects | Process-local empty schema created through real migrations; SQLite backup to fresh per-case files, refusing existing destinations. No identities/keys/test data cached. Explicit predecessor/upgrade/fresh-schema tests retain direct migration setup. |
| Synthetic scenario implementations | Retain unless a cohesive extraction is supported by passing baseline evidence | Synthetic stays a runtime adapter in Infrastructure. |
| Documentation / shipped sources | New `docs/README.md`, `docs/reports/README.md`, `agents/README.md`, script inventory and operations matrix | Navigation and ownership only. Preserve all historical reports, assets and Definition/resource bytes. |

No endpoint relayout, new generic Services/Helpers layer, schema changes, generated migration edits, visual redesign or new UI tokens. Existing feature owners and transaction boundaries stay authoritative.

## Dependency and persistence decision

Actual project references are API → Application, Infrastructure, Contracts; Infrastructure → Application, Domain; Application → Domain. Domain and Contracts have no project references. Application also consumes logging abstractions; Contracts consumes MessagePack annotations. No new reference is proposed.

The isolated source audit rejects a DbContext/migrations-only extraction: `AgentCoreDbContext` calls `Security.IamPersistenceModel.Configure` and maps `IamSystemIdentityRow`; persistence configuration also references `TenantApprovalRecord`. Concrete SQLite stores depend on this same context and Security partial-family transactions. Persistence attachment/artifact cleanup additionally depends on Workspace and Security implementations. Frozen SQL is loaded via the owning assembly with explicit `AgentCore.Infrastructure...` resource names. A naive move creates Persistence → Infrastructure → Persistence; an acyclic larger move must include the whole relevant Security/model/store closure and resource lookup callers. Moving half of `SqliteIamStore` or adding an internal-visibility escape is rejected.

**NO-GO for an additional production assembly.** The first mandatory boundary/proportionality gate fails for the narrow candidate; do not implement or benchmark a knowingly cyclic production project. Retain the five projects, DbContext, migration/resource identities and one DI owner. Baseline/final timings describe the retained graph, not a promised split speedup. Fresh/upgrade/reopen coverage remains in the existing Infrastructure tests. Reconsider only with new measured evidence and a separately reviewed complete ownership closure.

## Characterization and risk gates

Before structural edits: Release restore/build, current realtime race/queue/protocol regressions, Application Synthetic completion/checkpoint/browser/configuration scenarios, script tests and canonical authenticated General Assistant/Chat browser scenarios. Record baseline defects separately and correct only blocking fixtures or important correctness defects.

After each move/extraction: execute affected integration checks. Browser continuation coverage includes intervention redirects, same-origin refusal, alternative navigation and close; round projection covers repair/tool-choice reset, background completion, cleanup and finalization precedence. Realtime coverage includes binary forms, reconnect/sequence gaps, stale output, queues/receipts and fake-device voice.

Final gates include all four backend suites, complete discovered-case accounting through the existing shard planner, all Vitest suites, TypeScript/production build, current Synthetic browser journeys, definitions/resources, script tests, Compose rendering and disposable HTTPS/SQLite restart where Docker is available. The owner explicitly included CI in this initiative; hosted status must identify exact source and blocked permissions. No production/provider/device acceptance is inferred.

## Design and independent review

Reviewed against canonical architecture, backend interfaces, repository structure, protocol, persistence, testing and operations. Allowed references remain acyclic; no second Session, transport, approval, scheduling or persistence owner is introduced. Public API/MessagePack data, cancellation, response identity and terminalization fences remain unchanged. Preserve shipped immutable pins and tenant provisioning/adoption owners.

No visual UI surface is changed. Ant Design v6 and existing design guidance stay authoritative. Realtime checks exercise current Chat and Voice states; global UI polish and token changes remain outside this structural scope. The final review checks correctness/architecture/IAM/recovery first, test and documentation completeness second, then simplicity. A missing mandatory gate yields Needs Fixes/Not Ready, never a lowered acceptance standard.

Rollback is source revert per coherent commit; no persisted data mutation is introduced. Tests use disposable roots. Preserve the baseline inventory under ignored `local/refactoring/`, including hashes of migrations, definitions and report assets. Classify existing reports as historical evidence and their assets as referenced evidence; retain ambiguous/unreferenced material rather than claiming it is disposable. No customer/dev data reset, history rewrite or report relocation.
