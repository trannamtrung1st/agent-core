# Email monitoring integration with current main

Date: 2026-10-13. Branch: `develop/branch-1`. Merge source: `c789a54912b97bd14890276e052e2fd40fd34e58`, with parents email closure `60e3d2c3` and fetched main `d425b121`.

## Scope and conflict resolution

Merge the current Admin reorganization while preserving the original email watcher, APIs, migrations, Event-to-Run admission and security fences. The three textual conflicts in `docs/16-testing-strategy.md`, `docs/18-implementation-plan.md` and `web/src/app.css` retain both sides’ applicable sections/styles. Main owns the contextual navigation, canonical routes and 1024px breakpoint; the email branch keeps inline-size table containment and account detail composition. The automatically combined Playwright host config contained a duplicate OAuth property; it is reduced to one correctly isolated callback URL.

Email setup links use canonical registry helpers. Instance monitoring drafts report to the retained workspace’s unsaved indicator and departure guard. Credential refreshes retain mounted details so a connection check does not consume monitoring edits. The OAuth callback removes only `emailAccount` and preserves native history state, other queries and the anchor. Gmail access guidance names main’s Capabilities & credentials page.

This integration does not modify backend behavior or renew either historical milestone freeze. Earlier email and Admin reports remain evidence for their original source only. Real mailbox UAT remains separate and unverified.

## Observed browser use cases

Playwright MCP used the known disposable SQLite Synthetic host at API 5394 / Vite 5194, without operator databases or provider calls.

- Restarted Synthetic Gmail monitoring disclosed a coverage gap. Check now queued and completed a scan; Cancel restored the saved setting; saving Off returned the status to Off. The initial assertion expecting ordinary Monitoring On failed because this existing fixture had a persisted cursor from the previous process. Inspection confirmed the truthful gap state; the recovery scenario then passed. Fresh-process normal-path coverage is exercised separately by the E2E suite.
- A bound Custom account draft survived the canonical Automations link and return to Capabilities & credentials. The Instance displayed its unsaved warning. Chat departure opened the shared guard; Stay preserved the draft, and Cancel restored saved values and cleared the warning.
- At 390px, the navigation Drawer supported Escape and restored focus to its opener.
- Browser console: zero errors. Relevant owner API requests returned 200; no failed owner request was observed in the successful flows.

## Rendered integration evidence

One batched inspection of global Gmail details at 1440/1024/768/390/320 and bound Custom details at 1440/390 found no document horizontal overflow. Main’s 248px sidebar is present at 1024px and above; smaller widths use its existing Drawer. Global monitoring content widths were 1094/678/670/300/230px respectively. Existing AntD tokens, 8/12/16 spacing, shared panel/detail layouts and table-local scrolling remain the owners. No additional discretionary visual correction round was needed.

Captures: [global desktop](assets/email-main-sync/global-1440.png), [1024px boundary](assets/email-main-sync/global-1024.png), [tablet](assets/email-main-sync/global-768.png), [phone](assets/email-main-sync/global-390.png), [320px](assets/email-main-sync/global-320.png), [Instance desktop](assets/email-main-sync/instance-1440.png), [Instance phone](assets/email-main-sync/instance-390.png).

## Initial local verification

| Check | Result |
| --- | --- |
| Strict TypeScript and production Vite build | Passed; existing bundle size advisory |
| Initial focused route/email component suites | 43 passed, before the additional dirty-report regression |
| Integrated `EmailMonitoring*` API/watcher recovery/Event-to-Run tests | 4 passed, 0 skipped |
| Scoped Impeccable type/layout detector | Passed, no findings |
| Initial full frontend suite including dirty-report regression | 969 passed across 117 files, 505.51s, 0 failures |
| Email plus Admin shell/navigation/overflow E2E | 27 passed, 6.2 minutes, 0 failures |
| Documentation/design links and syntax | 18 Markdown files, 706 local targets and 217 anchors valid; design JSON parses and whitespace/conflict checks pass |
| Initial exact merge-source hosted workflow | [Run 38372022818](https://github.com/trannamtrung1st/agent-core/actions/runs/38372022818) tested `c789a549`: six jobs passed; browser shard 2 failed the inherited immutable-inspector readiness race described below |
| Inspector correction regression | 9 passed in 1.0 minute, including a deliberately held immutable-version read and the original publication/Skill-pin assertions |

Commands used Node 22.18.0 explicitly: from `web`, `node node_modules/typescript/bin/tsc --noEmit`, `node node_modules/vite/bin/vite.js build`, and `node node_modules/vitest/vitest.mjs run --maxWorkers=2`. Backend: `dotnet test tests/AgentCore.Api.Tests/AgentCore.Api.Tests.csproj --no-restore --disable-build-servers --nologo --filter 'FullyQualifiedName~EmailMonitoring'`, with live-provider opt-ins disabled. Browser: `node node_modules/@playwright/test/cli.js test --config=/tmp/email-monitor-review.playwright.config.mts --project=synthetic email-monitoring.spec.ts email-accounts.spec.ts system-credentials.spec.ts admin-tab-navigation.spec.ts admin-shell.spec.ts admin-table-overflow.spec.ts`, with separate API 6094 / Vite 6194, disposable SQLite and matching proxy/OAuth origins. Both native MCP hosts and the runner hosts were stopped after verification. Local logs remain under `/tmp/email-main-sync-*`.

The browser regression covers connection-test refresh while dirty, OAuth history index, canonical contextual links, retained monitoring choices and Instance departure protection at all four existing email viewports. Human screen-reader narration, device audio and live Gmail/IMAP remain unrun.

## Hosted inspector readiness correction

The initial hosted run passed backend (3,879 tests / 27 opt-in or runner-readiness skips), both frontend shards, core shard 1 (107 cases), all 16 separately isolated acceptance cases and both Compose checks. Core shard 2 passed 106 cases and failed `p8-skill-journey.spec.ts` while `navigateAdmin` waited for a Skills link in the contextual rail. The retained failure snapshot shows the immutable-version drawer had eventually rendered its local Skills & resources tab. The helper made its local-versus-route decision before that asynchronous version read completed, so it selected a destination absent from the Definition's global rail. This is a migrated test-helper race, not missing published Skills.

The shared helper now recognizes an inspector dialog and waits for its requested local tab before clicking. The P8 fixture deliberately holds the exact `ForkDurable` version read, starts navigation while the loading indicator is visible, releases the read, and asserts the original immutable Skills, protected read-only controls, managed-chat behavior and persisted Always/OnDemand pins. The first nine-case check passed; an initial delayed-read fixture used the wrong source-kind query and was interrupted, then corrected with a bounded request-start assertion. The final nine-case run passed with the actual held read. The inspector correction at `cb68e3bb` changed no product code, destination assertion, journey deadline or global timeout. The subsequent Gmail correction below changes only its asynchronous UI lifecycle.

The replacement [exact-source hosted workflow](https://github.com/trannamtrung1st/agent-core/actions/runs/38375504669) tests `cb68e3bbbaf496513c10c6069de21ba0d1e3a7d5` and is required before claiming whole-suite integration acceptance; its result is recorded after completion. Logs and the initial failure snapshot remain under `/tmp/email-main-sync-ci-*`; the corrected focused command adds `p8-skill-journey.spec.ts admin-collections.spec.ts` to the same isolated two-host runner configuration.

## Gmail lifecycle and native-target follow-up

Replacement run [38375504669](https://github.com/trannamtrung1st/agent-core/actions/runs/38375504669) passed the inspector journey, backend, both frontend shards, acceptance and Compose. Browser shard 1 passed 106 cases and failed the 390px Gmail Last checked assertion; shard 2 passed 105 cases and failed two native-link cases. This run is not acceptance.

The Gmail panel had unfenced startup reads that could replace a completed access check. A deterministic StrictMode regression holds the retired first read through a successful second read/check and then returns a null check timestamp. The corrected panel fences reads and actions by identity/revision, retires older reads when acting, and ignores results/refresh callbacks after account changes or closure. All three lifecycle regressions and the existing account/monitoring/credential tests passed: 29 tests. Playwright MCP held a stale GET projection with null Last checked, performed the real Synthetic POST check, released the old read, and observed Last checked and Access verified retained. This is a UI race fixture, not a provider failure simulation. Console errors: zero.

Native failure traces show a background destination issuing successful Admin requests while Playwright never delivered its Page event in the desktop case; the mobile case read its document during an execution-context replacement. The native helper foregrounds the source and waits for the real Admin tooltip before the gesture, then uses Chromium target discovery to activate only the newly created native page in the source context before Page initialization. It does not create a scripted replacement tab. Actual middle/modified gestures, canonical URL/content, source draft, Back and Stay/Discard assertions remain. Readiness polling retries only the specific transient context-replacement errors within the existing 15-second deadline. Three repetitions at each width passed: 6 cases, 42.0s.

After these corrections, strict TypeScript and Vite build passed (the first new unit fixture incorrectly used Playwright's `exact` option in RTL selectors; it was removed). All 11 Gmail/Custom/monitoring/shared-binding browser cases passed again in 3.5 minutes. The final local frontend suite passed all 972 tests across 118 files in 478.74s. The replacement [exact-source hosted gate](https://github.com/trannamtrung1st/agent-core/actions/runs/38381523202) tests `bd51d95047c857876d8fd9445c2e28a245ac1225` and remains in progress; its final result is recorded after completion. Product styles and the earlier bounded visual captures remain unchanged. All own native and CLI app hosts have been stopped.

## Native tab channel correction

Run [38381523202](https://github.com/trannamtrung1st/agent-core/actions/runs/38381523202) on `bd51d950` passed all Gmail and inspector cases, all 107 core shard 1 cases, both frontend shards, backend, acceptance and Compose. Core shard 2 passed 106 cases and failed only the mobile native-tab Page-event wait. Early activation did not fully remove the Chromium/Playwright Page-channel initialization race, so this run is not whole-suite acceptance.

The native helper now discovers a newly created page target in the source Chromium browser context after the actual middle/modifier click. It activates and attaches to that same target, reads its actual document through the public CDP session, verifies the unchanged canonical URL, complete document and visible exactly named table/heading, and closes that target. It never creates a replacement page or retries the click. Source URL, unsaved persona, Back, Stay and Discard assertions remain unchanged, as does the 90-second journey deadline. Direct target messaging avoids depending on a Page event that can remain undelivered despite a loaded destination. Specific initial execution-context transitions are polled within the existing 15-second readiness bound. Cleanup closes only the detected native target and detaches its test session.

Three repetitions at each existing width passed: 6 cases in 39.5 seconds, with all native creation/content/draft/history assertions intact. This correction changes only the browser fixture; product source remains identical to `bd51d950`. The final exact-source hosted result is recorded below.

## Final integration acceptance

All seven jobs in [run 38385688652](https://github.com/trannamtrung1st/agent-core/actions/runs/38385688652) passed on exact source `70691cad72cbaa1ed221eba4d3e534deb561d291`. Product behavior is unchanged from `bd51d950`; only the native browser fixture and evidence changed afterward. Main remained `d425b121` when rechecked and is an ancestor of this branch. The final evidence commit changes documentation only.

| Final gate | Observed result |
| --- | --- |
| Local full frontend | 972 tests across 118 files passed; 478.74s |
| Local focused component correction | 29 passed |
| Local email/monitoring/shared bindings after correction | 11 passed |
| Local native-target repetitions | 6 passed; three at each width |
| Earlier merge-source Admin shell/navigation/overflow/email batch | 27 passed |
| Inspector held-read regression and inventory consumers | 9 passed |
| Strict TypeScript and production build | Passed; unchanged bundle size advisory |
| Hosted backend | Domain 195, Infrastructure 1,319, Application 1,802, API 563: 3,879 passed / 27 skips / 0 failures |
| Hosted frontend | Both complete unit/build shards passed |
| Hosted core/browser speech | 107 + 107 = 214 passed / 0 failures, including native link, Gmail and inspector regressions |
| Hosted separate acceptance | 16 passed: Manual 1, lifecycle 1, Admin 1, harness 6, continuity 2, maintenance 1, Secretary 4 |
| Hosted Compose | Both SQLite/key-ring and email cursor/generation recreation checks passed |
| Documentation/design | 18 Markdown files, 706 local targets and 217 anchors valid; design JSON parses; whitespace checks pass; design hook/manual scoped check reported no deterministic findings |

Hosted backend skips retain the existing 21 opt-in checks and six Docker sandbox runner-readiness checks; the backend implementation and tests are unchanged by this integration. The final local backend evidence for this UI merge is the four integrated monitoring cases, not a newly claimed full local backend run. No provider credentials, real mailbox, external delivery, human screen-reader narration or hardware audio acceptance is claimed. Existing historical freezes and P10/P11 remain unchanged. The requested main merge and non-live UI integration are complete.

## Latest main synchronization and PR candidate

The next synchronization merges main `f2d5840d` into the email branch. Four conflicts were reconciled: design context, the Admin surface brief, the frontend specification and the Instance credential renderer. Both applicable documentation sections are retained. Main's focused Credential bindings route owns the bound-account view, while monitoring's stable dirty callback continues to participate in workspace status and departure protection. Gmail account-access guidance now targets `/credentials#credentials`; the monitoring browser journey follows the same focused page. Existing immutable-inspector readiness and native-target verification corrections remain.

Candidate verification: strict TypeScript and the production Vite build pass; 99 focused frontend tests across eight files pass. Playwright MCP edited the owned disposable Custom account's monitoring draft, followed its Automations link, returned to Credential bindings, confirmed the retained draft and departure guard, chose Stay, and canceled back to saved Off. No document overflow was observed at 1440 or 320px; the exercised requests succeeded and browser console errors were zero. A first return-navigation attempt used a hidden mobile rail; the check continued through the visible desktop rail without changing the application.

Repeatable Synthetic account, monitoring, credential, configuration-action and native-navigation checks plus the PR's complete hosted workflow establish the final candidate gate. Their completion is reported on the PR; the earlier exact-source acceptance above remains historical. Real-mail and human-device checks remain deferred, and no historical freeze or P10/P11 status changes.

The first PR run on `73f01c66` exposed an inherited lifecycle fixture mismatch: `savePersonaAndAwaitPatch` still clicked the retired local Save persona button after main introduced the shared Save configuration action. The hosted error context showed the shared action enabled and the edited persona retained. The helper now clicks that exact accessible action and continues to require the same successful durable persona PATCH. The complete local lifecycle journey then passed (25.3s), including Form/JSON persona revision checks, version/lifecycle operations, pinned conversations and deletion guards. This correction changes only test navigation, with no weakened timeout or behavior assertions.

The helper's other consumer also retained a Profile URL before Archive, although main moved lifecycle controls to their focused page. Its first local attempt was stopped after locating that stale destination; changing the destination to Lifecycle produced a complete 17.6s pass, including Form/JSON persona saves, archive rejection of new chats, owner chooser exclusion and both pinned histories. Together these two complete journeys verify every consumer of the corrected helper. The earlier 21 merged UI regressions and 99 focused tests passed before these test-only corrections. The superseding PR run is the final hosted gate.
