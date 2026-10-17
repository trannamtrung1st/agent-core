# Tenant approval worker and Runtime continuation verification

Date: 2026-10-17. Baseline: `25396b1f81a0ba1e31792583174ba1a5170eea66`. This continues the [full proposal delivery](multitenant-iam-delivery-2026-10-16.md). The previous turn made verified progress and pushed the scoped text Runtime; this successor connects its temporary approval exclusion to real exact-operation consumption. The full goal remains active.

## Implemented path

The existing worker drains the durable approval signal, the existing coordinator claims the original Run, and the existing Session Runtime resolves the saved request through `TenantRunContinuationService` before model work. Pending, foreign or mismatched requests cannot supply a continuation. Run/Session/root/Instance/executor/original requester references come from owned saved rows, not model arguments, login cookies or copied roles. Claim/cancellation/lease/generation are re-read before and after effect I/O.

Approved persona requests use the existing exact-effect service and its current IAM, grantor, policy, management, witness and transaction gates. Target mutation, exact grant consume-or-revoke, Run side effect, immutable receipt, security revision and audit still commit together. Existing receipts suppress another mutation/audit after recovery or repeated dispatch. No new table, scheduler, Task entity, RoleAssignment or public execution/debug endpoint is added.

Rejected, cancelled and expired requests produce stable denial data without executing an effect. A post-approval denial still preserves historical Approved status while its operation receipt records Denied. Runtime refreshes the actual Run revision, rechecks current authority, and continues the original pinned configuration with the outcome.

`TenantApprovalOutcome` contains scoped result references, outcome/reason and applicable target revisions. It contains no grant ID, bearer credential, action hash or replacement persona parameters. The prompt builder checks tenant/Run/root/Instance scope and places it in an `ApprovalOutcome` Evidence section. It is never a System/Developer instruction or a reusable permission. Optional evidence cannot crowd out that recorded result. Runtime captures the outcome with the dispatched Run snapshot before off-mailbox work and preserves it when rebuilding capability context.

## Runtime evidence

The Synthetic HTTP fixture gates only execution until the durable decision is saved, then releases the **actual hosted worker**. It does not manually claim the Run or call the effect executor. Each scenario creates an owned examiner, enables assisted Settings management, creates a text Session, submits a real authenticated message, prepares its typed exact request through the trusted service, decides through canonical HTTP, logs out and releases execution.

| Scenario | Expected and observed |
| --- | --- |
| Approve | Real claim; exact persona applied once; grant Consumed; immutable Completed receipt and one effect audit; Run completes with a saved response outcome. |
| Reject | Real claim; persona unchanged; no grant/effect receipt or effect audit; Run completes with `approval_rejected` evidence. |
| Approve, then freeze before worker execution | Real claim; persona unchanged; Approved decision retained; Denied receipt with `management_unavailable`; grant Revoked; Run completes with the denied outcome. |

All three run after logout. Anonymous HTTP reads remain Unauthorized; fresh login is required to read the decision/result. Captured actual model requests contain only the scoped operation result as User/Evidence, with the correct operation ID/reason and no grant/hash authority. A further coordinator pass dispatches no duplicate work. This is private persisted response execution, not tenant Chat delivery or proof of a live provider's natural-language interpretation.

Both-store service contracts exercise current grantor/executor permission loss, management freeze, target witness change and pinned management denial through the continuation service. Cancellation/expiry produce no persona mutation or effect receipt. Approved Review and ExceptionalGrant cases commit once, advance beyond the lease, reopen SQLite where applicable, recover/reclaim through the real scoped command store, reject the old generation and reuse the exact receipt without changing Instance/security revisions or adding audit. These store contracts use controlled persisted Run fixtures; the hosted HTTP cases above provide the actual worker-claim evidence.

## Focused checks

All test commands use `dotnet test <project> --no-restore --nologo --filter '<FullyQualifiedName OR filter>'`.

| Project/filter | Result |
| --- | --- |
| Infrastructure: `TenantOperationAuthorizationTests` or `TenantRunAdmissionTests` | 103 passed |
| Application: `PromptContextBuilderTests`, `ContextAssemblerTests`, `AgentRunDurabilityTests`, `AgentRunToolCallCheckpointTests`, `CapabilityProjectionTests` | 131 passed |
| API: `TenantSessionAccessTests` or `SessionHostRaceTests` | 15 passed before adding the freeze case |
| Final API hosted flow: `Canonical_approval_HTTP_projects_exact_receipt` | 3 passed, including the added freeze case; two overlap the broader API run |

Total: **250 distinct focused cases passed**. The narrower 18-case continuation/denial/recovery and 41-case context checks also passed and overlap the listed totals. Logs: `/private/tmp/tenant-continuation-infra.log`, `/private/tmp/tenant-continuation-application.log`, `/private/tmp/tenant-continuation-api.log`, `/private/tmp/tenant-continuation-final-flow.log`, `/private/tmp/tenant-continuation-store.log`, `/private/tmp/tenant-continuation-context.log`.

Documentation checks passed across ten changed Markdown files: 782 local links, 196 fragments, 20 complete JSON blocks and balanced fences. `git diff --check` passed.

The first integrated attempt exposed a missing explicit factory dependency; wiring the continuation service into the production factory resolved it before the hosted scenarios ran. An initial enum-column compilation error was corrected. No user data was reset, no Real process was restarted, and no provider credentials, deployment or CI were used.

## Remaining scope

This completes consumption of **existing typed persona requests in their original Run**, including truthful Runtime outcome evidence. Model-tool initiation and resource release/checkpointing while a live model requests approval are still required; current model offers remain IAM inspection only. Direct Allowed effects, exact Run-B consumption, trusted child/autonomous origins, other effect/secret/resource families, proactive authority-change propagation, aggregate storm controls, external reconciliation, canonical Approvals/Chat UI and authorized tenant output/audio remain pending.

Hosted execution used InMemory Synthetic; SQLite has store/transaction/reopen/recovery evidence, not full worker-host qualification. The final removal of legacy authority/schema and the reviewed reset/bootstrap consolidation are still required. This checkpoint is not full proposal completion, multi-tenant deployment qualification or milestone acceptance.
