# Browser semantic redesign verification

Complete native implementation on `develop/branch-1a`, 2026-10-09. Acceptance is still in progress: the initial paid candidate regressed Luna and the corrected schema awaits an explicitly approved repeat comparison. Exact-head hosted results are recorded in the delivery message and the linked hosted record. This report does not claim closure.

## Pinned evidence and public SDK decision

Behavior baseline: `09e20afea2ffbc381fce1b43f5c49fd3722e50e8`. Starting clean checkout: `b561997e393a6660d186cc091bbaa3c6659acaf3`, whose only additional change was an earlier verification-report update. The baseline's five hosted jobs subsequently passed in [run 37902111389](https://github.com/trannamtrung1st/agent-core/actions/runs/37902111389). No baseline CI wait delayed implementation.

Microsoft.Playwright remains 1.63.0, Chromium/headless revision 1243, browser version 153.0.8010.12. `NativeBrowserSdkQualificationTests` compiled and ran Chromium using the installed public NuGet binary: `IPage.GetByRef` is absent, ordinary Locator ARIA snapshots omit iframe interiors and refs, while AI snapshots contain both iframe subtrees and ref markers. Therefore production uses ordinary native ARIA and direct semantic actions. No AI refs, private SDK members or replacement ref store are emitted. [Public Page documentation](https://playwright.dev/dotnet/docs/api/class-page) describes newer API availability; the actual installed binary determines this implementation. Official MCP origin filters are not substituted for Core enforcement; [upstream's security boundary statement](https://github.com/microsoft/playwright-mcp) is consistent with retaining owned host controls.

The baseline's 137 browser-focused Infrastructure tests passed in actual Chromium in 3m38s. Original setup failure was an MSBuild IPC sandbox `SocketException: Permission denied`, before test execution; running with permitted local process/socket access and `-m:1` resolved that environment issue.

## One production architecture

`ToolRegistry` / `BrowserToolCatalog` → current authorization and admission → `BrowserToolArguments.TryRequest` → closed `BrowserCommand` record → Session/Instance-owned `IBrowser.ExecuteAsync` → `NativePlaywrightBrowser` → public .NET Playwright Locator/Page/Context API → redacted `BrowserResult` → bounded tool receipt and AgentRun checkpoint.

`BrowserTarget` has one by/value grammar: `role` may include accessible `name`; `text`, `label`, `placeholder`, `altText`, `title` and `testId` use their literal `value` and omit `name`. Optional `exact` defaults to true; `visible`/literal `hasText`, one bounded `within` criterion, and a current owned `frameRef` refine the query. Frame IDs carry their page/navigation generation; navigation, tab changes and recovery invalidate them using an event-safe map. Nested schema constraints distinguish role from literal criteria within this same grammar. They do not overload operations or translate an old contract. Unknown fields, selectors, scripts, ordinals and retired ref arguments fail validation. Actions require one live native scope and one live target. Find samples use ordinals only for observation and convey no positional action authority.

Examples of actual model calls:

```json
{"name":"browser.navigate","arguments":{"url":"http://127.0.0.1:5191/records"}}
{"name":"browser.type","arguments":{"target":{"by":"label","value":"Email"},"text":"demo@example.test"}}
{"name":"browser.fill_credential","arguments":{"target":{"by":"label","value":"Password"},"credentialRef":"demo-sso"}}
{"name":"browser.click","arguments":{"target":{"by":"role","value":"button","name":"Sign in"}}}
{"name":"browser.click","arguments":{"target":{"by":"role","value":"button","name":"Edit","within":{"by":"role","value":"row","hasText":"Pump 002"}}}}
{"name":"browser.snapshot","arguments":{}}
```

No find call is required between navigation and an ordinary action. A role/literal schema error provides safe, precise repair guidance. Equivalent malformed targets share the existing durable recovery counter; a corrected valid target remains permitted. Policy denial is not counted as malformed syntax. No model-specific schemas, retry logic, projection rules or locator paths exist.

`EffectAttempted`, `EffectConfirmedBySdk` and `ApplicationOutcomeVerified` are separate result facts. Native action completion does not prove authentication, publication or logout. Explicit native verification can report its observed predicate; snapshots and successful close remain evidence of their own operation only. Partial/error/intervention receipts preserve known effect facts. Non-replayable browser effects remain fenced by AgentRun, and current target state is rediscovered after uncertain recovery.

## Complete 37-operation catalogue

Every row is retained with one current operation schema and one concrete neutral command. Authorization, projection, per-effect policy and existing privilege gates apply before the native call.

| Tool | Public native implementation / retained Core boundary |
| --- | --- |
| browser.navigate | Page.Goto/GoBack/GoForward/Reload; separate destination/redirect/resource enforcement |
| browser.snapshot | Locator.AriaSnapshot; bounded semantic subtree and allowed frame inventory |
| browser.find | GetByRole/Text/Label/Placeholder/AltText/Title/TestId, Count and bounded observational samples |
| browser.click | Unique Locator.Click with click count; live protected-control and origin checks |
| browser.hover | Unique Locator.Hover |
| browser.drag | Unique source/destination Locator.DragTo; both destinations checked |
| browser.drop | Fixed target-local SDK evaluation dispatches DragEvent with bounded structured text; artifact drop remains unsupported |
| browser.type | Locator.Fill/PressSequentially and optional Press; password/OTP/credential-control denial |
| browser.fill_form | Preflight every target, recheck each field, Fill or SetChecked |
| browser.select_option | Locator.SelectOption |
| browser.press_key | Locator.Press or guarded Page.Keyboard.Press |
| browser.upload | Locator.SetInputFiles using approved owned Artifact bytes, never a host path |
| browser.fill_credential | Current active Instance binding → Password/exact origin/attached UserTurn → protected Locator.Fill |
| browser.wait_for | Native Locator/URL waits; bounded application quietness only for explicit stable |
| browser.tabs | Owned Context.NewPage, Page.BringToFront/Close and page inventory |
| browser.dialog | Explicit native dialog inspection, Accept/Dismiss; redacted prompt and pending-action fence |
| browser.resize | Page.SetViewportSize with cancellation fencing |
| browser.close | Owned context Close, persistent profile accounting; closed/already_closed/close_uncertain |
| browser.screenshot | Native Page/Locator.Screenshot and masks; protected frame/reflection masking and owned Artifact |
| browser.console_messages | Bounded redacted native Console events |
| browser.network_requests | Bounded native request metadata with opaque owned request IDs |
| browser.network_request | Owned request metadata lookup; no headers/bodies/secrets |
| browser.route | Native routing with bounded exact-approved privileged controls; policy cannot widen |
| browser.routes | Safe current route inventory |
| browser.unroute | Remove an owned approved route |
| browser.network_state | Native Context.SetOffline with existing privileged approval |
| browser.cookies | Context cookie APIs; origin/profile gate and protected key/value handling |
| browser.local_storage | Fixed target-local native storage operation; approved origin/key/value checks |
| browser.session_storage | Same current-page storage boundary |
| browser.verify | Native visibility/text/value/checked predicate; independent observed result |
| browser.generate_locator | Informational Locator text only, never executable authority |
| browser.mouse | Gated vision-only bounded native mouse; protected viewport point checks |
| browser.highlight | Native Locator highlight/hide, no model script |
| browser.emulate_media | Page.EmulateMedia; supplied overrides only, explicit null clears |
| browser.get_config | Safe effective host/provider/environment metadata; no paths or secrets |
| browser.set_geolocation | Native Context permissions/geolocation; exact origin approval and cancellation fencing |
| browser.scroll | Unique Locator.ScrollIntoViewIfNeeded |

Deliberate existing exclusions remain: model JavaScript/CSS/XPath, CDP escape hatches, raw traces/video/storage exports, arbitrary HTTP, blanket browser permissions and artifact drag/drop without a safe native capability. This cutover adds no new privilege defaults.

## Feature and security evidence

| Scenario / threat | Actual evidence and retained boundary |
| --- | --- |
| Simple form / SSO | Direct action SSO crosses SessionRuntime, capabilities.load, active Credential binding, two independent local origins and Chromium. Success requires protected DOM state plus one accepted login and protected server read. |
| Dense / custom SPA | Existing 2,000-entry independent fixtures verify targets beyond clipped output, exact scoped controls, rerender, custom tree, virtualization limits and observed state. No full-page custom index. |
| Native SDK semantics | Public SDK spike independently verifies role+name, label, placeholder, test ID, unique row text, DOM replacement, open Shadow DOM and strict duplicate failure. |
| Protected controls | Credential and boundary suites reject ordinary password, OTP, token and changed-field fills; secure values register before effects and redact before clipping. |
| Navigation / resource / frame / popup | Separate exact origin policies, denied redirects/metadata/local-file bypasses, permitted frame inventory, denied iframe text/pixels, resource/WebSocket routes and owned popup cleanup. A found provisional about:blank popup race now awaits its native transition and drains closes after observation before returning. |
| Owner / profiles | Session/Instance binding, persistent lease locks, wrong-owner/profile tests; copying a semantic query never selects another owner's context. |
| File / image / network | Bounded downloads and upload Artifact ownership, filename/MIME/size checks, screenshot masks/reflected secrets, safe diagnostic metadata without headers or bodies. |
| Dialog / cleanup | Alert/confirm/prompt, successive modal state, masked messages, explicit accept/dismiss, cancel/steer, truthful close uncertainty and context closure independent of sign-out. |
| Authorization / injection | Definition → Instance → current trigger/admission → approval → effect recheck. Page evidence and capability discovery grant no permission. Privileged network/storage/geolocation and coordinates retain their gates. |
| Durable execution | Existing recovery/claim loss, uncertain effect, approval resume, userSteer, timeout, screenshot rehydration and cleanup tests use the new contract. 300-second interactive budget, 60-second requested cleanup and 30-second tool-free final reserve remain. |

The affected UI was operated through Playwright MCP on an isolated Synthetic host (API 5188, UI 5278, fixture 5198): inspect v21/v8 and capability projection in Admin, create a v21 Instance, ask naturally for AC-1042, observe “AC-1042 is In review.” and Ready, then request a denied site and observe the truthful trusted-scope refusal. Console returned zero errors; affected HTTP requests succeeded. Existing user Real processes on 5080/5173 were not interrupted. Independent DOM/server acceptance comes from native integration tests, rather than treating the UI/model sentence as proof of external state.

## Migration and deletion audit

Removed production `BrowserOptionsData`, `BrowserTargetQuery`, Locator-ref registry, ref minting/lifetime parser and old action/ref resolver. Operation-specific records replace the giant optional union. The provider's obsolete operation-union schema translator is deleted; current schemas pass through unchanged except empty required-array formatting. No MCP subprocess, transport, provider toggle, dormant engine, alias or old-schema normalizer remains.

File built-ins now expose General Assistant v21 and Secretary v8. Retired general v17/v18/v20 and secretary v3/v5/v6/v7 are absent from executable file lookup; immutable historical bytes remain in Git and stored owner snapshots. Current Skills use direct targets and optional deep discovery. Publication validation, prompt-context construction (including pinned managed Skills) and direct browser execution reject known retired instructions. Current checkpoints carry browser contract version 1. Historical browser checkpoints remain readable, but cannot be resumed/replayed under the new contract. Historical non-browser checkpoints remain supported.

No owner database, Credentials, profiles, workspace or managed publication was reset or rewritten. Initial read-only owner inventory found 75 terminal Runs, 30 Sessions, seven Instances, four managed publications, one managed Skill, two Credentials and one user profile. A later read-only audit observed concurrent owner activity: 78 Runs / 35 Sessions including two active old browser Runs on the separate Real process; four managed publication payloads and the one local Skill contain retired instructions. Counts only were recorded, not payloads or identities. No external live owner execution was canceled. **Before deploying this code to that existing Real host, drain/cancel its old browser Runs through normal owner controls, revise/adopt current instructions and begin new Sessions.** The guarded runtime deliberately refuses those obsolete pins/checkpoints; there is no transparent owner-instruction migration.

The current test hosts use new temporary InMemory/SQLite data and isolated profile roots. Compose used only `agent-core-browser-redesign-20261009`, port 5089 and its disposable volume. Existing checked-in historical reports are explicitly historical; they are not acceptance for this redesign.

## Responsibility inventory and LOC

Counts include lines, not a claimed complexity reduction. File separation preserves one provider with the same private owner state; it adds no runtime layer.

| Responsibility | Baseline | Redesign candidate |
| --- | ---: | ---: |
| Main launch/navigation/session state | 2,771 | 876 |
| Profiles/close/lease owner (extracted) | in main | 521 |
| Origin/routes/popups/page ownership (extracted) | in main | 639 |
| Protected targets/credential/redaction (extracted) | in main | 360 |
| Screenshot/download bounds (extracted) | in main | 447 |
| Native operation dispatcher | 456 | 459 |
| Native observation/search | 134 | 87 |
| Direct target matcher | registry in main | 72 |
| Native state/environment | 86 / 197 | 85 / 197 |
| Neutral Application port | 230 | 333 |
| Strict tool arguments / catalogue | 202 / 85 | 256 / 87 |
| Browser executor | 749 | 757 |

Retained Core helpers are necessary for enforced origin/redirect/resource policy, host/profile ownership, credential reflection redaction, denied iframe pixels, artifact caps and cancellation/approval/replay fences. Public native APIs replace matching, accessible names, strict action targeting, rerendering, scoped ARIA and supported environment operations. Secret collection still scans known browser storage before output because SDK snapshot masking does not protect reflected secrets; no stale secret cache was introduced for speed. Native route fetch can buffer before a body cap, so the existing bounded attachment path remains. Application settling stays only where delayed SPA tests demonstrate an auto-wait gap. The complete custom element-ref registry is obsolete and deleted. Total security code was retained rather than trading it for a LOC target.

## Natural-language comparison and limitations

All paid trials were explicitly approved before dispatch. Payload: built-in demo instructions, generated local SSO pages/observations and the same natural-language instruction, synthetic account and exact tool/credential grants. OpenRouter key loaded privately; no private application or owner data was sent. Baseline source was archived from the pinned SHA into `/private/tmp/agent-core-browser-baseline`; only benchmark metrics/harness instrumentation changed there. Baseline uses its v20 instructions; candidate uses its replacing v21 instructions. Sites, task and grants are otherwise the same; ephemeral local ports vary. A fresh browser/profile starts every trial. “Cold/warm” describes HTTP client reuse, not a proved inference cache state.

| Model / candidate | Trial | Requests / calls | Valid / malformed / blocked | Find | DOM + correct status report | Seconds | Largest browser receipt bytes |
| --- | ---: | --- | --- | ---: | --- | ---: | ---: |
| Luna Medium / baseline | 1 | 25 / 24 | 17 / 2 / 0 | 8 | failed / failed | 69.3 | 459 |
| Luna Medium / baseline | 2 | 8 / 9 | 9 / 0 / 0 | 3 | passed / passed | 22.1 | 568 |
| Luna Medium / baseline | 3 | 9 / 9 | 9 / 0 / 0 | 3 | passed / passed | 20.6 | 568 |
| DeepSeek Medium / baseline | 1 | 8 / 9 | 9 / 0 / 0 | 3 | passed / passed | 10.1 | 568 |
| DeepSeek Medium / baseline | 2 | 8 / 9 | 9 / 0 / 0 | 3 | passed / passed | 11.1 | 568 |
| DeepSeek Medium / baseline | 3 | 8 / 10 | 10 / 0 / 0 | 3 | passed / passed | 29.8 | 568 |
| Luna Medium / initial redesign | 1 | 26 / 28 | 7 / 21 / 0 | 1 | failed / failed | 106.1 | 1,099 |
| Luna Medium / recovery revision | 2 | 12 / 11 | 3 / 8 / 2 | 0 | failed / failed | 36.5 | 1,227 |
| Luna Medium / recovery revision | 3 | 9 / 10 | 3 / 7 / 2 | 0 | failed / failed | 24.3 | 1,227 |
| DeepSeek Medium / initial redesign | 1 | 7 / 6 | 6 / 0 / 0 | 0 | passed / passed | 18.3 | 1,113 |
| DeepSeek Medium / recovery revision | 2 | 8 / 7 | 7 / 0 / 0 | 0 | passed / passed | 24.1 | 1,186 |
| DeepSeek Medium / recovery revision | 3 | 6 / 7 | 7 / 0 / 0 | 0 | passed / passed | 10.6 | 1,241 |

Initial harness restarted the OpenAI adapter on an already used HttpClient, so it threw before each model's second trial. That consumed four trials, not twelve. Constructing one adapter per model fixed the harness; resuming trials 2–3 consumed only the eight remaining approved trials. All twelve results above are retained, including failures. Trials 1 and resumed 2 use cold clients; trial 3 reuses its client's transport. Baseline Luna trial 1 had missing/ambiguous target and obsolete ref errors. Candidate Luna failures were structural `name_requires_role` and guessed frame IDs, then bounded malformed-strategy refusal—not policy, SDK or timeout failures. DeepSeek used strictly fewer unnecessary discovery calls, but this is not a major latency-speedup claim.

The final schema now explicitly excludes `name` for literal targets, adds minimal examples and keeps frame IDs optional for the main page. Corrected-target offline tests pass. **Luna success is not assumed:** a separate six-trial final-candidate comparison has been requested and awaits explicit approval. Prior candidate results cannot establish its acceptance. Real task success requires server login/read counters and final authenticated Chromium DOM; Final-candidate reply completion is scored separately. The first twelve Completed metrics checked a completed reply containing the correct status; they did not separately measure a completed blocker reply, so failed status reporting must not be read as proof that no reply appeared. Receipt byte counts are measured UTF-8 output sizes, not invented token estimates. Provider token/cache accounting was unavailable in this harness. Safe diagnostics retain counts, bounded error categories and receipt sizes, never prompts, arguments, protected values or page contents.

## Verification ledger

| Gate / exact command family | Observed result |
| --- | --- |
| Baseline native Chromium filter | 137 passed, 0 failed |
| SDK + direct target + popup focused tests | 3 passed; denied-popup repetition 10/10 passed |
| Final native/browser/credential boundary filter | 72 passed, including tab/frame generation rejection and independent effect assertions |
| Final effect/environment and route-restoration checks | 13 passed; six journey/direct-action tests passed after confirming unroute restores the actual server response |
| Stalled/failed/persistent close effects | Four passed; actual context/authentication state remains independent from effect attempt and SDK confirmation |
| `dotnet test tests/AgentCore.Domain.Tests/... -m:1 --no-restore --nologo` | 173 passed |
| Application full gate | 1,446 passed, 3 explicit live opt-in skips |
| API full gate | 390 passed, 2 explicit live opt-in skips |
| Infrastructure full gate | 926 passed, 7 explicit opt-in skips after final effect/route fixes (4m37s); subsequent close-status refinement passed all four cleanup scenarios |
| OrderEvents plugin full gate | 4 passed |
| Frontend Node 22 `vitest.mjs run --maxWorkers=1` | Latest full run: 769 passed, one unchanged Event credential-dialog lookup failure; isolated 4/4 file tests passed. Earlier full run: 768 passed, two Admin continuity timeouts; isolated 8/8 file tests passed. Hosted full gate pending. |
| Frontend `pnpm run build` | TypeScript/Vite passed; existing chunk-size warning |
| Full Synthetic core, browser-stt, browser-browser | Final 130/130 passed (13.2m). Prior 128/130 failures were an old version assertion and a detach timing failure; both passed focused rerun. |
| Focused Playwright recovery | 2 passed |
| faithful-manual / admin-lifecycle / p76-admin | 1 / 1 / 1 passed |
| p97-harness / p9899-continuity | 6 / 2 passed |
| p910-continuity-maintenance / secretary-demo | 1 / 4 passed |
| Isolated Compose SQLite volume survival | Passed, including recreation, persistence, credential protection and workspace controls |
| Playwright MCP affected UI + denied-origin journey | Passed; no browser console errors |
| Initial authorized paid comparison | 12 executed; baseline Luna 2/3, baseline DeepSeek 3/3, candidate Luna 0/3, candidate DeepSeek 3/3; final-candidate repeat awaiting approval |
| Source/deletion/doc-link audit | No retired execution classes/parsers/registry; historical/negative references only; 426 local links checked, none missing |
| Exact-head hosted Synthetic jobs (backend, frontend, core, acceptance, Compose) | [Hosted record for this branch](https://github.com/trannamtrung1st/agent-core/actions?query=workflow%3Asynthetic+branch%3Adevelop%2Fbranch-1a). Delivery records the exact final checkout SHA, terminal run URL and five job results. |

Reproduction uses isolated ports (API 5180–5182, UI 5273–5275, fixtures 5191–5193), fresh `/private/tmp` SQLite/profile/resource roots, `CI=1` and the seven projects in `.github/workflows/synthetic.yml`. Full core: `playwright test --project=synthetic --project=browser-stt --project=browser-browser`. Each acceptance project receives a separate DB; faithful-manual, p97-harness, p910-continuity-maintenance and secretary-demo use `PLAYWRIGHT_FAITHFUL_MANUAL=1` as in CI. Node 26's unavailable experimental localStorage caused the first frontend environment failures; using CI's Node 22 resolved the storage failures without product/test changes. Original local failure logs are retained separately during work; passing reruns do not erase their diagnosis.

The delivery message pins the final behavior SHA / five-job run from actual results. Remaining model acceptance is recorded only after an authorized final-candidate comparison. No integration with the independent main thread was attempted. SDK ref support, more advanced native protected-output APIs and broader real-application benchmarking remain future opportunities rather than custom ref emulation or current success claims.
