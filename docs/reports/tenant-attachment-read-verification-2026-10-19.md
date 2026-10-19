# Tenant Attachment model read qualification — 2026-10-19

This checkpoint converts conversational `attachments.read` under current Allowed policy through the existing scoped Attachment context/source owner, Runtime and tool loop. It builds on [initial input qualification](tenant-attachment-context-verification-2026-10-19.md). Public file messages, durable exact read Review, composer and Attachment publication remain unfinished. The full multi-tenant IAM proposal stays active; no full Attachment, milestone or production acceptance is claimed.

## Behavior and ownership

Explicit reads require actual claimed Run/Session ownership, bound same-Session sources, admitted/current Definition tool authorization, independent executor Session/child Attachment IAM and current Allowed policy. These are the Agent's own permissions. Initial provided-file processing remains a distinct input modality; no reread tool grant is added to shipped immutable definitions. General Assistant v32's omitted explicit read capability continues to deny rereads. `attachment_read_review_required` refuses before byte I/O and creates no implicit consent or grant.

Closed bounded arguments preserve DOCX offset/limit and XLSX sparse/sheet/range/cursor behavior. Strict UTF-8, PDF, guarded Office and sanitized images reuse incumbent processors. Typed results retain image parts; non-vision/unsupported files remain inert, with original downloads independently authorized. No workspace copy, path or publication/recipient authority is granted. Fixtures establish bounded observations, not exhaustive Office/PDF or visual understanding.

Private read-result provenance records canonical arguments and exact source/entry metadata/hash/version in existing checkpoints. It is factual data, not authorization. Current source/capability/permission/policy checks guard return, checkpoint writing and retained reuse. Existing checkpoint serialization omits raw image bytes; recovery rebuilds sanitized image parts from currently verified originals. Source-free input checkpoints keep their prior shape. No schema/table, parser/cache, alternate Runtime or receipt engine is added.

## Integration findings

The first hosted matrix exposed two missing integrations: the tenant tool offering still excluded Attachment reads, and each tool-loop capability reprojection rebuilt an empty legacy manifest. The explicit tenant offering and freshly scoped metadata-only reprojection now preserve the read tool without exposing unconverted tools or reextracting initial content. The same hosted cases are rerun after these corrections. Malformed provenance and missing typed bindings refuse retained reads. Final review also found the new typed interceptor could bypass the common output fitter for boundary errors; it now applies that fitter and retains image parts only when complete metadata fits.

## Executed checks

All application execution uses disposable stores/roots and Synthetic providers. Hosted cases assert zero outbound provider HTTP.

| Focused check | Terminal result | Action and observed outcome |
| --- | --- | --- |
| Infrastructure direct read boundaries plus scoped input regressions | 29 passed, 0 failed, 0 skipped / 38 s | Actual bound text reads and independent raw parent/child grants; missing admitted read capability and required Review read zero source bytes. Revocation during read withholds body. Stored results require exact metadata/native bytes/current access and a typed binding; forged witnesses or missing binding deny reuse. |
| Hosted historical media tool loop, both stores | 16 passed, 0 failed, 0 skipped / 1 m 15 s | Upload/internal bind and complete the first turn, then send a public plain-text follow-up. Its first model request has historical metadata without body reextraction; the read tool supplies guarded text/PDF/Office/image observations. DOCX continuation and sparse/ranged XLSX, non-vision and inert binary behavior remain truthful. Original HTTP downloads are byte-identical; raw image base64 stays out of checkpoints. |
| Actual SQLite host recreation for retained text/image results | 8 passed, 0 failed, 0 skipped / 47 s | Hold the provider after a completed read, dispose/recreate the actual host and expire its claim under a supported 600-second budget. Initial-input witnesses are metadata-only, isolating the explicit read guard. Unchanged sources complete with one new provider request, retaining the completed tool result and freshly rebuilt image parts. Revoke the actual grant, change native bytes or narrow current selected capabilities: `authority-unavailable`, zero new model requests. Capability cases author/publish an owned Definition, then use the actual settings API after restart; immutable templates remain unchanged. |
| Typed executor error output budget, both stores | 2 passed, 0 failed, 0 skipped / 5 s | An unavailable source returns valid JSON within the remaining ten UTF-8 bytes and no image parts. Normal typed media delivery is rechecked after applying the common fitter. |
| Application closed arguments and shared runtime/prompt/checkpoint regressions | 75 passed, 0 failed, 0 skipped / 5 s | Reject host paths, scope injection, duplicate fields and invalid windows. Existing processing, queued input, recall, prompt and source-free checkpoint/continuation cases remain passing. |
| Existing hosted Attachment and plain-text journeys | 34 passed, 0 failed, 0 skipped / 1 m 58 s | Reexercise initial file IAM/policy/media input, its retained-source recovery, actual uploads/downloads/Office preview and plain-text history after scoped reprojection changes. |

The checks cover **164 distinct passing cases** including the terminal media recheck after the shared fitter correction; repeated diagnostics/media runs are not added to the count. The original Infrastructure filter contained 29 cases before the two budget cases were added; rerunning that combined filter now selects 31.

Exact executed commands (common flags shown explicitly):

```sh
dotnet test tests/AgentCore.Infrastructure.Tests/AgentCore.Infrastructure.Tests.csproj --filter 'FullyQualifiedName~Tenant_attachment_model_read_|FullyQualifiedName~Tenant_attachment_context_' --nologo --no-restore -m:1 -nr:false -p:UseSharedCompilation=false
dotnet test tests/AgentCore.Infrastructure.Tests/AgentCore.Infrastructure.Tests.csproj --filter 'FullyQualifiedName~Tenant_attachment_model_read_typed_tool_boundary' --nologo --no-restore -m:1 -nr:false -p:UseSharedCompilation=false
dotnet test tests/AgentCore.Api.Tests/AgentCore.Api.Tests.csproj --filter 'FullyQualifiedName~Tenant_attachment_model_reads_historical_source' --nologo --no-restore -m:1 -nr:false -p:UseSharedCompilation=false
dotnet test tests/AgentCore.Api.Tests/AgentCore.Api.Tests.csproj --filter 'FullyQualifiedName~Tenant_attachment_model_read_checkpoint_rechecks_bytes' --nologo --no-restore -m:1 -nr:false -p:UseSharedCompilation=false
dotnet test tests/AgentCore.Application.Tests/AgentCore.Application.Tests.csproj --filter 'FullyQualifiedName~TenantAttachmentReadContractTests|FullyQualifiedName~AttachmentProcessingRuntimeTests|FullyQualifiedName~QueuedAttachmentRuntimeTests|FullyQualifiedName~SessionAttachmentRecallTests|FullyQualifiedName~PromptContextBuilderTests|FullyQualifiedName~AgentRunCheckpointFormatTests|FullyQualifiedName~AgentRunToolCallCheckpointTests|FullyQualifiedName~ModelContinuationCheckpointTests' --nologo --no-restore -m:1 -nr:false -p:UseSharedCompilation=false
dotnet test tests/AgentCore.Api.Tests/AgentCore.Api.Tests.csproj --filter 'FullyQualifiedName~Tenant_attachment_context_|FullyQualifiedName~Tenant_attachment_HTTP_|FullyQualifiedName~Tenant_conversation_HTTP_reads_actual_worker_result' --nologo --no-restore -m:1 -nr:false -p:UseSharedCompilation=false
```

These recovery cases simulate controlled host disposal/recreation, not physical process kills. Earlier setup failures and repeated diagnostics are excluded from distinct passing counts. A recovery test's analyzer error (`Assert.Single` filtering) was corrected before execution; no production behavior changed for that repair.

Static verification passed for 12 changed/new Markdown files: 1,750 local links/fragments, balanced fences and 29 complete JSON examples. `git diff --check` passed.

## Remaining boundaries

Exact durable read consent and refusal suppression require conversion before public file message/composer ingress opens. Attachment publication/virtual overlays, other Browser/resource adapters, autonomous/background/events/transport families and final obsolete-authority/schema removal remain proposal obligations. No frontend or visual design changed in this checkpoint. CI, live providers, installed-data migration/reset and physical process-kill recovery were not requested or run. Controlled host recreation is not a physical crash claim.
