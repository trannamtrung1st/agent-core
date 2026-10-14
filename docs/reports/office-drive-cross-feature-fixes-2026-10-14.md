# Final Office and Google Drive cross-feature fixes — 2026-10-14

This pass implements the supplied review brief using focused local tests. Its reviewed baseline was `e37c8254`; verification ran against the evolving shared working tree (HEAD `b5f4f34b`), including unrelated staged Sandbox/Admin work. Those changes were preserved. This report records new evidence; previous reports retain their historical coverage semantics and results.

## Findings and corrections

| Confirmed finding | Reproduction before correction | Result |
| --- | --- | --- |
| String type alone counted as XLSX content | Three type-only records appeared as phantom text; 50,010 records raised the populated-cell limit | Count actual stored value/inline/formula representations. Empty shared-index/numeric values and type-only records disappear; explicit empty strings, shared references, zero, false, errors and cacheless formulas remain. Structural limits are unchanged. |
| Final sparse page claimed complete coverage | A final selected-sheet cursor page reported `complete` despite omitted preceding data | Coverage describes this response. Worksheet/traversal end flags remain separate; `analysisCoverage` is `not_verified`. Updated tool/prompt guidance requires combining all required actual pages and disclosing missing coverage. Stateless cursors, hashes and authorization remain intact. |
| Preview errors confused unsupported files with corruption and exposed backend details | Four 400 code cases collapsed into invalid-package copy; 413 displayed supplied untrusted detail | Read at most 4 KiB of problem data; allowlist six Office/preview codes and use local recovery text. Unknown/malformed problems fall back safely. Access, missing-source and stale-version errors stay distinct; Download stays available on supported failure states. |
| Drive invalid Office title differed from other sources | Synthetic Drive DOCX preview returned `ValidationError` | Reuse Office problem normalization. Invalid DOCX and unsupported DOCX/XLSX retain correct titles, exact downloads, stale-version rejection and owner protection. |
| OAuth recovery named a legacy configuration section | Account recovery said `GoogleDriveOAuth` | Both account and resolve guidance name `GoogleOAuth`; existing error code/status and grant/provenance logic remain. |
| Workspace and Resource growth allocated full bytes after small preflight | Deterministic managed updates produced 413 only after a full read/blob request | Workspace uses existing bounded inspection. Resource reads check freshly resolved size before reading an immutable hash snapshot; Draft Resource bounded reads bind to one authorized metadata/hash snapshot. Actual byte ceilings are also checked. |

The suspected browser hyperlink gap was disproved: existing `WordPreview` sanitization and iframe policy passed without product changes. HTTP/HTTPS/mailto text remained visible; forbidden navigation attributes were absent; clicking all labels caused zero external requests and zero main-frame navigation. Original downloads matched fixture bytes before and during an unsupported-preview failure, and Retry restored the preview.

## Local verification

All final focused checks passed; 152 backend tests, 34 frontend tests and two targeted Playwright scenarios passed. These are ordinary change checks, not full CI or milestone acceptance.

| Check | Exact selection / command | Passed |
| --- | --- | --- |
| Infrastructure Office | `dotnet test tests/AgentCore.Infrastructure.Tests/AgentCore.Infrastructure.Tests.csproj --filter 'FullyQualifiedName~SparseWorkbookTests\|FullyQualifiedName~OfficeProcessorTests\|FullyQualifiedName~OfficePreviewPresentationTests\|FullyQualifiedName~WordHyperlinkTests' --nologo` | 82 |
| Drive persistence | Same project, `--filter FullyQualifiedName~DrivePersistenceTests --nologo` | 5 |
| Application contracts/results | `dotnet test tests/AgentCore.Application.Tests/AgentCore.Application.Tests.csproj --filter 'FullyQualifiedName~OfficeSparseCoverageTests\|FullyQualifiedName~ToolRegistryPolicyTests' --nologo` | 20 |
| API preview/OAuth | `dotnet test tests/AgentCore.Api.Tests/AgentCore.Api.Tests.csproj --filter 'FullyQualifiedName~OfficePreviewRaceTests\|FullyQualifiedName~FilePreviewPolicyTests\|FullyQualifiedName~DriveOfficePreviewTests\|FullyQualifiedName~DriveOAuthGuidanceTests' --nologo` | 23 |
| API preservation | Same project, `--filter 'FullyQualifiedName~OfficeJourneyTests\|FullyQualifiedName~AttachmentApiTests\|FullyQualifiedName~DriveRecoveryJourneyTests\|FullyQualifiedName~DriveApprovalJourneyTests\|FullyQualifiedName~DriveBatchJourneyTests\|FullyQualifiedName~DriveUploadCompletionTests\|FullyQualifiedName~DriveTrashJourneyTests' --nologo` | 22 |
| Frontend | From `web`: `pnpm exec vitest run src/features/filePreview/officeSource.test.ts src/features/filePreview/FilePreview.test.tsx src/features/filePreview/fileSource.test.ts src/features/filePreview/previewPolicy.test.ts src/features/filePreview/previewOptions.test.ts` | 34 |
| Hyperlink security/recovery | From `web`: `pnpm exec playwright test e2e/office-cross-feature.spec.ts --project=synthetic --reporter=line` | 1 |
| Existing range recovery | From `web`: `pnpm exec playwright test e2e/office-preview.spec.ts --project=synthetic --grep 'agent creates real Office' --reporter=line` | 1 |
| Backend build | `dotnet build src/AgentCore.Api/AgentCore.Api.csproj --no-restore --nologo --disable-build-servers` | 0 warnings/errors |
| Frontend build | From `web`: `pnpm run build` | Passed; existing Rollup annotation/chunk-size warnings |

Playwright used `PLAYWRIGHT_API_PORT=5180 PLAYWRIGHT_WEB_PORT=5273 PLAYWRIGHT_FAITHFUL_MANUAL=1 PLAYWRIGHT_SQLITE_PATH=/tmp/agent-core-office-review-20261014/synthetic.db` against task-owned native Synthetic API/Vite hosts, with disposable persistence paths and OpenSandbox disabled. No existing host was restarted. Task-owned Synthetic hosts were stopped and their disposable data removed at completion. CLI scenario actions included creating a fixture identity, saving DOCX to owned Workspace, previewing/clicking links, downloading originals, simulating unsupported-feature failure and retrying; the existing scenario additionally rejected XLSX `A1:B201`, recovered with `A1:B2`, and read cached formulas/sheets.

Playwright MCP independently navigated the verified Synthetic host, opened the same DOCX through visible controls, clicked all three hyperlink labels, checked the iframe sandbox/attributes, simulated the unsupported response, confirmed Download and Retry, and observed zero external requests, page exceptions or main-frame navigation. Console errors were only the intentionally injected HTTP 400. Recovery layout fit 1440px, 390px and desktop 200% CSS zoom. Impeccable copy review reused the AntD alert/actions and existing tokens; desktop/mobile screenshots are local ignored evidence in `.playwright-mcp/office-cross-feature-recovery-{desktop,mobile}.png`. No design token or component changes were needed.

Reproduction failures, a replaced backend-message browser assertion, a frontend transition assertion and test compilation corrections were resolved before final passing checks. The initial sandboxed backend build stalled in compiler-server communication and was cancelled; the escalated build with servers disabled passed. Whitespace checks passed; 346 relative Markdown links/anchors and fence balance passed across seven affected documentation files.

## Boundaries and remaining limitations

- No hosted CI, live Google operations, customer-file UAT, production credentials, schema changes or new dependencies were used. No broad Office/Drive refactor or storage layer was added.
- Drive preservation tests cover exact approvals, per-file/batch receipts, unknown-effect reconciliation, upload completion/recovery and persistence reopen; no destructive retry or scope expansion was introduced.
- Resource content stores still return whole immutable byte snapshots. Managed revision races now reject oversized current metadata before allocation and final byte checks reject oversize results. External mutation/tampering of physical blobs is an integrity failure, not a newly provided bounded streaming lifecycle.
- Stateless end positions cannot prove that a consumer retained or analyzed earlier pages. Synchronous ClosedXML load remains cooperatively bounded by the existing guards rather than forcibly preemptible.
- No operator action is required. Optional Impeccable `document` refresh can reconcile the pre-existing stale design sidecar; it is outside this fix pass.

## Exact files changed by this pass

- `README.md`
- `docs/04-backend-interfaces.md`
- `docs/12-backend-implementation-spec.md`
- `docs/13-frontend-implementation-spec.md`
- `docs/14-api-and-realtime-protocol.md`
- `docs/16-testing-strategy.md`
- `docs/reports/office-drive-cross-feature-fixes-2026-10-14.md`
- `src/AgentCore.Api/OfficePreviewEndpoints.cs`
- `src/AgentCore.Api/DriveEndpoints.cs`
- `src/AgentCore.Application/Admin/AgentDefinitionResourceService.cs`
- `src/AgentCore.Application/Admin/AgentInstanceResourceService.cs`
- `src/AgentCore.Application/Agents/PromptContextBuilder.cs`
- `src/AgentCore.Application/Drive/DriveService.cs`
- `src/AgentCore.Application/Office/OfficeReadResults.cs`
- `src/AgentCore.Application/Office/OfficeToolContracts.cs`
- `src/AgentCore.Application/Tools/ToolRegistry.cs`
- `src/AgentCore.Infrastructure/Office/OfficePackageGuard.cs`
- `tests/AgentCore.Infrastructure.Tests/SparseWorkbookTests.cs`
- `tests/AgentCore.Application.Tests/OfficeSparseCoverageTests.cs`
- `tests/AgentCore.Application.Tests/ToolRegistryPolicyTests.cs`
- `tests/AgentCore.Api.Tests/AttachmentApiTests.cs`
- `tests/AgentCore.Api.Tests/OfficeJourneyTests.cs`
- `tests/AgentCore.Api.Tests/FilePreviewPolicyTests.cs`
- `tests/AgentCore.Api.Tests/OfficePreviewRaceTests.cs`
- `tests/AgentCore.Api.Tests/DriveOfficePreviewTests.cs`
- `tests/AgentCore.Api.Tests/DriveOAuthGuidanceTests.cs`
- `web/src/features/filePreview/officeSource.ts`
- `web/src/features/filePreview/officeSource.test.ts`
- `web/src/features/filePreview/FilePreview.test.tsx`
- `web/e2e/office-cross-feature.spec.ts`
- `web/e2e/office-preview.spec.ts`
- `web/e2e/fixtures/file-preview/hyperlinks.docx`
