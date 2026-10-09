# Native Playwright wrapper verification

Date: 2026-10-09. Implementation branch: `develop/branch-1`. Baseline: `75bf40aaaaba5a256d528d9e3feeb25c7a967b0d`.

**Status: implemented candidate, not frozen.** The native replacement is the only registered browser path. Default provider tests remain offline. All five hosted Synthetic jobs passed on `ab6c232770255131a8c660e04ac6149a0b5dc094` ([run 37845285622](https://github.com/trannamtrung1st/agent-core/actions/runs/37845285622)). The implementation-review adoption changes behavior: exact-behavior-SHA `ab118790c797d77b8e13f4205fac175cf62b894c` CI is still running ([run 37872793696](https://github.com/trannamtrung1st/agent-core/actions/runs/37872793696)). The explicitly authorized configured-model journey passed with the same production behavior, as recorded below. Final acceptance awaits all five current CI jobs. A skipped opt-in test is not model evidence; historical Browser v2 freezes do not verify this candidate.

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
| `find` | GetByRole, GetByText, GetByLabel, GetByPlaceholder, GetByAltText, GetByTitle, GetByTestId, Filter(HasText), Count | One primary query, exact by default, unique scope/frame, bounded informational ambiguity; native contract/dense/rerender tests |
| `click` | Locator.Click | Button/count/modifiers, native actionability; protected and non-actionable denial |
| `type` | Fill / PressSequentially / Press | Ordinary text/contenteditable only; no model password bytes; native form and reclassification tests |
| `fill_form` | Fill / SetChecked | Preflight all owned fields and recheck before each effect; protected/newly ambiguous later fields denied; ordinary rerenders still work |
| `select_option`, `press_key` | SelectOption / Locator.Press or Keyboard.Press | Safe focused control reclassification and unique target; select/keyboard tests |
| `hover`, `drag` | Hover / DragTo | Both endpoints must be ordinary, owned and allowed; native interaction tests |
| `mouse` | Native Mouse.Move / Click / Down / Up / Wheel | Vision-enabled projection, finite viewport bounds, fixed point/ancestor protection checks and guarded drag endpoints; native visual-target and protected-boundary tests |
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
| `cookies` | Context.Cookies / ClearCookies | Values never exposed; active-host and applicable parent-domain deletion across all paths; unrelated hosts retained |
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
| Playwright core | `CI=1 pnpm exec playwright test --project=synthetic --project=browser-stt --project=browser-browser` | Local full run: 124 passed, two failures subsequently passed focused reruns. Hosted full 126-case gate passed on `20ae66f3` |
| Playwright acceptance | Seven sequential project invocations as CI | All seven project gates passed locally: Faithful Manual-A, Admin lifecycle, P7.6, six P9.7 harness cases, two continuity cases, maintenance and four Secretary cases |
| Compose survival | `./scripts/compose-sqlite-volume.sh`, unique project/image/volume, port 6400 | Passed; container recreation preserved SQLite, owner capability, home/scratch isolation, Skills and encrypted credential bindings |
| Configured paid model | `AGENTCORE_NATIVE_BROWSER_LIVE=1`, model/key supplied explicitly | Passed on native behavior `ab118790`; see configured-model journey below |
| Hosted exact-head CI | Existing `synthetic.yml` five independent jobs | All five passed on `20ae66f3`: backend 2,819 passes / 17 skips, frontend 763 tests/build, core 126 cases, acceptance 16 cases, Compose survival. The follow-up review needs a fresh run on [PR #4](https://github.com/trannamtrung1st/agent-core/pull/4) |

The first core run passed 121 of 126 cases. Five stale assertions were updated for the current heading/drawer controls, authorized browser bootstrap, v18 default and settled drawer geometry; the final local run passed 124 cases, with the retry-row assertion and an Event dropdown placement failure. Both affected journeys pass focused reruns; the unchanged Event journey completed ingress deduplication and exact Automation navigation. PR #4 tracks the required hosted full run. The remaining retry drawer assertion selects the unique observed `Retrying` row rather than a fixture-only title; its reminder → original result → cancel → reload journey passes in a focused rerun. The Secretary test-authored Definition now explicitly projects its authorized automation schemas and grants native `browser.find`. Its four-case rerun passes. A subsequent seed edit temporarily exceeded the existing eight-capability Skill limit; validation rejected that candidate, the projection was corrected without weakening policy, and Definition validation and the final API suite pass. Hosted backend validation also detected two Secretary tests asserting replaced demo-specific procedure text. They now check native targeting, bound credentials, artifact upload and verification while preserving the no-authority-escalation and owned-runtime assertions; all seven Secretary cases pass. Superseded run failures are not counted as final-head evidence.

Local logs are temporary evidence under `/tmp/native-*`; they are not shipped data. Default tests never spend provider credits. Node's experimental Web Storage shadowed jsdom storage in the initial local frontend run; disabling that runtime feature restored the affected storage tests without changing product behavior, test assertions or deadlines. Frontend build retains the existing large-chunk warning.

## Follow-up consistency and behavior review

The review started from clean branch `develop/branch-1` at `20ae66f3`. Real Chromium regressions reproduced three findings before fixes:

- Text waits used the first native text match, so a hidden first match caused `text` to time out despite visible later matches and caused `textGone` to falsely succeed. Native visibility filtering now checks for any visible match and waits for all visible matches to disappear.
- Form preflight did not revalidate later fields after earlier input handlers changed the page. Password/OTP reclassification was filled as ordinary input, and newly duplicated fields returned a generic stale error. Each field now rechecks its owned ref, current uniqueness, origin and protected classification before its effect. The already completed first input is retained; the denied later field is untouched. An ordinary DOM replacement still fills successfully.
- Multi-step typing reused pre-fill classification for sequential typing and Enter submission. Input-triggered password reclassification now stops the next effect; clearing/filling already performed is retained, and no subsequent text or Enter is sent.

`NativeBrowserConsistencyReviewTests` covers eight cases with actual DOM values and Enter counts. The first seven cases failed before their fixes; the eighth verifies that ordinary rerenders retain successful native Locator behavior. All eight pass. No deadline, policy, grant or seed version was relaxed.

The full backend run additionally exposed a SQLite fixture race in `BackgroundSessionJourneyTests`: its direct-store history arrangement ran after Continue in chat had created a runtime, competing with that runtime's detach checkpoint and causing `Stale session revision`. History arrangement now finishes before foregrounding. Original-result/title/origin/file/history and SQLite-reopen assertions remain intact. All three focused API cases pass; the final full API rerun is recorded below.

| Follow-up check | Command / evidence | Result |
| --- | --- | --- |
| Chromium regression | `dotnet test tests/AgentCore.Infrastructure.Tests --nologo --filter FullyQualifiedName~NativeBrowserConsistencyReviewTests -m:1 -p:UseSharedCompilation=false` | Eight passed |
| Backend regression | `dotnet test AgentCore.sln --nologo -m:1 -p:UseSharedCompilation=false` | Domain 173, Application 1,359, Infrastructure 911 and OrderEvents four passed; API 389 passed / one fixture race failed before correction. Eleven provider opt-ins skipped across backend suites |
| HTTP original-result / SQLite reopen | `dotnet test tests/AgentCore.Api.Tests --nologo --filter FullyQualifiedName~BackgroundSessionJourneyTests -m:1 -p:UseSharedCompilation=false` | Three passed after fixture correction |
| Final API regression | `dotnet test tests/AgentCore.Api.Tests --no-build --nologo` | 390 passed, two provider opt-in skips after fixture correction |
| Documentation / boundary audit | Changed-file links, fragments, fences, `git diff --check`; retired dispatch/index and vendor-type searches | Passed; no retired production path or Playwright type in Domain/Application/Contracts |

No frontend code changed in this follow-up. Its runtime evidence is the actual Chromium integration above; prior full frontend/MCP/Compose results remain tied to `20ae66f3`, and fresh hosted checks remain required for the new behavior head. At that earlier review, paid-model verification remained unrun because no outbound approval was provided; the later authorized native result is recorded below.

The production deletion audit still finds no retired browser dispatch, index/parser or alternative provider. Canonical contract and README/TODO/implementation-plan summaries now distinguish verified prior-head Synthetic evidence, follow-up verification and the unrun configured-model gate.

## Implementation-review adoption

The supplied external review assessed `ab6c2327`. All five cited jobs were independently checked through PR #4 and passed on that exact head. The review's two behavioral gaps were reproduced before changes and addressed within the native adapter:

- `browser.find` now accepts bounded literal `hasText` (1–200 characters), delegating to native Locator.Filter. Row/group record text identifies a unique container, then `scopeRef` finds its descendant Edit/Delete control. Primary-query `exact` does not change the native case-insensitive substring filter. No regex, selector, positional match or executable ambiguity sample is added. Generic unnamed row and group cases verify actual Record B Edit/Delete status, ordinary replacement, literal regex-looking strings, informational remaining ambiguity and denial after a container becomes duplicated.
- Tab selection now calls native BringToFront through the existing tracked action path after owner/origin checks. The selected page is bound before activation so cancellation fences that page, preserves the previous page, rejects the late call and recovers. The headed case failed before the fix because the logical selection left actual focus on the other tab, then passed after the native call. Playwright's default per-page focus emulation is disabled only inside the headed test; headless checks prove native delegation/identity/cancellation without claiming visible focus.
- Seed policy is deliberate: General Assistant v17 and Secretary v5 stay retrievable for immutable pinned Definition continuity; versionless lookup selects v18/v6. Their bytes, owner instances and data are unchanged. Definition retention does not re-enable retired tools/schemas, compatibility translation or the old browser provider. Canonical decisions and the store regression state this explicitly.

The first full backend rerun exposed an intermittent denied-popup leak (three pages rather than the expected opener plus allowed popup). Initial denied navigation can reach routing before Playwright exposes its Page, so abort completion alone is insufficient. The adapter now registers that native Page event before aborting, tracks closure before publishing completion, and preserves pre-existing blank tabs. The controlled test holds closure and asserts the tool cannot return until it completes; the existing external-denial/local-popup journey also passes unchanged.

Focused checks: five `NativeBrowserReviewAdoptionTests`, three Definition store tests and the existing popup regression passed together (nine cases); five `NativeBrowserContractTests` passed. The isolated actual-focus command `AGENTCORE_BROWSER_HEADED_REVIEW=1 dotnet test tests/AgentCore.Infrastructure.Tests --no-build --nologo --filter FullyQualifiedName~Selecting_a_tab_activates` passed. The final full backend command `dotnet test AgentCore.sln --nologo -m:1 -p:UseSharedCompilation=false` passed: Domain 173, Application 1,360, Infrastructure 916, API 390 and OrderEvents four (2,843 total; 11 explicit provider opt-in skips; zero failures). Changed-document local links/fragments/fences, SDK boundary audit and `git diff --check` passed. No frontend code or immutable seeds changed.

The options-union observation is a maintainability suggestion, not a demonstrated defect; operation enums and strict schemas remain the current typed contract. Typed grouping and repeatable performance profiling remain focused maintenance work. No overall speedup is claimed from the historical single dense sample, and no new architecture phase is started.

## Authorized configured-model journey

On 2026-10-09 the user explicitly requested the Real-profile journey. The existing opt-in `NativeBrowserLiveJourneyTests` ran the configured Real text provider through an isolated owned SessionRuntime/AgentRun, native browser adapter and local generic SPA. This is real hosted inference with the configured `deepseek/deepseek-v4.1-flash` model and medium reasoning through OpenRouter, not a Synthetic model or an API/UI deployment smoke. The configured key was read from API user-secrets into the child process environment without printing or persisting it. Only disposable fixture content and tool results were sent; no owner data or development hosts changed.

The first run completed in 27 seconds and passed all actual DOM assertions, but failed a later tool-use assertion: its prompt permitted valid individual field actions while the test required `browser.fill_form`. The test prompt now explicitly requests capability loading and batch form filling; all existing DOM, tool and completed-response assertions are unchanged. Safe evidence output records model, request count, tool names and the fixed fixture outcomes. No production code changed for this correction.

The corrected run passed in 24 seconds with ten model requests. It called `browser.navigate`, native `browser.find`, `capabilities.load`, `browser.click`, `browser.fill_form`, `browser.snapshot` and `agent_core_respond`. Actual native DOM assertions confirmed `Selected Asset 159`, Title `Native browser proof`, Notes `Generic SPA verified` and Enabled checked. A completed assistant response was present and the runtime emitted no ErrorOutput. The test's five-minute deadline and provider timeouts remained unchanged; this one sample is acceptance evidence, not a latency benchmark.

Command: `dotnet test tests/AgentCore.Application.Tests --no-build --nologo --filter FullyQualifiedName~NativeBrowserLiveJourneyTests --logger 'console;verbosity=detailed' -m:1`, with explicit `AGENTCORE_NATIVE_BROWSER_LIVE=1`, configured model/key and medium reasoning supplied in the process environment. The corrected detailed log is `/tmp/native-browser-real-profile-journey-corrected.log`; the first failed run is `/tmp/native-browser-real-profile-journey.log`. The test project rebuilt with zero warnings/errors. A separate default key-free filter of `NativeBrowserContractTests|NativeBrowserLiveJourneyTests` passed five cases and skipped the opt-in journey, proving default verification does not make paid calls.

The paid-model gate is now passed against production behavior `ab118790`. All five exact-behavior-SHA hosted jobs remain required for final freeze; [run 37872793696](https://github.com/trannamtrung1st/agent-core/actions/runs/37872793696) was still running when this evidence was recorded. PR #4 also reports merge conflicts with main, a separate integration issue. The suggested no-Page-event popup case remains a targeted follow-up scenario; this live journey does not establish that cancellation edge case. Prior Browser v2 model evidence is not used to validate this replacement.

## Maintainability and measured dense sample

Same local macOS/.NET 10.0.10 runner, pinned Playwright .NET 1.63.0 Chromium, unchanged `browser-dense.html` fixture. The native sample precedes the follow-up review; its source counts and durations are historical measurements of that candidate. The baseline was built from a temporary archive of the exact baseline commit; no branch checkout or owner data was changed. One sample per implementation, under concurrent local Synthetic test load; these are illustrative durations, not percentiles or a performance acceptance threshold.

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
