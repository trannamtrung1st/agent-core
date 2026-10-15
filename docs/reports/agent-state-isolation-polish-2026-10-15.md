# State and isolation review follow-up — 2026-10-15

Base: `main` at `e4af41ba`. This is a focused follow-up to the attached implementation review, not a new milestone acceptance or architecture change.

## Finding dispositions

1. **Preset detection:** complete effective recipes now include working cleanup, constrained visibility, Memory, Voice, Experience and maintenance. Independent field edits derive Custom. Persistent restores Definition State/Memory defaults and preserves saved independent enablement. Session-isolated preserves saved Session Memory and Voice; visibility follows the server recipe's Definition constraint. Stateless disables all Memory and Voice. Regression cases cover every editable State field, all Memory fields, independent enablement, constraints and inherited cleanup.
2. **Unsaved edits:** a native Ant Design confirmation protects any dirty isolation draft before another preset replaces it. Keep editing, Close and Escape retain edits. Replace draft requests an unsaved preset; ordinary preview/apply and optimistic revision checks remain. The existing AgentConfigurationPanel/form own spacing; the new dialog joins existing bounded modal styles and narrow 40px controls.
3. **Runtime observation:** measured before considering a behavior change; injection remains fresh before each authorized model call. No caching, skipped authorization/revocation checks or new observation policy was introduced. The small local fixture does not justify changing freshness semantics.
4. **Scratch retention:** the preview states that OnSessionDeletion retains working files after request completion while the historical Session remains. It explains the explicitly editable AfterTerminal alternative, including safe-cleanup eligibility. Both isolated preset defaults remain OnSessionDeletion; no automatic deletion policy changed.

## Focused checks

- `cd web && pnpm exec vitest run src/features/admin/StateIsolationConfigurationSection.test.tsx src/features/admin/InstanceConfigurationActions.test.tsx`: 38 passed, 0 failed.
- `cd web && pnpm build`: passed. Existing SignalR annotation and large-chunk warnings remain.
- `dotnet test tests/AgentCore.Api.Tests --no-restore -m:1 -p:UseSharedCompilation=false --filter 'FullyQualifiedName~StateIsolationConfigurationTests|FullyQualifiedName~State_refresh_replaces_one_trusted_runtime_section|FullyQualifiedName~State_tool_rejects_forged_ids' --logger 'console;verbosity=minimal'`: 7 passed, 0 failed/skipped. Covers retention preview without apply, busy browser preview, InMemory/SQLite atomic apply with stale owner revisions, replaceable Runtime facts with Evidence receipts, forged IDs, refresh limits and live revocation.
- Playwright MCP against task-owned Synthetic API `5088` / Vite `5188`, with InMemory persistence and native Browser/Sandbox disabled: passed the interaction journeys below. Sandbox socket restrictions required elevated local server/test execution; no user host was restarted.
- `git diff --check` and changed-document local-link/fence checks: passed.

### Browser actions and observed outcomes

- Created a disposable Approval Demo Instance through Admin. Selected Stateless, then changed Working cleanup to After safe terminal cleanup: label became Custom and the preview explained terminal cleanup.
- Selected another preset with a dirty draft: warning appeared before replacement. Keep editing and Escape retained cleanup and Experience edits; Escape returned focus to Isolation preset. Replace draft prepared Stateless with On Session deletion and explicit retention notice; no apply occurred until Save.
- Saved Stateless through the shared configuration bar, changed cleanup independently, injected one HTTP 409 on apply, then refreshed: the Custom draft and After safe terminal cleanup value survived. Removed the interception, saved and reloaded: the same cleanup value and Custom label were persisted.
- Inspected modal/form at 1440, 390 and 320px: no document horizontal overflow. Final modal widths were 520, 374 and 304px respectively; narrow Close/Keep editing/Replace draft controls were 40px tall. Existing heading/body gaps remain 8/16px. Local ignored captures: `.playwright-mcp/isolation-preset-confirm-desktop.png`, `.playwright-mcp/isolation-preset-confirm-mobile.png`, `.playwright-mcp/isolation-preset-confirm-final-mobile.png`.
- Network showed successful preset/preview/apply requests, the expected injected 409, then a successful retry. Clean reload had no browser warnings/errors; the injected conflict's expected console error is separate. Earlier connection-refused errors occurred before the isolated API became ready.

## Runtime observation measurement

A temporary probe in the existing `StateIsolationContextTests` fixture held one authorized active Run and called the actual `SessionToolExecutor.RuntimeStateFactsAsync` plus `RenderRuntimeStateFacts`. Each case used one warm-up and 30 measured serial samples, with a recording in-process browser reader. The probe passed and was removed after measurement; no benchmark hook or endpoint ships. Local detailed output is `/tmp/isolation-api-checks.log`.

| Browser reader | Median | p95 | Maximum projection | Reader calls including warm-up |
| --- | ---: | ---: | ---: | ---: |
| Immediate fixture | 0.026 ms | 0.070 ms | 957 UTF-8 bytes | 31 |
| Fixture delayed 20 ms | 20.650 ms | 21.528 ms | 957 UTF-8 bytes | 31 |

This confirms a fresh read on each call and the potential for reader latency to dominate. It measures a small InMemory/fixture scenario, not SQLite scale, native browser contention, actual model tokenization, a live Sandbox or production load. The serialized section already contains aggregate counts and bounded resource observations rather than transcripts; 957 bytes is not a measured token count. A future injection change should measure those larger scenarios and preserve live revocation and freshness contracts first.

## Boundaries

No hosted CI, live provider/OpenSandbox regression, production multi-customer qualification or milestone acceptance was requested or claimed. No user Session/database cleanup, data conversion or deployment was performed. Only task-owned verification hosts were stopped; optional qualification boundaries in the original ledgers remain.
