# Tenant Attachment runtime input context qualification — 2026-10-19

This checkpoint converts scoped internal conversational file input and historical bound-file metadata through the existing Runtime, Attachment, parser and checkpoint owners. It replaces the [internal-binding checkpoint's](tenant-attachment-binding-verification-2026-10-19.md) blanket dispatch refusal. Public HTTP/Hub Attachment IDs, model `attachments.read` and exact read Review, composer and Attachment publication remain unfinished. The full multi-tenant IAM proposal stays active; no complete Attachment, milestone or production acceptance is claimed.

## Behavior

Actual claimed Run/Session/Agent ownership and current independent executor Session/Attachment reads plus current policy guard prepared input. Raw-denied or policy-required Review reveals no file names, IDs or bytes to the model; only a generic unavailable-file notice remains. Entry-linked source identity prevents arbitrary or foreign-Session refs. Filtering precedes the 50-source bound, canonical metadata replaces supplied labels, and projected history never rewrites durable bindings.

Initial file parsing is a conversation input modality, distinct from the optional reread tool capability. The shipped General Assistant v32 has no explicit selected `attachments.read` tool capability yet can process initial input under its own current IAM and policy. No tool grant is silently added. Text/Office/PDF/image processing reuses the incumbent guarded parser through a frozen-source facet; no unscoped read, alternate parser/cache or workspace copy occurs. A non-vision model receives no image bytes, and unsupported binary files remain inert. Original downloads stay separately authorized and unchanged. Fixtures establish bounded processing/delivery, not complete Office/PDF analysis or visual understanding.

Private source witnesses record prepared source/entry identity, canonical metadata/hash and processing version. They are provenance dependencies, not role snapshots or allow decisions. The service verifies bytes after processing; the worker rechecks before provider dispatch and checkpoint save; current Run authority checks retained sources before reuse. Optional null checkpoint fields remain omitted, with no new table or migration. Initial input/image parts are rebuilt from current verified originals rather than persisted as authoritative context. Factual private provenance may remain after revocation but grants no reuse authority.

## Findings corrected

- The explicit runtime factory composition initially omitted its new typed dependency. The same hosted scenarios were rerun after wiring it.
- An initial capability check incorrectly treated automatic input processing as use of the reread tool. Existing input behavior and shipped definitions showed the distinction; input processing now requires current IAM/policy without granting a tool.
- Post-processing native source verification was added before returning prepared context. Malformed/null/duplicate or oversized provenance refuses safely.
- Recovery tests use explicit dispatch under a controlled clock and a supported 600-second instance budget that survives the five-minute claim lease. The original default budget expired during simulated downtime, correctly preventing recovery generation; this was a test setup issue. An initial empty checkpoint is factual preparation evidence; revocation must prevent tool/follow-up execution rather than erase it.

## Executed checks

All commands use `--nologo --no-restore -m:1 -nr:false -p:UseSharedCompilation=false`, disposable stores/roots and Synthetic providers.

| Project / focused filter | Terminal result | Evidence |
| --- | --- | --- |
| Infrastructure: Attachment storage, binding, context and processor checks | 65 passed, 0 failed, 0 skipped / 49 s | Both-store Attachment storage/binding/context, exact native bytes, current IAM/policy, malformed provenance, rollback/cleanup and incumbent guarded parsers. |
| API: Tenant Attachment and plain-text conversation checks | 34 passed, 0 failed, 0 skipped / 1 m 57 s | Actual hosted input, historical metadata, media/download, Office preview, permission loss and SQLite host recreation; plain-text history regression. |
| Application: Shared runtime, prompt and checkpoint checks | 64 passed, 0 failed, 0 skipped / 5 s | Incumbent processing/queued input/recall, prompt assembly, continuation and source-free checkpoint compatibility. |

These final commands cover **163 distinct passing cases**. Earlier diagnostic/repeated runs are not added to that count.

Exact commands:

```sh
dotnet test tests/AgentCore.Infrastructure.Tests/AgentCore.Infrastructure.Tests.csproj --filter 'FullyQualifiedName~Tenant_attachment_|FullyQualifiedName~Tenant_message_attachment_|FullyQualifiedName~AttachmentProcessorTests' --nologo --no-restore -m:1 -nr:false -p:UseSharedCompilation=false
dotnet test tests/AgentCore.Api.Tests/AgentCore.Api.Tests.csproj --filter 'FullyQualifiedName~Tenant_attachment_|FullyQualifiedName~Tenant_conversation_HTTP_reads_actual_worker_result' --nologo --no-restore -m:1 -nr:false -p:UseSharedCompilation=false
dotnet test tests/AgentCore.Application.Tests/AgentCore.Application.Tests.csproj --filter 'FullyQualifiedName~AttachmentProcessingRuntimeTests|FullyQualifiedName~QueuedAttachmentRuntimeTests|FullyQualifiedName~SessionAttachmentRecallTests|FullyQualifiedName~PromptContextBuilderTests|FullyQualifiedName~AgentRunCheckpointFormatTests|FullyQualifiedName~AgentRunToolCallCheckpointTests|FullyQualifiedName~ModelContinuationCheckpointTests' --nologo --no-restore -m:1 -nr:false -p:UseSharedCompilation=false
```

The hosted media matrix uploads actual DOCX/XLSX/PDF/PNG/binary sources, binds through internal admission, waits for worker completion, inspects prepared model input and downloads the exact unchanged original over HTTP. Allowed input is processed; missing independent reads or required Review excludes source labels/IDs/body. Later plain-text turns retain authorized metadata without automatically reextracting the prior body. No workspace copy or provider HTTP occurs.

The recovery matrix revokes actual read authority after the first model request: no tool execution or follow-up model request occurs. A separate actual SQLite host is recreated while the old model follow-up was held after a completed readonly IAM tool. Private source provenance survives; unchanged sources rebuild initial input and complete with one new model call. Revoked access or changed native bytes ends with `authority-unavailable` and zero model calls on the new host. Existing completed tool evidence is retained rather than replayed. These are controlled host disposal/recreation checks, not physical process kills.

Static documentation checks passed: 12 changed/new Markdown files, 1,739 local links/fragments, balanced fences and 29 complete JSON examples; `git diff --check` passed.

## Remaining boundaries

No public attachment message/composer UI or new visual design was changed or browser-qualified. Model reread and durable exact read Review still require conversion before public file messages open. Source publication/virtual overlays, other Browser/resource adapters, autonomous/background/events/transport families and final obsolete-authority/schema removal remain proposal obligations. CI, live providers, installed-data migration/reset and physical process-kill recovery were not requested or run. Controlled host recreation evidence is not a physical crash claim.
