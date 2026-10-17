# Tenant Instance Resources UI verification — 2026-10-18

This checkpoint implements the canonical tenant Instance Resources page on the exact item/Office backend prerequisite from `f112d107db98ddd0f6cd817c30ed05894d1ea4f8`. It advances Phase 7 Instance configuration; it does not accept the full multi-tenant IAM/durable-approval proposal or a milestone. Sandbox, other resource Access/effect families, Governance/platform, voice/rich Chat delivery, live input arbitration, autonomous origins and obsolete-authority/schema consolidation remain open. The prior SQLite direct-preference reliability follow-up remains tracked in the [delivery ledger](multitenant-iam-delivery-2026-10-16.md).

## Delivered behavior

`/t/{tenantId}/admin/agents/{instanceId}/resources` uses permission-filtered server metadata paging/search and exact item reads, with independent typed create/manage/delete/copy hints. Paths, origin, kind, bytes, media type, effective versus stored choices, readable dependencies and exact copy provenance remain inspectable. Item access does not imply sibling creation or IAM-inspector access. Archived/read-only views preserve permitted inspection.

The existing resource picker/import review, shared `InstanceResourceFields`/`InstanceResourceInspector`, native Drawer/confirmation and shared file renderers own presentation for the tenant and existing administrative consumers. File/folder bytes remain staged until explicit Add resources. Import preflight requires a coherent complete authorized manifest, including capacity beyond the editor's 100-item page. Sequential writes advance confirmed witnesses, retire acknowledged files and stop before a successor when a successful response cannot be read. Explicit recovery submits only the remaining files. Metadata-only edits preserve bytes and untouched enabled state; reload retains a draft, while explicit Discard adopts saved values. Copy records the exact source and preserves its current enabled choice. Dependencies are server-validated without implicit child changes.

The closed `tenantResource` file source uses canonical cookie/no-store metadata, bounded verified content, policy and Office reads. It never falls back to global owner services. Revoked nested preview/range/download reads retire protected detail and cached rows. Tenant/Instance retirement aborts old work; native Cancel/Escape, dirty guards and surviving-opener/fallback focus remain in use. Shared feedback placement includes marked body actions in the frontmost Drawer/modal, preserving Download and preview controls under hover-paused notices. Asynchronous actions and Word/image zoom handles retain explicit accessible names.

## Focused local checks

All final processes completed successfully:

| Check | Result |
| --- | --- |
| Eleven affected Vitest files: tenant Resources component/service/source, shared FilePreview/fileSource/officeSource/previewOptions/import, route, identity and feedback geometry | **67 passed**, 46.62s |
| `CI=1 TENANT_CHAT_API_PORT=61317 TENANT_CHAT_WEB_PORT=61318 pnpm run test:e2e:tenant-instance-resources` | **5 passed**, 40.5s; three tenant journeys and two existing administrative import/resource consumers |
| `pnpm run build` | TypeScript and production bundle passed; Vite built in 5.59s |
| Documentation checks | 848 local links across 12 changed/new Markdown files; zero missing targets/anchors, balanced fences; design JSON parsed, all 15 new resource provenance hashes matched and incumbent frontmatter/history preserved |
| `git diff --check` | Passed |

`CI=1` above is a local runner environment flag; no hosted CI was requested, dispatched, rerun or awaited. No full local CI suite ran. No backend source/schema changed in this checkpoint, so backend acceptance was not repeated; the [exact resource/Office backend evidence](tenant-resource-previews-verification-2026-10-18.md) remains separate. Existing bundle-size/Rollup annotation advice remains non-failing.

The final Vitest files were `TenantAgentResources.test.tsx` (9), `tenantAgentResources.test.ts` (4), `tenantResourceSource.test.ts` (6), `FilePreview.test.tsx` (9), `fileSource.test.ts` (4), `officeSource.test.ts` (13), `previewOptions.test.ts` (2), `resourceImportPanel.test.tsx` (5), `accessRoute.test.ts` (6), `identity.test.ts` (5), and `appFeedbackPlacement.test.ts` (4).

### Executed browser expectations and observations

1. At 320px, page inherited metadata, stage/save a local text file, inspect and render it, download and compare actual bytes, close with Escape and recover opener focus. Edit its path, Keep editing after Cancel, change conversation language independently, reject the stale save, Reload retaining the path, then explicitly save and delete. Disable/reset an inherited workbook, disable it again and review/copy a separate destination. Cancel returns focus to Review copy; explicit Copy leaves the source disabled, creates an enabled independent local file with exact provenance, and renders XLSX cell B4 as `$1,234.50`. No document overflow, page errors or owner/global fallback requests occurred in this journey.
2. Stage three files. The first saves, the second receives an injected pre-commit 503 and the third remains unsubmitted. Reload retains only the remainder. The second then really commits but receives a deliberately malformed success body; it retires from the draft and blocks the third until another explicit Reload. Final request order is first, second failed, second committed, third. Three saved local files retain their exact bytes; acknowledged files are never replayed.
3. A separately registered minimal member has exact parent and one resource read, without IAM inspection or sibling/create/manage access. The workbook preview renders its actual B4 value. Independently revoke the exact resource assignment, then request a new worksheet range: both preview and inspector close, the protected row disappears, and focus automatically returns to Reload resources. Browser traffic contains no owner/global or IAM-inspector fallback.
4. The two existing administrative resource journeys still pass metadata/replacement byte preservation, staged folder paths, partial import recovery, inherited built-in inspection/choices and Instance kind restrictions after shared field extraction.

Component/transport cases additionally exercise complete-manifest capacity/coherence, acknowledged-write recovery, independent drafts/discard, omitted availability, late-scope abort, authenticated bounded bytes/Office/policy recovery and existing shared consumers.

## Playwright MCP use cases and visual review

A separate fresh Development/Synthetic InMemory host on API 61315 and Vite 61316 used disposable isolated roots and dummy accounts. It exercised native mouse/keyboard interactions for staged local upload, original-byte preservation after metadata edits, independently induced 409 and exact draft recovery, inherited choice/reset/copy, actual folder imports, canonical downloads, actual XLSX/Word/image rendering, and named keyboard zoom. DOCX embedded image inspection observed 640×360 pixels; the image preview used the same existing fixture. Actual archive disabled changes while retaining reads; restore returned normal configuration. A separate exact-resource reader rendered the local workbook; independent grant revocation followed by a range request retired nested protected content and the cached row. The denied-state screenshot positions focus manually; automatic fallback focus is established by the final browser regression instead.

Representative fixtures were named `Operations — Resources review` and `Alex — Regional operations and customer care`, with thirteen inherited resources, long logical paths and local Office/image/text files. No live provider credentials, customer data, deployed services or persistent user data were used. The task-owned API/Vite handles were stopped after verification.

Twenty-five ignored captures live under `.playwright-mcp/tenant-resources-ui-20261018/`:

- `resources-{1440,1024,768,390,320}-top.png`, `resources-320-row.png`, `resources-320-pagination.png`.
- `resources-editor-1440-top.png`, `resources-editor-320-{top,lower}.png`.
- `resources-import-1440-top.png`, `resources-import-320-{top,lower}.png`.
- `resources-copy-confirm-{1440,320}.png`, `resources-provenance-1440.png`.
- `resources-xlsx-{1440,320}.png`, `resources-docx-320.png`, `resources-image-320.png`.
- `resources-empty-320.png`, `resources-archived-320.png`, `resources-readonly-320-{top,lower}.png`, `resources-denied-320.png`.

The shared form measured 768/744/736/366/296px at 1440/1024/768/390/320px, with 16px section gaps and no root horizontal overflow. Lower/row/pagination/empty images intentionally scroll the main area; the persistent header remains. DOCX was deliberately viewed at 50% zoom. The empty-state and desktop confirmation captures were corrected when they initially missed the intended content/settled modal. All final captures were opened and validated by the parent and fresh reviewer.

The fresh Impeccable finish reviewer returned **`disposition: ship`**, with all five contracted sections, all 25 evidence captures valid and no material fixes. Typography, material, ground, frame, responsive rows, editor/import, confirmation, preview and distinct state notices matched the incumbent system. The narrow design documentation handoff records observed reusable patterns; no token, theme, font, CSS system, UI kit or asset family was added.

Loading, transient-error, stale-error and malformed-acknowledgement states were functionally exercised where stated but were not captured for visual acceptance. The final source hook suppressed additional hints on the repeatedly edited resource page; no second detector was run and no universal final detector pass is claimed. This bounded review is not accessibility certification or full proposal acceptance.

## Failures found and corrected

Initial focused tests exposed a JSDOM Blob fixture incompatibility and an assertion that raced parent retirement; the fixtures/assertions were corrected. Initial TypeScript rejected a Testing Library-only use of Playwright's `exact` option; corrected before the passing build. Earlier browser attempts found a real hover-paused feedback obstruction over inspector Download and an unstable native loading-icon accessible name after stale recovery. Both were fixed in their shared/action owners, without forced clicks or arbitrary waiting, and the final five journeys pass. Cancel focus, disabled-source Copy wording and named image/document zoom were corrected and exercised. Native browser HMR retained the prior shared feedback effect during development; hard reload established fresh effects, and final CLI journeys used fresh pages.

Intentional 409 conflict and 403 revocation responses are expected evidence, not a claim of zero MCP console errors. Initial interrupted failing browser runs and build/test failures are retained in task logs; the final successful checks above supersede them. No behavior or full-proposal acceptance is inferred from screenshots or compilation alone.
