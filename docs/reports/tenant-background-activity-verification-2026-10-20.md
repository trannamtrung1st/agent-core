# Tenant Background Activity and explicit Continue backend — 2026-10-20

This checkpoint extends the [tenant background runtime core](tenant-background-runtime-verification-2026-10-20.md) with original-human Activity inspection and an explicit same-Session Continue in chat operation. It is a backend/runtime/HTTP checkpoint. Tenant Activity/Background UI conversion, other background effects/origins and the full [IAM proposal](../../local/proposals/agent-core-multitenant-iam-approval-final-proposal.md) remain open. The full original goal stays active.

## Implemented boundaries

`TenantActivityService` and the canonical tenant Activity routes read the incumbent Session, Run and completion receipt owners. Exact Agent ownership, allowed Session/Run IDs and optional background surface filter apply before count/paging. All-surface Session pages include archived and zero-run contexts, ordered by updated time descending then ID; Runs use creation time descending then ID. Exact detail reads do not depend on the current page. Original live human authentication/membership and current security revision are rechecked after awaited reads.

Activity requires Agent read and independent Session read. Initial Run metadata, task/result/failure/progress and completion accounting also require that Run's read permission. A Session read does not supply Run read, Agent chat, Session management or execution authority. Completion target references are withheld without parent Session read. Run responses exclude claim generations, checkpoints, tokens, tool results and effect bodies. Background inspection resolves the immutable initial Run even after new human turns are added to that same Session. Existing per-resource file reads remain independent; this checkpoint adds no aggregate file-content permission.

`TenantSessionForegroundService` prepares trusted original-human owner/revision references. The actual write requires current Session read/manage and Agent chat, a live original authentication session, active executor/Instance, current security/configuration witnesses and an eligible retained BackgroundWork Session. Continue adds ChatList; it does not clone work, attach or resume, change origin/lifecycle, admit a message or alter the original task/Run/root/approval.

For live work, the tenant host routes to the actual Runtime mailbox. The incumbent serialized persistence worker issues a specialized authority-checked save under its captured Run/generation. Ordinary tenant Runtime saves cannot mutate surface flags. The new surface is adopted only after success, then carried through already-queued snapshots; a refused human surface job neither leaks optimistic state nor fails the independent Run. Normal snapshot revision advances are allowed only under that exact live generation.

For quiescent work, the host reserves the existing Session write epoch/barrier against dispatch, shared with received-prefix writes. The partial update rechecks the exact observed Session revision and refuses any claimed live writer. Surface/revision/time plus the existing security revision and original-human audit commit together. Fresh-witness repeats add no second audit. No new schema, table, profile, copied cookie or competing mutable owner is introduced.

## Exercised use cases

- **Active model work:** actual production model/Runtime child is held inside its model request. Canonical HTTP Continue succeeds while that child is Running; the same Session appears in Chat and Activity. Releasing the child completes its original Run and report. Later saves retain both surfaces and unchanged origin. A new human message uses the same Session with a new root, and Background details still show the original initial result.
- **Failed human persistence:** prepare a valid request through a separate original human authentication session, sign that session out, then submit the prepared command to the real live host. Its serialized writer refuses the change. The child still completes and reports; Chat visibility and foreground audit remain absent.
- **Quiescent work and recovery:** continue a completed child through HTTP, dispose the actual SQLite application host and recreate it against the same disposable database. The same initial Run/result, origin, two surfaces, one human audit and Chat visibility survive with no model request or child replay.
- **Scoped Activity:** a member can read only the exact Agent/child Session. Page totals and subsequent pages exclude other Sessions. Initial result/delivery and Run history stay hidden until a separate exact Run-read grant. Parent target stays hidden without parent read; continuation stays unavailable without management/chat. Foreign tenant/Agent reads fail. Zero-run contexts remain visible to an authorized Owner; archived child detail retains inspection/result and remains listed.
- **Store fences:** direct ordinary surface mutation and replaced generation refuse. Prepared logout, stale witnesses, archived Session and revoked exact management/read authority produce no visibility or false success audit. The same completed/foregrounded Session accepts a later human turn with a fresh root.

## Focused verification

The combined module/regression selections cover 81 cases: 14 new store cases, five new API cases and 62 existing affected cases. Initial smoke/subset runs and final reruns overlap and are not added twice. All selected cases passed; final follow-ups are recorded below.

| Evidence log | Selection / result |
| --- | --- |
| `/private/tmp/tenant-activity-store-final.log` | 51 passed: 14 Activity/foreground cases plus existing 35 background admission/runtime and two catalog/management cases |
| `/private/tmp/tenant-activity-runtime-receipt-regression.log` | 12 passed: tenant Runtime generation/scope/revision/later-input and stale/forged authenticated receipt boundaries |
| `/private/tmp/tenant-activity-api.log` | Nine passed: two active foreground/Activity/new-message cases plus seven background model/host-recovery/actual MessagePack cases |
| `/private/tmp/tenant-activity-api-boundaries.log` | Six passed: two failed human foreground-persistence cases, two authenticated live JavaScript reconnect/switch/logout cases and two durable model-approval SQLite host-recreation cases |
| `/private/tmp/tenant-activity-boundaries-final.log` | Final 14-case store follow-up, including current archived inspection and scoped revocation: 14 passed |
| `/private/tmp/tenant-activity-api-final.log` | Final active flow plus actual post-foreground SQLite host recreation: three passed |
| `/private/tmp/tenant-activity-legacy-foreground.log` | Shared Runtime legacy foreground regression: two passed |

Commands use `dotnet test <project> --no-restore --filter '<selection>' --nologo`; the two no-build regression runs also use `--no-build`. Exact selections:

```text
Infrastructure module/regressions:
FullyQualifiedName~Tenant_foreground_|FullyQualifiedName~Tenant_activity_|FullyQualifiedName~Tenant_background_|FullyQualifiedName~TenantRunAdmissionTests.Canonical_runtime|FullyQualifiedName~TenantRunAdmissionTests.Tenant_delivery|FullyQualifiedName~Public_conversation_view_and_management_hints_keep_chat_read_and_scoped_management_separate

Runtime/receipt regression:
FullyQualifiedName~Tenant_runtime_fences_generation_scope_revision_and_later_input_in_both_stores|FullyQualifiedName~Tenant_receipt_writer_rejects_stale_human_or_forged_delivery_evidence_without_committing_credit

API module/background:
FullyQualifiedName~Tenant_active_background_continues_through_its_mailbox|FullyQualifiedName~Tenant_background_

API authority/transport/approval boundaries:
FullyQualifiedName~Tenant_rejected_foreground_persistence_|FullyQualifiedName~Tenant_live_MessagePack_JavaScript_receives_actual_worker_text_with_current_auth_reconnect_switch_and_logout|FullyQualifiedName~Model_approval_and_exact_checkpoint_survive_SQLite_host_recreation_before_human_decision

Final store follow-up:
FullyQualifiedName~Tenant_foreground_|FullyQualifiedName~Tenant_activity_

Final API follow-up:
FullyQualifiedName~Tenant_active_background_continues_through_its_mailbox|FullyQualifiedName~Tenant_foreground_visibility_and_original_Activity_result_survive_actual_SQLite_host_recreation

Legacy foreground:
FullyQualifiedName~Quiet_background_completion_and_foregrounding_preserve_same_session_for_a_new_turn
```

The actual API flows use InMemory and temporary SQLite WebApplicationFactory hosts, production workers and controlled Synthetic model gates. Existing wire regressions use loopback Kestrel and the real JavaScript SignalR/MessagePack client. No public debug endpoint, live provider or external account was used. Initial compile corrections used the actual nullable Runtime owner pattern and existing `AttemptCount` property; terminal verification supersedes those failed builds.

No frontend source/presentation changed, so no frontend build or new visual acceptance/MCP claim is made. The previous checkpoint's browser evidence remains historical evidence for that revision only. No CI/full local CI suite, milestone acceptance or full proposal completion was requested/run. Canonical relative links/anchors and whitespace are checked before commit. Test hosts dispose their disposable state; no user process/catalog is reset.

## Remaining scope

Tenant Activity/Background UI must consume these canonical reads and explicit transition with current scoped hints and saved-fact recovery. Creation Review/exact grants, protected/native background effects, inbox/wait tools, scheduled/event/initiative and autonomous Browser origins, other transport/voice/rich-input arbitration, general System guardrails/audit/reconciliation and final authority/schema consolidation remain required. Backend qualification is not acceptance of these remaining families or the complete goal.
