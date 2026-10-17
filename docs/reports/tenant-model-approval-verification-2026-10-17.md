# Model-initiated durable persona approval verification

Date: 2026-10-17. Baseline: `d1317b42aae3d877d24342c8c751b97fc8312ec0`. The previous [worker checkpoint](tenant-approval-worker-verification-2026-10-17.md) was verified progress. This successor connects actual model initiation and suspension to that worker. The [full delivery goal](multitenant-iam-delivery-2026-10-16.md) remains active; no milestone acceptance or deployment is claimed.

## Implemented boundary

`approvals.request_authorization` is a context-only tool for the closed operation `update_persona`. It accepts the proposed persona only. Unknown/duplicate fields, arbitrary operations, caller identity/permission/resource/grant/revision fields, invalid types, oversized arguments and invalid identity lengths reject. Core derives the actual executor/target, current witnesses, action hash and request identity from the saved Run and owned Instance.

Offering requires a trusted matching tenant Session and pinned Settings-management eligibility. Runtime requires the tool actually to be offered, model tool capability, a sole call in the batch and sufficient checkpoint/output reserve. Preparation uses fresh tenant identity/generation and pinned/current management checks. The shared executor rejects direct preparation calls lacking Runtime suspension; there is no legacy fallback or model-created authority object.

The normal tool loop first persists its exact pending call. Runtime then acknowledges a noncoalesced Session persistence barrier before starting preparation, suppressing later stale Session saves. A supervised worker performs preparation I/O with immutable references and returns normalized state through the mailbox. The existing transaction saves request/Run wait/security/audit together. A saved wait releases the claim, cancels response work and releases the Runtime slot without fabricating a completed assistant entry. Shutdown awaits preparation. Unknown commit status stops generation for durable recovery rather than assuming no request was saved.

The existing Domain transition pauses the active execution clock; human delay is excluded. After a durable decision, the existing worker claims the original Run. Its continuation validates the pending call's persona and single-call shape against the saved operation **before** any effect. Exact execution/denial reuses the existing receipt and grant transaction. `CompleteTenantAuthorization` closes the pending checkpoint with the recorded outcome while retaining format, projection/skill/protocol/execution metadata and budget. Repeated dispatch does not reask or reapply. Result values follow the existing camel-case convention and expose no grant authority.

A request or an Allowed decision is not an applied effect. Already Allowed operations create no approval/effect through this request-only function; direct Allowed execution remains required separately. No new table, scheduler, Task entity, RoleAssignment, public execution/debug endpoint or UI kit is introduced.

## Executed user flows

`TenantSessionAccessTests.ModelApproval` uses actual authenticated HTTP admission, the actual hosted worker/coordinator, the existing Runtime and a deterministic Synthetic model that proposes the typed call. It performs no trusted manual preparation or claim in place of the model path.

1. Create an owned examiner, enable assisted Settings management and submit text.
2. Observe a first-class request with the exact proposed persona, actual root and `WaitingForApproval`; verify the claim and budget timestamp are released and the persona is unchanged.
3. With maximum active Sessions set to one, successfully reserve/release that sole slot **before** deciding, proving the pending request released its Runtime.
4. Approve, reject or cancel through canonical HTTP. Observe worker resumption and a completed saved response outcome.
5. Approval applies the exact persona once and consumes the grant. Rejection/cancellation leaves the persona unchanged and creates no effect/grant. Each case has one request audit, the expected effect audit count and a closed tool result with the stable reason; no repeated request occurs.

`ModelApprovalSqlite` uses the existing real SQLite host fixture with a fresh database, explicit fixture bootstrap and isolated temporary roots. It runs model initiation to a pending saved request/checkpoint, logs out, disposes the first host, recreates a second host against the same database, authenticates freshly and approves. The real worker applies the exact persona once, consumes the grant and completes the Run. The second model receives the recorded outcome; request/effect audits each occur once. Both hosts and only their disposable directory are cleaned up.

Both-store contracts verify that different pending arguments or a multi-call checkpoint reject **before** the effect, retaining the available grant and producing no receipt/effect audit. A matching checkpoint closes once and replay adds no Run revision or effect audit. Shared-executor tests require the actual generation and Runtime path and leave no orphan request. A controlled 20-minute human delay preserves a 10-second active budget: after 500 ms of work the remaining 9500 ms and active 600 ms survive waiting/requeue/claim/continuation. The disabled pinned management policy still produces a denial despite approval.

Contract/projection tests reject caller authority fields, duplicates, arbitrary operations and identity-bound violations; unmanaged or untrusted contexts do not offer the request tool. Existing legacy preparation fences are retained.

## Focused local checks

Commands use `dotnet test <project> --no-restore --nologo --filter '<FullyQualifiedName OR filter>'`:

| Project/filter | Result |
| --- | --- |
| Infrastructure: `TenantOperationAuthorizationTests` or `TenantRunAdmissionTests` | 113 passed |
| Application: `TenantApprovalToolContractTests`, `AgentRunToolCallCheckpointTests`, `AgentRunDurabilityTests`, `CapabilityProjectionTests`, `ToolRegistryPolicyTests` | 104 passed |
| API: `TenantSessionAccessTests` or `SessionHostRaceTests` | 19 passed before adding SQLite host recreation |
| Final model flows: `Model_prepares_durable_persona_approval` or `Model_approval_and_exact_checkpoint_survive_SQLite` | 4 passed after supervised-worker and final-format changes; three overlap the broader API run |

Total: **237 distinct focused cases passed**. Narrower contract/store/flow runs overlap these totals. Logs: `/private/tmp/tenant-model-approval-infra.log`, `/private/tmp/tenant-model-approval-app-final.log`, `/private/tmp/tenant-model-approval-api.log`, `/private/tmp/tenant-model-approval-final-flows2.log`. The initial SQLite host run also passed in `/private/tmp/tenant-model-approval-sqlite-host.log`.

Documentation checks passed for eleven changed Markdown files: 801 local links, 202 fragments, 20 complete JSON blocks and balanced fences. `git diff --check` passed.

Build checks caught an accidental replacement of an existing partial-file helper and an incorrect checkpoint helper signature; the legacy fence was restored and signatures corrected before functional verification. Subsequent builds/tests passed. No provider credentials, user-data reset, Real-host restart, deployment or CI was used.

## Remaining scope

This is a verified private text path for persona operations requiring authorization. Direct Allowed effects, other typed operation/secret/resource families, exact Run-B grant consumption, trusted child/autonomous admissions, comprehensive revocation/storm controls, full judgment context and canonical tenant Approvals/Chat/Access UI remain required. Live tenant output/history recipient authorization, delivery receipts, audio and received/heard continuity are not qualified by these private-output tests. Abrupt process termination during preparation, all storage-failure recovery combinations and live-provider language behavior are not established by normal host recreation.

Final legacy-authority/schema removal and the reviewed reset/bootstrap consolidation remain required. The source is not qualified for multi-tenant deployment and the full proposal is not complete.
