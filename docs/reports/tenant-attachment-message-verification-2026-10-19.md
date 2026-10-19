# Tenant public file messages and composer — 2026-10-19

This checkpoint exposes the qualified tenant Attachment admission and processing owners through public HTTP, the incumbent SignalR/MessagePack contract and Organization Chat. It builds on [exact read Review](tenant-attachment-read-review-verification-2026-10-19.md). Attachment publication/virtual overlays and other proposal families remain open. The full IAM proposal stays active; this is not full Attachment, milestone or production acceptance.

## Behavior and authority

HTTP `messages` accepts optional `attachmentIds`; Hub `SendText` uses the existing `UserTextPayload.AttachmentIds`. Both enter `TenantRunService.SubmitMessageAsync` and its atomic entry/Activation/Run/source binding. File-only messages are supported. At most ten distinct nonempty UUIDs are accepted; rich Parts and Behavior remain closed on the tenant Hub. Null text fails validation instead of throwing. Malformed, foreign, expired, already-bound or unreadable sources cannot create a new input graph. Exact retries recover the original graph; changed payload refuses.

Only IDs explicitly submitted in the **current** human input remain untrusted selectors in native user text when policy/IAM prevents normal file projection. This is like a UUID typed by the human, not an Agent metadata query, proof of availability or permission. Canonical name/type/size/hash/body/manifest still require independent current Agent Session/Attachment IAM and policy. Historical protected IDs remain hidden. No stored text is rewritten and no automatic model tool call or consent is invented. A tool-capable model can use that selector to request the existing exact `read_attachment` Review. Missing raw Agent reads still cannot become an exceptional grant. Initial input processing remains independent of optional reread capability.

Cached Hub acknowledgement repeats current human Session/Chat and exact child Attachment reads. Selected-file refusal uses existence-safe `attachment_unavailable`, preserving otherwise-authorized Chat and draft/files. Parent/Chat revocation retains its existing retirement behavior. Denied fresh commands still consume their recorded transport sequence, so later authorized plain text works.

Organization Chat reuses Add content → Attach file, native picker/paste/drop, existing pending tiles/progress/Retry/Remove and file-only Send. Upload/abort serialize with fresh tenant and Session revision witnesses. Cookie/CSRF uploads do not automatically retry; explicit Retry retains the same upload ID and bytes after uncertain completion. The identity-only acknowledgement does not imply a metadata read: pending labels come from the human's local file. Tenant voice staging remains closed.

Delivered cards now carry tenant scope through the shared FileRef, metadata, preview policy, thumbnail, Office reader and original-byte download. Tenant source reads never use OwnerCapability fallback. A tenant change remounts the source owner and aborts previous image/download work. No CSS, token, second UI kit, file viewer, Runtime or approval engine is added. No migration is needed for this checkpoint; installed data was untouched.

## Executed local checks

All hosts use disposable roots/stores and Synthetic providers. No hosted CI or live provider was invoked.

| Check | Terminal result | Observed behavior |
| --- | --- | --- |
| Smallest public file-only Review path, both stores | 2 passed / 15 s | Public admission preserves only the submitted selector before consent; actual HTTP approval delivers the body through the successor without a grant. |
| Public HTTP, native JS wire, input scope and pending-consent recovery matrix | 18 passed / 1 m 21 s | Four public Review combinations, two malformed-input cases, two real JS MessagePack journeys, eight initial input scope cases and text/image pending-consent SQLite host recreation. Exact retry/reconnect preserves one graph; drift/foreign sources/revoked sender refuse. A later plain-text command remains usable. Revoking historical Agent reads hides old identifiers, labels and body. |
| Final admission/source/context regression | 32 passed / 51 s | Both-store binding, rollback, human/source revocation/expiry/abort, canonical history, input projection/checkpoints and missing raw Agent reads before Review. |
| Existing text wire, guarded media and exact consent/recovery regressions | 38 passed / 3 m 17 s | Ordinary tenant text delivery, current media processors and original downloads, saved-window decisions and actual SQLite consent/result recovery. |
| Shared frontend behavior after preview fix | 92 passed / 4.35 s | Identity uploads, serial witnessed attachment mutations, no implicit metadata query, selected-file denial retention, plain composer, shared Attachment card/FileRef, Office source and Conversation consumers. |
| Frontend production build | Passed | TypeScript and Vite output succeed; existing large-chunk warning remains. |
| Actual Playwright MCP composer/backend proof after preview fix | 1 repeated hosted case passed / 5 m 27 s | Multi-file upload/remove, file-only send, exact consent, actual successor, UTF-8 preview/original download, lost acknowledgement, explicit same-ID retry and canonical removal. |

These checks contain **178 distinct passing cases**: 32 Infrastructure, 54 API (the 18-case and 38-case selections overlap on two provided-file recovery cases), and 92 frontend. The smallest-path and held browser proofs repeat existing hosted cases. Documentation verification passed across 13 changed/new Markdown files, 1,881 local links/fragments and 29 complete JSON examples; `git diff --check` passed.

The first browser/backend proof also passed its backend assertions (1 repeated case / 11 m 1 s), then additional UI inspection found the preview routing issue described below. Repeated diagnostic/browser cases are not additional distinct coverage. The earlier frontend command hit Node's experimental `window.localStorage` behavior in a legacy test; the command-scoped `NODE_OPTIONS=--no-experimental-webstorage` fallback passes without an application workaround. Existing JSDOM `act`/height warnings and Vite chunk warnings are separate from actual browser errors.

## Browser journey and integration findings

Normal owner sign-in and `/t/{tenant}/c/{session}` ran against disposable Kestrel plus Vite on port 5221. Native multi-file selection produced serialized 201 uploads with successive security revisions. Remove retired one pending upload with 204. Empty-text Send bound only the remaining file; the first actual model request contained its submitted UUID but no canonical filename or body. Chat → Review details → Approve → confirmation → Refresh showed **Read authorized**. The actual successor received `Private read body → thử nghiệm`, no grant was created, and the original content remained byte-identical.

Additional inspection found that the delivered Attachment card created an unscoped FileRef: Preview failed at the owner guard before any HTTP read. The shared Attachment/FileRef source now accepts explicit tenant scope, including the existing Office and thumbnail paths. Repeating the complete journey after the fix produced scoped metadata, preview options and content responses, all 200. The actual viewer displayed the exact text; the card's Download produced the expected original filename and byte-identical UTF-8 content. The browser driver initially selected the download while the closing dialog was still animating; scoping to the conversation card after the dialog closed resolved that driver ambiguity.

For recovery, one native upload was forwarded to the real protected backend, committed with 201, and its acknowledgement replaced with an intentional 503. The UI showed a recoverable error, retained the file, disabled Send and made no automatic retry. Explicit Retry used the **same upload ID**, fresh revision 16 instead of 15, and returned 201 for the original attachment. The backend asserted unchanged immutable metadata and exactly one stored upload identity. Remove returned 204, the canonical lookup became unavailable, and content returned 403. The proof asserts zero outbound provider HTTP.

Playwright's `route.fetch()` did not retain multipart file bytes in the first diagnostic attempt. The successful injector reconstructs only the known text fixture inside the captured native multipart envelope before forwarding to the actual server. Subsequent explicit Retry uses `route.continue()` with the native File; no backend response/content is simulated except the intentional lost acknowledgement. This fallback does not claim transparent upload interception. Abandoned diagnostic empty uploads created no file and were removed from the pending UI before the successful proof. The corrected second journey has one expected console/network error, that injected 503, and no warnings or uncaught application errors.

## Responsive presentation

Two bounded visual rounds inspected 1440/1024/768/390/320 widths with long international filenames and the upload error state. Existing shared containment, ellipsis/tooltips, wrapped recovery controls and 8 px dock gap required no CSS correction. Settled error-state captures show no document or pending-list horizontal overflow; at 390/320, composer insets are 12 px and widths 366/296 px. Initial rapid desktop resize measurements preceded the sidebar transition; they are not claimed as settled desktop dimensions. The existing preview keeps its intentional local code scroll and Wrap lines control; it does not overflow the document.

Evidence: [composer 1440](assets/tenant-attachment-message-2026-10-19/composer-1440.png), [1024](assets/tenant-attachment-message-2026-10-19/composer-1024.png), [768](assets/tenant-attachment-message-2026-10-19/composer-768.png), [390](assets/tenant-attachment-message-2026-10-19/composer-390.png), [320](assets/tenant-attachment-message-2026-10-19/composer-320.png); settled upload-error [1440](assets/tenant-attachment-message-2026-10-19/ack-error-1440.png), [1024](assets/tenant-attachment-message-2026-10-19/ack-error-1024.png), [768](assets/tenant-attachment-message-2026-10-19/ack-error-768.png), [390](assets/tenant-attachment-message-2026-10-19/ack-error-390.png), [320](assets/tenant-attachment-message-2026-10-19/ack-error-320.png); [exact review](assets/tenant-attachment-message-2026-10-19/provided-file-review-320.png), [authorized](assets/tenant-attachment-message-2026-10-19/provided-file-authorized-320.png), [retry ready](assets/tenant-attachment-message-2026-10-19/retry-ready-320.png), [corrected preview](assets/tenant-attachment-message-2026-10-19/provided-file-preview-320.png).

## Reproduction

From the repository root, append `--nologo --no-restore -m:1 -nr:false -p:UseSharedCompilation=false` to each `dotnet test` command. After a matching build, `--no-build` is appropriate for repeated API execution.

```sh
dotnet test tests/AgentCore.Api.Tests/AgentCore.Api.Tests.csproj --filter 'FullyQualifiedName~Tenant_public_file|FullyQualifiedName~Tenant_attachment_MessagePack_admission|FullyQualifiedName~Tenant_attachment_context_uses_only_independent|FullyQualifiedName~Tenant_attachment_Review_checkpoint&DisplayName~provided'
dotnet test tests/AgentCore.Infrastructure.Tests/AgentCore.Infrastructure.Tests.csproj --filter 'FullyQualifiedName~Tenant_message_attachment|FullyQualifiedName~Tenant_attachment_context|FullyQualifiedName~Tenant_attachment_Review_cannot_replace_missing_raw_Agent_read'
dotnet test tests/AgentCore.Api.Tests/AgentCore.Api.Tests.csproj --filter 'FullyQualifiedName~Tenant_live_MessagePack_JavaScript|FullyQualifiedName~Tenant_attachment_context_reuses_guarded_media|FullyQualifiedName~Tenant_attachment_model_exact_Review_HTTP|FullyQualifiedName~Tenant_attachment_Review_checkpoint'
AGENTCORE_ATTACHMENT_MESSAGE_UI_PROOF=1 dotnet test tests/AgentCore.Api.Tests/AgentCore.Api.Tests.csproj --filter 'FullyQualifiedName~Tenant_public_file_only_message_preserves_submitted_selector&DisplayName~False&DisplayName~attachments.read'
```

For the opt-in held proof, use the generated disposable handoff URL for `VITE_API_PROXY_TARGET`, start `pnpm exec vite --host 127.0.0.1 --port 5221 --strictPort` in `web/`, and interact through Playwright MCP. Its private file gates release only after actual actions/assertions; no public debug endpoint is introduced. Stop only the owned hosts after releasing the fixture.

In `web/`:

```sh
NODE_OPTIONS=--no-experimental-webstorage pnpm exec vitest run src/services/identity.test.ts src/services/attachments.test.ts src/services/attachments.tenant.test.ts src/services/realtime.tenant.test.ts src/features/chat/Composer.test.tsx src/features/chat/AttachmentPreview.test.tsx src/features/chat/Conversation.test.tsx src/features/filePreview/tenantArtifactSource.test.ts src/features/filePreview/officeSource.test.ts
pnpm run build
```

## Remaining scope

This supersedes public message/composer pending statements and blanket hiding of IDs explicitly submitted in the current human input only. Earlier dated reports retain their historical evidence. Attachment publication/virtual overlays, other Browser/resource adapters, autonomous/background/events/transports, final OwnerCapability/unscoped/old approval removal, canonical schema consolidation and the proposal-wide final audit/design-system synchronization remain open. The preexisting Impeccable sidecar freshness finding is not closed by reusing existing presentation here. CI, live providers, installed-data migration/reset, tenant voice-file staging and physical process-kill recovery were not run or claimed.
