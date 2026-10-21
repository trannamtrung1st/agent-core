# File-client authority cutover — 2026-10-21

The shared preview reader now accepts only explicit tenant Workspace (Home/Working), Resource, Attachment and Artifact references. Unscoped Home/Resource, draft-resource and remote Drive variants are removed. All retained metadata, byte, Office and policy reads use canonical cookie/no-store tenant routes. Artifact SDK calls require the tenant; the global metadata cache and global policy promise/cache are removed. Missing-scope cards/collections make no file requests and expose no actionable private labels.

The existing preview limits, streamed bounds, immutable length/hash checks, status classification, Office recovery, original downloads, cancellation and Blob URL cleanup remain shared. This module changes the client boundary; it adds no backend authority or alternative viewer.

## Renderer and intake corrections

`HighlightedCode` previously fetched global policy when its caller supplied none, including from Chat Markdown. `MarkdownMessage` now passes its tenant or the enclosing preview's authorized policy. Scoped policy reads abort on owner replacement and ignore late results. An unscoped renderer displays escaped plain code without policy/worker requests. A delayed alpha→beta policy test verifies retirement and no late worker invocation.

A native narrow repeat also exposed an existing first-file intake race. File selection was creating its conversation before an upload tile existed; Send could still be clicked during that interval. The draft and pending original remained, but no message was dispatched. The Composer now counts pending intake promises and fences Send, Enter and form submission until they settle. Existing service upload/admission readiness remains authoritative. A deferred-promise regression checks no dispatch while intake is pending and one dispatch after it resolves. Both rapid native journeys then pass, including the new Working copy read steps. The failed pre-fix repeat is recorded in `/private/tmp/file-client-cutover/file-final.log`.

## Actual browser outcomes

`tenant-file-reads.spec.ts` uses canonical HTTP only for private fixture setup and native controls for the journey. At 1440×900 and 390×900 it selects/uploads a Markdown original, sends text and observes the completed Synthetic answer. It previews escaped code/Markdown, downloads exact original bytes, confirms Working/Artifact creation, then previews and downloads the Working file and immutable Artifact bytes. An obsolete token is seeded; no owner endpoint/header is requested.

The Artifact content response is deliberately replaced once with 403 to qualify client recovery. The card removes its private name and download action, then explicit Retry refreshes metadata and another explicit download returns the correct canonical filename and byte-exact original. Each flow has one expected 403 console/response entry, zero page exceptions and no other failing responses. This is a controlled denial, not a claimed server revocation test.

Separately, an actual minimally authorized Resource reader opens XLSX with the shared native viewer and then loses the exact resource grant; the nested protected view retires after real server denial. A 320px Resource owner imports, previews, downloads, edits, copies and deletes with exact stale recovery. These unchanged canonical backend paths run through the modified shared file reader.

MCP navigation was attempted against the disposable Synthetic app and returned `Transport closed`. CLI is the practical fallback; no successful MCP flow, physical audio or hosted provider result is claimed.

## Verification and retirement

`web/src/test/fixtures/retired-file-client-contracts.json` records baseline `ef400082`, the closed source variants and two retired owner-cache declarations. Stream/policy/Office/card/download/cancellation coverage is ported to canonical scopes. No test is skipped and no owner request shim/server is introduced.

The initial scoped unit command passed all 68 cases in its 12 non-card files; two old Attachment card fixtures failed because they omitted tenant scope. Corrected/extended cards and renderer tests pass below. An intermediate renderer fixture returned an empty token array; it now supplies valid tokens while preserving the owner-retirement assertions. These were fixture changes, not relaxed production authorization.

| Focused check | Result | Evidence |
| --- | --- | --- |
| Preview policy/Office/source/worker/renderer helpers, Artifact HTTP and Markdown: 12 non-card files selected from the initial scoped run | 68 passed | `/private/tmp/file-client-cutover/unit.log` (the same initial command also reported the two Attachment fixture failures) |
| Final AttachmentPreview, ArtifactView, SessionArtifacts, HighlightedCode | 23 passed, four files, 3.59s | `/private/tmp/file-client-cutover/final-cards.log` |
| Conversation, AttachmentMaterializeAction, TenantBackgroundRecovery, TenantWorkspaceFiles | 51 passed, four files, 26.52s | `/private/tmp/file-client-cutover/consumers.log` |
| Composer, including deferred first-file intake | 25 passed, 3.10s | `/private/tmp/file-client-cutover/composer-intake.log` |
| Final desktop/narrow original/Working/Artifact journey | 2 passed, 16.1s | `/private/tmp/file-client-cutover/file-intake-final.log` |
| Actual Resource reader/revocation journey | 1 passed, 9.7s | `/private/tmp/file-client-cutover/resource-runtime.log` |
| 320px Resource owner journey | 1 passed, 16.0s | `/private/tmp/file-client-cutover/resource-owner.log` |
| `pnpm exec tsc --noEmit` | Passed | `/private/tmp/file-client-cutover/typecheck-final.log` |
| `pnpm run build` | Passed; existing bundle-size warning, Vite 5.45s | `/private/tmp/file-client-cutover/build-final.log` |
| `pnpm run test:e2e:tenant-file-reads --list` | Two tests collected through the committed focused config/script | `/private/tmp/file-client-cutover/config-list.log` |

There are 167 distinct passing focused frontend cases across these groups and four final native browser journeys. Vitest runs use `NODE_OPTIONS=--no-experimental-webstorage`; the initial command selects `src/features/filePreview`, `src/services/artifacts.test.ts` and the four affected Chat test files. Disposable CLI configs under `/private/tmp/file-client-cutover/` import the repository's Synthetic config, use an existing API binary with `--no-build --no-restore`, and isolate ports/data. No user hosts/data were reused/reset. No C# sources, styles, fonts, assets, theme or design policy changed. No new Impeccable review/detector result is claimed; `.impeccable/design.json` SHA-256 remains `8bdc3659a25b706454ac8cc0394323c5e0e60ea1ade616756b180efbaecef49b`.

## Remaining full-goal work

This is the shared preview/Artifact-reader checkpoint, not full client or proposal acceptance. Core Chat/realtime and Attachment upload/list/staging helper token plumbing still remain, together with unscoped Activity/command defaults and URL builders, internal SessionHost/approval/workers, schema/DTO cleanup, remaining platform guardrails, canonical feature parity (including absent draft-resource/Drive preview surfaces) and exhaustive proposal qualification. CI/full local CI were not requested or run. The original proposal goal stays active.
