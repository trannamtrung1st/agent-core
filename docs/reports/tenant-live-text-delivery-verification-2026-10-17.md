# Authenticated tenant live text — 2026-10-17

Successor to `f5b63ca1` and [protected persisted history](tenant-conversation-history-verification-2026-10-17.md). This delivers tenant text subscriptions through the existing SignalR/MessagePack endpoint and Runtime output, plus cancellation of a response before its first checkpoint. The full proposal and Phase 5/6/7 acceptance remain open.

## Implemented path

`AttachTenant` on `/hubs/session` requires Account authentication and an exact same/configured Origin under HTTPS or the explicitly enabled trusted-loopback HTTP policy. It uses the existing protocol-1 session.attach envelope with sequence 0 and payload tenantId. The Hub derives the actual tenant User and authentication-session reference from the authenticated Account. Owner capabilities and caller-selected identity never grant tenant access. Legacy and canonical subscriptions occupy separate registries; switching retires the old binding, including on a denied tenant switch.

`TenantSessionLiveDelivery` holds ephemeral recipient transport state only. The actual tenant host still owns the existing Session Runtime and shared capacity. A generation-bound output source is activated with that Runtime and removed before its capacity releases. Current Run/owner/response/generation, cancellation and lease checks fence source output. Each recipient event repeats current human/session/Account, exact Session read and Instance Chat. Revoked, logged-out, stale or timed-out recipients stop receiving. Parallel bounded recipient sends avoid serializing unrelated slow recipients; source and connection gates preserve order and attachment identity.

Fresh attach emits a protected session.ready snapshot, new attachment ID and sequence 1. A mid-response ready can overlay only the current source's already-published contiguous display prefix, while preserving durable received/heard offsets. An actual current Running, unexpired and uncancelled source is required before reporting an active response. Changing source during attach causes a retry rather than crossing generations. Reattach does not replay events or audio. Switching/disconnect/logout does not erase or reparent durable Runs; HTTP admission and cancellation remain independent, current-authority command paths.

The allowlisted event subset is response started, text delta/completed, markdown block, response completed/interrupted and Session state. Untyped memory/effect receipts, resource cards, legacy approval payloads, raw ready/history and voice data are not passed through. The public descriptor advertises no tenant voice capability. Unsupported prefix discontinuity suppresses further partial output rather than inventing missing text; durable final history remains authoritative. Full cross-process/approval-continuation streaming and the richer frontend recovery path are not qualified here.

## Cancellation gap found and fixed

The held-generation test exposed a real race: text could be published before the first periodic checkpoint, and the old cancellation transaction closed only saved rows. The Run stopped, but its transient response row could be absent and its entry sequence unreserved afterward.

The owning mailbox now refreshes its current Streaming row from its accumulator and supplies that exact response as trusted internal cancellation evidence. The atomic store transaction validates response/Run identity, entry identity/sequence and monotonic saved text/receipt prefixes. It saves the transient row if absent, interrupts it and advances Session/entry sequence together with Run/security/audit and existing approval/grant cleanup. It never accepts a replacement public snapshot or model/HTTP response fields. Unrelated rows, terminal results and greater saved receipts cannot be overwritten. After commit, stale Runtime saves remain rejected and noncooperative late output cannot reappear. Public history still clamps interrupted text to real received evidence; this does not invent a delivery receipt.

## Executed journeys

| Actions and expected result | Observed evidence |
| --- | --- |
| Real JavaScript SignalR/MessagePack over loopback Kestrel, both stores: login, attach, HTTP text admission, actual worker response | Contiguous textStart, response/Run identity, server sequence and attachment values; terminal text and safe history agree; no provider HTTP. |
| Anonymous, missing-Origin and foreign-Origin subscription attempts | Anonymous method authorization rejects; origin failures return a denied ack and no Session events. |
| Fresh ready after completion and same-Account two-tenant switch | New attachment/sequence 1, authorized history and no event replay; later Alpha work does not reach the Beta subscription. Foreign Session/tenant pairing denies. |
| Logout while attached, then a newly authenticated client starts more work | The old authentication session receives no further events; durable work completes independently. |
| Hold actual generation after Hello, reattach through real MessagePack WebSocket | Ready contains exactly the published Hello prefix, active response and zero receipt credit; remaining deltas start at the same offset and complete contiguously under the new attachment. Both stores. |
| Revoke only the recipient's Chat permission or log that recipient out while the original requester remains authorized | Original Run completes, while the recipient gets no later text/completion. Authority is not merged with the powerful agent or original requester. Both stores. |
| Stop the held Run through authenticated HTTP, then release the noncooperative model | Interruption arrives, saved row retains Hello as Interrupted with zero received credit, Run stays Cancelled and late text/completion is excluded. Both stores. |
| Both-store transaction cases: transient row, newer known prefix, wrong response, sequence collision, receipt regression | Accepted rows commit atomically; invalid evidence causes no Run/security/audit/Session mutation. Actual receipt offsets and user rows remain unchanged. |

The initial build caught a captured primary-constructor parameter and a missing fixture namespace; these were corrected before execution. One early live test failed on the missing transient response row, leading to the product fix above. Earlier failed runs are not counted as passing evidence.

## Focused local checks

The initial real-JavaScript selection passed 2 cases. The initial mid-response reattach selection passed 2; the expanded live selection passed all 10 after the cancellation fix. The transient-row transaction selection passed 12 after adding two negative-offset rejection cases. **577 distinct affected checks passed**, with none skipped: 498 Infrastructure and 77 API in the broad selections below, plus the 2 subsequently added negative-offset cases. The final 12-case transaction rerun overlaps 10 of the broad Infrastructure cases, so they are counted only once (500 distinct Infrastructure in total).

```sh
dotnet test tests/AgentCore.Api.Tests --no-restore --filter 'FullyQualifiedName~TenantSessionAccessTests|FullyQualifiedName~Model_credential_check_waits|FullyQualifiedName~Credential_approval_HTTP_projects_exact_consent' --logger 'console;verbosity=minimal'
dotnet test tests/AgentCore.Infrastructure.Tests --no-restore --filter 'FullyQualifiedName~TenantRunAdmissionTests|FullyQualifiedName~TenantSessionLifecycleTests|FullyQualifiedName~TenantOperationAuthorizationTests|FullyQualifiedName~TenantPreferenceOperationTests|FullyQualifiedName~TenantCredentialIntegrationTests' --logger 'console;verbosity=minimal'
dotnet test tests/AgentCore.Infrastructure.Tests --no-restore --filter FullyQualifiedName~Tenant_cancellation_atomically_closes_mailbox --logger 'console;verbosity=minimal'
```

All hosts/data/key roots are disposable and Synthetic. JavaScript wire evidence is actual MessagePack/WebSockets against Kestrel, not TestServer or a rendered page. The native test host explicitly enables the supported loopback policy and retains ordinary cookie/Origin checks. The earlier API build passed with zero warnings/errors. Changed Markdown local-link targets and balanced fences, plus `git diff --check`, passed.

## Remaining scope

Tenant frontend/tenant-switch UI and live controls, Hub message/receipt integration, durable recipient acknowledgements, voice, approval links/notifications, richer resource-aware events and complete continuation/recovery delivery remain required. No browser/Playwright UI acceptance is claimed. Other effect/resource families, trusted autonomous admissions, proactive authority propagation and final obsolete-authority/schema removal remain in the proposal. No CI, provider network, Real-host operation, deployment or installed-data migration/reset was performed. The full goal stays active.

Subsequent [tenant Hub command verification](tenant-hub-commands-verification-2026-10-17.md) adds bound SendText/CancelResponse, safe result acknowledgements and current-authority replay. Its updated JavaScript journey now sends the primary messages through the Hub; this report retains its historical subscription checkpoint counts. Recipient receipts and frontend/voice remain separate.
