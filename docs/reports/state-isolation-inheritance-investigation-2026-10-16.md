# State & Isolation inheritance investigation — 2026-10-16

## Finding and change

No save, publication or inheritance reset was reproduced. All nine Definition State & Isolation fields survive save/reload, immutable publication and Instance creation. Existing Instances require explicit version adoption; narrowing overrides remain explicit until cleared. Experience enablement and autonomous identity maintenance are separate Instance-owned opt-ins, initialized Off for each new Instance. Definition eligibility permits use but does not activate either opt-in. Saving or adopting a Definition does not copy another Instance's activation.

Read-only inspection of the existing local application agreed with its persisted settings: the independently enabled Instance retained both opt-ins and the untouched Instance retained Off. No existing user settings, Definition publications or Sessions were changed.

The presentation fix replaces implementation-oriented transaction wording with explicit Instance-only activation guidance at each switch, and explains the same boundary in Definition authoring/inspection. Runtime defaults and persistence semantics are unchanged. A new integrated regression covers all nine fields through publication/adoption, explicit false overrides, inheritance reset, independent sibling activation and SQLite reopen. UI assertions cover the same distinction.

## Focused local verification

From the repository root:

- `dotnet test tests/AgentCore.Api.Tests/AgentCore.Api.Tests.csproj --no-restore --filter FullyQualifiedName~StateIsolationConfigurationTests --logger 'console;verbosity=minimal' -m:1 /nodeReuse:false` — 9 passed. Includes atomic coordinated updates, stale revisions, restrictive Definition boundaries, visibility ceilings and preset/reset preservation.
- The same command with `--filter FullyQualifiedName~StateIsolationDefinitionInheritanceTests` — 2 passed, covering InMemory and SQLite. Every state field is compared with Definition defaults, effective values and provenance after save/publication, fresh Instance creation, later publication without automatic adoption, explicit adoption, override reset and SQLite restart. Independent activation stays enabled only on the selected Instance; a new sibling starts Off.

From `web/`:

- `pnpm exec vitest run src/features/admin/DefinitionStateIsolationPolicy.test.tsx src/features/admin/StateIsolationConfigurationSection.test.tsx src/features/admin/InstanceIsolationWorkspace.test.tsx` — 50 passed. Covers preset field mappings, manual edits, visibility ceilings, immutable inspection, refresh, failed saves, shared Memory/Voice navigation, opt-in preservation and the clarified activation boundary.
- `pnpm build` — TypeScript and Vite passed. Existing SignalR annotation and bundle-size warnings remain.

The initial sandboxed MSBuild attempt could not create its named pipe. The focused commands above succeeded with local process permissions; this was a setup failure, not a test failure.

## Playwright MCP scenarios

Frontend `127.0.0.1:5186` used a separately started Synthetic host at `127.0.0.1:5096`, compiled from the current checkout. SQLite, workspaces and files were isolated under `/private/tmp/agentcore-state-save-20261016/`. Health and the process's open database handle confirmed this boundary before test-only publication. The existing Real application stayed running and was inspected read-only.

| Action | Expected and observed |
| --- | --- |
| Start with all four Definition boolean permissions Off; enable them through the UI, change Home, cleanup, browser and visibility, Save draft and reload | All four switches remain On. Readback matches all nine fields: Session, ReadOnly, AfterTerminal, EphemeralSession, CurrentSession and all booleans true; maximum visibility remains InstanceSummary. |
| Validate/publish that disposable draft and create an Instance from its exact version | All nine effective values equal the selected publication and have Definition provenance. No overrides exist. Independent Experience and maintenance activation are Off. |
| Enable both Instance opt-ins, Save configuration, navigate away and reopen | Both remain On with independent revisions 1 and Instance revision 2; inherited policy remains unchanged. |
| Inject one isolation-apply 409 while disabling maintenance | Unsaved intent survives. Refresh followed by explicit Save succeeds. Reload shows Experience On and maintenance Off. |
| Inspect updated Instance and Definition guidance at 1440×900, 768×900 and 390×844 | The distinction is explicit in both owners; existing shared grids wrap without horizontal document overflow. Mobile guidance was visually inspected. |

Console/network inspection included historical MCP entries from the previous task's stopped host and the new disposable database's initial owner-capability renewal; these were setup/history, not persistence failures. The intentional apply 409 was the exercised error boundary. All described subsequent reads, saves, validation and test-only publication succeeded. Temporary screenshots remain under the verification directory above.

No live-provider generation, hosted CI, full local suite, deployment or milestone acceptance was requested or claimed. This investigation verifies configuration persistence and presentation; it does not qualify generated Experience quality or autonomous maintenance content.
