# Shared Admin composition verification

Date: October 10, 2026. Scope: local frontend extraction and presentation consistency. Existing unrelated workspace edits remain intact. This does not change historical milestone or hosted acceptance.

## Change and ownership

Fourteen duplicated section templates across ten Admin feature files now consume the existing `AgentConfigurationPanel`. Consumers include inherited/local Skills and Resources, Workspace, Identity maintenance, Experience, Learned memory, Execution defaults, Harness management, Automations, Events, System credentials, Credential bindings, Browser state and Effective configuration.

The panel owns wrapping heading actions, an 8px heading gap, one 16px heading/body inset, and optional 8/12/16px content stacks. Direct Typography margins are cleared only inside those stacks so paragraphs cannot double the parent gap. Single tables and independently arranged forms retain their layout. Automation's enclosing stacks also use the resolved Ant Design section token. State, persistence, commands, confirmations and drawer lifecycle remain in their existing feature owners.

The existing dark Ant Design v6 baseline, `AppShell` token bridge, shared collection search/pagination, detail-label layout, `SkillDrawer`, resource selection and configuration navigation remain the reuse foundation. [Shared composition guidance](../../web/src/features/admin/README.md) documents usage.

## Runtime evidence

Playwright MCP used a disposable Synthetic SQLite backend on port 5096 and Vite on 5186. The existing host on 5080 reported Real and was used only to identify its profile; mutations and test scenarios ran on the disposable host.

- Created an Instance through visible controls. Submitted an empty Skill and observed required-field errors. Filled and saved a long-name Skill, reopened it, and verified the original procedure. Cancel closed the editor.
- Reloaded the empty Workspace and refreshed Experience; both returned their truthful empty states. Loaded learned-memory items in the selected scope.
- Injected a 503 for the Events collection, observed the inline error and Retry, removed the failure and retried successfully. Opened and cancelled a new Event draft.
- Measured Skill panel heading/body edges and insets at 1440, 768, 767 and 390px. Both ownership panels aligned; heading gaps were 8px, insets 16px, and document width equaled viewport width.
- Compared Resources, Credential bindings and Execution defaults at 1440/390px: 16px insets, 8px heading gap, 12/16px body gaps and no page overflow. Experience paragraph-to-form spacing measured exactly 16px at both widths after margin normalization.
- Inspected desktop/mobile Skills and Credentials captures. Before the intentionally injected HTTP error, the exercised flow had no console errors, page errors or failed network requests. The simulated 503 produced the expected HTTP console entry and recovered.

## Repeatable checks

The temporary Playwright configuration imports the repository runner's browser settings, changes baseURL/test output to the disposable host, and disables automatic web-server startup. `PLAYWRIGHT_SQLITE_PATH` points to that host's disposable database.

- `pnpm run build`: passed on the final frontend. Existing SignalR annotation and bundle-size warnings remain.
- Nineteen affected Synthetic scenarios passed across Admin polish, Definition configuration/JSON recovery, Instance Skills, scoped configuration, shared Events, System Credentials, Skill table overflow and the new shared panel layout regression. The new layout test additionally exercises keyboard tab activation at Ant Design's tablet overflow boundary and Workspace reload at all four widths. Its initial offscreen-click and ambiguous-locator errors were corrected in the test; final rerun passed.
- `git diff --check` and shared composition documentation link checks: passed.

The first full unit command, `pnpm run test --run`, used Node 26 through pnpm and failed with missing browser `localStorage` and concurrent UI timeouts. The repository CI specifies Node 22. The rerun used `/Users/trungtran/.nvm/versions/node/v22.18.0/bin/node node_modules/vitest/vitest.mjs run --maxWorkers=2`: 105 files and 862 tests passed, with one draft-editor test exceeding the existing 30-second limit. An isolated diagnostic with a 60-second limit completed in 30.44 seconds; that diagnostic is not an acceptance pass at the configured limit.

The slow regression repeatedly scanned every cached form control through broad role queries. It now queries the explicitly labeled textarea, checks hidden cached instructions directly for visibility, and reuses the persistent Save control while verifying it remains mounted. All invalid-JSON, blocked-save/publication and retained-draft assertions remain intact; the configured timeout remains 30 seconds. The final isolated command, `node node_modules/vitest/vitest.mjs run src/features/admin/definitionCandidateEditor.test.tsx --maxWorkers=1 -t 'keeps dirty edits across views and does not save invalid JSON'` under Node 22, passed in 25.27 seconds. The other 13 tests in that file already passed in the full run. The complete 106-file suite was not rerun after this test-query optimization; the final evidence consists of the 862 full-run passes plus the repaired test's separate pass.

Backend/full hosted/Compose gates and real voice/device checks are outside this presentation extraction's scope. Logs, temporary runner/configuration and database live under `/private/tmp/ac-component-extraction.DebcuK`; no user database was reset. Temporary profiling test files were removed, and only the hosts started for this check were stopped.

## Design documentation sync

The October 10 follow-up synchronizes [.agents/context/DESIGN.md](../../.agents/context/DESIGN.md), [.impeccable/design.json](../../.impeccable/design.json), the [Admin surface brief](../../.impeccable/surfaces/web-src-features-admin-adminapp-tsx.md) and [component guidance](../../web/src/features/admin/README.md). README now links the design system and shared compositions. The guide records the current Profile/Settings and Skills/Resources grouping, panel/body spacing ownership, bounded forms, expandable budgets, resource selection/import layouts and responsive drawers. Component notes appended below the do/don't lists now live under Components. The preview removes the superseded capability disclosure and adds the shared panel; narrative is extracted verbatim from the guide. Theme primitives and product behavior are unchanged by this sync.

Checks: schema v2 JSON and YAML frontmatter parse; frontmatter/component/token references resolve; canonical section order and narrative lists match; 247 relative links across changed Markdown files and balanced fences pass; `git diff --check` passes. Playwright MCP rendered the affected preview snippets in isolated Shadow DOMs on a neutral canvas. At 1440/768/767/390px the new panel has aligned heading/body edges, 16px insets, an 8px heading gap and 16px body gap, with no page overflow. Keyboard focus remains visible; action height is 32px at 768px+ and 40px below. Mobile capture was inspected and retained at gitignored `local/verification/design-sync/preview-390.png`. These previews validate documentation presentation; live application behavior remains supported by the Synthetic checks above and the accompanying feature reports. No full frontend suite, Compose or hosted gate was rerun for the documentation sync.

Before committing the related Admin configuration/resource work, the scoped API integration checks passed 19 tests (`ScopedInstanceConfigurationTests` and `Draft_csv_knowledge`) and `InstanceSettingsResolverTests` passed 6 tests. Commands used `dotnet test` on the respective API/Application test projects with `--no-restore --nologo -m:1` and the named filters. The first sandbox attempt could not create MSBuild's local pipes and was stopped; the authorized local rerun passed. This is focused regression evidence, not full backend acceptance.
