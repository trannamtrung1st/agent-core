# Tenant live human-input queue verification — 2026-10-20

This successor qualifies canonical queued text/file intake during live tenant execution. It supersedes the earlier active-binding `session_in_use` gap only for the converted queue path. The full multi-tenant/IAM/approval proposal and original goal remain active. Interrupt/Steer, voice arbitration, frontend live-send interaction, background mutation/publication/native effects, creation Review, inbox/wait tools, autonomous origins, remaining System/audit families and final authority/schema cutover remain open. No milestone or full proposal acceptance is claimed.

## Implementation

`TenantRunService` prepares immutable original-human, configuration and source witnesses. The tenant host reserves its existing quiescent write barrier or enqueues `TenantInputReceived` on the actual live Session generation. The mailbox reserves an opaque sequence before I/O. The existing writer commits current progress, immutable User entry, same-Session Attachment bindings, fresh UserTurn Activation/Run, tenant revision and canonical body-free audit together. Both stores repeat original-human/current permission, executor/configuration/lifecycle, source, revision and generation checks. A refusal cannot save a partial checkpoint, bind a file or fail the independent active Run.

Future human bodies remain outside the active task's prompt/history window. The Runtime carries committed sequence/activity high-water marks into later saves; same-message no-write replay cannot overwrite newer streaming state. Refused sequence reservations may leave gaps. Accepted Runs retain independent human roots, exact sources and durable ordering. Blocked same-Session work is filtered before global batch limits and fenced again at claim. Earlier retry work retains its position, approval waits release the slot, and queued reports yield to human work without a circular wait. Already-running Core reports and terminal/suspended generations finish before intake rebases only the saved Session witness; current human/security/configuration/source authority still applies.

HTTP and real MessagePack accept omitted/null or exactly `queue` behavior and reject interrupt. Existing tenant frontend local drafts remain on their current interaction; this change adds no frontend presentation or browser-send claim. No new queue table/migration, second mutable owner, provider credentials or raw-audio mailbox path was introduced.

The broader application checks found that the prior tenant background schema predicate also matched legacy BackgroundOutcome contexts. That removed legacy completion offering and caused invalid budget checkpoint transitions. Requiring trusted tenant execution context restores legacy offering; the full selected completion/recovery regressions and a specific projection regression pass.

## Exercised workflows

| Scenario | Observable result |
| --- | --- |
| Hold an actual background model, explicitly Continue its Session, upload an original and submit two messages | Admission binds the file once, creates distinct fresh human roots and keeps both Runs queued. Active runtime history/model input excludes future human messages. Releasing work completes each response without sequence collisions or changing initial background origin. |
| Exact replay, changed replay and unsupported behavior | Exact fresh-witness replay returns the original graph without another binding/audit. Changed text conflicts. Interrupt refuses without implied cancellation. |
| Stop active source after queue acceptance | The source becomes Cancelled; independently accepted queued messages still complete. |
| Real JavaScript SignalR/MessagePack | Actual attachment-bound `SendText` with queue behavior accepts while the model is held; exact command replay returns the saved acknowledgement, interrupt refuses, and one correctly attributed queued response arrives after release. |
| Queue/replay during a visible partial response, reattach, then release | The current `Hello` prefix remains intact; original and queued responses each finish as `Hello from synthetic.` with distinct entry sequences and retained last-user-activity metadata. |
| Prepared live input loses logout/chat/read authority, security/configuration/Session witness, frame generation/owner or exact file witness | No entry, new Run, binding, partial summary or success audit commits. The active source remains Running. |
| Equal-clock queued input, another Session, earlier retry and queue capacity | Source-entry ordering resolves equal timestamps. Blocked rows do not consume the global runnable limit. Later same-Session work cannot overtake an earlier retry; unrelated work remains runnable. A different authorized human retains their own original initiator/root without raw Run-create authority. At 100 nonterminal Runs overflow adds no partial state or audit; exact replay still succeeds. |
| Actual SQLite host recreation after accepted queued file and cancellation of source | The same queued Run/input/binding and original bytes survive. The queued response executes once; completed/cancelled source work is not replayed. |
| Explicit capable published definition reads a live-bound Attachment | Actual original-byte virtual read under allowed policy and typed exact Attachment read through Review use independent current scope/consent. The background successor retains its task/root and only its terminal result reports once to the original parent. General Assistant v32 still cannot acquire missing capability through raw IAM. |

Earlier reports retain their historical observations. Pending-only and missing-capability refusals remain tested separately from positive bound-source journeys. Explicit tools may inspect currently authorized saved sources; that is distinct from consuming a future human turn in the active task's input context.

## Checks

Checks are focused local Synthetic execution against disposable data. No hosted CI, full CI suite, live providers, Real application, user-data reset or deployment was requested or used. No frontend files changed, so backend integrated execution and real MessagePack are the applicable runtime evidence.

Final checks use `dotnet test <project> --no-restore --filter '<selection>' --logger 'console;verbosity=minimal'`. The API regression additionally uses `--no-build`. Builds are included in the other final test commands.

| Selection / local log | Result |
| --- | --- |
| Application Runtime queue/completion/persistence selection; `/private/tmp/tenant-live-input-application-final.log` | 97 passed. |
| Infrastructure admission/authority/Runtime/background/Continue/source selection; `/private/tmp/tenant-live-input-store-final.log`, corrected ordering/capacity `/private/tmp/tenant-live-input-order-cap-final.log` | 100 distinct cases passed across final selections: 96 passing cases in the grouped run plus four focused ordering/capacity cases. The grouped run's two ordering fixtures initially supplied an invalid uppercase failure code; the focused successor uses the canonical code. |
| API queue/Stop/MessagePack/host recreation/positive bound-source selection; `/private/tmp/tenant-live-input-api-final2.log`, streaming `/private/tmp/tenant-live-input-stream-final.log` | 13 distinct cases passed across final selections: eleven passing grouped cases plus two focused streaming cases. The grouped streaming assertions initially demanded timestamp equality; the successor verifies the required monotonic lower bound, including before model release. |
| Existing API background file/Review/report/Continue regression; `/private/tmp/tenant-live-input-api-regression.log` | 47 passed. |

**257 distinct local cases passed across the final selections.** All selected processes are terminal.

Exact selections:

```text
Application:
FullyQualifiedName~UserTextQueueTests|FullyQualifiedName~QueuedAttachmentRuntimeTests|FullyQualifiedName~AcceptedTurnDetachDurabilityTests|FullyQualifiedName~AgentRunDurabilityTests|FullyQualifiedName~PersistenceReceiptTests|FullyQualifiedName~MailboxBackpressureTests|FullyQualifiedName~SessionRuntimeCompletionTests|FullyQualifiedName~TenantBackgroundFileObservationTests

Infrastructure grouped:
FullyQualifiedName~Tenant_live_|FullyQualifiedName~Tenant_queued_report|FullyQualifiedName~Tenant_runtime_|FullyQualifiedName~Actual_message_admission_|FullyQualifiedName~Current_chat_and_session_read_|FullyQualifiedName~Prepared_admission_|FullyQualifiedName~Tenant_background_|FullyQualifiedName~Tenant_foreground_|FullyQualifiedName~Tenant_message_attachment_binding_

Infrastructure corrected ordering/capacity:
FullyQualifiedName~Tenant_live_queue_is_durable|FullyQualifiedName~Tenant_live_queue_capacity

API module:
FullyQualifiedName~Tenant_live_queue_|FullyQualifiedName~Tenant_live_queued_file|FullyQualifiedName~Tenant_background_capable_definition

API corrected streaming:
FullyQualifiedName~Tenant_live_queue_during_streaming

API existing regression:
FullyQualifiedName~Tenant_background_file_|FullyQualifiedName~Tenant_background_pending_Attachments|FullyQualifiedName~Tenant_background_model_|FullyQualifiedName~Tenant_background_completed_child_survives|FullyQualifiedName~Tenant_background_completion_crosses|FullyQualifiedName~Tenant_active_background_continues|FullyQualifiedName~Tenant_rejected_foreground|FullyQualifiedName~Tenant_foreground_visibility
```

The first representative queue attempts exposed test assumptions about duplicate HTTP status, JsonElement equality and metadata-only Session snapshots; the corrected journey uses 200 replay, UUID value comparison and authorized history. Positive bound-source setup initially attempted forbidden always-projection of the context-only reader, then incorrectly treated an allowed policy decision as executed content; the final disposable published definition respects projection rules and executes the offered virtual read, while the typed Review case verifies actual resumed content. These are not relabeled passing executions.

The application regression group initially passed 79 and failed 17 legacy background cases. Diagnostic logging identified the invalid checkpoint transition caused by the tenant schema predicate leaking into legacy work; the production predicate correction and specific regression produce the final 97 passing selection. Temporary diagnostic fixture logging was removed. Already-passing cases were not repeatedly rerun after test-only corrections.

Changed Markdown relative file links and `git diff --check` pass. All required scope remains focused local qualification; optional CI and full proposal acceptance remain unrun.
