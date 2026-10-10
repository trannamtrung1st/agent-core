# Heading metadata layout verification

Local change and documentation sync: October 10, 2026. This is a bounded presentation follow-up, not milestone or hosted acceptance.

Supporting counts, revisions, draft IDs and status/attention labels now follow their titles with the shared compact 8px gap and wrapping. Direct Ant Design Flex owns sibling spacing; existing heading/body insets and action placement remain. Background Work titles no longer expand to push status to the opposite edge. The [frontend specification](../13-frontend-implementation-spec.md#heading-supporting-text) owns surface behavior; [design guidance](../../.agents/context/DESIGN.md#heading-supporting-text) owns presentation.

## Executed runtime checks

Playwright MCP used a disposable Synthetic API on port 5097 with InMemory persistence and Vite on port 5197. The existing Real demo was left running. Temporary Instance creation was explicitly approved by the user. Stopping the test API discarded the test data.

| Journey | Expected and observed result |
| --- | --- |
| Definitions search → no matches → Examiner search | Empty state and recovery worked; heading count followed the title with an 8px measured gap at 1440/768/767/390px; no page overflow. |
| Temporary Instance → Profile | Persona revision and Lifecycle Active followed their titles with an 8px measured gap at all four widths; no page overflow. |
| Settings → Customize Operating instructions → edit → Discard | Unsaved changes followed the section title with an 8px measured gap at all four widths; Discard removed the marker. No save was performed. |
| Console and network inspection after merge recovery | No browser console errors; inspected application requests succeeded. |

A concurrent repository merge temporarily introduced syntax conflicts and Vite reload errors. After that merge settled, the build and fresh-browser runtime checks passed. An initial wrong field selector in the Settings probe was corrected to the observed Operating instructions textbox.

## Component and static checks

- `pnpm run build`: passed after merge recovery, with existing bundle-size and SignalR annotation warnings.
- `pnpm run test --run src/features/admin/AdminApp.test.tsx src/features/admin/InstanceSettingsSection.test.tsx src/features/admin/ExecutionBudgetsSection.test.tsx src/features/chat/BackgroundWorkDrawer.test.tsx src/features/chat/AutomationDrawer.test.tsx`: four suites passed, 54 tests; Admin initially could not transform during the concurrent merge.
- `pnpm run test --run src/features/admin/AdminApp.test.tsx src/features/admin/definitionCandidateEditor.test.tsx`: Admin passed 42 tests; Definition editor passed 14 and failed two restricted Event-authority cases because the section-opening helper used raw header text.
- After changing that helper to accessible section names, `pnpm run test --run src/features/admin/definitionCandidateEditor.test.tsx -t 'preserves restricted'`: both failed cases passed; 14 other cases skipped in this targeted rerun. The complete editor suite was not rerun after the helper change.
- Impeccable layout detector on affected UI sources: no findings. `git diff --check`: passed.

## Limits

Live responsive checks cover the Admin paths above. Background Work and Automations have component-test evidence for this change, not live responsive drawer verification. Full frontend/backend/Playwright suites, live-provider/device checks, Compose and hosted acceptance were not run for this bounded follow-up. Documentation synchronization checks links, wording, JSON and token preservation; it adds no new application runtime evidence.

## Documentation synchronization checks

Seven affected Markdown files and 110 local links checked successfully; the new heading anchors resolve and fenced blocks are balanced. The preview sidecar parses as JSON. Design frontmatter and sidecar token metadata match the existing baseline; only presentation narrative and examples changed. `git diff --check` passed. README and implementation-plan milestone claims require no change for this presentation follow-up. No application tests were rerun during the docs-only sync.
