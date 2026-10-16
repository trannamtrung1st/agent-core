# Design system sync — October 16, 2026

This focused pass preserves Ant Design v6 and synchronizes presentation guidance with the implemented State, State & Isolation, Memory, Voice and canonical Workspace surfaces. It changes no architecture, policy, transport or milestone acceptance status. The inspected checkout started at `fcbc4920`; concurrent semantic-response work was committed independently as `7d622c85` and is outside this report’s verification scope.

## Changes and ownership

- `IsolationChangePreview` now uses `useAdminDetailLayout`, matching existing Admin records: a top-aligned 12rem label column at widths of 768px and above, stacked labels below. Complete policy names and before/after/source values wrap. The shared component serves Instance State & Isolation, Memory policy and Voice, and Definition policy previews.
- `AgentConfigurationPanel` continues to own 16px heading/body insets; body stacks and field grids own their existing token-based sibling gaps. No CSS exception, control wrapper, new palette or UI framework was added.
- The canonical frontend specification records the shared preview layout. DESIGN.md keeps its eight canonical sections and folds the appended Sandbox/canonical-file guidance into Components. Current State/preset guidance, the Admin surface brief and the preview sidecar now agree. Workspace preview copy names live canonical files and current-folder search; one illustrative shared change preview was added.
- PRODUCT.md removes obsolete profile-only Voice availability and reconnect guidance in favor of the current canonical frontend rules. This is a documentation correction, not a Voice implementation change.
- README and the implementation plan retain their current Ant Design v6 and historical acceptance statements. No new milestone is started or accepted.

## Local verification

A disposable InMemory Synthetic API and Vite host ran on `127.0.0.1:52986` and `127.0.0.1:52987`, with separate temporary attachment/workspace/artifact/key roots. The existing Real application and user data were preserved.

Playwright MCP created a General Assistant Instance, prepared a Stateless draft, navigated through Voice and Memory policy, retained the same preview, cancelled preset replacement, discarded the draft, and inspected State. An injected State HTTP 503 retained the prior counts with Stale and Retry; removing the failure and clicking Retry restored a current observation. Unknown provider values remain explicit rather than measured zeroes.

Computed geometry at 1440, 1024, 768, 767, 390 and 320px showed no document horizontal overflow. Desktop preview labels measured 192px with aligned value starts; narrow layouts stacked labels and values. The panel body retained its single 16px inset. Long real policy labels and full before/after values were included. Voice and Memory policy reused the same preview layout. Desktop/mobile captures were inspected.

A separate focused Playwright CLI check, with its temporary configuration in `/private/tmp/agent-core-design-check/playwright.config.ts`, repeats the six-width preview assertions, navigation across Memory policy and Voice, Escape after the native preset dialog opens, draft Discard, State 503/Retry and empty Workspace at 1440/390px. It passes with no page errors, unexpected failed requests or unexpected HTTP errors; the injected 503 is intentional. The temporary harness and screenshots are local evidence, not new product fixtures. Early harness runs needed selector/transition corrections; an unrelated chat assertion assumed the wrong canned Synthetic response and was removed from this layout check.

Focused tests:

```sh
cd web
pnpm run test --run src/features/admin/StateIsolationConfigurationSection.test.tsx src/features/admin/DefinitionStateIsolationPolicy.test.tsx src/features/admin/IsolationPresetControl.test.tsx src/features/admin/InstanceStateSection.test.tsx src/app/AppShell.test.tsx src/app/antdTheme.test.ts
pnpm run build
```

All six test files and 54 tests pass; the TypeScript/production build passes. The build retains existing bundle-size and SignalR annotation warnings. The design hook found no deterministic issues in the changed component. JSON, design-section order, sidecar narrative/reference consistency, changed Markdown links/fences and `git diff --check` were checked locally.

## Limits

This is representative local UI verification, not an exhaustive audit of every Admin form. Definition draft/publication behavior is covered by focused component tests; this pass does not repeat its browser publication lifecycle. No real microphone, speaker, hosted provider, external account, OpenSandbox or Office-client fidelity check was performed. Voice documentation corrections were compared to the canonical specification; audio/reconnect behavior was not requalified. CI was not requested or run. Historical hosted and milestone evidence remains tied to its original source.
