# Main UI style consistency follow-up

Verified locally on October 10, 2026, on `develop/branch-1` after `8d671fd1`.

## Synchronization and scope

`git fetch origin` found no additional main commits. `origin/main` remains
`24aa7bb7cd95f4bb4f7b867bf27579c9814f1036`, already an ancestor of this branch.
The preceding merge retained main's Browser privacy and unified Event/Automation
behavior. This follow-up uses the branch's refactored Admin compositions as the
presentation baseline; it does not change main or renew milestone acceptance.

## Presentation changes

- Screenshot privacy now uses `admin-config-form`, the existing 48rem form limit,
  16px field spacing and bounded supporting-copy measure. Its containing Browser
  provider section and responsive effective-policy Descriptions remain full-width.
- Built-in Event subscribers use the existing compact collection table, bounded
  name column, full-name tooltip, local horizontal scrolling and 8px section gap.
  Exact owned Automation navigation and independent read recovery are retained.
- Built-in Event delivery diagnostics use the same collection table wrapping and
  numeric presentation rules, with an accessible table name.

No new tokens, control wrappers, palette, navigation model or UI kit were added.

## Runtime evidence

Playwright MCP exercised a disposable Synthetic host on API 5287 / Vite 5277,
with separate temporary SQLite and file roots:

- Created an Instance and saved a disabled Built-in `run.failed` subscription with
  a long Automation name. Selected its owner in global Events and navigated through
  the subscriber link to the exact expanded Automation. Keyboard Enter followed
  the same link; the focused link retained the shared 2px primary outline.
- Compared 1440, 768, 767 and 390px layouts. Document width equaled viewport width
  in every case. Subscriber table bounds stayed inside its 640px/full-mobile drawer;
  its name text ellipsized with the full title available and 40px mobile hit height.
- Privacy form width was 768px at 1440px and 332px at 390px. Computed field margins
  were 16px. Save/Reload actions wrapped with 40px mobile heights and stayed inside
  the form. Saved Disabled successfully and observed the pending-restart revision.
- Injected one privacy-read 503, verified the inline Reload recovery, removed the
  injected failure and recovered the saved revision. Select keyboard focus survived
  opening/dismissing the options. Normal API requests succeeded and there were no
  application console errors; the deliberate 503 produced its expected resource error.

Local ignored captures: `.playwright-mcp/main-style-{privacy,subscribers}-{1440,768,767,390}.png`.

## Repeatable checks

- Node 22.18.0 Vitest: BrowserPrivacySection, BuiltinEventSubscribers, EventsSection
  and InstanceAutomationsSection — **40 passed**, four files.
- `pnpm run build` — passed TypeScript and Vite; existing vendor/chunk warnings remain.
- `pnpm exec playwright test --config playwright.browser-privacy.config.ts` —
  **5 passed**, including acknowledgement, conflict, retained edits, retry and
  1440/768/390 keyboard/overflow checks.
- `pnpm exec playwright test --project=synthetic e2e/unified-event-automation.spec.ts
  e2e/multi-trigger-automation.spec.ts e2e/shared-events.spec.ts
  e2e/admin-table-overflow.spec.ts` — **8 passed**, on isolated Synthetic ports
  5380–5382 / 5373–5375 with temporary SQLite/file roots and `CI=1` to prevent
  reusing another host. Covered nested creation, retained child samples/identities,
  keyboard focus return, webhook delivery and exact Run/Automation navigation.
- `git diff --check` — passed.

Backend code was unchanged, so backend suites were not repeated. Full frontend
unit/browser suites and hosted CI were not rerun for this bounded presentation
change. Prior full-suite limitations remain in the scoped configuration report.
