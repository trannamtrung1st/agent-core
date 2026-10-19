# Tenant Browser file publication UI qualification — 2026-10-20

This bounded slice connects canonical capture/download publication facts to the shared organization/Chat Approval details inspector. It completes the dedicated result presentation left after [backend/model/native publication qualification](tenant-browser-artifacts-verification-2026-10-20.md); it does not complete the full IAM proposal or claim milestone acceptance.

## Behavior and ownership

The existing Operation result continues to report native command completion separately from application/business verification. A Browser files section distinguishes full publication, partial publication with a safe reason, an empty publication, and unavailable publication. Legacy receipts without publication facts acquire no inferred empty/success state. Partial/unavailable outcomes offer no replay. Technical references retain exact file IDs without interpreting them as access authority.

`Inspect published files` deliberately loads the existing `ArtifactView` composition using the actual tenant/Session/Artifact scope. No metadata read occurs merely from showing publication facts. The incumbent card, file preview and download independently reauthorize current recipient access. Refresh retires inspected metadata and cancels its in-flight reads; request/tenant changes also replace the subtree. Denied metadata shows File unavailable and Retry without a filename or preview/download control. Publication facts remain separate from that recipient failure.

Presentation preserves the dark Ant Design v6 Drawer, one-column responsive descriptions, shared confirmation, literal parameter summary, wrapping footer and existing spacing. Measured file-section gap is 8px, outer section gap 16px and Drawer body inset 24px. No CSS, token, UI-kit, public debug endpoint or shipping raster asset is added. [UX owner](../19-multi-tenant-iam-ux.md#browser-capture-and-download-facts), [frontend owner](../13-frontend-implementation-spec.md#tenant-browser-review-inspector-compatibility).

## Functional execution

Playwright MCP operated a dedicated Vite app at `http://127.0.0.1:5234` against disposable loopback Kestrel fixtures. `/health` confirmed Synthetic. Tests used actual local native Chromium, owned IAM/Run/Session/Artifact stores and ScriptedLanguageModel; no hosted model or external account was used. The fixture exposes no product test endpoint. The opt-in proof handshake pauses the existing capture integration case before the human decision and after its result, with an 18-minute timeout, then disposes its data/Browser profiles.

| Journey and expected result | Observed result |
| --- | --- |
| Sign in, inspect saved capture parameters, approve through native confirmation | Exact image publication choice, privacy mode and byte/task limits visible; decision POST 200; approval remains separate from execution |
| Refresh recorded capture | Command completed/application-unverified and Files published/one captured image shown separately; zero Artifact API requests before deliberate inspection |
| Inspect, preview and download | Scoped metadata 200; filename `screenshot.png`, 37.8 KB; shared preview loaded a complete 1280×720 image; content 200; native download saved a valid 38,689-byte 1280×720 PNG (SHA-256 `10827296f26ffa8d51241b906b3881aece3c9ac604df25201a66dafbff87eadf`) |
| Refresh after file inspection | Card/filename removed; Inspect published files returned; no automatic replay or file read |
| Open original conversation, Review details | Actual historical Synthetic response and same shared publication inspector; no replacement Chat consent/result implementation |
| Approve a second actual native capture with executor Session read/create role revoked after SDK completion | Native outcome remained Command completed; publication unavailable and no-replay guidance; no Artifact references/body/metadata requests, no published Artifact in storage |
| Resize shared presentation | 1440/1024/768/390/320px inspected; mobile controls, filename actions and footer remained reachable; measured document overflow false in sampled wide/narrow states |

Active journey routes produced no console errors/warnings. React development lifecycle/request retirement produced cancelled requests followed by current successful reads. Initial anonymous/retired-cookie navigation produced expected authentication responses; those are not publication failures.

## Focused automated checks

- `NODE_OPTIONS=--no-experimental-webstorage pnpm run test --run src/features/access/TenantApprovals.test.tsx src/features/chat/ArtifactView.test.tsx src/features/filePreview/tenantArtifactSource.test.ts` — **72 passed**. Includes full/partial/empty/unavailable/unknown-reason/legacy receipt presentation, deliberate current scoped reads, denied-read retry, read-state retirement, and incumbent Artifact preview/download read-loss behavior. Existing AntD/jsdom NaN height warning appeared; no test failed.
- `NODE_OPTIONS=--no-experimental-webstorage pnpm run build` — **passed**; incumbent chunk-size advisory remains.
- `AGENTCORE_CHROMIUM_ISOLATION_TESTS=1 AGENTCORE_BROWSER_FILES_UI_PROOF=published dotnet test tests/AgentCore.Api.Tests/AgentCore.Api.Tests.csproj --filter FullyQualifiedName~Tenant_Browser_model_capture_HTTP --nologo` — **6 passed**, no skips. Covers original memory/SQLite capture and per-generation read-loss cases plus two new native-completed/publication-unavailable cases. The published case was decided through MCP while the host was held.
- The same focused command with `AGENTCORE_BROWSER_FILES_UI_PROOF=unavailable` — **6 passed**, no skips. Its controlled revocation case was decided and inspected through MCP. Repeated cases count once: **78 distinct automated cases** across the bounded checks.
- Impeccable detector ran once on the two changed product components: **`[]` findings**. Final whitespace check passed; **661 relative links across six changed Markdown files** resolved with no missing targets/fragments.

The first native attempt skipped because its explicit Chromium opt-in was absent. The first opted-in proof needed loopback cookie settings and the test expectation corrected to the canonical safe generic publication-unavailable reason. Those fixture issues were resolved before the passing published/native journey. No provider, production authority or publication semantics were weakened.

## Presentation evidence

All captures are actual current local app screenshots under [the evidence directory](assets/tenant-browser-files-ui-2026-10-20/). Desktop/tablet heights are 1000px; mobile heights are 844px. The independent review is scoped to these captured states; empty and partial publication have component-test coverage, not rendered native UI acceptance.

| State | Captures |
| --- | --- |
| Published facts before file inspection | [1440](assets/tenant-browser-files-ui-2026-10-20/published-1440.png) |
| Published file card | [1440](assets/tenant-browser-files-ui-2026-10-20/inspected-1440.png), [1024](assets/tenant-browser-files-ui-2026-10-20/inspected-1024.png), [768](assets/tenant-browser-files-ui-2026-10-20/inspected-768.png), [390](assets/tenant-browser-files-ui-2026-10-20/inspected-390.png), [320](assets/tenant-browser-files-ui-2026-10-20/inspected-320.png) |
| Shared image preview | [1440](assets/tenant-browser-files-ui-2026-10-20/preview-1440.png) |
| Same inspector opened from Chat | [1440](assets/tenant-browser-files-ui-2026-10-20/chat-1440.png), [320](assets/tenant-browser-files-ui-2026-10-20/chat-320.png) |
| Native completed, publication unavailable | [1440](assets/tenant-browser-files-ui-2026-10-20/unavailable-1440.png), [320](assets/tenant-browser-files-ui-2026-10-20/unavailable-320.png) |

## Remaining qualification

The fresh independent finish reviewer returned **`disposition: ship`**, individually viewing all eleven current captures. No material fixes were requested. Its verdict covers the captured outcome/preview/responsive presentation; top identity/consent was outside that visual verdict. The independent documenter confirmed preservation of the incumbent DESIGN/sidecar, reviewing current source/theme/CSS and four captures: inspected-1440, inspected-320, unavailable-320 and preview-1440. No new visual-system rule or token was needed. Its README status correction is included. Pre-existing design-sidecar drift remains unchanged. No Real provider, Docker, live download website, actual 200% browser zoom, assistive-technology/device or hosted CI acceptance is claimed. Dedicated rendered empty/partial publication and recipient-denied UI states remain outside this visual verdict; focused component tests cover their behavior. Artifact-backed drops, Browser privacy/reset/decommission/autonomous origins, remaining effect/resource/cleanup families, voice/rich Chat/live input arbitration, autonomous/transport integration, platform/system guardrails, audit/reconciliation/notifications, final isolation gates and obsolete-authority/schema cutover remain open. The full original goal remains active.
