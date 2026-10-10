# Resource management enhancement verification

Date: 2026-10-10. This is a bounded follow-up to existing Definition/Instance resource authoring. It does not reopen a milestone, move a historical freeze or establish hosted acceptance.

## Behavior

CSV was absent from both the backend resource media allowlist and browser inference map. Authoring now recognizes common textual data, Office/OpenDocument, image, audio and video formats, including browser CSV MIME aliases and files with missing MIME metadata. CSV/TSV and other supported structured text can be Knowledge. Binary resources retain exact bytes; there is no new extraction or execution path. Known executable/active-content extensions cannot bypass validation by claiming an allowed MIME. Quotas, owner capability and immutable Run content remain unchanged. The canonical format policy is in [Backend Implementation](../12-backend-implementation-spec.md#common-definition-and-instance-resource-formats).

Definition draft resources now use the existing Admin table composition and preserve path, kind, media, size and hash information. Both editable resource collections share a token-spaced selection toolbar and a bounded, scrollable confirmation path list. Published/inherited Definition files remain read-only; archived Instances cannot select or delete. Bulk deletion uses existing single-item APIs, advancing only committed revisions from that batch. It stops on failure, reports the completed count and preserves earlier commits. This is intentionally not an atomic batch API. [Frontend Implementation](../13-frontend-implementation-spec.md#resource-selection-and-bulk-deletion) owns the behavior.

## Executed scenarios

- API upload/bind: CSV Knowledge and Office Reference resources were stored and read with matching metadata and exact bytes. CSV/YAML containing the existing forbidden credential sentinel were rejected. Executable original filenames/target paths with allowed MIME were rejected. A stale second deletion returned 409 without deleting that resource; deletion at the next correct revision succeeded. Instance checks passed with both InMemory and temporary SQLite stores.
- Definition browser journey: uploaded CSV/YAML/text with missing MIME metadata, selected two files, canceled confirmation and observed both selections intact, then confirmed deletion and observed only those files removed. The unselected file remained.
- Instance browser journey: uploaded CSV with the browser Excel MIME alias and YAML, selected both, injected a conflict on the second delete and observed “1 of 2 resources deleted.” Mutations stayed disabled until Retry resources; reload showed the remaining file, which was then selected and successfully deleted.
- Definition conflict journey: the first deletion committed and the second returned an injected 409. The completed count and error stayed visible across the automatic draft/resource refresh; the remaining resource became selectable at the refreshed revision.
- Playwright MCP independently exercised Definition CSV/YAML upload, cancellation and deletion, plus Instance CSV Knowledge/YAML upload and row selection. Fresh CLI journeys asserted no page exceptions or unexpected failed requests; expected native draft-read aborts are excluded using the existing lifecycle helper.
- Responsive checks: Definition and Instance tables were inspected at 1440, 768 and 390 pixels, including a long CSV path and selection state. Body width matched viewport width; tables scroll locally and the toolbar wraps on mobile. Existing Admin panel/table spacing and AntD tokens own presentation. Captures are in gitignored `local/verification/resource-management/` (`instance-1440.png`, `instance-768.png`, `instance-390.png`, `definition-final-1440.png`, `definition-768.png`, `definition-final-390.png`).

## Checks

| Command | Result |
| --- | --- |
| `dotnet test tests/AgentCore.Api.Tests/AgentCore.Api.Tests.csproj --filter 'FullyQualifiedName~Common_resource_files\|FullyQualifiedName~Draft_csv_knowledge' --nologo` | 3 passed |
| `dotnet test tests/AgentCore.Api.Tests/AgentCore.Api.Tests.csproj --no-restore --filter 'FullyQualifiedName~resource\|FullyQualifiedName~Resource' --nologo` | 18 passed |
| `dotnet test tests/AgentCore.Infrastructure.Tests/AgentCore.Infrastructure.Tests.csproj --no-restore --filter FullyQualifiedName~AgentDefinitionResourceAdminStoreContractTests --nologo` | 30 passed |
| `pnpm exec vitest run src/features/admin/resourcePreview.test.ts src/features/admin/resourceImportPanel.test.tsx --maxWorkers=1` (web) | 23 passed |
| `pnpm exec vitest run src/features/admin/AdminApp.test.tsx --testNamePattern 'resource\|resources' --maxWorkers=1` (web) | 6 passed, 36 intentionally filtered |
| `pnpm exec playwright test --config .resource-management.verify.config.ts` (web) | 3 passed |
| `pnpm run build` (web) | Passed; existing bundle-size/SignalR annotation warnings |
| `pnpm exec vitest run src/features/admin/AdminApp.test.tsx src/features/admin/definitionCandidateEditor.test.tsx --maxWorkers=1` (web) | AdminApp: 42 passed; editor: 13 passed, one 30-second timeout in Form/JSON dirty-edit case |
| `pnpm exec vitest run src/features/admin/definitionCandidateEditor.test.tsx --testNamePattern 'keeps dirty edits across views and does not save invalid JSON' --maxWorkers=1 --testTimeout=90000` (web) | Passed, 20.7 seconds; timing diagnostic only |
| `pnpm exec vitest run src/features/admin/definitionCandidateEditor.test.tsx --testNamePattern 'keeps dirty edits across views and does not save invalid JSON' --maxWorkers=1` (web), final retry | Passed, 14.5 seconds at the default timeout |
| `pnpm exec tsc --noEmit` (web) | Passed |
| `git diff --check` | Passed |

The disposable browser verification used a new Synthetic API at 127.0.0.1:5099 with InMemory persistence and Vite at 127.0.0.1:5199. The temporary config extended the standard Playwright config, selected only `resource-management.spec.ts`, set its baseURL to the disposable Vite server and disabled runner-managed webServer startup. It is removed after verification. Existing hosts/data were not reset.

The first broader frontend run had timeouts and an obsolete assertion that expected immediate row removal. The removal test now confirms the destructive action. The serial rerun passed all 42 AdminApp tests and 13 editor tests. `keeps dirty edits across views and does not save invalid JSON` exceeded its existing 30-second timeout; the first isolated retry also timed out. A diagnostic retry with `--testTimeout=90000` passed all assertions in 20.7 seconds without changing repository timeout settings. The final isolated retry at the unchanged default 30-second timeout passed in 14.5 seconds. No timeout setting was edited; this resolves the failed case as a timing issue, while the full combined command remains recorded as a failed attempt. Full solution, full Synthetic browser suite, Compose and hosted CI are outside this bounded verification; no full milestone acceptance is claimed.

All affected resource checks pass. The broader Admin/editor selection has passing evidence for each of its 56 tests across the serial run and isolated default-timeout retry. Disposable servers and the MCP browser were stopped after verification.

## Follow-up consistency review

The requested second review reproduced a Definition recovery defect through Playwright MCP: deleting one binding committed, but an injected 503 on the following draft read left the remaining checkbox enabled with a stale revision. Clicking Reload resources produced an unhandled page error. Draft revision readiness is now tracked independently from resource catalog availability. Retry catches failures, keeps the notice visible, locks mutations until both reads recover and does not repeat the committed deletion. Catalog read epochs prevent older results and loading completions from superseding newer reads.

Single-file Definition uploads now use the same path, kind, format and quota validation as imports, and both lock the enclosing draft controls while uploading/binding. Import state is scoped to the draft identity. Instance replacement copy uses the shared common-format description. Browser/backend filename checks now agree on trimmed extensions; executable filenames with trailing whitespace are rejected. Extensionless filenames such as `exe` are accepted when their declared media type is supported, `text/xml` is recognized without an extension, and textual preview checks are case-insensitive.

Runtime observations after correction:

- MCP deleted one of two CSV bindings, injected repeated draft-refresh failures, observed disabled remaining checkboxes and handled retry notices, restored the read, and deleted the remaining file at the refreshed revision. No unhandled page error occurred.
- MCP selected an Office file as Knowledge and observed the validation notice and disabled upload. Changing it to Reference allowed storage. Holding its content upload confirmed that the path/editor controls stayed disabled until completion.
- The four browser regressions cover CSV uploads, invalid binary Knowledge, confirmation cancellation, preservation of unselected files, partial Definition/Instance deletion conflicts, and repeated failed-refresh recovery. Each asserts no page errors or unexpected failed requests.
- Definition resources and the Instance replacement drawer were inspected at 1440/768/390 after responsive layout settled. Page width matched each viewport, tables scrolled locally, long Office media types wrapped, and controls remained reachable. Final MCP inspection recorded no console errors, page errors or unexpected failed requests. Captures: `local/verification/resource-management/review-definition-{1440,390}.png` and `review-instance-{1440,390}.png`.

| Follow-up check | Result |
| --- | --- |
| `dotnet test tests/AgentCore.Api.Tests/AgentCore.Api.Tests.csproj --no-restore --filter 'FullyQualifiedName~resource\|FullyQualifiedName~Resource' --nologo` | 18 passed, including InMemory/SQLite executable-filename rejection |
| `pnpm exec vitest run src/features/admin/resourcePreview.test.ts src/features/admin/resourceImportPanel.test.tsx --maxWorkers=1` | 24 passed |
| `pnpm exec vitest run src/features/admin/AdminApp.test.tsx --testNamePattern 'resource\|resources' --maxWorkers=1` | 6 passed; 36 intentionally filtered |
| `pnpm exec playwright test --config .resource-management.verify.config.ts --project synthetic` | 4 passed, including the new failed-refresh regression |
| `pnpm run build` | Passed; existing SignalR annotation and bundle-size warnings |
| `git diff --check` | Passed |

The first expanded browser run passed three scenarios and timed out selecting Ant Design's hidden virtual option in the new Knowledge validation check. Using the actual combobox keyboard interaction corrected that test; both subsequent complete runs passed all four scenarios. Intermediate MCP exploration also caught an overlapping-read retry issue, which was corrected and included in the passing recovery scenario. The final checks use the same disposable Synthetic/InMemory API and Vite ports described above. Temporary configuration and owned hosts are cleaned up after verification. Full solution/browser suites, Compose and hosted CI were not rerun for this bounded review; no full milestone acceptance is claimed.
