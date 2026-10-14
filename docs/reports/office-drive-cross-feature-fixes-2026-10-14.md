# Final Office and Google Drive cross-feature fixes — 2026-10-14

This pass implements the supplied review brief using focused local tests. Its reviewed baseline was `e37c8254`; verification ran against the evolving shared working tree (HEAD `b5f4f34b`), including unrelated staged Sandbox/Admin work. Those changes were preserved. This report records new evidence; previous reports retain their historical coverage semantics and results.

## Qualification closure

**Office + Google Drive v1 implementation is closed** following the owner's request to complete the remaining review qualifications without waiting for CI. Verification ran on `2766d08e793bd32cdc2257d93bad740edafe307c` (2026-10-14), containing Office fixes `13f10901` and Sandbox/Workspace integration `86e81b80`. No product or test changes were needed. All selected checks passed: **209 backend tests, 34 frontend tests, three CLI browser scenarios, independent Playwright MCP interaction and both builds**. No selected test was skipped.

Both qualifications are resolved:

- The earlier Sandbox report's sparse-coverage failure is historical. The exact `OfficeJourneyTests` class now passes **6/6**, including `Published_office_capability_reads_sparse_owned_workbook_and_rechecks_revision_on_continuation`. It reads all 101 cells across two sheets, expects response coverage `partial` and traversal completion separately, rejects a stale cursor after an edit and recovers the new value/hash. The assertion already shipped in `13f10901`; this run independently executes it.
- Current-head integrated evidence now covers registered Office tools and protected previews, upload/bind/read attachments, Synthetic Drive approvals/receipts/recovery, General Assistant v30's Office → Drive workflow, and the changed Workspace atomic import/crash/reopen boundary. This is a focused Office/Drive qualification after the Sandbox changes, not a new full Sandbox or milestone acceptance run.

### Commands and results

Backend commands ran from the repository root with `AGENTCORE_LIVE_PROVIDER_TESTS=0`. Infrastructure/Application builds used `--nologo --disable-build-servers -m:1`; API tests used the freshly built assembly with `--no-build --nologo`. Filters below are the actual executed selections (pipe characters separate alternatives).

| Project / command | Filter or selection | Result |
| --- | --- | --- |
| `dotnet test tests/AgentCore.Api.Tests/AgentCore.Api.Tests.csproj` | `FullyQualifiedName~OfficeJourneyTests` | 6 pass |
| `dotnet test tests/AgentCore.Infrastructure.Tests/AgentCore.Infrastructure.Tests.csproj` | `FullyQualifiedName~SparseWorkbookTests\|FullyQualifiedName~OfficeProcessorTests\|FullyQualifiedName~OfficePreviewPresentationTests\|FullyQualifiedName~WordHyperlinkTests\|FullyQualifiedName~DrivePersistenceTests\|FullyQualifiedName~WorkspaceStructureTests` | 108 pass |
| `dotnet test tests/AgentCore.Application.Tests/AgentCore.Application.Tests.csproj` | `FullyQualifiedName~OfficeSparseCoverageTests\|FullyQualifiedName~ToolRegistryPolicyTests\|FullyQualifiedName~SandboxSetupResolverTests` | 23 pass |
| `dotnet test tests/AgentCore.Api.Tests/AgentCore.Api.Tests.csproj` | `FullyQualifiedName~OfficePreviewRaceTests\|FullyQualifiedName~FilePreviewPolicyTests\|FullyQualifiedName~DriveOfficePreviewTests\|FullyQualifiedName~DriveOAuthGuidanceTests\|FullyQualifiedName~AttachmentApiTests\|FullyQualifiedName~DriveJourneyTests\|FullyQualifiedName~DriveUploadCompletionTests` | 72 pass |
| From `web`: `pnpm exec vitest run` | `src/features/filePreview/officeSource.test.ts src/features/filePreview/FilePreview.test.tsx src/features/filePreview/fileSource.test.ts src/features/filePreview/previewPolicy.test.ts src/features/filePreview/previewOptions.test.ts` | 34 pass |
| From `web`: `pnpm exec playwright test e2e/office-cross-feature.spec.ts e2e/office-preview.spec.ts e2e/drive-office.spec.ts --project=synthetic --grep 'DOCX hyperlinks\|agent creates real Office\|connect explicit Drive binding' --reporter=line` | Three targeted scenarios | 3 pass, 40.7 seconds |
| `dotnet build src/AgentCore.Api/AgentCore.Api.csproj --no-restore --nologo --disable-build-servers -m:1` | Backend build | 0 warnings/errors |
| From `web`: `pnpm run build` | TypeScript/Vite | Pass; existing Rollup annotation/chunk-size warnings |

The API preservation filter deliberately names the actual partial class `DriveJourneyTests`, covering its approval, batch, Trash and recovery methods rather than relying on source filenames as class names.

### Executed workflows

| Scenario and actions | Expected and observed outcome |
| --- | --- |
| Sparse owned XLSX: create, paginate both sheets, edit, reuse old cursor, restart reading | All 101 cells preserved in order; final page remains partial response coverage with completed traversal; stale continuation rejected; new content/hash returned. |
| Workspace import: stage exact bytes, inject conflicts/unsafe/quota/cancellation failures and crash at swap phases, reopen | Rejected imports preserve canonical bytes; successful import is atomic; durable commit evidence survives reopen and prevents replay. |
| Drive browser with v30: create/connect a Synthetic account, bind explicitly, generate Office Artifacts, inspect remote files/previews, reconnect epoch, reject batch, then approve batch | Protected remote Office previews/downloads work; stale epoch rejected/recovered; rejected batch leaves all 8 files; approved exact 7-file batch reports 7 trashed, zero failures/uncertainty and leaves 1 file. |
| DOCX hyperlinks: open owned fixture, click HTTP/HTTPS/mailto labels, download, inject unsupported-preview failure, download again, Retry | Readable inert labels; zero external requests/navigation/page exceptions; both downloads match original bytes; safe recovery text and Retry restore preview. |
| Spreadsheet/Word browser: create Office Artifacts, select sheets/cached formulas, reject `A1:B201`, recover with `A1:B2`, download and inspect isolated Word pages | Correct formula/cache metadata and sheet values; bounded-range error recovers; original ZIP downloads preserved; Word content visible in sandboxed iframe. |
| Independent Playwright MCP: v30 Chat → send `synthetic-office: q3-sales` → XLSX preview → Assumptions → rejected range → smaller range → select `B2` → DOCX preview | Observed rejection, recovery to `B2 · 0001`, visible report title/recommendations and `sandbox="allow-same-origin"`. Console has only the deliberate HTTP 413; network records show the expected rejection/recovery requests. |

Task-owned API/Vite hosts used `6180`/`6273`, with health reporting `Synthetic` and build version `2766d08e793bd32cdc2257d93bad740edafe307c`. CLI used `PLAYWRIGHT_API_PORT=6180 PLAYWRIGHT_WEB_PORT=6273 PLAYWRIGHT_FIXTURE_PORT=6191 PLAYWRIGHT_FAITHFUL_MANUAL=1 PLAYWRIGHT_SQLITE_PATH=/tmp/agent-core-office-close-20261014/synthetic.db`. Persistence roots were disposable under the same `/tmp` directory; `OpenSandbox__Enabled=false`, provider spending disabled. Existing hosts/data were untouched. Task-owned hosts were stopped after verification. Logs remain local in `/tmp/agent-core-office-close-20261014/{office-journey,infrastructure,application,api-regressions,frontend,browser,backend-build,frontend-build}.log`.

The first sandboxed .NET invocation built successfully but aborted before tests because VSTest could not bind its local socket (`SocketException (13): Permission denied`). The permitted local-socket rerun passed; this was a setup restriction, not a product failure. An initial MCP fixture POST used a relative URL without a configured base URL; correcting it to the task host's absolute URL completed the journey.

The application/test/frontend source remained identical to the verified HEAD throughout and at completion. Concurrent edits to native launcher scripts and related documentation appeared during this run and were preserved; they are not covered by these Office/Drive checks. Documentation closure updates preserve the historical failed and passed evidence.

Documentation verification passed: 286 relative links/anchors and fence balance across the five closure documents; `git diff --check` is clean. The MCP network record remains local at `/tmp/agent-core-office-close-20261014/mcp-network.log`.

### Closure boundaries

There are no remaining mandatory Office/Drive implementation findings from the supplied review. Hosted CI was neither dispatched nor awaited, and the full CI suite was not run locally. Customer DOCX/XLSX UAT, hosted-model complete multi-sheet summary quality, dedicated live Google account/batch Trash, desktop Office fidelity and actual native browser zoom remain separately recorded manual/Real acceptance gaps. Synthetic batch approval does not claim live Google execution. Real OpenSandbox/Docker acceptance was not rerun; the focused Workspace seam checks do not renew its historical acceptance. Reopen this implementation topic for a reproduced regression or a new requirement.

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
