# Session and Runtime boundary reconciliation — 2026-10-24

Preimage: `52177338`. This follows the [Automation/event checkpoint](tenant-automation-event-boundary-reconciliation-2026-10-24.md) under the same local working-tree base; no new commit is claimed. The complete **68-method Session/Runtime family** now has source dispositions. This slice fixes an actual private metadata read race and qualifies its affected integrated workflows; other families and final I/O/worker/rendered-design acceptance remain open.

## Reproduced failure and change

Both Session metadata GET handlers passed only a Principal ID to `TenantSessionService.ReadAsync` / `ListAsync`. If the authenticated login retired after storage captured the private metadata, later permission checks could still authorize that active Principal. All **16 baseline HTTP cases returned 200 OK instead of the required refusal**: list/read × in-memory/SQLite × logout/relogin/password rotation/concurrent IAM change. The fixture captures real stored metadata containing a private title, then performs the actual competing HTTP operation before returning the captured value.

Both entry points now accept the existing server-derived `TenantSessionInitiator`, require the original login before storage and recheck it after all metadata/permission I/O with the same tenant security revision. The Principal-only entry points are removed. Both GET handlers use the existing `Initiator` helper. Readable-ID filtering still precedes totals/paging; `sessions.read` retains its independent metadata meaning. No `agents.chat` prerequisite is added to metadata, and no new human-login prerequisite is added to durable autonomous work.

Six native fixture files now pass actual existing login references or explicitly sign in for their factual read. Scoped read/manage and cross-tenant tests retain their original permissions and assertions. They do not receive an authority bypass. Wire DTOs, schema, approval floors, provider configuration and execution frameworks remain unchanged.

## Current source dispositions

The [method index](tenant-store-method-source-index-2026-10-24.json) records these nine groups:

| Methods | Current boundary |
| --- | --- |
| 29 | Generic Run contract/native data and canonical bridge: the factory binds actual owner/Session/Run/generation; the current worker Coordinator also receives the tenant bridge. Unsupported native admission/continuation paths refuse or return safe empty data. Native Run/Activation access and mutation use legacy owner/Profile checks; creation refuses an actual owned Session or Instance. |
| 4 | Owned Session lifecycle/metadata: actual tenant roots, immutable creator/executor, current original human and revision witnesses at authoring commits; current human after complete metadata I/O; allowed IDs/surface/Instance filtering before totals; deletion retires actual grants and tombstones/redacts owned data. |
| 1 | Private conversation history: exact owned Session/revision/cursors, independent `sessions.read` plus `agents.chat`, current per-resource projections and original-human/Session/security checks after complete I/O. |
| 8 | Owned Run admission/activity/replay/cancellation: compound owner joins and visibility before totals; original human in owning writes/after private reads; exact input/parts/files/voice/interruption; full replay identity and immutable lineage; atomic graph/receipt/grant/inbox/audit settlement. |
| 13 | Common Runtime/claim/history/child/outcome/report transactions: current actual owner/generation/lease/policy, bounded admitted-input windows, revision CAS, exact parent/child/source/destination/configuration, protected output checks and atomic outcome/learning/continuation. Exact saved source-read and communication consent settle once; factual skips/recovery do not replay native effects. |
| 3 | Completion inbox: actual live parent frame/tool/current eligibility, initial-child/current-task lineage, independent source/Artifact visibility before totals, exact pending call/token/receipt revision/claim generation/expiry, and source revalidation at output. |
| 5 | Human input/foreground/voice: original human and current compound witnesses for positive admission; incumbent mailbox frame or quiescent reservation; actual server-minted voice lease/operation. Retirement only downgrades the same owner; refused human persistence does not fail independently accepted work. |
| 2 | Recipient delivery facts: current original-human/security and exact entry/sequence/response/block/Artifact/voice bounds; live frame or quiescent reservation; partial received/heard facts cannot credit unrelated output or initiate work. |
| 3 | Runtime State: compound actual owner and independently readable Session/Run/profile IDs before aggregates; bounded source reads, no private bodies/stdout/stderr, and original human/current Agent/Session/final security after I/O. |

The generic Run contract includes **13 uncalled slots**. Retained native data is not automatically tenant-authorized. In particular, the native SQLite pending-input method remains an unscoped metadata scan; the canonical bridge returns an empty result for that slot. The production Coordinator is constructed with that bridge by `TenantRunExecution`, and the hosted entry uses `ITenantRunExecution`. There is no current global Coordinator registration or private fallback to that raw scan. This is a data/authority distinction, not a claim that every raw query independently filters every tenant identifier.

The actual host rereads the claimed owned Run, checks current authority, builds the exact frame and reserves its Session before creating the canonical Runtime. Worker enumeration grants no execution authority. Completion/source-read consent and requester attribution remain independent of the requester's live login. Typed source/effect families and every final provider/hosted fault combination retain their separate acceptance obligations.

## Focused verification

**572 distinct cases pass, zero failures or skips:** the 16 changed-path API cases (57s), 50 grouped integrated API/wire cases (4m38s), and 506 owning-store/native Runtime cases (26m). All processes are terminal with exit code 0. TRX names have no overlap across these three groups, and every selected method/class executed. The earlier intentional baseline is recorded separately: 16 failed, zero passed, all with actual 200 instead of the expected refusal (55s).

The fixed cases perform actual storage reads, retire the original login or change IAM before release, assert withheld private metadata and expected 403/409, then read the same private title successfully with fresh authority. The broader API cases exercise actual worker output/history, scoped Chat discovery, real MessagePack queued input, exact text/voice delivery receipts, revoked recipients, foreground continuation/reopen and bounded Runtime State observations. Store cases exercise current human/executor/owner scope, actual Session lifecycle, input/replay/interruption, claimed Runtime persistence/recovery, background admission/outcome/report/source consent, completion inbox, typed waits and retained native Run contracts. Controlled adapters establish these local outcomes; live customer/provider effects are not claimed.

The serial fixed solution build passes with **zero warnings/errors in 49.63s**, log `/private/tmp/tenant-session-metadata-fixed-build-20261024.log`. The baseline build also passed cleanly (54.74s). Both use `dotnet build AgentCore.sln --no-restore --nologo -m:1 -nodeReuse:false -p:UseSharedCompilation=false`. No production C# changed after the passing fixed build.

All tests use `dotnet test <project> --no-build --no-restore --nologo --filter '<selection>' --logger 'trx;LogFileName=<name>.trx'`. The changed-path filter is `FullyQualifiedName~Session_metadata_HTTP_rechecks_original_login_and_security_after_storage_IO`. Exact broader filters, source-file/method/class selections and executed wrappers are retained at `/private/tmp/tenant-session-runtime-reconciliation-{api,infra}-20261024{-filter.txt,-selection.json,.sh}`. They select 16 API methods and 79 Infrastructure methods plus `TenantSessionIntegrationTests`, `TenantSessionLifecycleTests` and `AgentRunAdmissionStoreTests`. No full local or hosted CI was run.

Logs: `/private/tmp/tenant-session-metadata-{baseline,fixed}-20261024.log` and `/private/tmp/tenant-session-runtime-reconciliation-{api,infra}-20261024.log`; matching TRX files are in each project's `TestResults`. No installed-data reset, Real-host restart, push, PR or `.agents` edit occurred.

## Remaining closure

The new source index resolves **92 interfaces, 544 callable methods, 547 syntax bodies (three explicit inherited defaults), 1,542 call/reference sites and 1,712 production C# files**. Two direct metadata IAM snapshot calls are replaced by the shared original-human path. It records **355 reviewed / 189 pending methods**, with **123 of 180 uncalled slots reviewed / 57 pending**. Source SHA-256: `cec0e52b40e84140993af4c9c7ffdaa3e419156be6064eeebde48367a8c5f707`. Nine expected generated-regex diagnostics belong only to the read-only source compiler; the real solution builds cleanly.

Remaining families: Profile/Memory/Experience (57), files/Artifacts (56), Sandbox (41), and Credential/account effects (35). The [acceptance matrix](tenant-proposal-acceptance-matrix-2026-10-24.md) retains final I/O/worker reconciliation, rendered keyboard/read-only/error/responsive confirmation, the shared singular-count copy finding, design hashes and final delivery. This checkpoint does not complete the original proposal.
