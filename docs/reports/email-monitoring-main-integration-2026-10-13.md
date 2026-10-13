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

## Verification status

| Check | Result |
| --- | --- |
| Strict TypeScript and production Vite build | Passed; existing bundle size advisory |
| Initial focused route/email component suites | 43 passed, before the additional dirty-report regression |
| Integrated `EmailMonitoring*` API/watcher recovery/Event-to-Run tests | 4 passed, 0 skipped |
| Scoped Impeccable type/layout detector | Passed, no findings |
| Full frontend suite including new regression | 969 passed across 117 files, 505.51s, 0 failures |
| Email plus Admin shell/navigation/overflow E2E | 27 passed, 6.2 minutes, 0 failures |
| Documentation/design links and syntax | 18 Markdown files, 706 local targets and 217 anchors valid; design JSON parses and whitespace/conflict checks pass |
| Initial exact merge-source hosted workflow | [Run 38372022818](https://github.com/trannamtrung1st/agent-core/actions/runs/38372022818) tested `c789a549`: six jobs passed; browser shard 2 failed the inherited immutable-inspector readiness race described below |
| Inspector correction regression | 9 passed in 1.0 minute, including a deliberately held immutable-version read and the original publication/Skill-pin assertions |

Commands used Node 22.18.0 explicitly: from `web`, `node node_modules/typescript/bin/tsc --noEmit`, `node node_modules/vite/bin/vite.js build`, and `node node_modules/vitest/vitest.mjs run --maxWorkers=2`. Backend: `dotnet test tests/AgentCore.Api.Tests/AgentCore.Api.Tests.csproj --no-restore --disable-build-servers --nologo --filter 'FullyQualifiedName~EmailMonitoring'`, with live-provider opt-ins disabled. Browser: `node node_modules/@playwright/test/cli.js test --config=/tmp/email-monitor-review.playwright.config.mts --project=synthetic email-monitoring.spec.ts email-accounts.spec.ts system-credentials.spec.ts admin-tab-navigation.spec.ts admin-shell.spec.ts admin-table-overflow.spec.ts`, with separate API 6094 / Vite 6194, disposable SQLite and matching proxy/OAuth origins. Both native MCP hosts and the runner hosts were stopped after verification. Local logs remain under `/tmp/email-main-sync-*`.

The browser regression covers connection-test refresh while dirty, OAuth history index, canonical contextual links, retained monitoring choices and Instance departure protection at all four existing email viewports. Human screen-reader narration, device audio and live Gmail/IMAP remain unrun.

## Hosted inspector readiness correction

The initial hosted run passed backend (3,879 tests / 27 opt-in or runner-readiness skips), both frontend shards, core shard 1 (107 cases), all 16 separately isolated acceptance cases and both Compose checks. Core shard 2 passed 106 cases and failed `p8-skill-journey.spec.ts` while `navigateAdmin` waited for a Skills link in the contextual rail. The retained failure snapshot shows the immutable-version drawer had eventually rendered its local Skills & resources tab. The helper made its local-versus-route decision before that asynchronous version read completed, so it selected a destination absent from the Definition's global rail. This is a migrated test-helper race, not missing published Skills.

The shared helper now recognizes an inspector dialog and waits for its requested local tab before clicking. The P8 fixture deliberately holds the exact `ForkDurable` version read, starts navigation while the loading indicator is visible, releases the read, and asserts the original immutable Skills, protected read-only controls, managed-chat behavior and persisted Always/OnDemand pins. The first nine-case check passed; an initial delayed-read fixture used the wrong source-kind query and was interrupted, then corrected with a bounded request-start assertion. The final nine-case run passed with the actual held read. No product code, destination assertion, journey deadline or global timeout changed after `c789a549`.

The replacement exact-source hosted workflow is required before claiming whole-suite integration acceptance; its result is recorded after completion. Logs and the initial failure snapshot remain under `/tmp/email-main-sync-ci-*`; the corrected focused command adds `p8-skill-journey.spec.ts admin-collections.spec.ts` to the same isolated two-host runner configuration.
