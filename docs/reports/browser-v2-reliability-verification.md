# Browser v2 reliability, discovery and bounded recovery

Date: 2026-10-09. Scope: focused follow-up to the existing Browser v2/AgentRun cutover. No migration, new architecture, widened capability/origin policy or immutable Definition rewrite. P10/P11 remain unopened.

## Reproductions before correction

- `BrowserReliabilityTests`: three UTF-8 snapshot-budget cases (512/1024/4096 bytes) failed because a successful dense snapshot became `output_limit`; a non-default Definition bootstrap test failed because `browser.close` was absent. Four failures reproduced before changes.
- `BrowserReliabilityJourneyTests`: actual Chromium returned `stale_reference` for literal `main`; expected `invalid_reference`. One failure reproduced before changes.
- Static trace confirmed scope capture always called full-page capture first and depth only filtered content, with full element/text summaries still serialized. No screenshots or model explanations were treated as proof of an underlying tool failure.

## Implementation

Application projects successful browser observations against actual remaining UTF-8 headroom. Metadata, identity, discovery guidance, accessibility content, visible text and prioritized control/action summaries share that budget. Provider snapshots/indexes survive presentation clipping. Find pagination accounts for results omitted by byte projection.

Infrastructure retains native ARIA/native Locator semantics, a 4096-node index, a 512-KiB per-frame native-input bound, bounded role batches for explicit accessible names, and bounded concurrent reads for computed names. Scope/depth reads avoid full-page enumeration and retain last-observed outside-subtree index entries with fresh opaque refs; use-time Locator checks reject missing/ambiguous targets. Native ancestry refs support subtree discovery without model selectors or provider-wide enumeration per search. Search includes safe accessible names/node text and ancestry, role/name/region filters, counts and pagination. Find control-state JSON uses consistent lower-case fields.

Reference errors distinguish malformed, unknown, foreign Session, invalidated snapshot, missing Locator, new ambiguity and non-actionable targets. A bounded retirement cache recognizes recent invalidations. Compact eligible Browser bootstrap includes closure; advanced tools use existing AgentRun-owned discovery/load state. Guidance requires discovery, one effect, observed resulting state and bounded recovery. Alternative semantic evidence can clear repetition; repeated identical matches cannot. No automatic effect retry mechanism was added.

## Runtime evidence

The independent `browser-dense.html` fixture has 2000 entries in nested collections plus a grid and a form. It contains no real application identifiers or selectors in the model contract.

| Scenario | Actions and expected/observed outcome |
| --- | --- |
| Dense discovery/rerender | Navigate, search Entry 1999 outside clipped content by role/collection, replace the DOM nodes, click the native ref. Chromium status becomes `Opened Entry 1999`. Reusing the invalidated ref returns `stale_reference`. |
| Grid/scoping | Discover `Open review` through grid ancestry and click; Chromium status becomes `Review entry opened`. Repeated scoped depth-2 captures describe under 1% of full captured nodes and return under 10% of full content size. Outside-target discovery remains available. Shallow full-page presentation retains deep discovery. |
| Pagination | Search 2000 entries with offset 1990 and limit 3: matchCount 2000, returnedCount 3, nextOffset 1993. Byte-clipped find results advance only past returned matches. |
| Ref boundaries | `main` → invalid_reference; invented opaque token → unknown_reference; another Session's token → wrong_session_reference; removed node → target_missing; invalidated snapshot → stale_reference; newly duplicated name → ambiguous_reference; heading click → non_actionable_target. |
| Synthetic owned Run | Full SessionRuntime → executor → Chromium loop navigates, discovers/clicks Entry 1999, loads initially omitted `browser.fill_form`, fills Summary and reads its value. Actual Chromium value is `Verified change`; the advanced capability is retained on that AgentRun continuation. |
| Consecutive closure | Following user turns invoke browser.close, return closed then already_closed, and have no live browser context. The independently admitted Runs have reset loaded advanced capabilities. |
| Negative closure | Synthetic unavailable/unauthorized cases finish in two model requests with explicit inability/no authority, zero provider close effects and no false success claim. |
| Byte budgets | Successful snapshots remain valid/useful through real executor at 512/1024/4096 bytes, including multibyte content. No provider index entries are removed by presentation. |

## Verification ledger

Commands use `dotnet test <project> --nologo -m:1 -nr:false -p:UseSharedCompilation=false`. Single-process MSBuild avoids a reproduced local worker IPC permission failure; tests ran with allowed local process/socket execution.

- Focused Application/recovery/discovery/Synthetic journey: **45 passed** before the additional executor/pagination boundaries.
- Executor output/control-state boundaries: **9 passed**.
- Dense/new-reference/existing generic Chromium journeys: **6 passed**.
- Profile/foreign-ref/dense boundaries after updating precise contract assertions: **4 passed**.
- Final shared-checkout Application: **1359 passed, 2 explicit opt-in skips, 0 failed**.
- Final shared-checkout Infrastructure: **897 passed, 9 explicit opt-in skips, 0 failed**. The initial run found two old foreign-ref error assertions; both were corrected, with rejection/profile isolation unchanged.
- Isolated Browser-only candidate: Application **1345 passed, 2 explicit opt-in skips, 0 failed**; Infrastructure **893 passed, 9 explicit opt-in skips, 0 failed**. Counts differ because concurrent changes were excluded from that candidate. The final shared-checkout suites cover the latest combined working state.
- Whitespace check: `git diff --check` passed.
- Real-provider smoke: not run; no live-provider spend was authorized. Existing configured live smoke remains explicit opt-in.
- Exact-SHA hosted Synthetic gates: deferred by the user's explicit instruction to leave fixes for the combined commit. No commit or publication was performed for this follow-up. Hosted acceptance must be checked against the eventual combined commit SHA.

Local runtime verification and full suites passed. Final hosted acceptance remains pending the combined commit and its exact-SHA gates. Test logs are preserved under `local/verification/browser-v2-reliability/` (ignored local evidence).

## Follow-up consistency review

The second review reproduced two scoped-observation defects in actual Chromium: after a Collection 19 scope, an Entry 1999 search with Catalog ancestry/target returned no match; a depth-1 collection snapshot exposed Entry 1999 through unrestricted visible text. The index merge now retains page-relative depth and outer text/ref ancestry across repeated nested scopes. Shallow scopes omit unrestricted subtree text and mark text truncation. Refreshed root refs retain their original semantic ambiguity fence. Native match counts include existing depth/node-omitted duplicates, so omission alone does not create a false ambiguity rejection. Replaced/unindexed reminted refs are removed from the live ref map.

The extended dense journey scopes Collection 19 repeatedly, scopes its nested Entries group, discovers Entry 1999 through Catalog text/ref ancestry, and rejects a newly duplicated Collection target. A separate Chromium scenario captures one of two pre-existing Choose buttons through a shallow scope, clicks it and verifies its actual text becomes `1`.

- Reviewed Chromium reliability/native journeys: **7 passed, 0 failed**.
- Wider Browser regression before the final ambiguity refinement: **109 passed, 2 explicit opt-in skips, 0 failed**; the final refinement is covered by the 7 reviewed journeys and final full suite.
- Synthetic owned-run, budget, recovery and capability-projection checks: **44 passed, 0 failed**.
- Documentation links/fences: **0 issues**; authoritative port/backend contracts updated together.
- Full reviewed-build Application: **1359 passed, 2 explicit opt-in skips, 0 failed**.
- Full reviewed-build Infrastructure: **898 passed, 9 explicit opt-in skips, 0 failed**. Commands: `dotnet test tests/AgentCore.Application.Tests --no-build --no-restore --nologo -m:1 -nr:false -p:UseSharedCompilation=false` and the equivalent Infrastructure project command, against the freshly built reviewed code. Evidence: `local/verification/browser-v2-reliability/browser-review-*-full.log`.
- Publication remains deferred for the user’s combined commit; exact-SHA hosted acceptance remains pending.

## Custom caption discovery follow-up

The user reported that a visible sidebar label could be read but not opened because only its tree/container icons were indexed. The screenshot was treated as a reported symptom, not as evidence of a successful browser action. A separate generic fixture reproduces the behavior with 2000 captions, named Assets/Collection regions, repeated expand icons and delegated DOM click handlers, without per-caption ARIA roles. Before supplemental discovery, actual Chromium `browser.find` returned no match for the final caption (`browser-custom-reproduction.log`).

The provider now supplements native ARIA with bounded visible leaf span/div captions carrying pointer-cursor, click-handler or nonnegative keyboard-focus evidence. One read captures safe target metadata, exact-text duplicate ordinals and region ancestry; refs use exact native text Locators and a fixed provider-owned leaf filter. Targets rebind across DOM replacement, but fresh snapshots invalidate old refs and newly introduced duplicates reject effects. Hidden/static captions, disabled/inert nodes, protected-label descendants and form/frame-containing wrappers receive no custom click refs. Existing protected-field and origin policy checks run before effects. Text search includes both observed names and safe node text. Model guidance recommends role-free text discovery for custom controls.

The initial per-caption implementation also reproduced a scoped-snapshot deadline failure. Supplemental discovery now uses a single bounded bulk read; native explicit-label scoped batches describe only the selected nodes. No deadline or security check was relaxed.

- Chromium custom/native/reliability journeys: **9 passed, 0 failed**, command `dotnet test tests/AgentCore.Infrastructure.Tests --nologo -m:1 -nr:false -p:UseSharedCompilation=false --filter 'FullyQualifiedName~BrowserCustomTargetJourneyTests|FullyQualifiedName~BrowserReliabilityJourneyTests|FullyQualifiedName~BrowserV2JourneyTests'`.
- Full Synthetic owned SessionRuntime: **4 passed, 0 failed**, command `dotnet test tests/AgentCore.Application.Tests --nologo -m:1 -nr:false -p:UseSharedCompilation=false --filter FullyQualifiedName~BrowserReliabilityRuntimeTests`. The custom tree Run finds Entry 1999, clicks its ref, verifies `Opened Entry 1999` in actual Chromium, loads the form capability, updates/reads Summary, and closes across independent turns.
- The direct custom journey scopes Assets, rediscovers the target, replaces the DOM, clicks and asserts `Entry 1999 detail` plus status. Duplicate captions in North/South regions select the correct native ordinal despite preceding static/hidden equal text, including a tree named through `aria-labelledby` rather than an `aria-label` attribute. New ambiguity, lost pointer evidence and changed API-key semantics reject effects without changing the verified status.
- Final custom read/effect boundary: **2 Chromium journeys passed**, including read-only scope after lost pointer evidence.
- Full Application/Infrastructure regressions: pending the final verified-build runs after the `aria-labelledby` scope correction.
- Original live application: not retested; its URL/session was not supplied. No application identifiers, host-specific selectors or live model calls were used to implement or validate this fix.
- Shared ongoing changes remain preserved, with publication deferred for the user's combined commit and exact-SHA hosted gates still pending.

Logs: `local/verification/browser-v2-reliability/browser-custom-*.log`.
