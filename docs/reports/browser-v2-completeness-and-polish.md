# Browser v2 completeness and polish audit

Audit date: 2026-10-08. Repository starting point: `8d845972`. This is a targeted follow-on to the implemented Browser v2; historical migration evidence remains unchanged.

## Reference inventory

The comparison uses the official [Playwright MCP inventory at `a6d7678b7bc10d9fb2ae828a103e9872cf75e483`](https://github.com/microsoft/playwright-mcp/blob/a6d7678b7bc10d9fb2ae828a103e9872cf75e483/README.md#tools), package `0.0.83`: all **72 named operations** are accounted for below. Native controls were checked against [Playwright .NET emulation](https://playwright.dev/dotnet/docs/emulation), [Browser context options](https://playwright.dev/dotnet/docs/api/class-browser#browser-new-context), [permissions](https://playwright.dev/dotnet/docs/api/class-browsercontext#browser-context-grant-permissions), [media emulation](https://playwright.dev/dotnet/docs/api/class-page#page-emulate-media) and the [installed 1.63.0 SDK definitions](https://github.com/microsoft/playwright-dotnet/tree/v1.63.0/src/Playwright/API/Generated). Upstream inventory is an audit reference, not a dependency or Core authorization policy.

## Coverage matrix

**Provider compatibility:** `P/<feature>` means the first-party Chromium provider advertises that neutral feature; partial providers must advertise it independently. `Unsupported/<feature>` means the neutral capability exists but this provider does not advertise it. `—` means no Core capability exists. Advertised browser actions still require readiness, Definition grants, contextual projection and execution-time policy. Safe Configuration inspection requires the provider feature and the same Definition/context authorization, and remains available when the engine is disabled or not ready. **Implement** includes retaining a working equivalent. **Privileged** identifies exact authority requirements; it does not imply a new implementation. Other decisions apply to the missing portion of a partial row.

| Playwright MCP operation | Agent Core equivalent | Support | Provider compatibility | Security / authorization | Gap / boundary | Decision |
| --- | --- | --- | --- | --- | --- | --- |
| `browser_click` | browser.click | Implemented | P/Click | Write | Double click, buttons and modifiers covered. | Implement |
| `browser_close` | browser.close | Implemented | P/Close | Write / idempotent close | Closes live context; retains owner profile. | Implement |
| `browser_console_messages` | browser.console_messages | Bounded subset | P/Console | ReadOnly | 50 redacted messages; arbitrary file output and unbounded history excluded. | Implement |
| `browser_drag` | browser.drag | Implemented | P/Drag | Write | Two opaque targets; no model selectors. | Implement |
| `browser_drop` | browser.drop; browser.upload | Partial | P/Drop / Upload | Write | Text MIME drop and approved upload work; Artifact-backed drop deferred. | Deferred |
| `browser_emulate_media` | browser.emulate_media | Enhanced | P/Media | Write | Preserves omissions; null reset, contrast and forced colors added. | Implement |
| `browser_evaluate` | browser.evaluate (unsupported) | Disabled | Unsupported/Evaluate | SensitiveWrite; attached user only | No isolated, redacted evaluation boundary. | Privileged |
| `browser_file_upload` | browser.upload | Bounded subset | P/Upload | Write | Approved resources only; arbitrary host paths and implicit chooser authority excluded. | Implement |
| `browser_fill_form` | browser.fill_form; browser.select_option | Implemented | P/FillForm / SelectOption | Write | Text, textarea, contenteditable, checkbox/switch and selects covered. | Implement |
| `browser_find` | browser.find | Implemented | P/Find | ReadOnly | Full permitted index; bounded regex and 20 results. | Implement |
| `browser_handle_dialog` | browser.dialog | Implemented | P/Dialog | Write; inspect read path | Pending alert/confirm/prompt inspect, accept and dismiss. | Implement |
| `browser_hover` | browser.hover | Implemented | P/Hover | Write | Native Locator actionability. | Implement |
| `browser_navigate` | browser.navigate (goto) | Implemented | P/Navigate | ReadOnly; destination policy | HTTP(S) only; redirects checked. | Implement |
| `browser_navigate_back` | browser.navigate (back) | Implemented | P/Navigate | ReadOnly; destination policy | Forward and reload also supported. | Implement |
| `browser_network_request` | browser.network_request / network_requests | Bounded subset | P/NetworkInspect | ReadOnly | 50 requests, method/type/origin only. Raw headers, bodies and paths deliberately excluded. | Intentionally Excluded |
| `browser_network_requests` | browser.network_request / network_requests | Bounded subset | P/NetworkInspect | ReadOnly | 50 requests, method/type/origin only. Raw headers, bodies and paths deliberately excluded. | Intentionally Excluded |
| `browser_press_key` | browser.press_key | Implemented | P/PressKey | Write | Bounded keyboard combinations, optional target. | Implement |
| `browser_resize` | browser.resize | Implemented | P/Resize | Write | 320–1920 by 240–1080 CSS pixels. | Implement |
| `browser_run_code_unsafe` | None | Excluded | — | Host execution privilege | Server-process code bypasses provider, resource and credential boundaries. | Intentionally Excluded |
| `browser_select_option` | browser.select_option | Implemented | P/SelectOption | Write | Multi-select and native values. | Implement |
| `browser_snapshot` | browser.snapshot | Implemented | P/Snapshot | ReadOnly | Native ARIA; subtree/depth/boxes; full internal index. | Implement |
| `browser_take_screenshot` | browser.screenshot | Bounded subset | P/Screenshot | ReadOnly; Artifact/vision policy | Masked viewport/target/full-page png/jpeg/webp. Arbitrary output paths excluded. | Implement |
| `browser_type` | browser.type | Enhanced | P/Type | Write; ordinary fields only | Sequential keyboard-event typing added; empty clears, submit covered. | Implement |
| `browser_wait_for` | browser.wait_for | Implemented | P/Wait | ReadOnly | State/text/load/URL/stability waits; fixed sleeps intentionally replaced by evidence. | Implement |
| `browser_tabs` | browser.tabs | Implemented | P/Tabs | Write; list read path | List/new/select/close with opaque refs. | Implement |
| `browser_get_config` | browser.get_config; Admin effective configuration | Added | P/Configuration | ReadOnly | Safe active environment, provider/engine/readiness/features and restriction summaries. | Implement |
| `browser_network_state_set` | browser.network_state | Implemented | P/NetworkControl | SensitiveWrite / exact approval | Online/offline context state. | Implement |
| `browser_route` | browser.route / routes / unroute | Bounded subset | P/NetworkControl | SensitiveWrite; list ReadOnly | 16 exact allowed URL rules; glob routes intentionally excluded. | Privileged |
| `browser_route_list` | browser.route / routes / unroute | Bounded subset | P/NetworkControl | SensitiveWrite; list ReadOnly | 16 exact allowed URL rules; glob routes intentionally excluded. | Privileged |
| `browser_unroute` | browser.route / routes / unroute | Bounded subset | P/NetworkControl | SensitiveWrite; list ReadOnly | 16 exact allowed URL rules; glob routes intentionally excluded. | Privileged |
| `browser_cookie_clear` | browser.cookies (clear/delete/list) | Bounded subset | P/Storage | SensitiveWrite / exact approval | Current-origin metadata/deletion; values withheld. | Privileged |
| `browser_cookie_delete` | browser.cookies (clear/delete/list) | Bounded subset | P/Storage | SensitiveWrite / exact approval | Current-origin metadata/deletion; values withheld. | Privileged |
| `browser_cookie_get` | browser.cookies list; secure fill_credential sink | Excluded value surface | P/Storage | Privileged storage | Raw authentication cookie reads/writes bypass the credential sink. | Intentionally Excluded |
| `browser_cookie_list` | browser.cookies (clear/delete/list) | Bounded subset | P/Storage | SensitiveWrite / exact approval | Current-origin metadata/deletion; values withheld. | Privileged |
| `browser_cookie_set` | browser.cookies list; secure fill_credential sink | Excluded value surface | P/Storage | Privileged storage | Raw authentication cookie reads/writes bypass the credential sink. | Intentionally Excluded |
| `browser_localstorage_clear` | browser.local_storage (clear) | Bounded subset | P/Storage | SensitiveWrite / exact approval | Current origin; bounded redacted reads, protected-key writes forbidden. | Privileged |
| `browser_localstorage_delete` | browser.local_storage (delete) | Bounded subset | P/Storage | SensitiveWrite / exact approval | Current origin; bounded redacted reads, protected-key writes forbidden. | Privileged |
| `browser_localstorage_get` | browser.local_storage (get) | Bounded subset | P/Storage | SensitiveWrite / exact approval | Current origin; bounded redacted reads, protected-key writes forbidden. | Privileged |
| `browser_localstorage_list` | browser.local_storage (list) | Bounded subset | P/Storage | SensitiveWrite / exact approval | Current origin; bounded redacted reads, protected-key writes forbidden. | Privileged |
| `browser_localstorage_set` | browser.local_storage (set) | Bounded subset | P/Storage | SensitiveWrite / exact approval | Current origin; bounded redacted reads, protected-key writes forbidden. | Privileged |
| `browser_sessionstorage_clear` | browser.session_storage (clear) | Bounded subset | P/Storage | SensitiveWrite / exact approval | Current origin; bounded redacted reads, protected-key writes forbidden. | Privileged |
| `browser_sessionstorage_delete` | browser.session_storage (delete) | Bounded subset | P/Storage | SensitiveWrite / exact approval | Current origin; bounded redacted reads, protected-key writes forbidden. | Privileged |
| `browser_sessionstorage_get` | browser.session_storage (get) | Bounded subset | P/Storage | SensitiveWrite / exact approval | Current origin; bounded redacted reads, protected-key writes forbidden. | Privileged |
| `browser_sessionstorage_list` | browser.session_storage (list) | Bounded subset | P/Storage | SensitiveWrite / exact approval | Current origin; bounded redacted reads, protected-key writes forbidden. | Privileged |
| `browser_sessionstorage_set` | browser.session_storage (set) | Bounded subset | P/Storage | SensitiveWrite / exact approval | Current origin; bounded redacted reads, protected-key writes forbidden. | Privileged |
| `browser_set_storage_state` | browser.storage_state (unsupported) | Deferred | Unsupported/StorageState | Privilege required; unadvertised | Import/export includes authentication material; safe owner-scoped format absent. | Deferred |
| `browser_storage_state` | browser.storage_state (unsupported) | Deferred | Unsupported/StorageState | Privilege required; unadvertised | Import/export includes authentication material; safe owner-scoped format absent. | Deferred |
| `browser_annotate` | Masked screenshots + highlight (no drawing UI) | Deferred | — | Privilege required for annotated output | No manual annotation/dashboard workflow in current conversational UI. | Deferred |
| `browser_hide_highlight` | browser.highlight (hide/show) | Enhanced | P/Highlight | Write; opaque target policy | Removal added; arbitrary CSS excluded. | Implement |
| `browser_highlight` | browser.highlight (hide/show) | Enhanced | P/Highlight | Write; opaque target policy | Removal added; arbitrary CSS excluded. | Implement |
| `browser_resume` | None | Deferred | — | Privileged developer control | Paused scripts could stall mailbox workers; no debug ownership model. | Deferred |
| `browser_start_recording` | generate_locator and verify only | Deferred | — | Privileged capture | Recording can capture secrets; locator hints do not constitute action recording or test generation. | Deferred |
| `browser_start_tracing` | browser.trace (unsupported) | Deferred | Unsupported/Trace | Privilege required; unadvertised | Trace DOM/network payloads cannot meet current secret/output boundary. | Deferred |
| `browser_start_video` | browser.video (unsupported) | Deferred | Unsupported/Video | Privilege required; unadvertised | Continuous pixels and action labels lack protected-output and retention guarantees. | Deferred |
| `browser_stop_recording` | generate_locator and verify only | Deferred | — | Privileged capture | Recording can capture secrets; locator hints do not constitute action recording or test generation. | Deferred |
| `browser_stop_tracing` | browser.trace (unsupported) | Deferred | Unsupported/Trace | Privilege required; unadvertised | Trace DOM/network payloads cannot meet current secret/output boundary. | Deferred |
| `browser_stop_video` | browser.video (unsupported) | Deferred | Unsupported/Video | Privilege required; unadvertised | Continuous pixels and action labels lack protected-output and retention guarantees. | Deferred |
| `browser_video_chapter` | browser.video (unsupported) | Deferred | Unsupported/Video | Privilege required; unadvertised | Continuous pixels and action labels lack protected-output and retention guarantees. | Deferred |
| `browser_video_hide_actions` | browser.video (unsupported) | Deferred | Unsupported/Video | Privilege required; unadvertised | Continuous pixels and action labels lack protected-output and retention guarantees. | Deferred |
| `browser_video_show_actions` | browser.video (unsupported) | Deferred | Unsupported/Video | Privilege required; unadvertised | Continuous pixels and action labels lack protected-output and retention guarantees. | Deferred |
| `browser_mouse_click_xy` | browser.mouse; semantic browser.click | Bounded subset | P/VisionMouse / Click | Write + vision capability | Coordinate actions use the left button. Semantic click covers buttons/multiple clicks; canvas-specific button/count/delay options deferred. | Deferred |
| `browser_mouse_down` | browser.mouse; semantic browser.click | Bounded subset | P/VisionMouse / Click | Write + vision capability | Coordinate actions use the left button. Semantic click covers buttons/multiple clicks; canvas-specific button/count/delay options deferred. | Deferred |
| `browser_mouse_drag_xy` | browser.mouse | Implemented | P/VisionMouse | Write + vision capability | Viewport-bounded safe coordinate targeting; protected fields/frames denied. | Implement |
| `browser_mouse_move_xy` | browser.mouse | Implemented | P/VisionMouse | Write + vision capability | Viewport-bounded safe coordinate targeting; protected fields/frames denied. | Implement |
| `browser_mouse_up` | browser.mouse; semantic browser.click | Bounded subset | P/VisionMouse / Click | Write + vision capability | Coordinate actions use the left button. Semantic click covers buttons/multiple clicks; canvas-specific button/count/delay options deferred. | Deferred |
| `browser_mouse_wheel` | browser.mouse | Enhanced | P/VisionMouse | Write + vision capability | Safe x/y target plus signed deltaX/deltaY (±2000); upward/left scrolling fixed. | Implement |
| `browser_pdf_save` | browser.pdf (unsupported) | Deferred | Unsupported/Pdf | Protected-output privilege; unadvertised | PDF can include secret-bearing rendered DOM; redacted PDF pipeline absent. | Deferred |
| `browser_generate_locator` | browser.generate_locator | Implemented | P/Testing | ReadOnly | Informational only; cannot become selector authority. | Implement |
| `browser_verify_element_visible` | browser.verify | Implemented | P/Testing | ReadOnly; value sink guard | Visibility, text, ordinary value and checked-state assertions. | Implement |
| `browser_verify_list_visible` | snapshot/find + repeated verify | Composed subset | P/Snapshot / Find / Testing | ReadOnly | No atomic list-items assertion; bounded native list assertion deferred. | Deferred |
| `browser_verify_text_visible` | browser.verify | Implemented | P/Testing | ReadOnly; value sink guard | Visibility, text, ordinary value and checked-state assertions. | Implement |
| `browser_verify_value` | browser.verify | Implemented | P/Testing | ReadOnly; value sink guard | Visibility, text, ordinary value and checked-state assertions. | Implement |

## Native environment controls and related operations

| Operation | Agent Core equivalent | Support / provider | Security / authorization | Gap and decision |
| --- | --- | --- | --- | --- |
| Viewport | `browser.resize`; `Browser:Environment:ViewportWidth/ViewportHeight` | Runtime + host creation / P Resize | Ordinary Write + interaction policy | Implemented; bounded CSS dimensions. |
| Named device, touch, mobile layout, pixel ratio | `Browser:Environment:Device/IsMobile/HasTouch/DeviceScaleFactor` | Context creation / first-party P only | Host configuration | Implemented for ephemeral and persistent contexts. Device catalog stays inside provider. Runtime replacement deferred to preserve tabs, storage and profile ownership. |
| Locale / timezone | `Browser:Environment:Locale/TimezoneId` | Context creation / first-party P only | Host configuration | Implemented. Browser resolves locale independently of invariant .NET globalization. Runtime replacement deferred. |
| Geolocation | `browser.set_geolocation` set/clear | P Geolocation | SensitiveWrite, exact approval, attached UserTurn, explicit active allowed origin | Implemented; one origin grant at a time, no coordinate echo; no default grant. |
| Media / color / reduced motion / forced colors / contrast | `browser.emulate_media` | P Media | Ordinary Write + interaction policy | Implemented with omission preservation and individual null reset. Overrides belong to the active page, not all tabs. |
| Online/offline | `browser.network_state` | P NetworkControl | SensitiveWrite / exact approval | Existing control retained; config inspection reports active state. |
| Downloads | Action result → owner-scoped Artifacts | First-party P | Existing action authority + MIME/size/redaction policy | Implemented; no extra download tool needed. |
| Frames | Snapshot refs carry permitted frame ownership | First-party P Snapshot/actions | Navigation/read and interaction origins independently enforced | Implemented; no model frame selectors needed. |
| Forward/reload | `browser.navigate` operations | P Navigate | Destination policy | Already implemented; no new tools. |
| Other permissions, HTTP authentication, custom headers, TLS bypass, proxy, arbitrary initialization scripts | No ordinary model operation | Unavailable | Host/network or credential privilege | Intentionally excluded from ordinary tools; do not widen permissions or bypass System Credentials. Native local-network prompts may still block a destination Core allows. |

Configuration inspection returns provider, Chromium engine, runtime availability, advertised features, active context environment/viewport, page media overrides, online state, permission presence, policy modes, restriction counts and output limits. It does not start a context, require a page URL, read page JavaScript, echo coordinates, list private origins, or expose headers, credentials, cookies, executable/profile paths. Disabled or unavailable engines can be inspected without starting them. No active context means `contextOpen=false` and `environment=null`; host availability is not a guarantee that a later launch/navigation succeeds. Admin reuses existing effective configuration and adds the engine. Partial providers omit unsupported Configuration/Geolocation features normally.

## Targeted changes and decisions

The original audit found missing session inspection, context creation emulation options, origin-scoped geolocation, forced-color/contrast/reset controls, sequential typing, signed wheel scrolling and overlay removal. It also found an actual media bug: setting one value reset omitted values. Wheel scrolling also confused target coordinates with scroll deltas, preventing upward/left scrolling. These are closed through the current port/registry/provider. Context mutations keep the owner gate; an in-flight canceled permission command closes the context before releasing it so a late grant cannot become active afterward. A canceled page media/action command retains the existing page fence. Persistent bytes remain owned by the existing profile.

Native resource policy now guards WebSocket destinations using the corresponding HTTP(S) resource origin. Allowed message traffic forwards normally; no frame contents are exposed or editable. Service workers are blocked at creation because HTTP routing cannot inspect worker-handled requests. The host's configured Chromium channel now applies to ephemeral launches as it already did to readiness and persistent launches, closing a mismatch between advertised readiness and the actual engine process.

There is no new subsystem, browser runtime, selector authority, compatibility layer, schema migration or branch-specific tracked configuration. Existing immutable Definition versions are unchanged. `browser.get_config` and `browser.set_geolocation` are catalog capabilities that an operator can grant in a new Definition version through existing Selected capability authoring; loading never grants them. Existing general-assistant v17 / secretary v5 therefore retain their prior exact grants. No shipped default Definition grants geolocation, network or storage mutations, evaluation or capture extensions. Runtime permission grants begin empty. Privileged operations stay separate from ordinary tools.

## Advanced capabilities

| Capability | Decision | Current boundary and reason |
| --- | --- | --- |
| Action recording / full test generation | Deferred | No recorder. Native locator hints and assertions are useful equivalents for their individual operations, not recording. Captured user actions can contain credentials; a safe recording/export and retention boundary is absent. |
| Manual visual annotations | Deferred | Existing masked screenshots and show/hide target highlights work. A drawing/dashboard workflow would introduce a new user surface and protected-output lifecycle. Arbitrary overlay CSS remains excluded. |
| Advanced trace and video, chapters/action overlays | Deferred | Trace/Video feature IDs exist but are unadvertised. DOM/network/pixel recording can retain secrets. Any later implementation needs separate privileged authorization, exact approval and protected artifacts before advertisement. |
| Debugger pause/resume/step | Deferred | No paused-script owner or durable recovery contract; cannot strand a Session worker or monopolize its browser context. |
| WebSocket message inspection/interception | Deferred | Only destination enforcement and normal forwarding implemented. Exposing/modifying frames requires payload redaction, explicit privilege and lifecycle bounds. |
| Service workers / CDP access | Intentionally Excluded | Workers are blocked to preserve resource policy. CDP exposes engine-specific unrestricted commands beyond the neutral port and credential sink. |
| WebMCP | Intentionally Excluded | Page-provided tools are untrusted and cannot create Definition grants, Core tools or approval authority. No dynamic page-tool execution subsystem. |
| JavaScript evaluation | Privileged, disabled | `Evaluate` remains unsupported and SensitiveWrite/attached-turn gated. No safe isolated evaluation result boundary; internal fixed provider scripts do not grant model script authority. |
| Arbitrary Playwright code | Intentionally Excluded | Host-process code execution bypasses filesystem, origin, credentials and provider negotiation. No ordinary or privileged code executor is added. |
| Coordinate button/count/delay options | Deferred | Semantic clicks already support buttons, modifiers and multiple clicks. Coordinate actions retain the safe left-button workflow; canvas-specific variants need native gesture/held-button boundary coverage before expansion. |
| PDF; raw storage state; Artifact-backed file drop; atomic list assertion | Deferred | PDF/storage require safe output/import boundaries. File drop needs the existing approved-resource pipeline extended to structured drop. Snapshot/find plus bounded assertions already support practical list inspection; no atomic list-items operation is claimed. |

## Verification ledger

Local operational results and completed hosted CI are recorded below. Historical full-cutover acceptance and its separately authorized live-model journey remain in [the original ledger](browser-v2-full-cutover-verification.md). This audit does not rerun or claim that pending hosted-model journey, and does not reopen P10/P11.

### Operational scenarios

- Real Chromium, both ephemeral and disposable persistent ownership: configure Pixel 5, `fr-FR` and `Europe/Paris`; observe native viewport/screen, locale/timezone, mobile UA, touch and DPR; resize without replacing the context or losing local storage. Set dark/print then reduced motion/forced colors/contrast; verify native CSS matches preserve prior settings; clear individual overrides with null. Toggle offline/online and verify `navigator.onLine` plus safe configuration output. Expected and observed: settings apply and retained state survives.
- Configuration admission with a disabled host and a missing-engine probe: authorized inspection reports `available=false`, `contextOpen=false`, Chromium engine and no profile path; no browser context is launched. Page navigation remains unconfigured and a Definition without the inspection grant is denied. Partial providers omit unsupported inspection/geolocation features. Expected and observed: diagnosis does not enable the browser or widen authority.
- Native keyboard/overlay and wheel behavior: sequential typing emits a/b/c keydown events and fills the ordinary field; highlight is removed. A generic scroll surface moves right/down and returns left/up with signed deltas; missing/unbounded distances are rejected. Expected and observed: real input/scroll behavior, not registration-only coverage.
- Geolocation: reject mismatched origins, origin paths, missing/out-of-range coordinates; grant one exact active origin, observe native coordinates only inside the test, verify a second allowed origin remains `prompt`, clear permission and reject a pre-canceled grant. Outputs do not echo coordinates; snapshot recovery succeeds.
- Resource policy: permitted native WebSocket delivers its message; denied destination never reaches its server (connection counts 1/0). The test grants Chromium's independent `local-network-access` permission only to its disposable fixture origin. Registration of a service worker yields no active worker/registration in either context mode. No production permission grant or message interception is added.
- Playwright MCP UI: create a temporary General Assistant instance in the isolated Synthetic host, open Effective configuration, and verify provider/engine/readiness/features alongside existing settings. Desktop 1440px, intermediate 768px and mobile 390px retain the shared AntD detail layout and wrap long values without horizontal overflow. Screenshots are ignored local evidence (`local/browser-completeness/admin-1440.png`, `admin-390.png`). Four initial stale-owner 401s recovered through the existing bootstrap; subsequent Admin calls returned 200/201 with no new console errors. Original checkout ports, database and browser profile remained untouched.

### Local checks

| Check | Result |
| --- | --- |
| `dotnet build AgentCore.sln --no-restore -v quiet` | Passed, zero warnings/errors at initial enhancement checkpoint; final solution test recompiles all affected projects. |
| Final complete backend `dotnet test AgentCore.sln --no-restore` | Domain 152, Application 1318, Infrastructure 831 and API 371, OrderEvents 4 passed: **2676 passes**, 20 skips (14 live opt-ins + six Docker prerequisite skips). Docker cases also passed separately as recorded below; no browser cases skipped. |
| Native `BrowserEnvironmentJourneyTests` | 7 passed, no skips: 2 inspection admission cases, 2 ephemeral/persistent environment cases, geolocation, WebSocket and signed wheel cases. |
| Browser adapter / environment focused regressions | 33 passed at the pre-wheel checkpoint; full solution covers the final source. |
| Docker sandbox prerequisite | Installed missing pinned `busybox:1.36`; all 8 DockerSandboxExecutorTests passed separately, including the six skipped by the broad gate (the image was absent again at that gate’s probe). No containers or unrelated images were pruned. |
| Owner-authorized Admin API tests | 79 passed, including safe additive engine mapping. |
| Affected Admin UI unit suite | 42 passed. |
| Complete frontend unit gate | 96 files / 739 tests passed with `NODE_OPTIONS=--no-experimental-webstorage pnpm run test --run --maxWorkers=1`. |
| Frontend production build | Passed; existing SignalR annotation / bundle-size notices remain. |
| Affected CLI Playwright | 9 passed: four Synthetic Admin polish cases plus five Browser STT/TTS fake-device cases. Disposable API/Vite ports 5280–5282 / 5373–5375, separate SQLite/profile roots, CI server lifecycle. |
| Documentation links/fences and `git diff --check` | Passed for changed canonical documents and the 72-operation report. |

The initial parallel frontend run had 738 passes and one 30-second timeout in the existing fractional-recurrence editor test under concurrent heavy Admin/.NET load. The fresh serial run using unchanged CI `--maxWorkers=1` passed all 739 tests across 96 files (632.94 seconds). No test assertions or timeouts were weakened. Live-provider tests remain explicit opt-in and were not run.

### Hosted required gates

All five jobs in [Synthetic run 37729115112](https://github.com/trannamtrung1st/agent-core/actions/runs/37729115112) **passed** on the exact implementation commit **`c5bc6143322559e818e290bd87934c2391bd32a4`**. The workflow checked out that PR head, not a generated merge commit. Subsequent evidence-note edits change documentation only; browser source, tests, shared settings and workflow remain identical to the verified behavior commit.

| Hosted job | Actual result |
| --- | --- |
| Synthetic backend | Domain 152, Infrastructure 831, Application 1318, API 371 passed (2672 total); 20 explicit skips: 14 live opt-ins and six absent-image Docker cases. Native browser cases executed. Local Docker rerun passed all eight cases, closing those prerequisite skips. Infrastructure build: zero warnings/errors. |
| Synthetic frontend | 96 files / 739 tests passed with the unchanged one-worker gate; production build passed. |
| Synthetic Playwright core | 116 passed across Synthetic, Browser STT and Browser STT/TTS projects; API host build zero warnings/errors. |
| Synthetic Playwright acceptance | 16 passed: Manual-A 1, Admin lifecycle 1, P7.6 Admin 1, P9.7 Harness 6, continuity 2, identity maintenance 1, Secretary 4. |
| Synthetic Compose smoke | `compose sqlite volume check passed`: owner-capability path and durable SQLite/keyring/profile/workspace/Skill-copy survival through recreation. |

### Closure and remaining scope

The targeted completeness/polish work is verified operationally and meets its key-free gates. The 72-operation matrix deliberately does not claim unrestricted MCP parity: bounded diagnostics/storage/input, host-only context creation settings and protected advanced capture/code boundaries remain explicit. Existing immutable Definition versions are retained; operators publish a new authorized version to grant new tools, and geolocation still requires exact attached-turn approval. The historical full-cutover real-model Journey L remains separately unrun; this audit does not declare that migration behavior-frozen or reopen P10/P11.
