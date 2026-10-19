# Tenant Attachment exact read Review qualification — 2026-10-19

This checkpoint converts explicit conversational `read_attachment` consent through the existing authorization request, human decision, local consent receipt, successor Run and checkpoint owners. It builds on [Allowed-policy reads](tenant-attachment-read-verification-2026-10-19.md). Public provided-file handoff/HTTP/Hub Attachment IDs, composer and Attachment publication remain unfinished. The full multi-tenant IAM proposal stays active; no full Attachment, milestone or production acceptance is claimed.

## Behavior and ownership

Preparation derives actual bound Session/Attachment/entry identity, canonical stored metadata/checksum, processor version, model vision mode and exact read arguments. Preparation and human inspection do not read content bytes. Agent raw Session and child Attachment reads and admitted/current tool capability remain independent prerequisites. Either parent or child policy can require Review; missing raw reads cannot become an exceptional grant.

Human inbox filtering applies actual source and independent parent/child reads before paging/counts. Decisions repeat current reviewer qualification, underlying approval scope, independent-review policy and exact action/security/request revisions. Stale requests retain eligible cancellation. Exact source/window hashes prevent substitution; root semantic identity prevents refusal bypass through changed metadata or relaxed policy.

Consent creates no OperationGrant. `readAuthorized` records consent completion and `applied` remains false. The nullable private `attachmentObservation` reports actual data availability separately. Guarded text, sanitized images and bounded Office pages/ranges use incumbent readers and limits. Delivery and retained reuse repeat current source, decision, receipt, reviewer, Agent, claim and capability fences. Raw image bytes stay out of checkpoints and are rebuilt from verified originals. No parser, Runtime, file store or receipt engine is duplicated.

Migration `20261019114545_TenantAttachmentReadReview` extends the existing approval constraint; no new table is added. Source-free payloads omit the nullable new operation. Installed databases were not migrated/reset.

## Executed checks

All runtime execution uses disposable stores/roots and Synthetic providers. Hosted journeys assert zero outbound provider HTTP.

| Focused check | Terminal result | Actions and observed outcome |
| --- | --- | --- |
| Consent boundaries plus directly affected input/read fences, both stores | 48 passed, 0 failed, 0 skipped / 1 m 4 s | Four exact body continuations verify zero byte reads before decision, idempotency and no grant. Missing Agent parent/child reads cannot prepare consent. Scoped human reads filter counts before paging; independent requester and insufficient approval scope cannot decide. Revocation disables decision but preserves cancellation. Rejected/cancelled roots still refuse direct/repeated reads after policy relaxation. SQLite rejects mismatched source, mixed payload and exceptional-grant shapes and rolls back unsafe downgrade. Existing processing/read/result-budget/source fences remain passing. |
| Hosted exact consent/media/decision matrix, both stores | 12 passed, 0 failed, 0 skipped / 1 m 11 s | Upload/internal bind and complete the first turn, then send public plain text naming the known Attachment UUID. Policy hides historical file metadata/body before consent. Actual HTTP approve returns text, sanitized image, saved DOCX page or XLSX range through the successor; reject/cancel return no file observation. No grant; original downloads remain byte-identical. |
| Actual SQLite host recreation | 10 passed, 0 failed, 0 skipped / 1 m 6 s | Recreate actual hosts with pending consent or a saved approved text/image tool result. Fresh authenticated approval after recreation completes once. Retained text resumes with one new model request and images rebuild sanitized parts. Revoked Agent reads, corrupt native bytes and narrowed current capability fail before a new model request. No grant and one consent audit remain. |
| Application contracts and shared runtime/prompt/checkpoint consumers | 86 passed, 0 failed, 0 skipped / 5 s | Closed Attachment consent arguments reject scope injection and duplicate operation fields. Changed source metadata changes exact action hash while retaining refusal identity; a changed window cannot match consent. Explicit selected capability fixtures preserve credential/schema isolation. Existing attachment processing, prompt, recall and source-free checkpoint/continuation behavior remains passing. |
| Existing Allowed-policy historical media and retained-result recovery | 24 passed, 0 failed, 0 skipped / 1 m 58 s | Reexercise guarded text/PDF/Office/image/inert historical reads in both stores and actual SQLite recovery after consent integration. Existing direct read authority, current native source checks and fresh image rehydration remain passing. |
| Shared approval frontend | 47 passed, 0 failed / 47.66 s | Existing approval consumers plus exact Attachment metadata/page, escaped script-like filename, zero bytes and separate read-authorized versus application status. |
| Production frontend build | Passed | TypeScript and Vite production output succeed. Existing large-bundle warning remains. |

These checks cover **227 distinct passing cases** (48 Infrastructure, 46 API, 86 Application and 47 frontend). Static verification passed for 13 changed/new Markdown files, 1,868 local links/fragments and 29 complete JSON examples; `git diff --check` passed.

The opt-in browser fixture also passed (one repeated hosted case / 3 m 54 s), after real Playwright MCP interaction. Its time includes human-style inspection and confirmation, rather than provider execution time. Repeated diagnostic/browser cases are excluded from distinct passing counts.

## Synthetic browser evidence

A disposable Kestrel host and Vite server on port 5221 used normal authenticated UI routes; no endpoint was mocked or added. Sign in, inspect Organization approval details, follow **Open original conversation**, open the same shared detail from Chat, approve and confirm. Refresh shows **Read authorized**, no Applied label; the backend fixture confirms the exact body reached the actual successor without a grant. A second request loses its actual Agent read assignment: Refresh shows **Decision unavailable**, disables Approve/Reject and keeps cancellation enabled. Confirm cancellation; HTTP returns 200 and the fixture observes durable Cancelled status and continuation settlement.

At 1440/768/390/320 widths, the settled drawer is respectively 736/736/390/320 pixels wide, with no document or exact-read-region horizontal overflow. The shared parent owns drawer width/insets; existing Ant Design Descriptions switch to stacked labels at narrow widths and footer controls wrap. Long international filename remains escaped readable text. Initial rapid-resize captures preceded Ant Design's layout transition; final evidence waits for settled bounds and replaces those captures. No CSS adjustment was needed.

Evidence: [wide pending](assets/tenant-attachment-read-review-2026-10-19/pending-1440.png), [768 pending](assets/tenant-attachment-read-review-2026-10-19/pending-768.png), [390 pending](assets/tenant-attachment-read-review-2026-10-19/pending-390.png), [320 pending](assets/tenant-attachment-read-review-2026-10-19/pending-320.png), [confirmation](assets/tenant-attachment-read-review-2026-10-19/confirmation-320.png), [read authorized](assets/tenant-attachment-read-review-2026-10-19/read-authorized-320.png), [stale decision](assets/tenant-attachment-read-review-2026-10-19/stale-320.png), [cancelled](assets/tenant-attachment-read-review-2026-10-19/cancelled-320.png).

The signed-in current journey has no uncaught browser errors, console warnings or failed HTTP responses. An expired cookie from a prior disposable fixture initially produced expected 401 responses; normal sign-in recovered. React cleanup/navigation aborts duplicate GETs before their replacements return 200. The current stale/cancel navigation records zero console errors/warnings and successful 200 detail/inbox/refresh/cancel requests. Historical console messages from other fixture ports are not evidence about this journey. Vite's initial sandbox port-binding refusal was resolved by granting local bind permission. The browser navigated away before both owned servers stopped.

## Reproduction commands

Commands run from the repository root unless marked `web/`; all .NET checks used `--nologo --no-restore -m:1 -nr:false -p:UseSharedCompilation=false`. The recovery matrix, browser fixture and final API regression reused the already rebuilt matching API test assembly with `--no-build`.

```sh
dotnet test tests/AgentCore.Infrastructure.Tests/AgentCore.Infrastructure.Tests.csproj --filter 'FullyQualifiedName~Tenant_attachment_Review|FullyQualifiedName~Tenant_attachment_read_Review|FullyQualifiedName~Tenant_attachment_model_read_|FullyQualifiedName~Tenant_attachment_context_' --nologo --no-restore -m:1 -nr:false -p:UseSharedCompilation=false
dotnet test tests/AgentCore.Api.Tests/AgentCore.Api.Tests.csproj --filter FullyQualifiedName~Tenant_attachment_model_exact_Review_HTTP --nologo --no-restore -m:1 -nr:false -p:UseSharedCompilation=false
dotnet test tests/AgentCore.Api.Tests/AgentCore.Api.Tests.csproj --filter FullyQualifiedName~Tenant_attachment_Review_checkpoint --nologo --no-restore --no-build -m:1 -nr:false -p:UseSharedCompilation=false
dotnet test tests/AgentCore.Application.Tests/AgentCore.Application.Tests.csproj --filter 'FullyQualifiedName~TenantAttachmentReadContractTests|FullyQualifiedName~TenantApprovalToolContractTests|FullyQualifiedName~AttachmentProcessingRuntimeTests|FullyQualifiedName~QueuedAttachmentRuntimeTests|FullyQualifiedName~SessionAttachmentRecallTests|FullyQualifiedName~PromptContextBuilderTests|FullyQualifiedName~AgentRunCheckpointFormatTests|FullyQualifiedName~AgentRunToolCallCheckpointTests|FullyQualifiedName~ModelContinuationCheckpointTests' --nologo --no-restore -m:1 -nr:false -p:UseSharedCompilation=false
dotnet test tests/AgentCore.Api.Tests/AgentCore.Api.Tests.csproj --filter 'FullyQualifiedName~Tenant_attachment_model_reads_historical_source|FullyQualifiedName~Tenant_attachment_model_read_checkpoint_rechecks_bytes' --nologo --no-restore --no-build -m:1 -nr:false -p:UseSharedCompilation=false
AGENTCORE_ATTACHMENT_REVIEW_UI_PROOF=1 dotnet test tests/AgentCore.Api.Tests/AgentCore.Api.Tests.csproj --filter 'FullyQualifiedName~Tenant_attachment_model_exact_Review_HTTP&DisplayName~False&DisplayName~text&DisplayName~approve' --nologo --no-restore --no-build -m:1 -nr:false -p:UseSharedCompilation=false
```

In `web/`, `pnpm exec vitest run src/features/access/TenantApprovals.test.tsx src/features/chat/TenantConversationApprovals.test.tsx` selected the existing shared inspector file (47 tests; no separate Chat test file exists). `pnpm run build` passed. The browser fixture writes disposable local handoff files; start Vite with `VITE_API_PROXY_TARGET` set to that fixture's actual URL and `pnpm exec vite --host 127.0.0.1 --port 5221 --strictPort`, then exercise the normal controls through Playwright MCP. Never point this fixture at installed data or live providers.

## Integration findings

The first hosted SQLite consent case exposed an inbox predicate using an integer for an Attachment state stored as text. The filter now checks the actual `Bound` representation; the full hosted matrix passes after that fix. The store-level test now asserts pending inventory as well as exact detail access.

A shared credential-isolation unit fixture explicitly selects only its intended capability; its prior legacy implicit Attachment capability now legitimately offers read consent. Diagnostic test corrections are excluded from passing counts. Direct denial uses the existing authorization exception and `previously_rejected` / `previously_cancelled` reason, returning the prior request for duplicate preparation. Frozen-clock recovery explicitly pulses existing worker/approval owners rather than relying on wall time.

## Presentation and remaining scope

Organization and Chat reuse the shared Ant Design inspector and confirmation flow. Filename/type/stored bytes/window/disclosure occupy the primary read summary; checksum and opaque identifiers remain in Technical references. No CSS, visual token or second UI kit is added. The preexisting Impeccable design-sidecar freshness finding remains separate from this presentation change.

Review-required initial provided-file projection still hides protected names/IDs/body. Known-ID explicit rereads do not resolve the public provided-file UX handoff. Public message/composer integration, Attachment publication/virtual overlays, other Browser/resource adapters, autonomous/background/events/transports and final obsolete-authority/schema removal remain proposal obligations. CI, live providers, installed-data migration/reset and physical process-kill recovery were not requested or run. Controlled host recreation is not a physical crash claim.
