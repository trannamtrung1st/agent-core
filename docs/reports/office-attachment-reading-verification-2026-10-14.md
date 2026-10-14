# Office attachment reading verification — 2026-10-14

The user reported an agent stopping after roughly 20 rows of one XLSX tab and treating `invalid_office_package` as proof that another workbook was corrupt. This correction builds on the implemented Office and composer attachment successors; it does not open P10/P11 or claim a new milestone acceptance.

Initial attachment text is compact context. Previously the prompt sliced serialized cell objects, and another read repeated the same extraction; further Office inspection required a Workspace copy. `attachments.read` now reads owned Office originals directly, with optional XLSX sheet/range or DOCX offset/limit. Office projections preserve complete document elements as well as cells. Spreadsheet output includes all sheet extents, selected/returned/used ranges and continuation. Context/output fitting preserves complete rows. Agents can choose targeted partial reads or continue across relevant sheets/pages for a full summary without asking permission to continue. Existing per-call/run and package safety budgets remain enforced.

Recognized unsupported features and explicit processing bounds return `office_feature_unsupported` / `office_processing_limit`, with the actual reason. Invalid ZIP/XML/type or unsafe package paths retain `invalid_office_package`. External relationships remain rejected without fetching them; the correction does not enable macros, external evaluation or unsupported edits. Cache version `/5` prevents reuse of the old excerpt/failure projection.

## Executed scenarios

- XLSX: HTTP upload → SignalR attach/send/bind → initial extraction/prompt → registered tool dispatch. A three-tab fixture has 240 checklist items, a guest tab, an empty tab and a value in column BM. Repeated 8 KiB reads returned all 240 items in order; direct tab/column selection returned the expected values. A 4 KiB initial prompt slice remained valid JSON with whole rows and coverage. Oversized A1:A201 rejected; A240 then read successfully. A foreign Session was denied and protected download matched the original bytes.
- DOCX: upload/bind a 120-paragraph fixture, then follow `nextOffset` through 4 KiB result pages. All paragraphs returned once in order; XLSX-only arguments rejected, and original download bytes were unchanged.
- Text budget boundary: a 17-row workbook with 4,000 characters per cell returned 16 complete rows for agent inspection, followed by A17. The UI preview of A1:A17 still returned its structural-limit failure, and its smaller range recovered.
- Package failures: corrupt ZIP retained invalid-package failure; an external relationship returned unsupported-feature with the exact reason; an oversized package returned processing-limit. Existing malicious-package and cancellation fixtures passed.

## Checks

All commands run from the repository root with `--nologo --no-restore -m:1 /p:UseSharedCompilation=false /nodeReuse:false`:

| Command | Result |
| --- | --- |
| `dotnet test tests/AgentCore.Api.Tests/AgentCore.Api.Tests.csproj --filter 'FullyQualifiedName~AttachmentApiTests\|FullyQualifiedName~OfficeJourneyTests\|FullyQualifiedName~FilePreviewPolicyTests'` | 30 passed |
| `dotnet test tests/AgentCore.Infrastructure.Tests/AgentCore.Infrastructure.Tests.csproj --filter 'FullyQualifiedName~OfficeProcessorTests\|FullyQualifiedName~OfficePreviewPresentationTests\|FullyQualifiedName~AttachmentProcessorTests'` | 27 passed |
| `dotnet test tests/AgentCore.Application.Tests/AgentCore.Application.Tests.csproj --filter 'FullyQualifiedName~AttachmentProcessingRuntimeTests\|FullyQualifiedName~SessionToolExecutorTests\|FullyQualifiedName~ToolRegistryPolicyTests\|FullyQualifiedName~SessionAttachmentRecallTests'` | 42 passed |

The first sandboxed MSBuild run failed on local named-pipe permission; the tests ran successfully with approved local execution outside that sandbox. Earlier runs exposed a stale UTF-8 test expecting base64 despite the existing unsupported-reader behavior, a cache-version assertion and an old inspection-limit assertion; those expectations/fixtures were corrected and the focused suites passed.

`git diff --check` passed. Relative Markdown file links and balanced code fences passed for the seven affected canonical/report documents.

## Limits of evidence

The exact two Vietnamese workbooks were not found in the repository fixtures or checked local API attachment records, so their content and File 1's precise rejection reason were not verified. This evidence establishes deterministic reading/continuation and error semantics, not hosted-model summarization quality. No live provider call, frontend change, browser run, Compose run or hosted CI was required/performed for this backend correction. Broader Office/preview acceptance and existing manual/Real gaps retain their prior status. A follow-up review, correction, commit and push were requested. Delivery status is recorded below.


## Follow-up review

The second review found and fixed unavailable-tool guidance in Office provenance and targeted parameters silently falling back to a cached/ordinary excerpt. Guidance now depends on the actual prompt tool projection; unavailable Office processors and wrong-format arguments fail explicitly. DOCX initial context, tool dispatch and Workspace inspection share the same whole-element budget projection, including nextOffset. Runtime regression additionally checks a valid DOCX prompt slice and rejection of a null XLSX range on DOCX. Focused local verification was rerun; no hosted CI was requested.

The prerequisite upload, spreadsheet-preview and file-card work was committed separately during this review; delivery uses those commits as its base and excludes unrelated staged changes. An attempted extra isolated snapshot restore was blocked by NuGet audit errors NU1902/NU1903 in existing SignalR 10.0.12 and System.Security.Cryptography.Pkcs 10.0.0 dependencies. No dependency/audit policy was changed. This is a fresh-restore limitation; the focused current-tree tests use the existing assets with `--no-restore` and compile/run the affected code.
