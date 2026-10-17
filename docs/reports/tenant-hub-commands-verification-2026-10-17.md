# Tenant Hub text and cancellation commands — 2026-10-17

The [tenant receipt successor](tenant-delivery-receipts-verification-2026-10-17.md) adds durable text acknowledgements over the same authenticated command adapter. The receipt gap below describes this historical command checkpoint.

Successor to `547d600b` and the [authenticated live-text checkpoint](tenant-live-text-delivery-verification-2026-10-17.md). This delivers canonical SendText and CancelResponse over an authenticated tenant subscription, using the existing admission and owning-mailbox cancellation services. Durable recipient acknowledgements, frontend/voice and full proposal acceptance remain open.

## Delivered contract

After AttachTenant, the existing Hub SendText and CancelResponse methods select the canonical connection adapter. The server-held subscription supplies the actual human/authentication-session, tenant and Session. Each command requires its exact attachment/session, valid protocol envelope and next client sequence; current human/Session-read/agent-Chat checks run before execution or cached acknowledgement reuse. Payload identity/role/root fields do not select authority.

SendText accepts plain text plus positive expectedRevision/expectedSessionRevision witnesses. The event UUID is the durable message correlation. Existing TenantRunService derives requester/executor/root/configuration, applies its current policy and atomically admits through the existing stores. Ack adds safe nullable result metadata: created, Run/entry/response/Activation/root IDs and Session/security revisions. Nonempty rich parts/attachment IDs and any behavior selector reject in this checkpoint; live interruption/queue arbitration remains separate.

CancelResponse resolves responseId to a trusted Run observed in this attachment's response-start or active-ready publication. It separately requires current Run read/cancel and current revision witnesses, then uses the existing whole-Run cancellation path. A known response is correlation, not permission. Accepted result reports the exact Run's Cancelled status/revision. A Reader with Chat/history but no Run control cannot cancel. Cancellation still preserves exact approval/effect evidence and excludes noncooperative late output.

Command serialization is independent of event publication. The adapter releases the publication lock before waiting on Application/Runtime work, so mailbox cancellation can emit its interruption before acknowledging. It retains at most 128 command fingerprints/outcomes and 128 observed response mappings per attachment. Exact retries reuse safe saved acknowledgements after fresh authority checks; changed payload/sequence under an old event ID rejects. Old attachments and command gaps reject. Commands reaching operation handling consume their client sequence even on a business validation/conflict response; earlier binding/ordering/authority rejection does not consume it.

Reattach resets the ephemeral command window/sequence. A fresh-witness text retry with the original event UUID/text goes through existing durable human-message dedupe and returns the original graph without another Run. Cancellation has no durable replay token: after losing its ephemeral window, clients read actual Run state. Cache contents, revision witnesses and response mappings grant no reusable effect authority.

## Executed journeys

| Actions and expected result | Observed evidence |
| --- | --- |
| Real JavaScript MessagePack/WebSocket: attach, submit text through SendText, receive actual worker response | Both stores return a safe admission result and contiguous live reply. Actual Run requester remains the server-resolved tenant User despite forged payload principal/tenant/root fields. |
| Repeat the same accepted command after completion, then change text, skip sequence or use an old attachment | Exact retry returns the same Run; changed event reuse, sequence gaps and stale attachment deny. Stale witnesses return stale_revision without another admission. |
| Reattach and retry the original message event/text with fresh witnesses | Existing durable admission returns created:false with the original Run, without duplicate history. |
| Switch the same Account to Beta, submit through the new attachment, try an Alpha command | Beta Run has its distinct actual tenant User; stale Alpha attachment cannot submit through Beta and old Session output stays excluded. |
| Complete a command, log out and immediately repeat its exact cached request before further output | Cached success rejects on the revoked authentication session; a new authenticated client can continue without delivering to the old connection. Both stores. |
| Hold generation after Hello, attempt cancellation as a Chat-only recipient, then cancel through an authorized Hub connection | Reader denies; authorized cancellation emits interruption without a publication-lock deadlock, commits one audit and retains the interrupted prefix. Late noncooperative output stays excluded. Both stores. |
| Cancel as a limited Run controller, repeat exactly, revoke only agent-runs.cancel and repeat again | Permitted exact retry reuses the terminal result; subsequent cached retry denies despite retained Chat/read. No second cancellation audit. Both stores. |

The wire payload guard initially expected only the older text fields. It now explicitly allows the two nullable concurrency witnesses while retaining role/destination exclusion and verifies their JSON values. Protocol errors and authorization errors retain their corresponding safe categories. A broad development run was already executing when the category assertions/server code changed, so its JavaScript portion compared the updated fixture with the earlier loaded server binary. The final rerun rebuilds matching source/fixtures. Earlier failing regression output is not counted as passing evidence.

## Focused verification

The representative selection passed 4 cases before expanded cache-authority coverage; the expanded selection passed 6. These overlap the final affected regression and are not double-counted. **61 distinct affected API cases passed** in the final diagnostic regression, with none skipped. An isolated four-case preference rerun also passed and overlaps that selection.

```sh
dotnet test tests/AgentCore.Api.Tests --no-restore --filter 'FullyQualifiedName~Tenant_live_MessagePack_JavaScript|FullyQualifiedName~Tenant_live_reconnect&DisplayName~cancel' --logger 'console;verbosity=minimal'
dotnet test tests/AgentCore.Api.Tests --no-restore --filter 'FullyQualifiedName~TenantSessionAccessTests|FullyQualifiedName~CommandAdmissionTests|FullyQualifiedName~ApplicationMessageWireTests' --logger 'console;verbosity=minimal'
```

The API build passed with zero warnings/errors before the integrated checks. Changed Markdown local-link targets and balanced fences, plus `git diff --check`, passed. The selected journeys use real JavaScript MessagePack over disposable loopback Kestrel and gated real WebSockets on both InMemory and SQLite. Existing legacy command-admission/public-wire checks protect the shared Hub and Contracts surface. No frontend implementation or Playwright UI acceptance is claimed.

## Observed reliability follow-up

One earlier matched-binary regression returned provider-unknown in the SQLite allowed direct-preference scenario. Its isolated four-case rerun and the final 61-case diagnostic regression passed without a production change to that workflow. The cause is not established; it remains a reliability follow-up, not a claimed fix or evidence of full phase acceptance. The final diagnostic output is retained at `/private/tmp/agent-core-hub-command-regression-20261017.log`. The command/authority flows themselves have the direct wire evidence above.

## Remaining goal

ResponseReceived and durable recipient evidence still need canonical mailbox/persistence integration, including receipt timing after Runtime release; neither a sent event nor a command Ack advances received/heard offsets. Tenant frontend, live input arbitration, voice, approval links/notifications and richer resource-aware events remain required, alongside other effect/resource families, autonomous origins and final obsolete-authority/schema removal. No new schema or alternative scheduler was added. No CI, provider networking, deployment, Real-host operation or installed-data reset/migration was performed. The full goal remains active.
