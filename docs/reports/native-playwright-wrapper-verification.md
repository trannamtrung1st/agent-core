# Native Playwright wrapper verification

Date: 2026-10-09. Implementation branch: `develop/branch-1`. Baseline: `75bf40aaaaba5a256d528d9e3feeb25c7a967b0d`.

**Status: implemented candidate, not frozen.** The native replacement is the only registered browser path. Default provider tests remain offline. Final acceptance requires all five hosted Synthetic jobs on the PR's exact implementation head and the explicitly authorized configured-model journey. A skipped opt-in test is not model evidence. CI and operator-authorized model results must be recorded before closure; historical Browser v2 freezes do not verify this candidate.

## Contract and deletion audit

Application owns one typed `BrowserRequest` / `BrowserResult` path through `IBrowser.ExecuteAsync`. Independent lease, profile-binding/reset, unattended context and password-sink ports retain their security responsibilities. No Playwright type crosses into Domain, Application or Contracts. The Infrastructure registration resolves the same `NativePlaywrightBrowser` instance for browser, lease and hosted lifecycle.

Removed: the six old `PlaywrightBrowser` partials, the duplicate Application `BrowserV2` executor, overlapping public navigation/interaction/command/screenshot/close result types, custom ARIA parsing and full index, caption scanner, index ancestry/ordinal reconstruction, snapshot lineage/retired-reference caches, second visible-text capture, and the retired evaluate/storage-state/PDF/trace/video catalog placeholders. Tests use native semantic discovery and strict neutral requests. Private application probes were removed; generic credential/profile, form and resource tests carry their security expectations. The independent commerce event plugin is unchanged.

The audit searches production, tests, current canonical docs, seeds and shared agent instructions for `IndexNativeAsync`, `AddCustomTargetsAsync`, `SnapshotIndex`, `MaxIndexedNodes`, `BrowserV2`, retired tool IDs and legacy registration. Remaining retired-tool mentions are negative exclusion assertions or immutable historical reports. The old `browser-v2-contract` document anchor remains only so historical report links resolve; the current contract is `native-browser-contract`. Test fakes may have local helper names such as `NavigateAsync`; these are not public port members, registered providers or compatibility dispatchers.

A semantic token contains only owned Session identity, current page generation, native Locator, action hints and expiry. It is bounded to 256 live tokens per Session and ten minutes. Queries resolve live native semantics; duplicate samples never contain executable refs. Tokens survive observations and ordinary rerenders, and recheck uniqueness, field protection and frame origin before effects. Navigation invalidates a token; later cleanup can make it `unknown_reference` rather than retaining an unbounded tombstone. Both outcomes require rediscovery.

## Feature and authority matrix

All listed tools first intersect Definition authorization, current projection, readiness and provider feature support. Browser content remains untrusted. Native methods below describe delegation, not additional authorization.

| Tools | Native delegation / result | Additional boundary and evidence |
| --- | --- | --- |
| `navigate` | Goto / GoBack / GoForward / Reload | Navigation/resource/redirect policy; settled bounded observation; adapter and origin tests |
| `snapshot` | Locator.AriaSnapshotAsync, native depth and optional BoundingBox | Target subtree reads, redaction before UTF-8 clipping; no discovery prerequisite; dense/scoped tests |
| `find` | GetByRole, GetByText, GetByLabel, GetByPlaceholder, GetByAltText, GetByTitle, GetByTestId, Count | One primary query, exact by default, unique scope/frame, bounded informational ambiguity; native contract/dense/rerender tests |
| `click` | Locator.Click | Button/count/modifiers, native actionability; protected and non-actionable denial |
| `type` | Fill / PressSequentially / Press | Ordinary text/contenteditable only; no model password bytes; native form and reclassification tests |
| `fill_form` | Fill / SetChecked | Preflight all owned fields; secure fields denied; form and policy tests |
| `select_option`, `press_key` | SelectOption / Locator.Press or Keyboard.Press | Safe focused control reclassification and unique target; select/keyboard tests |
| `hover`, `drag` | Hover / DragTo | Both endpoints must be ordinary, owned and allowed; native interaction tests |
| `drop` | Fixed structured DataTransfer dispatch | Bounded text payload; artifact drop explicitly unsupported; no arbitrary model JS |
| `scroll` | Optional target Hover then Mouse.Wheel | Bounded signed deltas, ordinary allowed target; virtualized fixture reveals actual rendered content |
| `wait_for`, `verify` | Native locator state waits / observed value/text/checked state | Bounded 100–5000 ms wait; missing hidden/detached targets handled without fabricated elements |
| `tabs` | Context.NewPage / Page.BringToFront / Close, native page inventory | Session gate, allowed destination/popup, invalidated page generation; tab/lifecycle tests |
| `dialog` | IDialog.Accept / Dismiss, pending dialog inventory | Message fully masked; bounded pending-action continuation; modal/cancellation tests |
| `resize` | SetViewportSize | Bounded dimensions; non-cooperative resize fenced before recovery |
| `close` | Context/browser page lifecycle | Actual `closed` / `already_closed` or error; profile retained; separate-turn Synthetic Runtime tests |
| `screenshot` | Native page/locator Screenshot | Protected fields/reflections and cross-origin frame pixels masked; owned image Artifact; screenshot tests |
| `upload` | SetInputFiles | Application resolves owned approved Artifact bytes; bounded accepted files; upload/effect tests |
| `fill_credential` | Live password Locator.Fill through independent sink | Existing encrypted binding, actual field/origin recheck, attached direct turn; detached/registration/OTP denied before resolver |
| `console_messages` | Page.Console events | Bounded inventory, oversized messages masked before clipping; known secret redaction on read |
| `network_requests`, `network_request` | Native request/response events | Origin-only diagnostic URLs, no sensitive headers/body; bounded owned request refs |
| `route`, `routes`, `unroute`, `network_state` | Native context routing / SetOffline | Exact privileged mutation approval; cannot widen host origin/resource policy |
| `cookies` | Context.Cookies / ClearCookies | Values never exposed; origin-scoped domain/path deletion; parent-domain cookie tests |
| `local_storage`, `session_storage` | Fixed bounded storage access | Exact privileged mutation approval, protected keys denied, values redacted before bounds; canceled mutation fences original page |
| `generate_locator`, `highlight` | Locator.ToString / Highlight | Informational generated text confers no execution authority; current live target checks |
| `emulate_media` | Page.EmulateMedia | Omitted vs explicit-null overrides retained; native environment tests |
| `get_config` | Safe descriptor/environment/policy inspection | Works without context creation; no paths or secrets; disabled/unavailable engine tests |
| `set_geolocation` | Context.SetGeolocation / scoped permissions | Direct attached user turn and exact origin/coordinate approval; no coordinate echo; reset/revoke tests |

Exclusions remain explicit: arbitrary model JS/Playwright/CSS/XPath/CDP, raw storage import/export, unrestricted PDF/trace/video, WebSocket payload inspection, proxy/TLS bypass, extensions and desktop control. Open shadow roots and allowed frames are supported. Closed roots, CAPTCHA/MFA/OTP, inaccessible/canvas-only controls and unrendered virtual records have truthful limitations. Scroll or application filters can render virtual content; Core never fabricates offscreen targets.

## Exercised behavior and local checks

- Dense 2,000-entry SPA: initial content clips before Entry 1999. With a capture-failure probe enabled, direct native find still returns its unique Locator. A DOM rerender preserves semantic targeting; click changes actual status to `Opened Entry 1999`. Scoped review inspection uses a materially smaller excerpt and acts only in the unique grid.
- Duplicate and role-less captions: no arbitrary first match, bounded samples without refs, unique region scoping, native event-delegated click, new ambiguity/missing/protected rerender denial. Actual DOM state is asserted.
- Virtualized list: absent Row 200 returns `not_found`; native scroll renders it. Open-root button changes actual status; closed-root target remains unavailable. Hidden/detached native wait succeeds after removal.
- Registry: the 257th live token is rejected. Advancing injected time expires the old token; rediscovery reclaims slots.
- Owned Synthetic SessionRuntime: two dense fixtures traverse actual Session/Activation/AgentRun, load an advanced authorized tool in the same Run, fill Summary and verify browser DOM. Separate closure turns confirm `closed`, then `already_closed`; provider-unavailable/unauthorized close never claims success.
- Credentials/profile/resources/cancellation: generic native tests cover attached/detached sink, registration/verification resolver denial, live OTP/API-key reclassification, reflected long-secret masking, domain cookies, popup/redirect/WebSocket denial, screenshot/upload/download Artifacts, owner profile retention/isolation/reset, and cancellation recovery.
- Playwright MCP Admin/Chat: disposable General Assistant v18 created through Admin. Effective configuration displays native features and `Snapshot 8000 UTF-8 bytes`. Chat searched AC-1042, emitted application progress, and finished `AC-1042 is In review.` with no recovered-flow console errors or failed API calls. Earlier setup failures were caused by local fixture-disabled overrides; the verification host explicitly enables its Restricted fixture.

| Check | Command / evidence | Result at candidate preparation |
| --- | --- | --- |
| Solution build | `dotnet build AgentCore.sln --nologo -m:1 -p:UseSharedCompilation=false` | Pass, zero warnings/errors |
| Domain | `dotnet test tests/AgentCore.Domain.Tests --no-build --nologo` | 173 passed |
| Application | `dotnet test tests/AgentCore.Application.Tests --no-build --nologo` | 1,359 passed, two explicit opt-in skips |
| Infrastructure | `dotnet test tests/AgentCore.Infrastructure.Tests --no-build --nologo` | 903 passed, seven explicit opt-in skips, including the registry case |
| API | `dotnet test tests/AgentCore.Api.Tests --no-build --nologo` | 390 passed, two explicit opt-in skips |
| Dense/registry runtime | Filter `BrowserReliabilityJourneyTests`, detailed logger | Five passed |
| Frontend unit/build | `NODE_OPTIONS=--no-experimental-webstorage pnpm run test --run --maxWorkers=1`; `pnpm run build` | 763 tests in 99 files passed; build passed |
| Playwright core | `CI=1 pnpm exec playwright test --project=synthetic --project=browser-stt --project=browser-browser` | Full run/rechecks in progress; no acceptance claim yet |
| Playwright acceptance | Seven sequential project invocations as CI | All seven project gates passed locally: Faithful Manual-A, Admin lifecycle, P7.6, six P9.7 harness cases, two continuity cases, maintenance and four Secretary cases |
| Compose survival | `./scripts/compose-sqlite-volume.sh`, unique project/image/volume, port 6400 | Passed; container recreation preserved SQLite, owner capability, home/scratch isolation, Skills and encrypted credential bindings |
| Configured paid model | `AGENTCORE_NATIVE_BROWSER_LIVE=1`, model/key supplied explicitly | Not authorized or run; open gate |
| Hosted exact-head CI | Existing `synthetic.yml` five independent jobs | Draft [PR #4](https://github.com/trannamtrung1st/agent-core/pull/4); all five final-head jobs required; the PR checks and description carry their eventual exact-SHA evidence; open at ledger preparation |

The first core run passed 121 of 126 cases. Five stale assertions were updated for the current heading/drawer controls, authorized browser bootstrap, v18 default and settled drawer geometry; the full final rerun is tracked in PR #4 and remains in progress at ledger preparation. The remaining retry drawer assertion selects the unique observed `Retrying` row rather than a fixture-only title; its reminder → original result → cancel → reload journey passes in a focused rerun. The Secretary test-authored Definition now explicitly projects its authorized automation schemas and grants native `browser.find`. Its four-case rerun passes. A subsequent seed edit temporarily exceeded the existing eight-capability Skill limit; validation rejected that candidate, the projection was corrected without weakening policy, and Definition validation and the final API suite pass. Superseded run failures are not counted as final-head evidence.

Local logs are temporary evidence under `/tmp/native-*`; they are not shipped data. Default tests never spend provider credits. Node's experimental Web Storage shadowed jsdom storage in the initial local frontend run; disabling that runtime feature restored the affected storage tests without changing product behavior, test assertions or deadlines. Frontend build retains the existing large-chunk warning.

## Maintainability and measured dense sample

Same local macOS/.NET 10.0.10 runner, pinned Playwright .NET 1.63.0 Chromium, unchanged `browser-dense.html` fixture. The baseline was built from a temporary archive of the exact baseline commit; no branch checkout or owner data was changed. One sample per implementation, under concurrent local Synthetic test load; these are illustrative durations, not percentiles or a performance acceptance threshold.

| Measure | Baseline | Native candidate |
| --- | ---: | ---: |
| Browser provider partials, source lines | 4,311 in six files | 3,563 in five files (17.3% smaller) |
| Application browser executor, source lines | 1,146 in two files | 739 in one file (35.5% smaller) |
| Required full ARIA discovery indexes | 1 | 0 |
| Custom ARIA parser / caption scanner | Both present | Both deleted |
| Initial navigation + observation | 1,481.7 ms | 1,282.9 ms |
| Deep target find after navigation | 23.7 ms, cached indexed search | 27.3 ms, direct native query |
| Click + resulting observation | 252.4 ms | 910.4 ms |

The measurement does **not** establish an overall speedup. The old find performs zero page calls after paying for whole-page indexing; the new unique unscoped find performs Count, context cookies, fixed security reads per permitted frame, and one target-local description. It performs zero ARIA capture/parser/index calls. Scoped find adds unique-scope/origin checks. Native target actions recheck authority/uniqueness/security and use the SDK, then capture one bounded native excerpt plus required security/settle checks. The former path additionally parsed every ARIA line, rebuilt role/name counts/descriptions and synthetic refs, scanned custom captions, and reconstructed ordinal/ancestry authority on observations. The new work scales with bounded query samples and permitted-frame security reads rather than a page-wide executable target index.

Fixed scripts still exist where Core must enforce secret masking/collection, current target classification, intervention classification, bounded structured drop, storage policy and the existing quiet-page signal. They do not accept model code or implement general page discovery. Browser settle behavior and the existing 25-second operation deadline were retained; tests were not made green by increasing those limits.

## Adoption and data inventory

General Assistant v18 and Secretary v6 are new immutable seed files. Existing v17/v5 files are byte-for-byte unchanged. Owners can select the new version for new Sessions through Admin; existing pinned Sessions remain pinned. The catalog is a full replacement: an old persisted browser tool call is not translated or replayed through an alias. Stop in-flight browser work before deploying the new binary and obtain fresh semantic refs after reopening.

No user Sessions, databases, Credentials, existing owner profiles, shared local overrides, user-secrets or running development hosts were reset or terminated. Only test-owned temporary databases, ephemeral browser contexts, profile directories and a uniquely named Compose project/volume were used. The baseline archive is disposable test material. Any future demo reset requires a reviewed explicit target list; this implementation does not need a reset to adopt its new seed versions.
