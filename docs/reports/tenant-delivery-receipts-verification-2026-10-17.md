# Tenant text delivery receipts — 2026-10-17

Successor to `9a595076` and the [tenant Hub command checkpoint](tenant-hub-commands-verification-2026-10-17.md). This implements canonical text acknowledgements over the existing authenticated Hub and Session Runtime. The proposal remains in progress: tenant frontend/voice, rich receipts, live input arbitration, approval links/notifications, remaining resource/effect families, autonomous origins and final obsolete-authority/schema removal remain open.

## Delivered behavior

`ResponseReceived` retains type `response.received` and payload `{ textEndExclusive }`. The server-held attachment ledger derives the actual entry/sequence/response from successfully sent ready/text events, with a maximum of 128 delivered responses. The command requires the same exact Session/attachment, command sequence/fingerprint rules and current human/Session-read/agent-Chat authority as the other canonical commands. Nonempty blockIds explicitly reject until rich receipt conversion exists. An acknowledgement never proves audio, resource access or block delivery.

The active Runtime receives a typed mailbox command and queues its snapshot through the existing sole persist writer. The store checks the actual Run frame, immutable Session owner, current human authentication session and prepared tenant-security witness inside the write transaction/locks. Credit is adopted only after successful persistence. A rejected recipient receipt leaves the Run running and cannot put uncommitted credit into a later ordinary snapshot. Committed receipt offsets carry forward by exact entry identity into previously queued snapshots.

When the Runtime has retired, the existing host reserves the Session against new Runtime admission and the store patches only its actual saved assistant text entry. A host admission epoch invalidates snapshots loaded across the reservation, including a reservation that completes before Runtime registration. The host never holds its registry lock across database work, mailbox waits or worker cancellation callbacks. No additional conversation owner, scheduler, Task entity, role grant, audit event or durable schema is added.

Per-attachment acknowledged ends are monotonic and cannot exceed the published display prefix. The Session stores the maximum across legitimate recipients; a new attachment can acknowledge less without regressing that aggregate. Interrupted/failed saved entries reject new credit. Completed history can be acknowledged without an active output source. Receipt input supplies no text or status changes; an active save can also checkpoint the Runtime's already generated row. Receipts do not advance heard offsets, change Run outcome or approval/effect evidence, or increment the tenant-security revision. A quiescent no-op leaves the Session revision unchanged; a changed received maximum increments it.

## Executed use cases

| Actions and expected result | Observed result |
| --- | --- |
| Hold actual Synthetic generation after Hello; send negative, unsent, block and unknown-response receipts | All reject without received credit. Both stores. |
| Acknowledge Hello and inspect storage immediately after Ack | Received is 5 durably, heard remains 0; exact command replay does not write another Session revision. Both stores. |
| Send a regressing receipt; reattach; try the old attachment; acknowledge 2 through the new attachment | Regression and old attachment deny; new attachment succeeds while the Session aggregate stays 5. Both stores. |
| Release the model, acknowledge the full completed response, reattach completed history and acknowledge again | Full received offset persists; completed history works after generation/Runtime retirement; no heard credit. Both stores. |
| Revoke only recipient Chat or log out after a valid receipt, then retry cached/fresh acknowledgements | Both deny; independently authorized Run completes and saved credit stays at 5. Both stores. |
| Cancel the held response after a valid receipt, then send a fresh receipt and release noncooperative late output | New receipt denies, Run remains Cancelled, interrupted prefix retains received 5/heard 0. Both stores. |
| Prepare trusted receipt evidence, then change security, revoke authentication, or change bounds/entry/owner references before store commit | Runtime and quiescent store paths reject; no received credit or Session revision change; Run stays Running. Both stores. |
| Hold the real receipt writer after mailbox admission, revoke Chat or log out, then release the write and model | Receipt rejects at commit; the independently authorized Run completes with received/heard both zero. This proves rejected queued evidence cannot leak through a later normal save. Both stores. |
| Complete one response, hold a new Run after it loads its snapshot, then acknowledge the old response before releasing admission | The quiescent receipt commits, the obsolete snapshot is discarded and the new Run loads again, completes and retains the full old received offset. Both stores. |

All new hosts use fresh temporary roots, actual cookie/CSRF HTTP authentication and native MessagePack WebSockets. The actual JavaScript SignalR client also exercises completed-history receipts and cached receipt rejection immediately after logout; its fixture requires zero outbound provider HTTP. No Real provider, installed-data reset/migration or CI run is part of this checkpoint.

## Focused verification

The initial hosted receipt selection passed 8 cases; its expanded deterministic-race selection passed 14. The both-store transaction selection passed 10; the existing Application delivery/persistence receipt selection passed 16. The hosted cases overlap the affected API regression and are counted only once in the final totals below.

The first restricted build emitted no output and was stopped only by its exact task-owned PID before the normal execution fallback. Two compile errors were corrected before execution: nullable Runtime-owner access and a mistaken assumption that serialized Run response/cancellation fields were indexed columns. The final implementation uses saved entry status and the actual active Runtime frame, without scanning serialized Run history. The transaction test also needed its existing execution namespace import. No compile failure is counted as passing verification.

An intermediate affected API run passed 68 of 69 cases. Its completed-history test could select an older ready event from its existing queue before the newest attach event arrived, then submit that stale attachment. The corrected fixture waits only for events after that specific attach attempt and reports the safe acknowledgement error code on mismatch. The expanded 14-case rerun passes with that correction and explicit commit/admission gates; the intermediate failure is not counted as passing evidence.

The final matching-binary API regression passed **75 cases**, including the real JavaScript MessagePack journeys and all 14 hosted receipt cases. Together with **10** both-store receipt transaction cases and **16** existing Application receipt cases, **101 distinct focused checks passed**, with none failed or skipped. The overlapping initial/race reruns are not added again. The previous SQLite direct-preference provider-unknown failure did not recur in either affected run here, but remains a separate unresolved reliability follow-up; passing reruns do not establish its cause.

```sh
dotnet test tests/AgentCore.Api.Tests --no-build --no-restore --filter 'FullyQualifiedName~TenantSessionAccessTests|FullyQualifiedName~CommandAdmissionTests|FullyQualifiedName~ApplicationMessageWireTests' --logger 'console;verbosity=normal' --nologo
dotnet test tests/AgentCore.Infrastructure.Tests --no-restore --filter FullyQualifiedName~Tenant_receipt_writer --logger 'console;verbosity=minimal' --nologo
dotnet test tests/AgentCore.Application.Tests --no-restore --filter 'FullyQualifiedName~DeliveryReceiptTests|FullyQualifiedName~PersistenceReceiptTests' --logger 'console;verbosity=minimal' --nologo
```

The final API diagnostic log is `/private/tmp/agent-core-tenant-receipts-api-final-20261017.log`; the expanded deterministic receipt selection is `/private/tmp/agent-core-tenant-receipts-focused-20261017.log`. These disposable local logs are not repository artifacts.

No frontend/browser, microphone/speaker, rich receipt or milestone acceptance is claimed.
