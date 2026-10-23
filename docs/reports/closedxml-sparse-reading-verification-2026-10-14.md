# ClosedXML sparse XLSX reading — local verification, 2026-10-14

The requested reading enhancement is implemented on the existing `main` checkout, initially clean at `3119e00adde950c4fff69767039a0d0248e4db57`. The pasted proposal's older verified baseline was `13aacbf05ac14ff4116066657f243ee56b5470b3`; the current empty-range fixes and audited dependency versions are retained. This is an ordinary scoped enhancement, not milestone acceptance. Hosted CI and real-file verification were explicitly waived; the owner will verify their own files.

## Result and boundaries

`office.spreadsheet.inspect` and XLSX `attachments.read` now default to sparse content pages when `range` is omitted. Each response contains one worksheet, up to 500 populated cells, original coordinates, sheet names/ordinal, SHA, displayed values/types/formulas/number formats/cache status, warnings and continuation. Optional sheet selection remains fixed for the traversal; otherwise worksheets are visited in workbook order. Empty and formatting-only sheets return an empty page and can advance to the next sheet. A selected sheet can complete without completing the workbook.

The stateless versioned cursor binds source hash, sparse mode, optional selection, sheet name/ordinal and last emitted coordinate. Calls reauthorize the original source each time; the cursor grants no access. Whole-cell UTF-8 fitting recomputes continuation after the last retained cell. Source changes reject with `office_revision_conflict`; invalid/mismatched cursors reject with `office_cursor_invalid`; incompatible explicit range/cursor rejects with `invalid`. A cell or metadata envelope that cannot fit returns `office_output_limit` without a cursor. `coverage`/`sheetCoverage` describe traversal completion; `coverageScope` distinguishes workbook/selected sheet and `pageCoverage` describes the current response alone. Consumers must retain and combine preceding successful pages before claiming a full summary.

ClosedXML 0.105.1 still opens and interprets the workbook. `CellsUsed(XLCellsUsedOptions.Contents)` finds content without enumerating a rectangular matrix. The existing package safety scan supplies bounded hints for explicit empty text (omitted by ClosedXML's `IsEmpty`) and absent formula caches, including inferred source coordinates. `CachedValue` is used throughout; inspection never requests lazy formula calculation. Investigation used the pinned [cell implementation](https://github.com/ClosedXML/ClosedXML/blob/0.105.1/ClosedXML/Excel/Cells/XLCell.cs), [used-cell enumerator](https://github.com/ClosedXML/ClosedXML/blob/0.105.1/ClosedXML/Excel/Cells/XLCells.cs) and [workbook loader](https://github.com/ClosedXML/ClosedXML/blob/0.105.1/ClosedXML/Excel/XLWorkbook_Load.cs), with deterministic behavioral verification below.

Only the XLSX initial extraction switches to this path, with an additional 16 KiB UTF-8 excerpt ceiling. Prompt fitting preserves valid whole-cell JSON and adjusts its cursor; authorized `attachments.read` supplies continuation. DOCX and other processors are unchanged. Explicit rectangular reads retain blank cells, extents and `nextRange`, including the existing empty/out-of-range behavior. Preview, create/edit, FortuneSheet, frontend, Drive/OAuth, Workspace/Artifact persistence and execution lifecycles are unchanged by this enhancement. No stored Session migration, cache, read-session table, streaming engine or parser dependency is introduced.

## Effective limits

| Resource | Sparse content reading | Existing strict profile |
| --- | --- | --- |
| Compressed / expanded package | 10 MiB / 32 MiB | unchanged |
| Entries / worksheets | 512 / 50 | unchanged |
| Stored XML cell records across package | 250,000 | 50,000 |
| Meaningful cells | 50,000, conservatively including aggregate formula-reference materialization area | stored-cell ceiling above |
| Shared-string entries | 50,000 | existing XML/part guards |
| Worksheet XML part | 16 MiB / 1,500,000 nodes | 4 MiB / 250,000 nodes |
| Other parts | 4 MiB / 250,000 XML nodes | unchanged |
| Package XML nodes / depth | 3,000,000 / 64 | existing per-part nodes / 64 |
| Returned sparse cells | 500, reduced to fit caller UTF-8 budget | explicit range: 200 rows × 64 columns |
| Initial XLSX excerpt | 16 KiB UTF-8, additionally cell-bounded | not a full-file coverage claim |
| Processor deadline | cooperative 10 seconds | existing strict behavior retained |

Formatting-only records, including empty numeric `<v/>` records, consume scanning resources rather than the populated-cell budget. Text, numeric zero, false, errors and formulas consume the content budget; explicit empty text remains populated. Formula-reference area is conservatively counted in addition to explicit cell markers, so near-limit array/shared-formula workbooks can be rejected before materialization even when their distinct output coordinates would be fewer. Worksheet identity follows workbook relationships rather than assumed ZIP filenames; XML root identity selects worksheet versus ordinary part limits. Non-worksheet sheet kinds return an unsupported-feature result instead of being mislabeled corrupt.

ZIP paths/duplicates/symlinks, expanded bounds, macros/active package content, external relationships/references, invalid XML/DTDs, depth, shared strings and cancellation remain guarded. Malformed/invalid packages map to `invalid_office_package`, supported resource ceilings to `office_processing_limit`, and recognized unsupported features to `office_feature_unsupported`. A sparse cooperative deadline is a processing-limit result without an invented continuation.

## Executed local scenarios

- Generated workbook with 120,024 formatting-only records, first-sheet XML above 4 MiB, four sheets and only 13 meaningful first-sheet cells: sparse read returned the expected cells in original order, including `B500000` and `XFD1048576`; strict preview/edit/rectangular reads still rejected that large template with `office_processing_limit`.
- Zero, false, errors, formula-only cells, empty cached formula strings, zero caches, inline/shared Unicode and explicit empty strings: values, formulas, types and cache status matched expectations without evaluating an unsupported function. Inferred addresses and column-format-only sheets were preserved/disclosed. Worksheet identity followed relationships even with nonstandard ZIP filenames.
- 1,101 populated cells with 8 KiB output pages: all coordinates appeared exactly once, worksheets stayed in workbook order, empty/formatting-only sheets progressed, and traversal terminated with no cursor. Output remained valid bounded JSON.
- Whole-cell/output boundaries: a 16,000-character cell with a 2 KiB budget returned `office_output_limit`; prompt refitting to 4 KiB emitted valid JSON and continued after its actual last retained cell.
- Security/resource boundaries: stored/populated/shared-string/part/node/package-node/expanded/depth/formula materialization limits rejected precisely; unsafe paths, duplicate entries, DTDs and malformed XML remained invalid; macros and external relationships/formulas remained unsupported; pre-cancelled reads/scans stopped.
- Synthetic attachment integration: upload/bind an original three-sheet workbook, extract/refit initial sparse context, traverse `attachments.read` with 4 KiB results, then read explicit ranges and recover from out-of-range windows. Expected addresses were returned exactly once, foreign Session reads (including with a valid cursor) were denied, and original download bytes were unchanged. DOCX format-specific validation remained intact.
- Synthetic Workspace integration using published General Assistant v26: create an owned workbook, traverse `office.spreadsheet.inspect` with 4 KiB output, reject cursor plus range, edit with exact SHA, reject the old cursor, then restart and observe the edited value/hash. Existing targeted edits, preview/download authorization and stale-write regressions passed.
- Application contracts/prompt checks: both model schemas advertise `cursor` (max 2,048), range compatibility and 500-cell sparse behavior; ReadOnly/ReplaySafe/Session scope and existing capability authorization remain. No Definition or Instance grant/version is added.

## Commands and results

All tests used installed dependencies (`--no-restore`) and local Synthetic/offline fixtures, with no provider calls. MSBuild/test-runner local sockets required the existing sandbox escalation; the first sandboxed runner attempt was blocked before test execution. The authorized local runner then executed the cases successfully.

```sh
dotnet test tests/AgentCore.Infrastructure.Tests/AgentCore.Infrastructure.Tests.csproj --no-restore --filter 'FullyQualifiedName~SparseWorkbookTests|FullyQualifiedName~OfficeProcessorTests|FullyQualifiedName~OfficePreviewPresentationTests|FullyQualifiedName~AttachmentProcessorTests' --nologo -m:1 -p:UseSharedCompilation=false

dotnet test tests/AgentCore.Api.Tests/AgentCore.Api.Tests.csproj --no-restore --filter 'FullyQualifiedName~OfficeJourneyTests|FullyQualifiedName~AttachmentApiTests.Spreadsheet_attachment|FullyQualifiedName~AttachmentApiTests.Document_attachment_read|FullyQualifiedName~AttachmentApiTests.Office' --nologo -m:1 -p:UseSharedCompilation=false

dotnet test tests/AgentCore.Application.Tests/AgentCore.Application.Tests.csproj --no-restore --filter 'FullyQualifiedName~ToolRegistryPolicyTests|FullyQualifiedName~SessionToolExecutorTests|FullyQualifiedName~PromptContextBuilderTests' --nologo -m:1 -p:UseSharedCompilation=false

dotnet test tests/AgentCore.Infrastructure.Tests/AgentCore.Infrastructure.Tests.csproj --no-build --no-restore --filter FullyQualifiedName~SparseWorkbookTests.Measure_generated_template --nologo --logger 'console;verbosity=detailed'
```

Results: Infrastructure **54 passed**, API **10 passed**, Application **42 passed**; **106 distinct focused tests**, zero failures or skips. The detailed measurement rerun passed separately. The final cursor-field validation recheck (`--filter FullyQualifiedName~SparseWorkbookTests.Cursor_rejects`, with the same Infrastructure command options) also passed, 1/1. A concurrent unrelated Sandbox `CS8604` at `OpenSandboxService.cs:212` temporarily blocked that recheck; an existing-dependency fallback with `-p:BuildProjectReferences=false` passed, and a subsequent ordinary fresh build/recheck passed after the concurrent code was repaired separately. No build blocker remains for these focused checks. `git diff --check` and affected documentation link/fence checks passed. No full CI suite, hosted CI, browser/manual UAT or real-file/provider check was run.

## Measured generated-fixture cost

One detailed local .NET 10.0.12 run, using a 318,855-byte compressed fixture with 120,024 formatting-only records and 13 meaningful first-sheet cells:

| Phase | Elapsed | Managed allocations on calling thread |
| --- | ---: | ---: |
| Security scan | 168.9 ms | 7.16 MiB |
| ClosedXML opening | 770.4 ms | 53.14 MiB |
| Contents enumeration | 80.2 ms | 37.55 MiB |
| Full first sparse read | 636.9 ms | 98.18 MiB |
| Next worksheet continuation, including reopening | 588.1 ms | 60.50 MiB |
| Same worksheet continuation, including reopening | 310.5 ms | 95.43 MiB |

The final detailed rerun overlapped a local API build; an earlier detailed run measured a 239.6 ms full read. These are independent ordered measurements with warm-up/JIT/cache effects, not additive timing components or performance guarantees. Allocation counts are cumulative calling-thread managed allocations, not retained heap, process peak RSS or a hard memory ceiling. ClosedXML enumeration still incurs allocation costs for stored styled cells; sparse reading avoids a blank rectangular result matrix but does not avoid workbook loading. No fragile timing assertions were added.

The measured workload is practical within the conservative local bounds, so the ClosedXML solution is retained. Each stateless continuation still reopens and rescans the source. Synchronous vendor loading cannot be interrupted mid-load; cancellation/deadline checks surround it. Large metadata or indivisible cells can still exceed output headroom, and complete summaries may exceed existing Run step/checkpoint limits. Real-file performance/fidelity is unverified here and remains owner-run. Any future streaming reader needs a separate measured decision and authorization.

## Changed files

- Infrastructure: [OfficePackageGuard.cs](../../src/AgentCore.Infrastructure/Office/OfficePackageGuard.cs), new [OfficeProcessor.SparseSpreadsheet.cs](../../src/AgentCore.Infrastructure/Office/OfficeProcessor.SparseSpreadsheet.cs), XLSX branch of [AttachmentProcessor.cs](../../src/AgentCore.Infrastructure/Attachments/AttachmentProcessor.cs).
- Application: [Office.cs port](../../src/AgentCore.Application/Ports/Office.cs), new [OfficeSparseCursor.cs](../../src/AgentCore.Application/Office/OfficeSparseCursor.cs), [OfficeReadResults.cs](../../src/AgentCore.Application/Office/OfficeReadResults.cs), [OfficeToolContracts.cs](../../src/AgentCore.Application/Office/OfficeToolContracts.cs), SessionToolExecutor.Office.cs (historical `SessionToolExecutor.Office.cs`), [SessionToolExecutor.cs](../../src/AgentCore.Application/Tools/SessionToolExecutor.cs), [ToolRegistry.cs](../../src/AgentCore.Application/Tools/ToolRegistry.cs), [PromptContextBuilder.cs](../../src/AgentCore.Application/Agents/PromptContextBuilder.cs).
- Tests: new [SparseWorkbookFixture.cs](../../tests/AgentCore.Infrastructure.Tests/SparseWorkbookFixture.cs) and [SparseWorkbookTests.cs](../../tests/AgentCore.Infrastructure.Tests/SparseWorkbookTests.cs), updated [AttachmentApiTests.cs](../../tests/AgentCore.Api.Tests/AttachmentApiTests.cs), [OfficeJourneyTests.cs](../../tests/AgentCore.Api.Tests/OfficeJourneyTests.cs), [ToolRegistryPolicyTests.cs](../../tests/AgentCore.Application.Tests/ToolRegistryPolicyTests.cs).
- Documentation: [README](../../README.md), canonical [ports](../04-backend-interfaces.md), [decisions](../10-technology-decisions.md), [backend](../12-backend-implementation-spec.md), [protocol](../14-api-and-realtime-protocol.md), [testing](../16-testing-strategy.md), and this ledger.

Unrelated Workspace/Domain/OpenSandbox edits appeared concurrently after the initially clean checkout; they were not changed or reverted by this task and are not part of the sparse enhancement file inventory. The owner subsequently requested a scoped commit and push to `main`; unrelated concurrent work is excluded from that delivery.
