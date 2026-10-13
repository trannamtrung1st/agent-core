# Email monitoring integration with current main

Date: 2026-10-13. Branch: `develop/branch-1`. Merge parents: email closure `60e3d2c3` and fetched main `d425b121`.

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
| Full frontend suite including new regression | Running; final result recorded below after completion |
| Email plus Admin shell/navigation/overflow E2E | Running; final result recorded below after completion |
| Exact merge-source hosted Synthetic workflow | Pending dispatch and completion |

The browser regression now covers connection-test refresh while dirty, OAuth history index, canonical contextual links, retained monitoring choices and Instance departure protection at all four existing email viewports. Full hosted coverage remains required before claiming whole-suite integration acceptance. Human screen-reader narration, device audio and live Gmail/IMAP remain unrun.
