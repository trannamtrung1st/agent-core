# State and isolation consistency review — 2026-10-15

Review baseline: `2f65f0f9` on `develop/branch-1`. The user authorized review, corrections, commit and push. This follows the [initial implementation ledger](agent-state-isolation-verification-2026-10-15.md); it does not reopen milestone acceptance or request hosted CI.

## Findings and corrections

1. **Strict Session admission could be relaxed while its child was settling.** The runtime and stores checked only the next Run's effective policy. Changing the Instance from Stateless to Session-isolated allowed another independent input into the still-active original strict Session. The runtime and both atomic stores now also enforce the Session's original request boundary. Same-admission replay and validated configuration/completion continuations remain allowed. InMemory and SQLite tests reproduced the bypass after reopen before correction; the Synthetic runtime test changes policy while the first request awaits its child, rejects the second request, then completes the original request normally.
2. **Shared-profile uncertainty affected unrelated ephemeral Sessions.** The native adapter applied an Instance's shared-context pending fence to all its views, and fresh ephemeral binding unnecessarily required the shared owner gate. Pending native mutations now carry the originating Session identity. They fence that Session, including cancelled creation before view publication, and other users of the same shared context. Independent ephemeral views keep their own observation/native access and can bind while shared work owns the Instance gate. The existing Session gate covers provisioning and policy adoption; shared native access acquires owner before Session so an interactive waiter cannot block an already admitted unattended owner.
3. **State UI hid a pending transition behind “Observation unavailable.”** The established `policy_transition_pending` issue now explains that shared-context work must settle. Unknown live counts remain unknown; no resource operation is offered or performed by inspection.

The mixed-mode browser regression uses real disposable Chromium contexts and local fixture pages. It injects a held native acknowledgment into the existing shared context to deterministically exercise the uncertainty boundary. It asserts shared/owner State stays Partial, the originating unpublished Session cannot bypass retirement, an ephemeral sibling stays Available and usable, a new ephemeral view binds independently, and an interactive waiter does not deadlock the adopted unattended owner. The initial test reproduced the incorrect Partial observation before the fix. No public test/debug endpoint or provider action was added for verification.

## Focused verification

Backend commands use `dotnet test tests/<project>/<project>.csproj --no-restore --nologo --filter '<filter>' -m:1 /nodeReuse:false`; every filter fragment uses `FullyQualifiedName~`, joined with `|`.

| Check | Result |
| --- | --- |
| Infrastructure with `AGENTCORE_CHROMIUM_ISOLATION_TESTS=1`: `BrowserStateIsolationTests`, `AgentBrowserProfileTests`, `BrowserHardeningTests`, `NativeBrowserBoundaryReviewTests`, `BrowserCleanupTests`, `BrowserPrivacyTests`, `BrowserCredentialSinkTests`, `BrowserEnvironmentJourneyTests`, `Request_isolation` | Final run: 79 passed, 0 failed/skipped (2 m 19 s), including originating unpublished-view fencing. An earlier run after the gate changes also passed all 79 tests. |
| API: `StateIsolation`, `RuntimeStateApiTests`, `SessionWorkingCleanupTests`, `BackgroundSessionJourneyTests` | 23 passed, 0 failed/skipped (13 s). Includes live policy relaxation during strict child settlement, scoped state, configuration, workspace, continuity and background lifecycle. |
| Application: `StateIsolationPolicyTests`, `ContextAssemblerTests`, `ToolRegistryPolicyTests`, `CompletionInboxCapabilityTests`, `ToolApprovalTests` | 61 passed, 0 failed/skipped (617 ms). |
| `pnpm --dir web exec vitest run src/features/admin/InstanceStateSection.test.tsx src/features/admin/StateIsolationConfigurationSection.test.tsx src/services/realtime.race.test.ts` | 68 passed, 0 failed/skipped (4.59 s). |
| `pnpm --dir web build` | TypeScript and production build passed; Vite phase 5.90 s. Existing SignalR annotation and chunk-size warnings remain. |
| Repository consistency | Nine changed Markdown files have balanced fences and existing local link targets; all 18 authored Definition JSON files parse with schema 2; production fingerprint matches; `git diff --check` passes. |

Invocations overlap with the initial ledger and with reproduction runs; counts are not summed as unique coverage. The deliberate pre-fix failures establish the two boundary defects, not passing acceptance. No paid/live model tests, live OpenSandbox, full CI suite or hosted CI was run for this review.

## Executed UI behavior

Playwright MCP used a separate task-owned Synthetic SQLite API on 5097 and Vite on 5187, with Browser/OpenSandbox disabled and data under `/tmp/agent-state-isolation-review-20261015`. Only the new disposable approval-demo Instance was configured.

- Selected Stateless, reviewed the draft and saved through the existing configuration toolbar. The saved fields and source labels showed request scope, disabled Home, ephemeral Browser and disabled continuity; Voice was unavailable in Chat.
- On State, intercepted only the task fixture's response to present the pending-transition condition. The explanatory text appeared with unknown live counts. Document/main widths fit 390 and 1440 px without changing shared layout/components.
- Injected a 503 for that State read. The prior observation and pending explanation remained marked Stale. Removed interception, clicked Retry, and observed the actual disabled providers recover as unavailable/Partial. This was response fault injection, not proof of a live provider transition through the UI; native behavior is covered separately above.
- Sent `REVIEW_STRICT_REQUEST_ONE`, observed the completed Synthetic response and New chat action, clicked it, then sent `REVIEW_STRICT_REQUEST_TWO`. The Session IDs differed, the second conversation contained none of the first marker, and both requests completed normally. Final Chat navigation had zero console errors/warnings. The injected 503 and initial refresh of an old task-browser owner capability were expected diagnostics; accessible-selector mistakes were corrected without product changes.

## Consistency and limits

Architecture, ports, backend execution, testing, README/TODO and implementation-plan references describe the corrected boundaries. Existing one-mailbox Session ownership, immutable Run pins, atomic source receipts, typed context assembly, approval/effect fences and retained data remain authoritative.

Reviewed production-source SHA-256: `b2aebdf6842e462197c6c244423615e23315f876f5f98d48e99baa6c77e9956d`, covering 1,334 sorted tracked paths under `src`, `web/src` and `agents`, hashing UTF-8 path, NUL, file bytes and NUL. Tests/docs are excluded. The initial ledger's different fingerprint remains historical evidence for its original implementation.

No Real host, user data, external account, provider profile or deployment was changed. Live-provider/customer-account/device qualification and optional CI remain separate from this focused local review.

The task-owned browser tab and API/Vite hosts were closed after verification. Disposable SQLite and workspaces remain available for inspection; unrelated services were left running.
