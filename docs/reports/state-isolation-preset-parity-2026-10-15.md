# Definition and Instance isolation preset parity — 2026-10-15

Focused owner-requested follow-up on main baseline `27f13384`. Earlier UI polish changes remain included. This is not milestone acceptance or a deployment.

## Result

- Definition drafts and read-only publications share the Instance preset selector, policy fields and preset replacement interaction. Editable owners share the change-preview composition. The existing form and configuration panels own 16px gaps and outer insets.
- Definition recipes coordinate State policy, Memory and Voice in the existing candidate, preserve unrelated fields/capabilities, and clear speech aliases for Stateless. Saved baseline Voice aliases restore when a replacement recipe restores saved Voice. Definition edits still require Save, validation and publication.
- Experience enablement and autonomous identity maintenance remain Instance-owned opt-ins. Definitions control their existing eligibility flags; no schema, migration, new runtime mode or implicit tool grant was added.
- Persistent requests the explicit compatibility State recipe rather than using inheritance as its label. A separate Use Definition defaults action clears State/Memory overrides and Voice enablement while preserving independent Instance opt-ins. Definition ceilings and atomic revision/fingerprint validation remain authoritative.
- Blocked previews explain that restrictions must be resolved before calculating the complete change preview, rather than presenting an invalid request as an unchanged configuration.

## Initial local verification

- Focused API suite: `dotnet test tests/AgentCore.Api.Tests --no-restore --no-build -m:1 --filter FullyQualifiedName~StateIsolationConfigurationTests --logger 'console;verbosity=minimal'`: 6 passed. Tests were built first. The sandbox prevented the test runner's local socket; the same focused suite passed with elevated local execution. Includes both InMemory and SQLite atomic apply, stale independent-owner revisions, explicit Persistent versus inheritance reset, restricted broadening rejection, all coordinated override clears, and preserved independent opt-ins.
- Focused frontend suite: `cd web && pnpm exec vitest run src/features/admin/DefinitionStateIsolationPolicy.test.tsx src/features/admin/StateIsolationConfigurationSection.test.tsx src/features/admin/InstanceConfigurationActions.test.tsx src/features/admin/AdminInstanceNavigation.test.tsx`: 49 passed. Covers recipes, speech aliases, preserved authority, replacement cancellation, Custom detection, read-only publication, inherited isolated labels, separate defaults reset, conflict retention and existing navigation/actions.
- `cd web && pnpm exec vitest run src/features/admin/AdminApp.test.tsx -t 'marks State & Isolation'`: 1 passed (43 unrelated tests intentionally skipped), confirming the real Definition workspace reports unsaved State changes and clears that indication after Save. Total focused frontend coverage: 50 passing tests.
- `cd web && pnpm run build`: passed; existing SignalR annotation and bundle-size warnings remain.
- `git diff --check`: passed.

## Synthetic browser journey

Playwright MCP used task-owned InMemory Synthetic API 5088 and Vite 5188, with native Browser and Sandbox disabled. The existing Real host/database were preserved.

1. Created an Approval Demo Instance, saved Stateless, then used Use Definition defaults and saved again: State values and inheritance returned correctly.
2. Forked its v1 Definition. Selected Stateless: Voice turned off and speech aliases cleared in the same candidate. Navigated to Voice and back: edits remained. Cancelled a replacement: Stateless remained. Confirmed Session-isolated: saved Voice and speech aliases restored. Returned to Stateless, saved and reloaded: the complete recipe persisted.
3. Ran ordinary Definition validation, reviewed its generated safe diff and published v2 through the existing confirmation. Published State & Isolation showed a disabled Stateless selector and read-only controls.
4. Reopened the pre-existing Instance: it still used v1 and retained its original State defaults. Explicitly adopted v2: it inherited Stateless. Requested Persistent: the preview disclosed the Definition ceiling and Save stayed disabled. Used the separate defaults action: the actual label returned to Stateless.
5. Forked v2, selected Persistent in the Definition draft and independently changed Home to Read only: the label became Custom. This confirms Persistent changes the State recipe even when saved Definition defaults are isolated.
6. Measured both editable owners at 1440/1024/768/767/390/320px after native dropdown transitions settled: no horizontal page overflow, switches below dropdowns, 16px grid gaps and the existing single-column breakpoint. Desktop/mobile captures are ignored local files under `.playwright-mcp/isolation-preset-parity-*`.

The unrelated Test & Publish diff uses Ant Design's deprecated List and emits its existing development warning. A fresh State & Isolation reload has no warnings/errors; the filtered browser log contains 64 successful isolation/draft/version requests and no failures. Changed-document links/fences and the final diff whitespace check passed. No live provider, device audio, native Browser/OpenSandbox, hosted CI or full milestone suite was requested or run. Only task-owned verification servers and data are disposable.

## Review follow-up

The second source and runtime review reproduced and fixed two additional issues:

1. Refresh retained old untouched Experience/maintenance values, producing accidental drafts or overwriting saved independent settings alongside a policy edit. The editor now tracks explicit manual/preset intent per flag. Untouched flags rebase to refreshed saved values; deliberate preset choices survive even when they originally matched the baseline. Save and Discard clear that intent.
2. Preset preparation could construct values from an older owner read and attach newer revisions from its subsequent preview. The service now compares all three revisions and returns conflict rather than presenting a stale recipe as current. An injected Definition-read boundary reproduces the race deterministically and verifies retry uses current opt-ins without mutation.

Current focused checks:

- The same four-file frontend command above: 52 passed, including three refresh regressions (clean editor, unrelated policy draft, explicit preset intent). The focused AdminApp navigation/save check also passed: 53 total frontend tests.
- `dotnet test tests/AgentCore.Api.Tests --no-restore -m:1 -p:UseSharedCompilation=false --filter FullyQualifiedName~StateIsolationConfigurationTests --logger 'console;verbosity=minimal'`: 7 passed, including the new deterministic preset race. The new regressions were first observed failing before the fixes.
- Production build passed with the same existing warnings. Documentation links/fences and `git diff --check` passed.
- Playwright MCP used fresh task-owned Synthetic ports 5088/5188. A second real browser tab changed saved Experience through its UI. Refresh picked up untouched values without marking the editor dirty, retained a separate recording-policy draft, and retained explicit Stateless opt-ins after another external change. Saving and reloading persisted the intended values. The separate defaults action restored inheritance and saved correctly.
- A Definition draft with saved Session Memory and retrieval enabled was switched to Stateless. Memory and Voice pages showed the coordinated unsaved changes; preset cancellation retained them. One intercepted HTTP 409 on Save retained the entire candidate; an explicit retry saved it, and reload retained Stateless. Published inspection remained read-only. No publication or user database change was made during this review.
- Fresh final State & Isolation console inspection is clean; the injected Save conflict is expected test evidence. Hosted CI remains unrun as requested by repository policy, and the existing Real API remains unrestarted.
