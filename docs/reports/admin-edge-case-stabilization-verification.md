# Admin edge-case stabilization — 2026-10-13

The supplied implementation review on `08838c79` identified two P2 reliability defects and optional sidebar density guidance. This follow-up fixes the two defects without changing the focused destinations or introducing another Admin refactor. Concurrent speech-locale/Email work is independent and is not included in this change. No new milestone or hosted acceptance is claimed.

## Fixes

- `useAdminUnsavedChanges` accepts an explicit discard callback. Browser privacy restores its latest saved mode/origin fields and clears validation/write feedback before approving departure. Stay retains edits. Discard makes no privacy write and releases the retained form’s dirty/unload protection.
- Archived Automations and Unattended routes carrying `run` recover to read-only Runs with the exact ID preserved. This includes the legacy `automation/controls` translation. Closing inspection remains on Runs. Requests without `run` retain Lifecycle restriction recovery and existing server authority.
- The [frontend specification](../13-frontend-implementation-spec.md) owns both behaviors. Layout, Ant Design tokens, sidebar groups and override semantics are unchanged; the review’s P3 density suggestion remains optional.

## Runtime evidence

Playwright MCP used disposable native Synthetic API 5280/Vite 5373 and `/private/tmp/admin-edge-20261013.db`, with separate temporary attachment/workspace/artifact/Definition-resource/credential-protection roots. Its `/health` identifies API build `1.0.0+08838c796b137e1c3a6bd8c99a163711ed88043d`. No operator data was reset and no provider call was required.

1. Before the fix, Discard and leave → Credentials → Browser privacy restored Disabled selections and still prevented unload. Afterward, Stay kept Disabled, Discard restored Protected and the unload event was no longer prevented. A genuine dirty reload produced the expected native beforeunload dialog; it was explicitly accepted. Clean reload after approved discard produced no dialog in regression coverage.
2. Unit coverage also changes saved graphics-origin selections, injects a save failure, and verifies discard restores the saved selections and clears the failure without another write.
3. Created an Examiner Instance and real text Session, sent Hello and observed a completed Synthetic Run. Archived the Instance, opened its legacy Automation controls link with the exact Run, and inspected Completed / Hello from synthetic on read-only Runs. Browser regressions repeat this for Automations, Unattended and legacy controls at 1440/390px, assert the rendered `data-agent-run-id`, close inspection and verify ordinary unavailable Automation navigation still returns to Lifecycle.

MCP console/network records are `.playwright-mcp/admin-edge-console.log` and `.playwright-mcp/admin-edge-network.log`. No unexpected page-console error was observed in the final exercised journeys. Owned hosts are stopped during delivery; unrelated processes and concurrent working-tree changes are preserved.

## Local checks and limits

- The new regressions reproduced the two source failures. Privacy’s initial unit unload assertion required awaiting its watch update before reaching the real discard failure.
- Final focused Vitest: **34/34**, four files (`BrowserPrivacySection`, `AdminInstanceNavigation`, `appRoute`, `InstanceConfigurationActions`), with `--maxWorkers=1`. Log: `/private/tmp/admin-edge-final-unit.log`.
- Shared `AdminApp` draft/archive/persona regression selection: **17/17**, with 26 unrelated tests intentionally skipped. Log: `/private/tmp/admin-edge-adminapp-unit.log`. Total: **51 distinct affected unit tests**.
- **14 distinct Synthetic browser cases** have passing evidence across configuration saving, the four new desktop/mobile edge cases, canonical/legacy navigation, draft departure and native links. Nine passed in `/private/tmp/admin-edge-final-browser.log`; its broader run was interrupted during a restricted-shell health-probe investigation. The five remaining cases passed in `/private/tmp/admin-edge-remaining-browser.log`. The interrupted invocation is not a wholly passing run.
- The first fresh-runner attempt could not start its additional backend (`config.webServer` build exited 1 during concurrent backend edits). The practical fallback uses the already verified owned Synthetic host, a temporary `/private/tmp/admin-edge-playwright.config.mjs`, isolated Chromium contexts and no test-managed servers. The host remained healthy; a sandbox-restricted curl result did not establish a host failure. This does not claim the concurrently edited backend was rebuilt or its suites passed.
- Early browser fixture checks incorrectly expected API `Completed` instead of wire `completed`, and a visible Run ID instead of the existing rendered identity attribute. Both were corrected against actual response/component contracts before final passing evidence.
- TypeScript and Vite production build pass; the existing bundle-size warning remains. Logs: `/private/tmp/admin-edge-final-ts.log` and `/private/tmp/admin-edge-final-build.log`.
- Relative Markdown links/anchors, balanced fences, existing preview structure and whitespace checks pass. No visual-system change required a new preview/capture cycle.

Hosted CI was neither awaited nor used as acceptance, as requested. Full backend, unrelated frontend/browser matrices, hosted providers, physical audio and manual assistive-technology checks were not run for these frontend edge cases.
