# Exact tenant Attachment virtual file read verification — 2026-10-19

This checkpoint implements exact bound Attachment file reads through `workspace.read`, `office.document.inspect` and `office.spreadsheet.inspect`. It builds on [canonical Attachment reads](tenant-attachment-read-verification-2026-10-19.md), [exact read consent](tenant-attachment-read-review-verification-2026-10-19.md) and [bound-original publication](tenant-attachment-artifact-verification-2026-10-19.md). Namespace browsing/search, human materialization and the remaining IAM proposal are still open. No full Attachment, milestone, production or proposal acceptance is claimed.

The implementation is the commit containing this report, based on `ece80d58`. Only disposable Synthetic stores, files and hosts were used.

## Implemented boundary

An exact `/attachments/{canonical-lowercase-UUID}` path selects the actual bound same-Session original through the incumbent Attachment source owner. It never selects a physical workspace or grants Workspace/HomeAccess authority. Both the requested reader and `attachments.read` must be admitted and currently enabled, with current independent executor `sessions.read` and exact-child `attachments.read`. Missing bindings, pending uploads, foreign/different-Session originals and narrowed authority refuse.

Workspace reads require actual readable text. Office readers require the corresponding actual MIME type and retain their closed DOCX offset/limit or XLSX sheet/range/cursor window. The canonical guarded text/Office processor, immutable bytes/checksum, live claim, source and post-I/O fences remain the owners. Image/PDF processing stays with the existing explicit Attachment reader.

Exact `inspect_workspace` consent for this source normalizes to the existing `AttachmentRead` operation and `read_attachment` inspector. The saved source/window, qualified reviewer, decision, receipt, successor and root refusal history remain canonical. A different source, media reader or read window cannot match the consent. Rejection suppresses both canonical and virtual retry in the same root, without another request, grant or receipt. Approval returns the existing explicit canonical Attachment observation; it creates no read grant or recipient download access.

Direct virtual results append the original canonical tool/path/window to the existing private Attachment result provenance. Retained delivery checks the original reader capability as well as current canonical Attachment capability, source bytes, IAM/policy and claim. Missing context binding refuses retained virtual observations. No new entity, migration, parser, file store, public endpoint or effect ledger is introduced.

## Local verification

| Check | Observed result | Exercised scenario and expected outcome |
| --- | --- | --- |
| Grouped new API and recovery cases | 37 passed / 3 m 7 s | Both stores: upload/bind → actual model Workspace/Office read and exact saved window; actual HTTP normalized consent → successor result. Independent parent/child denial, pending originals, different Session, required Review, disabled reread capability and MIME mismatch return body-free refusal. Actual SQLite host recreation preserves virtual text results/pending and completed consent, while raw read loss, corrupted source bytes and original-reader/Attachment capability loss prevent provider delivery. |
| Post-refactor virtual module and isolated canonical DOCX checks | 29 passed, 3 failed / 3 m 21 s | Adds both-store cross-tool rejected-root checks. Reexercise final source/media matching, text, DOCX bounded paragraph, XLSX sparse/range, direct refusal and unchanged original download bytes. The failed canonical DOCX case captured a pre-model `AgentCoreException`; a virtual XLSX Run and a child-denial fixture also timed out. See the findings below. |
| Application exact contracts/projection | 47 passed / <1 s | Canonical/noncanonical paths, traversal, aliases, zero IDs, discovery/mutation substitution and caller scope injection refuse. Exact consent cannot substitute a workspace source, different Attachment, incompatible reader/media or read window. Existing Workspace/Office projection and authorization schemas remain valid. |
| Canonical both-store authority/Review regressions | 22 passed / 32 s | Existing explicit Attachment source reads, independent raw authority, required-Review no-byte boundary, post-I/O revocation, retained source/provenance and exact Review/receipt continuation remain working. |
| Canonical historical media and HTTP Review regressions | 29 passed, 1 failed / 2 m 36 s | Existing text/PDF/image/Office/inert reads and approve/reject/cancel flows, plus both-store canonical/virtual refusal correlation. The one failed case is recorded below rather than counted as a pass. |
| Narrowed failure diagnosis | 10 passed / 53 s; isolated canonical DOCX passed, then 5/5 bounded no-build repetitions passed | Reexercise both-store XLSX sparse/range, child-read refusal and canonical DOCX without changing product retry or test deadlines. These passes do not establish the cause or erase the earlier failures. |
| Sequential module pass | 30 passed, 1 failed / 2 m 53 s | Reexercise the 30 new virtual cases and the exact InMemory canonical DOCX baseline in one process with diagnostic capture. |
| Budget and policy-order corrections | 8 passed / 44 s; final staged-source 4/4 passed / 22 s | A 4 KiB result retains a nonempty truncated UTF-8 prefix plus virtual provenance; zero/tiny budgets remain bounded. Required Review on an incompatible Office reader returns the generic Review refusal before media/source probing. Existing text direct and normalized-consent paths remain the regression consumers. |
| Whitespace and new Markdown link targets | Passed; 1,481 relative Markdown file targets checked, zero missing | Check the changed source/docs and relative links to this report and its canonical owners. |

The first test attempt could not create .NET IPC sockets in the filesystem sandbox. Focused execution used the permitted escalation; no CI, hosted provider or network service was invoked. Initial test setup incorrectly forced the context-only Attachment capability into the always-offered set, and its DOCX expectation omitted the generated title paragraph. Those fixture assumptions were corrected before the passing module matrix. A missing import during the shared media-predicate extraction was corrected before the final run; compile failures are not passing test evidence.

Two grouped runs observed an InMemory canonical DOCX failure with `provider-unknown`, zero model requests and a captured `AgentCoreException`. The post-refactor group also observed a virtual XLSX Run exceeding the existing 25-second terminal wait, and a child-denial fixture timing out while binding its ordinary original before the new adapter ran. The narrowed ten-case group passed, followed by one isolated canonical DOCX pass and five bounded no-build passes of that exact case. The cause remains unproven; passing reruns do not establish a fix.

Test diagnostics now retain the actual error exceptions and report a timed-out Run's current status, revision, attempt, claim, checkpoint/effect state and failure code. The sequential pass captured one ordinary binding timeout as a Running Run at revision 2/attempt 1 with no checkpoint, effect or failure; the cause remains open. Coordinator warnings are now captured by the opt-in test logger too. The existing wait duration is unchanged. No automatic model/effect replay or broad reliability workaround was introduced. This is an open execution-reliability qualification gap, distinct from claiming that the new adapter caused it. The older [policy-read reliability follow-up](tenant-policy-read-recovery-verification-2026-10-18.md) is not closed or assigned the same cause by this checkpoint.

Batch review also found two adapter issues and corrected them before their focused checks: reserve the actual serialized virtual-provenance overhead before the canonical reader clips text/complete Office elements, and check direct operation policy before media compatibility can disclose anything about an unreviewed source. Direct virtual authorization failures use the existing body-free audit owner. These changes do not retry model calls, weaken source fences or address the independent claimed-Run stall.

## Reproduction

From the repository root, using local .NET IPC permissions:

```sh
dotnet test tests/AgentCore.Api.Tests --no-restore --nologo -m:1 /p:UseSharedCompilation=false --filter 'FullyQualifiedName~Tenant_attachment_virtual_file_reader|DisplayName~virtual-text' --logger 'console;verbosity=minimal'
dotnet test tests/AgentCore.Api.Tests --no-restore --nologo -m:1 /p:UseSharedCompilation=false --filter 'FullyQualifiedName~Tenant_attachment_virtual_file_reader|FullyQualifiedName~Tenant_attachment_model_reads_historical_source&DisplayName~docx' --logger 'console;verbosity=minimal'
dotnet test tests/AgentCore.Application.Tests --no-restore --nologo -m:1 /p:UseSharedCompilation=false --filter 'FullyQualifiedName~TenantAttachmentReadContractTests|FullyQualifiedName~TenantWorkspaceReadContractTests|FullyQualifiedName~TenantWorkspaceReadAuthorizationContractTests' --logger 'console;verbosity=minimal'
dotnet test tests/AgentCore.Infrastructure.Tests --no-restore --nologo -m:1 /p:UseSharedCompilation=false --filter 'FullyQualifiedName~Tenant_attachment_model_read|FullyQualifiedName~Tenant_attachment_read_Review' --logger 'console;verbosity=minimal'
dotnet test tests/AgentCore.Api.Tests --no-restore --nologo -m:1 /p:UseSharedCompilation=false --filter 'FullyQualifiedName~Tenant_attachment_model_reads_historical|FullyQualifiedName~Tenant_attachment_model_exact_Review_HTTP|DisplayName~rejected: True' --logger 'console;verbosity=minimal'
dotnet test tests/AgentCore.Api.Tests --no-restore --no-build --nologo -m:1 --filter 'FullyQualifiedName~Tenant_attachment_virtual_file_reader|FullyQualifiedName~Tenant_attachment_model_reads_historical_source&DisplayName~sqlite: False&DisplayName~docx' --logger 'console;verbosity=minimal'
dotnet test tests/AgentCore.Api.Tests --no-restore --nologo -m:1 /p:UseSharedCompilation=false --filter 'FullyQualifiedName~Tenant_attachment_virtual_file_reader&(DisplayName~large-text|DisplayName~media-review)' --logger 'console;verbosity=minimal'
git diff --check
```

## Presentation and remaining proposal work

The existing shared Attachment consent inspector and independently authorized original preview/download contracts are reused. No React, CSS, visual token, Ant Design composition or design sidecar changed. This checkpoint has backend HTTP/model evidence, not a new browser or Impeccable qualification. Final proposal-wide responsive/design-system acceptance remains open.

Attachment namespace browsing/search and legacy human materialization remain unconverted. Other virtual/resource/Browser adapters, Browser lifecycle/autonomous origins, voice/rich input arbitration, background/event/transport integration, platform/system guardrails, general audit/reconciliation/notifications, final isolation qualification and obsolete-authority/schema removal remain proposal obligations. No installed-data reset, Real host restart, physical process-kill recovery, deployment or CI was run or claimed. The full goal stays active.
