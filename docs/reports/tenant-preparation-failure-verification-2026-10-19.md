# Tenant preparation failure lifecycle verification — 2026-10-19

This checkpoint closes the deterministically reproduced context/brain preparation stall in the existing Session Runtime. It follows the [virtual Attachment read findings](tenant-attachment-virtual-read-verification-2026-10-19.md), on base commit `480b996b`. The implementation is the commit containing this report. It does not establish the cause of every earlier intermittent pre-model failure, or accept the full tenant IAM proposal.

## Reproduced problem and correction

`LaunchBrain` previously caught errors from `DecideAsync` but left earlier context construction outside that catch. Such an exception could escape the worker without a mailbox outcome. Even a caught brain error only published idle/error output: its already claimed Run remained Running. Four deterministic both-store cases, faulting canonical Attachment context processing or the real brain, all timed out waiting for terminal state before the correction. No provider request or tool effect was involved.

The entire context/decision worker is now supervised. Unexpected errors, null decisions and relevant cancellation return a normalized failure through the existing urgent mailbox. Only the current epoch, turn, response and claim generation can settle the Run; the existing Fail command supplies generic bounded failure metadata and preserves Domain unsafe-effect rules. No model/tool retry is introduced. A later independent message may proceed normally in the same Session.

Evaluation completion also returns its exact handle to the mailbox for identity-checked cleanup. A retired worker cannot clear a newer evaluation, including when it ignores cancellation and fails later. Shutdown acknowledges abandoned failure waiters and disposes retired handles without another execution owner or durable Task/CTS payload.

Before an answer exists, a preparation error creates no fake assistant row. If an approved successor already has streaming progress, the incumbent noncoalesced writer saves its Failed status and safe diagnostic reference before the matching Run is settled. Text/envelope and conservative receipt projections remain intact. This is needed to preserve truthful history when an approved operation completed but preparation of its later answer failed.

## Executed local checks

| Check | Result | Expected versus observed |
| --- | --- | --- |
| Deterministic baseline before correction | 4 failed / 37 s | Canonical context and brain faults in memory/SQLite reached the controlled fault, then exceeded the unchanged five-second terminal wait. The first claimed attempt remained open instead of failing. |
| Initial correction | 4 passed / 22 s | Each controlled fault now yields a terminal `preparation-failed` Run, releases the claim, keeps attempt count 1, records no provider/effect work and permits a new independent HTTP message. |
| Complete lifecycle/effect slice | 14 passed / 1 m 10 s | Both stores: context, brain and null-decision failures settle; no invented assistant answer appears in actual conversation history. A noncooperative context/brain callback is held, its Run is cancelled over HTTP, a newer Run starts, then the old failure arrives: old stays Cancelled and new stays Running until its own model completes. |
| Completed-effect and recovery boundaries within those 14 | 4 passed, included above; final explicit reopened-grant readback 2/2 passed / 17 s | Exact Artifact Review and exceptional grants use actual model/HTTP consent and the incumbent successor. Publication completes before the controlled preparation failure. Exactly one Artifact/download, completed receipt and consumed grant remain; saved progress is Failed. Repeated worker/approval passes do not repeat publication. Both SQLite variants recreate the application host and retain the same failed Run, completed side effect/receipt, original UTF-8 download and failed history with zero new model requests. |
| Original affected Attachment group after initial supervision correction | 35 passed / 2 m 47 s | Reexercise virtual text/Office, exact consent, source/permission denial, budget/policy order and the InMemory canonical DOCX baseline. Earlier unexplained observations are not erased by this pass. |
| Shared Runtime regression selection | 102 passed / 1 s | Run ownership and headless retry, accepted-turn detach, Attachment processing, initiative, cancellation and completion preserve their expected state/output behavior. |
| Existing tenant cancellation/SQLite read recovery | 31 passed / 3 m 5 s | Noncooperative model cancellation still releases capacity; pending/completed Attachment consent and direct retained text/image results recreate hosts, while revoked authority/source/capability still prevents delivery. |
| Ordinary Artifact consent/rich continuation | 12 passed / 57 s | Existing Review/exceptional grant approve/reject/cancel and independently scoped rich references remain working with normal preparation. |
| Whitespace/relative Markdown targets | Passed; 1,373 relative Markdown file targets checked, zero missing | Validate the changed source/docs and their real local file targets. |

All hosts, users, stores and files were disposable Synthetic fixtures. Assertions use the real conversation content owner, not metadata-only Session inventory, to establish failed/absent assistant rows. Early completed-effect assertions mistakenly queried the metadata view and were corrected before the passing 14-case matrix. An earlier completed-effect setup group also observed a pre-approval timeout and a `provider-unknown` failure; their cause was not established and they are not counted as passes.

## Reproduction commands

From the repository root, with local .NET IPC permissions:

```sh
dotnet test tests/AgentCore.Api.Tests --no-restore --nologo -m:1 /p:UseSharedCompilation=false --filter 'FullyQualifiedName~Tenant_preparation_' --logger 'console;verbosity=minimal'
dotnet test tests/AgentCore.Application.Tests --no-restore --nologo -m:1 /p:UseSharedCompilation=false --filter 'FullyQualifiedName~AgentRunDurabilityTests|FullyQualifiedName~AcceptedTurnDetachDurabilityTests|FullyQualifiedName~AttachmentProcessingRuntimeTests|FullyQualifiedName~InitiativeTests|FullyQualifiedName~SyntheticTextSliceTests|FullyQualifiedName~SessionRuntimeCompletionTests' --logger 'console;verbosity=minimal'
dotnet test tests/AgentCore.Api.Tests --no-restore --nologo -m:1 /p:UseSharedCompilation=false --filter 'FullyQualifiedName~Active_tenant_HTTP_cancellation|FullyQualifiedName~Tenant_attachment_Review_checkpoint|FullyQualifiedName~Tenant_attachment_model_read_checkpoint' --logger 'console;verbosity=minimal'
dotnet test tests/AgentCore.Api.Tests --no-restore --no-build --nologo -m:1 --filter 'FullyQualifiedName~Artifact_model_HTTP_exact_review_or_grant|FullyQualifiedName~Reviewed_Artifact_successor_can_publish' --logger 'console;verbosity=minimal'
dotnet test tests/AgentCore.Api.Tests --no-restore --no-build --nologo -m:1 --filter 'FullyQualifiedName~Tenant_attachment_virtual_file_reader|FullyQualifiedName~Tenant_attachment_model_reads_historical_source&DisplayName~sqlite: False&DisplayName~docx' --logger 'console;verbosity=minimal'
git diff --check
```

## Boundaries and remaining goal

This corrects an observed failure-handling mechanism; it does not prove the root cause of every earlier sporadic InMemory/SQLite pre-model failure or claim stability from passing reruns alone. No automatic model/effect retry, relaxed IAM/source fence, second authority/scheduler, schema migration, installed-data reset, Real-provider call, deployment or GitHub CI was added or run. Actual controlled host recreation is distinct from physical process-kill recovery.

No UI composition or visual token changed; this is backend HTTP/runtime/history qualification, not a new browser/Impeccable acceptance claim. Attachment namespace browsing/search and human materialization, other resource/Browser lifecycle adapters, autonomous/background/event/transport families, final obsolete-authority/schema removal and proposal-wide final qualification remain open. The original full goal stays active.
