# Protected tenant conversation history — 2026-10-17

Successor to `46b4877b` and the [active cancellation checkpoint](tenant-active-run-cancellation-verification-2026-10-17.md). This delivers authenticated persisted content reads over the existing tenant Session/Entry rows. It supplies the protected history prerequisite for tenant Chat/reconnect, without claiming live transport, frontend or proposal completion.

## Delivered contract

`GET /api/v2/tenants/{tenantId}/sessions/{sessionId}/messages` accepts mutually exclusive `after` (>=0) or `before` (>=1), and `limit` 1–100/default50. It returns the existing public history item shape with `nextAfter`, `hasMore`, `hasOlder`, `nextBefore`, `sessionRevision` and `securityRevision`. Newest and older pages are chronological; forward pages preserve the existing conservative hasMore behavior. Invalid bounds/cursors are 400; unavailable/foreign/deleted/denied content is masked as 403; changed witnesses during reads return 409 stale_revision. Canonical cookie/no-store rules apply and owner capabilities cannot authenticate this route.

`TenantConversationService` validates the actual human/session and tenant membership, exact Session read and owning Instance Chat. Metadata read is independently useful and does not itself authorize conversation content. The service resolves actual saved ownership before the separate typed content store reads. It repeats human, Account, IAM and Session checks after content/resource I/O and fences the resulting security/Session revisions. No human login/session, copied grants or fabricated Run claim enters model context or the content store.

`ITenantConversationStore` returns bounded stored rows with immutable ownership and the observed Session revision. InMemory uses its existing shared gates. SQLite reads root, revision and an indexed range in one transaction, at most limit+1 rows backward or limit rows forward; it does not load snapshot/Definition payloads for that content query. Existing metadata APIs remain content-free, and legacy memory/history still excludes owned rows. No migration or new row family is introduced.

Projection reuses `PublicHistory`: completed/failed text is visible; interrupted/streaming text is conservatively received-clamped. Reads never advance received/heard offsets, change a revision, write an audit or revive cancelled output. Artifact/attachment cards and attachment labels require independently owned recipient-readable targets; a tenant-wide grant cannot make an invented target exist. Source-event references and memory receipts have their separate read checks. Canonical admission remains text-only: unsupported legacy structured locators/labels are replaced with Resource unavailable, and untyped effect receipts are omitted pending target-aware conversion. This does not claim full rich-resource support or a universal free-text disclosure filter. Least-privilege agent authority and restricted Chat audiences remain necessary.

## Executed use cases

| Actions and expected result | Observed result |
| --- | --- |
| Authenticate, create an owned Instance/Session, submit text through HTTP, let the real production worker complete, read the saved reply through HTTP | Both InMemory and SQLite return the actual user and assistant text with matching terminal status and unchanged zero delivery offsets. Paging/no-store and safe fields pass. |
| Try anonymous, owner-capability-only, same-Account foreign-tenant and logged-out reads | Canonical content denies; legacy history cannot read the owned Session. |
| Seed trusted saved completed/interrupted/streaming rows, page newest/older/forward/empty and reopen SQLite | Same ordered public projection; terminal generated text is readable, partial text remains received-clamped and receipt offsets stay truthful. No read mutation occurs. |
| Give a member exact Session read but no Chat, then grant exact agent Chat; try a sibling Session or forged Principal | Metadata alone is readable; content initially denies, then succeeds only on the exact granted Session. Sibling and forged-human requests deny. |
| Read content under a limited recipient and inspect resource cards/labels/metadata | Unreadable or unowned cards, attachments, source references and memory/untyped-effect metadata are absent. Owner's independent memory permission restores its memory receipt; even Owner cannot expose invented attachment/artifact targets. |
| Hold a content read after storage, revoke read/Chat, log out, suspend Account, delete Session, admit newer work or cancel the read | Both stores deny or return stale/cancelled instead of returning the held private page. Account suspension is checked independently of tenant revision. |

The representative hosted journey passed before broader regression coverage. A missing execution namespace and an invalid nullable fixture audit actor were corrected during test compilation; neither failed build is counted as passing evidence.

## Focused checks

**172 distinct affected checks passed**, with none skipped: 126 Infrastructure and 46 API in the two final regression commands below. The initial hosted selection passed 2 cases and the expanded new Infrastructure selection passed 18; these overlap the final selections and are not counted twice.

```sh
dotnet test tests/AgentCore.Api.Tests --no-restore --filter FullyQualifiedName~Tenant_conversation_HTTP --logger 'console;verbosity=minimal'
dotnet test tests/AgentCore.Infrastructure.Tests --no-restore --filter FullyQualifiedName~Tenant_conversation --logger 'console;verbosity=minimal'
dotnet test tests/AgentCore.Api.Tests --no-restore --no-build --filter 'FullyQualifiedName~TenantSessionAccessTests|FullyQualifiedName~ApplicationMessageHistoryTests|FullyQualifiedName~ApplicationMessageWireTests' --logger 'console;verbosity=minimal'
dotnet test tests/AgentCore.Infrastructure.Tests --no-restore --no-build --filter 'FullyQualifiedName~TenantRunAdmissionTests|FullyQualifiedName~TenantSessionIntegrationTests|FullyQualifiedName~TenantSessionLifecycleTests|FullyQualifiedName~MemoryStoreContractTests' --logger 'console;verbosity=minimal'
```

The API build passed with zero warnings/errors. Changed Markdown local-link targets and balanced fences, plus `git diff --check`, passed. Fixtures are Synthetic and disposable; no hosted provider calls or installed data changes are part of these checks.

## Remaining proposal work

The Runtime still discards transport events. Authenticated tenant Hub/live text and voice, delivery acknowledgements, Chat frontend/tenant switching, original-Chat approval links/notifications, complete rich-resource read/effect integration, trusted autonomous origins and obsolete-authority/schema removal remain required. HTTP readback is not a durable delivery acknowledgement or proof of heard context. No frontend code changed, so browser verification belongs to the upcoming frontend integration. CI, deployment and Real-provider acceptance were not run. The full goal remains active.

Subsequent [authenticated live text verification](tenant-live-text-delivery-verification-2026-10-17.md) adds tenant text subscriptions and preserves a mailbox response atomically during cancellation before its first checkpoint. Earlier private-output/saved-row-only statements describe their original checkpoints; frontend, Hub commands/receipts and voice remain separate.
