# P9 — Visible browser closure evidence

**P9** is **closed** on `bba1de437b8410b2226b846adc8a0418af0c69cb`. Hosted Synthetic workflow [`36890525463`](https://github.com/trannamtrung1st/agent-core/actions/runs/36890525463) is **green** on that SHA. **P8** remains frozen on `ca3eb235a3b458e55002d2c8f610df868b2cd39d`. **P8.5** remains closed on `1461567fba062774647e80827f4f97c11e41221f`. This report does not rewrite those reports. P10 and P11 stay requirement-triggered.

## Candidate

| Item | Value |
| --- | --- |
| Baseline before P9 | `60543e3d3988e92b50deb6d0242a07c634f0dafb` |
| Policy and admission | `b4b2e821ef38caaf9a463de02e7d8898ef01d3a3` |
| Fixture and Playwright adapter | `1fabaa6` then review fix `8d2490033e3b2df66fdef778e99818ebd2736b0f` |
| v11 offer | `00deb92` then review fix `b0a717f` |
| Session release | `a7b932d` |
| Scripted AC-1042 journey | `0d9214e6327d36f6ceff228b1ea5b8b69a854d77` |
| `Using browser…` and docs | `db43f9c6428eb76d00bf7e3e9632d61e1be4fe7d` |
| Vitest headroom | `ac8029f548efd15fa4a64b34525248fac319ca2a` |
| Offline job timeout | `bba1de437b8410b2226b846adc8a0418af0c69cb` |
| Closure candidate | `bba1de437b8410b2226b846adc8a0418af0c69cb` |
| This report | The commit that records the hosted URL. Its parent is the closure candidate |
| Hosted checkout | [workflow `36890525463`](https://github.com/trannamtrung1st/agent-core/actions/runs/36890525463) — **green** on `bba1de437b8410b2226b846adc8a0418af0c69cb`. Synthetic offline gates 31m 46s and Synthetic Compose smoke 1m 19s both succeeded |

## What shipped

`general-assistant` v11 allowlists `browser.navigate`, `browser.observe`, and `browser.act` and publishes skill `browser.record.lookup`. Versions v1–v10 are unchanged. Application owns `IBrowserSession`, `IBrowserSessionLease`, and `BrowserTargetPolicy`. Infrastructure owns direct `Microsoft.Playwright` 1.63.0 and the loopback fixture. Playwright types do not leave Infrastructure. There is no MCP sidecar and no second tool host.

A user message containing `record AC-1042` navigates the trusted fixture start URL, observes, loads the skill, sends one application message, fills and searches, opens the record, and answers `AC-1042 is In review.` The first browser generation does not send a chat message. Chat progress for `browser.*` reuses `agent.progress` kind `runningTool` with message `Using browser…`. There is no browser panel, iframe, screenshot, or click log.

## Headed demo

2026-10-01, local Synthetic. API `http://127.0.0.1:5080` with `Browser__Headless=false`, `Browser__Enabled=true`, `Browser__InteractionMode=InteractiveDemo`, `Browser__FixturePort=5091`, `Browser__TargetOrigins__0=http://127.0.0.1:5091`, and a disposable SQLite file. Vite `http://127.0.0.1:5173` proxied at that API. Health was `{"status":"healthy","profile":"Synthetic","protocolVersion":1}`.

`pnpm exec playwright test e2e/p9-browser-journey.spec.ts --project=synthetic` reused that host (no `CI=1`). The test browser was headless and drove Chat only. It did not click the product window. During the journey, process samples showed `Google Chrome for Testing` from `~/Library/Caches/ms-playwright/chromium-1243` with no `--headless` flag, and a separate `chrome-headless-shell` for the test runner. Chat showed one application message, `I found the record. I'm checking the details now.`, and one answer, `AC-1042 is In review.` The activity row cleared. The test passed in 11.2s.

## Verification

Local commands ran on this machine before the hosted row. `dotnet test AgentCore.sln` ran on `db43f9c` plus the uncommitted Vitest timeout; that timeout does not affect the .NET suites. The frontend suite and build ran after the timeout change. Playwright and Compose ran on that same tree. The hosted row is `bba1de4`, which adds the 45 minute offline-job limit on top of `ac8029f`.

| Command | Result |
| --- | --- |
| `dotnet test AgentCore.sln --nologo` | Passed. Domain 140. Application 1010 passed, 1 skipped. API 256. Infrastructure 608 passed, 13 skipped |
| `pnpm install --frozen-lockfile` was already current; `pnpm run test --run --maxWorkers=2 && pnpm run build` in `web/` | 77 files, 566 passed. Production build succeeded |
| `CI=1` Playwright `synthetic` + `browser-stt` + `browser-browser` | 70 passed in 5.3m, including `e2e/p9-browser-journey.spec.ts` |
| Faithful Manual-A, admin-lifecycle, p76-admin | 1 passed each |
| `./scripts/compose-sqlite-volume.sh` | Passed. The script does not send the AC-1042 prompt. The image does not install Chromium |
| Headed Chat demo above | Passed. Product Chromium had no `--headless` flag |
| Hosted workflow `synthetic` on `bba1de437b8410b2226b846adc8a0418af0c69cb` | [workflow `36890525463`](https://github.com/trannamtrung1st/agent-core/actions/runs/36890525463) green. Offline gates 31m 46s. Compose smoke 1m 19s. Earlier run [`36886208279`](https://github.com/trannamtrung1st/agent-core/actions/runs/36886208279) on `ac8029f` was cancelled at the 30 minute job limit during Chromium install. `bba1de4` raises that job to 45 minutes |

The Application skip is the opt-in live historical-image reread. The Infrastructure skips are opt-in live-provider or Docker-sandbox tests. Live-provider probes were not run.

## Acceptance map

| AC | Evidence |
| --- | --- |
| AC-01 | `git diff 60543e3..bba1de4` does not touch `agents/general-assistant-v1.json` through v10, `docs/reports/p8-freeze-candidate.md`, or `docs/reports/p8.5-freeze-candidate.md`. Freeze SHAs stay `ca3eb23` and `1461567` |
| AC-02 | Headed demo. Chat drove the turn. The product window was Chrome for Testing without `--headless`. The test runner did not click that window |
| AC-03 | `BrowserRecordJourneyTests.Scripted_record_lookup_sends_one_message_and_one_answer` ends in one assistant answer on the existing pump |
| AC-04 | `BrowserToolTests` deny role, closed gate, and out-of-scope calls without `Navigate` |
| AC-05 | `BrowserTargetPolicy` tests plus `Redirects_and_popups_stay_inside_the_allowlist` |
| AC-06 | `Contexts_do_not_share_cookies_storage_or_element_refs` and cross-session ref denial |
| AC-07 | `Browser_port_does_not_reference_playwright`. `Microsoft.Playwright` is referenced only from `AgentCore.Infrastructure.csproj` |
| AC-08 | Headless adapter journey plus the headed Chat demo use the same fixture script |
| AC-09 | Headless adapter journey and Chat E2E navigate, search, open the detail, and answer `In review` |
| AC-10 | `Observation_bounds_hostile_page_text_without_widening_policy` |
| AC-11 | `Malformed_browser_arguments_do_not_call_the_provider` |
| AC-12 | `Stale_and_cross_session_refs_do_not_click` and `Stale_ref_is_rejected_after_navigation` |
| AC-13 | The scripted journey's first request has no `app.message.send`. `Message_unlock_and_budgets_stay_unchanged` |
| AC-14 | `Skill_requirement_does_not_grant_browser_tools_or_origins`. The journey loads `browser.record.lookup` without granting origins |
| AC-15 | `Cancel_and_steer_drop_a_late_navigation_and_do_not_navigate_again` |
| AC-16 | `Reconnect_keeps_one_answer_and_does_not_navigate_again` |
| AC-17 | `Provider_loss_keeps_history_and_does_not_navigate_again` and `Closed_context_and_release_are_unavailable_without_affecting_another_session` |
| AC-18 | `Background_and_unconfigured_browser_work_does_not_call_the_provider` |
| AC-19 | `Headless_journey_reaches_the_record_and_redacts_isolate_secrets` |
| AC-20 | `Act_stays_on_interactive_loopback_pages` and `Read_navigation_can_open_a_page_but_cannot_act`. The fixture has no irreversible action |
| AC-21 | `activityState.test.ts` prefers trusted `Using browser…` on the existing `runningTool` kind. No new conversation role |
| AC-22 | No Chat or Admin component layout changed. The activity row already renders the trusted progress message. W06 review 0010 accepted that empty visual diff. No new Impeccable findings to fix |
| AC-23 | Observed behavior is in `/docs` owners and `.agents/context/PRODUCT.md` and `DESIGN.md`. Historical freeze reports were not edited |
| AC-24 | Commands in the verification table. No hosted key and no public-site dependency |
| AC-25 | Headed demo section. Headless CI is not this gate |

## Architecture self-review

No universal plugin abstraction. No MCP dependency in the core runtime. No Playwright type in Domain or Application. Target checks reuse `BrowserTargetPolicy` and the existing executor; there is no second policy engine. Progress reuses `runningTool`. The scripted journey is one user-turn pump, not a recursive loop. Browser contexts are per session, not an Agent Instance profile. Scheduled occurrences and application events do not run browser tools. Session Runtime remains the mailbox owner.

## Security self-review

The model cannot add origins. Page text, including the hostile fixture sentence, is untrusted and does not widen the allowlist. Cookie, storage, and password-field values are redacted before they leave Infrastructure. Cancel and steer drop a late navigation. Element refs do not work across sessions. `browser.act` stays on InteractiveDemo loopback pages that are already allowlisted.

## UX self-review

The headed window is the browser view. Chat stays primary and shows one useful intermediate message, then one answer. `Using browser…` is transient progress and is not stored as a conversation turn. Failure and stop keep the existing Chat patterns. No browser-control panel was added.

## Documentation self-review

`/docs` describes the observed port, adapter, fixture, progress message, and journey. `TODO.md` records P9 status. `PRODUCT.md` and `DESIGN.md` say the activity row carries `Using browser…` and that no browser panel is added. P8 and P8.5 reports stay historical. P10 and P11 are not pulled forward.

## Hosted conclusion

Workflow [`36890525463`](https://github.com/trannamtrung1st/agent-core/actions/runs/36890525463) is **green** on `bba1de437b8410b2226b846adc8a0418af0c69cb`. Synthetic offline gates succeeded in 31m 46s. Synthetic Compose smoke succeeded in 1m 19s. P9 is closed on that SHA. This report commit does not move it.

## Appendix — post-closure Impeccable evidence

This appendix does not move `bba1de4`. No Chat or Admin layout was redesigned. The activity row still renders trusted `Using browser…` and then clears.

Audit date: 2026-10-02. Surface: Chat during the AC-1042 journey. Mode: Operate. Detector command: `.agents/skills/impeccable/scripts/impeccable detect --json web/src/features/chat/AgentActivity.tsx web/src/features/chat/activityState.ts web/src/app.css`. Result: `[]`.

Journey command, on ports 5090/5193 because 5080 was not a disposable Synthetic host: `CI=1 PLAYWRIGHT_API_PORT=5090 PLAYWRIGHT_WEB_PORT=5193 PLAYWRIGHT_SQLITE_PATH=/tmp/agent-core-p9-hardening-3.db pnpm exec playwright test e2e/p9-browser-journey.spec.ts --project=synthetic` in `web/`. Result: 1 passed (18.3s). The test browser was headless. It sent `Please look up record AC-1042.` and did not click the product Chromium window. The 390 shell measurement below is from the immediately preceding run of the same journey.

| Check | Expected | Observed |
| --- | --- | --- |
| Progress | `Using browser…` appears, then the activity row clears | Appeared during the turn. `.agent-activity` count was 0 after settle. History rows did not keep that text |
| Copy | One application message, then one answer | `I found the record. I'm checking the details now.` then `AC-1042 is In review.` |
| 1280×900 | Conversation column and document do not overflow | Neither overflowed |
| Reduced motion | No animation on the conversation column | `animation-name: none`, `transition-duration: 0s` |
| 390×844 | Conversation column does not overflow | Column did not overflow. The document did: `.app-shell` was 552px wide in a 390px viewport, and the header row was wider than its box. That is existing shell chrome. It was left unchanged |
| Console | No new product errors | No console errors. No page errors |

No polish edit. The progress row stays the existing activity line.

## Appendix — post-closure browser hardening

This appendix does not move `bba1de4`. Hosted Synthetic has not been re-run for this pass.

Two local Real attempts failed for different reasons. The nopCommerce attempt hit `target_denied` because the browser was limited to the Record Lookup fixture origin, and a later turn opened that fixture because it was advertised as `Trusted browser start`. `target_denied` itself is ordinary tool JSON. A separate Google attempt did open the site; Google then showed a human-verification page, and the text turn failed as `InvalidResponse` / `invalidSpeech` / `responseFunction` because unused speech metadata invalidated `agent_core_respond`. That Google failure is not evidence of `InvalidAgentStep`. Text turns now canonicalize unused speech to `same` with null text. `PolicyMode` `OpenWeb` is the local/demo browser policy. `Restricted` remains the Synthetic and CI policy, and it still appends a fixture start sentence only when every navigation origin is loopback. Hosted Synthetic has not been re-run for this pass.
