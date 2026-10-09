# Browser model compatibility enhancement

User-authorized scope: all recommendations from the DeepSeek/Luna review, on `develop/branch-1`. This follows browser discovery recovery `13ba2f59`; its earlier five-job CI run does not verify this enhancement. The native cutover freeze `345e8f7a` remains historical. P10/P11 remain unopened.

## Implemented behavior

- `browser.find` requires one `by`/`value` strategy. Optional role name, literal text filter, scope/frame and other refinements accept omission/null; whitespace-only optional string refinements mean absent. Conflicting non-empty criteria and retired parallel query fields fail validation. Internal `BrowserTargetQuery`, native Locators, opaque references and origin/owner checks remain unchanged.
- Shared runtime recovery annotates the first invalid call, gives a stronger second correction, and refuses a third equivalent strategy before dispatch. A different valid call remains allowed. A fourth equivalent invalid attempt requires an interactive final explanation; further tool requests fail the Run rather than spin. Background work terminates durably with `invalid-tool-strategy`. Counters reconstruct from Core-generated checkpoint receipts. Policy denials retain their existing handling.
- Trusted prompt facts derive only registered tool/capability names, bounded outcome counts, Core Run identity/status and allowlisted interruption reasons. Current and previous Runs are separate. Tool arguments, page content, references, protected values, private URLs and model claims never enter these facts. A successful protected fill or click does not establish authenticated state.
- General Assistant v20 and Secretary v7 publish the current search guidance. v17/v18 and v5/v6 remain immutable and retrievable for pinned owners. v19 is not a built-in publication, avoiding collision with an existing owner-authored version. Existing owner selections/data were not changed.
- `credentials.list` normalizes null/blank cursor and null limit to omission, preserving pagination, alias validation, bounds and owner scope. Live comparison exposed this optional-field issue. Invalid non-empty values still fail.
- The Real demo already defaults to DeepSeek V4.1 Flash Medium. That default and explicit per-Session choices are preserved.

## Runtime verification

The two-origin generic SSO fixture redirects from the application to an identity page, validates synthetic credentials on the server, issues a single-use callback ticket and serves a protected record only after authentication. Native Chromium DOM inspection and server counters independently verify the result. The task prompt supplies only the demo address, ordinary email, saved credential name and requested record status; it contains no tool names, targeting hints or call sequence.

Offline scenarios exercise normal login, third-call refusal followed by valid recovery, continued malformed-strategy termination, userSteer after protected fill, denied identity origin, unknown binding and wrong credential origin. The interruption case proves the next Run sees previous loaded/fill evidence, empty current capability loads, and no authenticated state. Background Experience regressions verify rejected content cannot change its source or promote memory and terminates within the recovery budget.

The cross-model comparison requires explicit `AGENTCORE_BROWSER_BENCHMARK_LIVE=1` plus `OPENROUTER_API_KEY`; default suites skip it without credentials. Each configuration executes three isolated trials with Medium effort. Metrics contain model/configuration, trial, request/tool counts, valid/malformed calls, repeated/blocked failures, capability load, externally verified completion and elapsed time. No prompts, arguments, URLs or protected values are logged. The optional higher-effort Luna comparison was not requested as a mandatory configuration and was not run.

## Initial live comparison — retained failures

Before the credential-list optional-field correction, both models authenticated and retrieved the record in two of three trials. Neither produced malformed browser.find calls. Luna produced seven credential validation failures across its three trials; the initial metric's malformed counter excluded `ValidationError`, so the corrected count comes from the retained safe receipt-code diagnostics. DeepSeek's third trial stopped before protected-fill capability loading. These failures are retained; successful model claims are counted only when the DOM/server verification agrees.

| Medium configuration | Verified completion | Credential validation failures | Mean elapsed |
| --- | --- | --- | --- |
| DeepSeek V4.1 Flash | 2/3 | 0 | 14.8 s |
| GPT-5.6 Luna | 2/3 | 7 | 25.7 s |

## Corrected candidate verification

The corrected six-trial comparison independently verified authenticated protected-record state in all six trials, with capability loading in every trial and zero malformed/repeated/blocked tool calls. Luna completed all three final reports. DeepSeek completed two; its second trial authenticated successfully but failed the final semantic response with `InvalidResponse/invalidJson` after the existing bounded repair. The paid compatibility gate therefore remains **5/6 completed reports**, not fully green. The native login gate is **6/6 independently verified**. This small sample does not establish a general model ranking.

| Medium configuration | Trial | Requests / tool calls | Valid / malformed / repeated / blocked | Capability loaded | DOM verified | Report completed | Elapsed |
| --- | --- | --- | --- | --- | --- | --- | --- |
| DeepSeek V4.1 Flash | 1 | 7 / 10 | 10 / 0 / 0 / 0 | Yes | Yes | Yes | 14.7 s |
| DeepSeek V4.1 Flash | 2 | 8 / 36 | 36 / 0 / 0 / 0 | Yes | Yes | No: invalidJson | 55.9 s |
| DeepSeek V4.1 Flash | 3 | 8 / 10 | 10 / 0 / 0 / 0 | Yes | Yes | Yes | 11.4 s |
| GPT-5.6 Luna | 1 | 7 / 9 | 9 / 0 / 0 / 0 | Yes | Yes | Yes | 22.5 s |
| GPT-5.6 Luna | 2 | 9 / 9 | 9 / 0 / 0 / 0 | Yes | Yes | Yes | 21.5 s |
| GPT-5.6 Luna | 3 | 9 / 9 | 9 / 0 / 0 / 0 | Yes | Yes | Yes | 22.1 s |

The follow-up recovery receipt budget/restore guards do not alter successful browser operations or provider response parsing. They are covered by final offline tests; no additional paid trials were used to replace the failed model sample.

Local backend: the complete solution passed (2,876 tests, 12 opt-in skips); final Application rechecks added two checkpoint regressions and passed **1,395 Application tests**, making the exercised backend total **2,878 passes / 12 skips**. Domain 173, Infrastructure 916, API 390 and OrderEvents 4 passed. The targeted durable background regression passed all four cases. Final Application verification includes native SSO positive/recovery/interruption/security cases and checkpoint budgets.

Frontend production build passed after the final diagnostic allowlist change. The initial frontend run failed on unavailable localStorage and rendering timeouts. Local Node 22 experimental server webstorage conflicted with jsdom; `node --no-experimental-webstorage node_modules/vitest/vitest.mjs run --maxWorkers=2` passed all **764 tests / 99 files**, preserving all assertions. The earlier single-worker attempt was stopped after identifying the environment conflict. The added diagnostic case then passed with its focused **13-test** file; the current suite contains 765 tests.

Focused Synthetic Playwright verification passed **10/10** diagnostic and Instance Skills cases, including the new invalid-tool-strategy regression and v20 provenance. Playwright MCP independently created a v20 General Assistant in an isolated SQLite Synthetic host, triggered repeated malformed calls, observed a bounded failure with `invalidToolStrategy`, copied the details, reloaded and verified the same diagnostic ID/reason, then sent `Hello` and received a normal response in the same Session. The UI stayed Ready, with zero browser console errors and successful API reads. These hosts used disposable data; no owner Session or credential was modified. Exact-candidate hosted CI is pending.

PR #4 has separate conflicts with main; this work does not resolve them. Implementation/verification of the review recommendations is separate from claiming 100% live-model task completion or final migration acceptance.
