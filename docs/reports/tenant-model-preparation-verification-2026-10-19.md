# Tenant model preparation verification — 2026-10-19

This checkpoint follows the [Attachment namespace findings](tenant-attachment-namespace-verification-2026-10-19.md) on base `3cba244b`. It reproduces and corrects a concrete owned-progress/read-fence race and an unsupervised model-prelude outcome, rather than treating a passing rerun as a cause analysis. The implementation is the commit containing this report. It complements the [earlier brain-preparation correction](tenant-preparation-failure-verification-2026-10-19.md); the original full tenant IAM goal remains active.

## Evidence and mechanism

A diagnostic reexercise of the existing 29 namespace cases had 27 passes and two pre-namespace failures (`/private/tmp/agent-core-tenant-handoff-diagnostic.log`, 3 m 22 s). A focused 12-case run then had nine passes and three initial binding/setup failures (`/private/tmp/agent-core-tenant-handoff-stage.log`, 1 m 56 s). Test-only inspection during a stall found a live Runtime with healthy waiting mailbox/persist loops, no in-flight work, an active generating response and a streaming assistant row, while the claimed Run still had no checkpoint. This places that observed dropout after brain return/assistant creation. No Runtime exception had reached the diagnostic logger. Temporary private-field inspection was removed from the test code after localization.

`PumpModelAsync` previously performed checkpoint restoration, current-directory acquisition, previous-Run inspection and capability/context projection before entering its provider-loop `try`. Its caller paired work accounting in `finally` but did not supervise these earlier failures. A fault there could end the task with no mailbox outcome and leave the Run/streaming row open. A failed current-directory reply also returned silently.

The normal writer saves the assistant placeholder concurrently with that read-only projection. Attachment context preparation compared the entire Session snapshot revision before/after its reads. Its own placeholder commit could therefore produce `attachment_context_changed` even though Run/security/configuration, parent lifecycle and the bound original had not changed. When raised in the unprotected prelude, that conflict abandoned generation; when raised within the later provider handler, it could instead appear as `provider-unknown`.

The deterministic fixture holds the real assistant save until a post-brain context read captures the older Session metadata, commits that save, then returns the captured metadata. It verifies that the committed revision advanced. A separate fixture throws a private IOException at the same pre-provider boundary. All four both-store cases timed out awaiting terminal state before correction; all four pass afterward. This establishes this mechanism. It does not prove that every historical sporadic provider/setup failure used the same path.

## Correction and ownership

The full model worker lifetime is now supervised around the existing provider/effect handler. Unexpected prelude/cleanup errors return safe diagnostic references through the incumbent preparation mailbox path. Missing current-directory context becomes a fenced failure. Only the matching active nonterminal response, epoch, turn and claim may close its streaming history and Run. The existing noncoalesced writer saves Failed history first; the current Run settles, failed response completion is emitted and active work/capacity is cleared. Already terminal/superseded responses reject the late failure. No new persistence, retry, approval, effect or scheduling owner is introduced.

Attachment context fencing retains current Run/security/Instance witnesses, independent current IAM/policy/capability, Session ownership/mode/lifecycle/archive/deletion and exact bound source/byte integrity. A conversation revision-only assistant-progress save is not new read authority or changed source provenance. Permitting that valid concurrent progress requires no retry of generation or effects, no provider bypass and no cached permission acceptance.

## Executed local checks

| Check | Result | Expected versus observed |
| --- | --- | --- |
| Deterministic before correction | 4 failed / 37 s | Both stores: owned placeholder commit between Attachment fences, or a private pre-provider I/O fault, reaches its controlled boundary and then fails the unchanged five-second terminal wait. |
| First corrected integrated path | 4 passed / 24 s | The controlled legitimate revision race completes with one model request containing the original authorized content. Unrelated setup I/O fails its Run and real assistant history safely with no model/effect call. Each case permits a new independent message in the same Session. |
| Complete lifecycle/Attachment selection | 87 passed / 7 m 46 s | Actual both-store model/HTTP flows include controlled races/faults, late cancellation, exact consent with completed effects/consumed grants, actual SQLite host recreation, namespace reads, source/permission refusal and virtual text/Office readers. |
| Actual MessagePack completion | 2 passed / 13 s | A real cookie-authenticated loopback WebSocket receives one failed response completion after Failed history/Run settlement, no text delta/model call for that failure, then a completed response for the new independent message on the same subscription. |
| Shared Runtime regressions | 102 passed / 2 s | Run claim/retry, accepted-turn detach, Attachment processing, initiative, Synthetic text and completion retain expected state and output behavior. |
| Current source/authority fences | 41 passed / 54 s | Both-store Attachment context, model/read and read-consent checks retain current independent permission denial, source integrity, historical revalidation and post-I/O refusal. |
| Whitespace/local Markdown targets | Passed; 1513 local Markdown targets checked, zero missing | Validate changed source/docs and their real local targets. |

Across the complete lifecycle, wire, shared Runtime and source selections, 232 distinct focused cases passed; the initial four corrected-path cases are included in that total rather than added again.

Late-fault tests hold a deliberately noncooperative old preparation callback across actual HTTP cancellation and a newer held model request, then release its exception. The old Run/history remain cancelled/interrupted and the new Run remains Running until its own model completes. Completed-effect tests use actual Review/exceptional-grant publication before the controlled model-prelude fault, retain one Artifact/receipt and a consumed grant (or no grant for Review), then recreate both SQLite hosts to verify retained factual failure/history/download without replay.

All users, hosts, stores and files were disposable Synthetic fixtures. The simultaneous focused builds reported one transient output-copy retry; it succeeded and the affected tests executed. The wire fixture first injected through a shared recipient read and was corrected to a generation-only Run-facts boundary; its next assertion used an incorrect `failed` boolean instead of the existing protocol `status` field and was corrected. No failed/aborted diagnostic, fixture or baseline run is counted as passing evidence. No installed data was reset and no migration was added.

## Commands

```sh
dotnet test tests/AgentCore.Api.Tests --no-restore --nologo -m:1 /p:UseSharedCompilation=false --filter 'FullyQualifiedName~Tenant_model_preparation_|FullyQualifiedName~Tenant_preparation_|FullyQualifiedName~Tenant_attachment_namespace_|FullyQualifiedName~Tenant_attachment_Review_checkpoint&DisplayName~namespace-|FullyQualifiedName~Tenant_attachment_virtual_file_reader' --logger 'console;verbosity=minimal'
dotnet test tests/AgentCore.Application.Tests --no-restore --nologo -m:1 /p:UseSharedCompilation=false --filter 'FullyQualifiedName~AgentRunDurabilityTests|FullyQualifiedName~AcceptedTurnDetachDurabilityTests|FullyQualifiedName~AttachmentProcessingRuntimeTests|FullyQualifiedName~InitiativeTests|FullyQualifiedName~SyntheticTextSliceTests|FullyQualifiedName~SessionRuntimeCompletionTests' --logger 'console;verbosity=minimal'
dotnet test tests/AgentCore.Infrastructure.Tests --no-restore --nologo -m:1 /p:UseSharedCompilation=false --filter 'FullyQualifiedName~Tenant_attachment_context_|FullyQualifiedName~Tenant_attachment_model_read_|FullyQualifiedName~Tenant_attachment_read_' --logger 'console;verbosity=minimal'
dotnet test tests/AgentCore.Api.Tests --no-restore --nologo -m:1 /p:UseSharedCompilation=false --filter 'FullyQualifiedName~Tenant_model_preparation_fault_emits' --logger 'console;verbosity=minimal'
git diff --check
```

## Remaining goal and practical limits

This closes the reproduced mechanism; attribution of every older SQLite preference/provider failure or binding timeout remains unproven. Controlled application-host recreation is distinct from physical process-kill/multi-instance recovery. No live provider/native external effect, deployment or GitHub CI was exercised. The two completion cases use a real .NET SignalR/MessagePack client and loopback Kestrel; no fresh JavaScript/browser/Impeccable acceptance is claimed. No frontend composition or visual token changed.

Human Attachment materialization, remaining Browser/resource lifecycle adapters, autonomous/background/event/transport families, obsolete-authority/schema consolidation and final requirement-by-requirement isolation/UX/design-system qualification remain open. The full proposal goal remains active.
