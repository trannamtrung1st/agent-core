# Tenant public Chat discovery — 2026-10-17

Successor to `565f8d5e`. Inspection of the existing frontend found that its managed agent picker still uses administrative Instance inventory and owner transport. Reusing that inventory for canonical Chat would exclude a Chat-only member or require unnecessary administrative read access. This checkpoint adds the public discovery prerequisite; it does not enable the tenant Chat frontend or claim full proposal/milestone acceptance.

## Delivered contract

`GET /api/v2/tenants/{tenantId}/chat/agents` uses the existing canonical Account cookie, tenant User and no-store policy. `TenantChatService` checks the actual current human/session, selects active tenant agent executors and evaluates scoped `agents.chat` per exact Instance with the existing IAM engine. Administrative `agents.read` neither substitutes for Chat nor becomes a prerequisite for it.

The endpoint accepts validated page/pageSize and a trimmed, bounded, case-insensitive public name/role query. It authorizes before search, then passes only eligible matching IDs to the existing owned store for count/offset/order. Unauthorized agent names and private tone/settings never influence results or counts. Search currently uses trusted point reads of authorized candidate persona labels before the paged store query; no new index/schema or parallel inventory is added. This is not a load/performance acceptance claim.

Each item returns only public persona, exact Instance/Definition/version identity, creation revision and effective language. Voice availability remains false for the current tenant text transport. No tone, instructions/settings policy, credential, executing Principal/SystemIdentity row, role snapshot or grant token is returned. `securityRevision` and `canCreateSessions` are concurrency/presentation hints; Session creation independently checks current human, Chat and Session-create/read authority and exact witnesses.

After store/definition I/O, actual human/session and security are rechecked. Revocation, logout or archive during the read prevents a stale page/count from returning. Discovery creates no Session, Run, profile, role, grant or audit entry. The selected revisions can feed the existing canonical Session creation and authenticated text Hub without owner-capability fallback.

## Executed use cases

| Actions and expected result | Observed result |
| --- | --- |
| Give a member administrative agent read without Chat, then inspect both inventories | Admin can enumerate the three Instances; public Chat total is zero. Both stores. |
| Revoke administrative read, grant Chat on two exact agents, read one-item pages | Both authorized IDs appear once with total 2; the hidden third agent never affects paging/counts; direct administrative detail still denies. Both stores. |
| Search case-insensitive public labels, an unauthorized name, or a private tone token | Only the authorized label matches; hidden/private-field queries return total zero. Invalid page, size and oversized query reject. Both stores. |
| Grant Session-create/read, select the public row and create using its revisions | Actual member owns creating attribution and the actual agent retains executor identity. Chat discovery grants no additional permissions. Both stores. |
| Archive a visible executor or revoke its Chat grant | Subsequent inventory/count excludes it. A different tenant owned by the same Account sees its own empty inventory; the wrong tenant User reference denies. Both stores. |
| Hold an actual store read, revoke Chat/log out/archive, then return its saved page | Human/security revalidation rejects the stale result. Both stores, deterministic gates. |
| Authenticate a Chat-only member through HTTP, select/create from the new directory, attach through MessagePack and send text | The real tenant worker completes Hello from synthetic; administrative reads stay denied and public rows omit private fields. Both stores. |
| Revoke Chat after completion and repeat discovery/cached command/attach | Inventory becomes empty and commands/attach deny; anonymous, owner-token-only and logged-out directory reads are 401. Both stores. |

The native JavaScript two-tenant SignalR/MessagePack journey now also selects each new Instance from the public Chat directory and supplies its exact revision/security witnesses to Session creation. It then exercises actual worker replies, switching, reconnect, delivery receipts, logout and stale-command guards. The fixture requires zero outbound provider HTTP. All hosted data uses fresh disposable Synthetic roots.

## Focused verification

The initial store selection passed six gates and failed two setup cases because the fixture referred to historical customer-support/compliance filenames that are not shipped in this checkout. Replacing those references with existing examiner Instances produced 8/8 passing cases. Public-label search was then added and the final eight-case selection passed again. The new hosted conversation selection passed both cases, then passed both again with search/validation assertions. A missing test-probe namespace import was corrected before hosted execution. Setup/compile failures are not counted as passing evidence.

The final matching-binary affected API regression passed **6 cases**: both new hosted discovery conversations, both real JavaScript two-tenant MessagePack journeys, existing agent inventory/archive/restore and canonical Session HTTP lifecycle. Together with the **8** final store/service cases, **14 distinct focused checks passed**, with none failed or skipped. Initial and repeated hosted selections overlap these results and are not counted again.

```sh
dotnet test tests/AgentCore.Infrastructure.Tests --no-restore --filter FullyQualifiedName~Tenant_chat_discovery --logger 'console;verbosity=minimal' --nologo
dotnet test tests/AgentCore.Api.Tests --no-build --no-restore --filter 'FullyQualifiedName~Tenant_chat_inventory_HTTP|FullyQualifiedName~Tenant_live_MessagePack_JavaScript|FullyQualifiedName~TenantAgentAccessTests|FullyQualifiedName~Canonical_Session_HTTP' --logger 'console;verbosity=normal' --nologo
```

The final API diagnostic log is `/private/tmp/agent-core-chat-discovery-api-20261017.log`. No CI, Real provider, installed-data migration/reset or browser/UI acceptance was run or claimed. Frontend inspection identified the missing prerequisite; no product UI or design-system files changed. The earlier intermittent SQLite preference failure remains the separate unresolved follow-up recorded in the delivery ledger.

## Required continuation

Extend the existing frontend service/store and Chat compositions with trusted tenant context; use these public candidates rather than admin inventory. Adapt cookie-authenticated HTTP, tenant Session routes/rail, AttachTenant and witness-aware text/cancellation/receipts, preserving drafts and retiring prior scoped state/transport on switches/logout. Exercise actual rendered conversations, reconnect and authority loss in Synthetic browsers at desktop/mobile sizes. Canonical voice, rich delivery, live input arbitration, approval links/notifications, other resource/effect families, autonomous admissions and final legacy-authority/schema removal remain required for the full goal.
