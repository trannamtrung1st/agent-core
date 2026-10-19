# Tenant Attachment namespace verification — 2026-10-19

This checkpoint implements conversational `/attachments` list/search on base commit `b92e07e0`, through the existing Workspace read/Review and checkpoint owners. It supersedes namespace-pending statements in earlier Attachment checkpoints only. The implementation is the commit containing this report. The original full tenant IAM goal remains active; neither proposal acceptance nor runtime reliability is established by this bounded qualification.

## Implemented behavior

Tenant models now use `workspace.list` and `workspace.search` on the exact `/attachments` root. Canonical UUID paths identify originals; display names remain labels for name/glob matching. Current raw Session and child Attachment reads, admitted/current original tool and Attachment reread capabilities are independently required. Storage filters permitted IDs and bound state before pages or scan bounds; hidden, pending and different-Session originals contribute no result/count/cursor information. Home/working filesystem access and Agent metadata reads are unnecessary.

List uses existing created-time/UUID paging and bounded complete-result fitting. Search scans at most the existing bounded file count, matches labels and supported UTF-8 originals within the content size limit, and leaves binary/Office/media extraction to their existing readers. It checks original byte size/hash and repeats current ownership, authority and collection fences after I/O. Bounded negative/glob-skipped candidates and lookahead participate in source fingerprints.

Exact policy Review uses `inspect_workspace` and `PreparedWorkspaceRead.AttachmentMembersJson`: an exclusive canonical bounded set of AttachmentId/EntryId/SHA-256 witnesses. The primary Session scope and independent child/reviewer reads remain separate. Consent stores arguments and source digests, without file bodies/snippets; it supplies no IAM, file changes, recipient download or later-read authority. Existing local receipt, successor, root suppression and retained-checkpoint owners remain in use. Both stores recheck saved bound member ownership during the protected transaction and filter current reviewer scope before inbox totals/pages. Single-file Workspace/Office aliases continue to use canonical single-Attachment consent.

Migration `20261019151350_TenantAttachmentNamespaceReview` extends only the canonical SQLite consent shape. Other variants omit the new nullable payload. No new approval/effect owner, scheduler, cache, HTTP/Hub route or compatibility authority is introduced.

## Executed evidence

| Check | Result | Expected versus observed |
| --- | --- | --- |
| First runnable Synthetic model/list case | 1 passed / 4 s | An actual owned bound original is returned by the model tool loop; its inert pending sibling remains absent. |
| Initial namespace/recreation matrix | 24 passed, 2 failed / 2 m 34 s | Both-store direct reads, boundary checks and recreation ran. Two search-Review assertions incorrectly treated the deliberately saved search query as leaked file content; the assertion/query fixture was corrected. These failures are not counted as passes. |
| Extended 28-case matrix | 24 passed, 4 failed / 4 m 5 s | Exact Review, scope-filtered inventory, parent/child/capability/policy fences and recreation were exercised. Two newly added paging cases omitted their explicit capabilities; the fixture now admits exactly list/search plus Attachment reread. An initial InMemory binding and SQLite pre-approval setup separately stalled before namespace dispatch/consent; their cause remains unproven. |
| Corrected paging, scoped boundaries, SQLite schema | 9 passed / 50 s | Both-store three-page continuation, name/content/glob/binary behavior, tiny byte budgets and retained-result revocation pass with explicit capabilities. InMemory independent-scope boundary cases pass with captured diagnostics. SQLite rejects ten malformed/mixed consent shapes and rejects narrowing while a new consent row remains, preserving the exact original request and current model. |
| Final namespace SQLite recovery selection | 6 passed / 42 s | Actual application hosts are disposed/recreated with pending consent or a saved observation. List/search provide retained exact data to one fresh continuation model request without repeating the original model/tool call; revoked current authority or changed text-source bytes prevent delivery. No read permission grant exists. |
| Shared closed read contracts | 57 passed / 179 ms | Reject namespace aliases, traversal, file-as-directory cursors, duplicate/invalid member witnesses and mixed resource authority. Existing virtual-file and Workspace projection/authorization contracts retain expected behavior. |
| Shared read/consent stores | 41 passed / 1 m 24 s | Resource namespace, Workspace and Attachment read boundaries retain expected both-store consent, source, scope and rollback behavior. |
| Existing HTTP/model paths | 48 passed / 3 m 58 s | Exact virtual text/Office readers, Workspace Review and resource namespace direct/Review model-to-HTTP workflows retain their expected data/denial behavior. |
| EF model comparison | Passed | No pending model change after the generated migration. The installed tools 10.0.5/runtime 10.0.12 notice did not prevent generation/comparison. |
| Whitespace/local Markdown targets | Passed; 1347 local Markdown targets checked, zero missing | Check current changed sources/docs and real local file links. |

The first test build exposed a helper signature edit and was corrected before execution; the first EF generation command selected the API startup project, which lacks the design package, and was corrected to the repository's Infrastructure design-time factory. No database update/reset command was issued against installed data. Disposable fixtures apply their own canonical migrations and explicit Synthetic bootstrap.

The failed combined run is retained at `/private/tmp/agent-core-attachment-namespace-final.log`. Corrected selected checks do not erase its two intermittent pre-namespace setup stalls or establish their root cause. Diagnostics now accompany namespace terminal/pre-approval waits. The [earlier preparation-failure correction](tenant-preparation-failure-verification-2026-10-19.md) remains a deterministic lifecycle correction, not proof that every earlier stall shares that cause.

## Reproduction

```sh
dotnet test tests/AgentCore.Api.Tests --no-restore --nologo -m:1 /p:UseSharedCompilation=false --filter 'FullyQualifiedName~Tenant_attachment_namespace_|FullyQualifiedName~Tenant_attachment_Review_checkpoint&DisplayName~namespace-' --logger 'console;verbosity=minimal'
dotnet test tests/AgentCore.Application.Tests --no-restore --nologo -m:1 /p:UseSharedCompilation=false --filter 'FullyQualifiedName~TenantAttachmentNamespaceContractTests|FullyQualifiedName~TenantAttachmentReadContractTests|FullyQualifiedName~TenantWorkspaceReadContractTests|FullyQualifiedName~TenantWorkspaceReadAuthorizationContractTests' --logger 'console;verbosity=minimal'
dotnet test tests/AgentCore.Infrastructure.Tests --no-restore --nologo -m:1 /p:UseSharedCompilation=false --filter 'FullyQualifiedName~Tenant_resource_namespace_|FullyQualifiedName~Tenant_workspace_read_|FullyQualifiedName~Tenant_workspace_runtime_|FullyQualifiedName~Tenant_attachment_read_' --logger 'console;verbosity=minimal'
dotnet test tests/AgentCore.Api.Tests --no-restore --no-build --nologo -m:1 --filter 'FullyQualifiedName~Tenant_attachment_virtual_file_reader|FullyQualifiedName~Workspace_read_Review_model_HTTP|FullyQualifiedName~Tenant_resource_namespace_model_HTTP' --logger 'console;verbosity=minimal'
dotnet ef migrations has-pending-model-changes --project src/AgentCore.Infrastructure --startup-project src/AgentCore.Infrastructure --no-build
git diff --check
```

## Remaining goal and limits

All exercised users, hosts, stores and files were disposable Synthetic fixtures. No Real provider, native Browser/SDK effect, physical process-kill, multi-instance deployment or GitHub CI was exercised. No frontend composition or visual token changed; reuse of the existing exact read inspector is backend contract evidence, not a fresh browser/Impeccable acceptance claim.

Runtime stall attribution remains open. Human Attachment materialization, remaining Browser/resource lifecycle adapters, autonomous/background/event/transport families, obsolete-authority removal, clean canonical schema/reset/bootstrap consolidation and final requirement-by-requirement isolation/UX/design-system qualification remain open. The full proposal goal remains active.
