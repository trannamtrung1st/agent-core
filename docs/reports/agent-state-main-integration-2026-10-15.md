# State/isolation and current main integration — 2026-10-15

Integrated `origin/main` at `2322d4927936fe816bc1aa066e89492fae5c6b05` into `develop/branch-1`, whose prior tip was `18623c235d9255c96c2b9123175c7fedc093ca1b`. The common ancestor is `ff875ea48cb42a72829dac51f87a9c8bd939e8b8`. Main contributed context identity/media hardening (`ffbfa8e7`) and current typed-context/recovery fixtures (`dec10df3`, `2322d492`). Git reported no textual conflicts; overlapping runtime, assembler and canonical documentation were reviewed semantically before committing.

The [original implementation](agent-state-isolation-verification-2026-10-15.md), [isolation review](agent-state-isolation-review-2026-10-15.md) and [main context ledger](three-layer-context-cutover-verification-2026-10-15.md) remain evidence for their respective earlier source snapshots. This report records the combined local verification, not a deployment or milestone/CI acceptance.

## Combined behavior retained

- `CoreStateObservation` remains a conditional fixed Core procedure. Main's `ContextPurpose.AgentExecution` requires Runtime identity/instructions; explicit platform auxiliaries retain `Platform`, and section updates preserve purpose.
- The model loop still applies live restrictive policy, filters continuity and refreshes its single trusted Runtime state section. Main's final single projection runs after tool-environment/recovery metadata is selected, and its duplicate tool-ID validation happens before new checkpoints or effects. State tool results remain native Tool Evidence.
- Immutable image bytes, cached media hashes, both provider-role transports and semantic preparation remain as implemented on main. No alternate assembler or legacy-role fallback was introduced.
- Strict Session boundaries after policy relaxation, same-Run retries, receipt-fenced completion continuations and safe child/resource settlement remain intact. Home and browser ownership restrictions and immutable resolved browser pins are retained through SQLite conversion/reopen.
- Main's recovery fixture uses the current interaction-only checkpoint contract. Shell syntax and all four embedded Python blocks in `scripts/compose-sqlite-volume.sh` parse; Compose execution itself was not run for this integration.

Only two fixture waits needed adjustment during verification: the asynchronous preset-preview assertion now permits a bounded five-second wait under concurrent build load; native popup readiness waits for the actual fixture heading rather than a page-wide load event. Ownership/foreign-handle assertions, validation and production behavior were not relaxed.

## Focused verification

Backend command form: `dotnet test tests/<project>/<project>.csproj --no-restore --nologo --filter '<filter>' -m:1 /nodeReuse:false`. Each fragment below uses `FullyQualifiedName~` and alternatives use `|`.

| Check | Result |
| --- | --- |
| API: `StateIsolation`, `RuntimeStateApiTests`, `SessionWorkingCleanupTests`, `BackgroundSessionJourneyTests`, `ChatAutomationJourneyTests`, `ScopedInstanceConfigurationTests` | 46 passed, 0 failed/skipped (35 s). Real Synthetic runtime/HTTP behavior includes scoped state, refreshed model context, strict child completion and live policy relaxation, workspace/continuity gates, automation and configuration. |
| Application: `ContextAssemblerTests`, `PromptContextBuilderTests`, `ToolWorkflowRuntimeTests`, `ResponsesCheckpointRecoveryTests`, `CompletionEvaluatorTests`, `CompactionLifecycleTests`, `ConversationCompactionTests`, `HistoricalImageRereadTests`, `StateIsolationPolicyTests`, `SessionMemoryPromptTests`, `ToolApprovalTests`, `CompletionInboxCapabilityTests`, `CapabilityProjectionTests` | 166 passed, 3 live opt-ins skipped, 0 failed (2 s). |
| Infrastructure with `AGENTCORE_CHROMIUM_ISOLATION_TESTS=1`: `ThreeLayerProviderTests`, `OpenAICompatibleLanguageModelTests`, `ResponsesTransportTests`, `SemanticResponseLanguageModelTests`, `ContextCutoverTests`, `Request_isolation`, `BrowserStateIsolationTests` | 185 passed, 6 live opt-ins skipped, 0 failed (10 s final run). Includes both offline provider transports, immutable media, typed checkpoint conversion/reopen and disposable real Chromium ownership/storage. |
| Vitest: `InstanceStateSection.test.tsx`, `StateIsolationConfigurationSection.test.tsx`, `realtime.race.test.ts`, `ContextProvenance.test.tsx` | 70 passed (6.32 s). |
| `pnpm --dir web build` | TypeScript and production build passed; Vite phase 7.11 s. Existing SignalR annotation and chunk-size warnings remain. |
| `bash -n scripts/compose-sqlite-volume.sh`; Python `ast.parse` of its four embedded scripts | Passed. |
| Repository consistency | Seven merged/updated Markdown files have balanced fences and valid local file targets; all 18 authored Definition JSON files parse with schema 2; production fingerprint matches; no unmerged index entries or whitespace errors remain. |

The first concurrent frontend invocation hit its one-second readiness wait, then passed in isolation. The initial Infrastructure invocation passed 184 tests with six opt-ins skipped but timed out waiting for one popup's load event; its isolated five-test browser rerun passed. After the targeted readiness corrections, the complete selected sets above passed. Failed attempts are not recorded as passing acceptance, and overlapping reruns are not summed as unique coverage.

## Synthetic browser journey

Playwright MCP used only a task-owned SQLite API on 5097 and Vite on 5187, with separate data under `/tmp/agent-state-main-sync-20261015` and Browser/OpenSandbox disabled. A new approval-demo Instance was configured through the actual State & Isolation surface: select Stateless, save with the existing configuration toolbar and verify the saved request/Home/Browser/continuity fields.

State reported Partial with unavailable Browser/Sandbox, unknown live counts and zero hosted Sessions; visiting State allocated no provider resources. In Chat, `MERGED_MAIN_REQUEST_ONE` completed with the Synthetic response and New chat action. Clicking New chat and sending `MERGED_MAIN_REQUEST_TWO` created another ordinary Session, completed normally and contained none of the first request marker. Final Chat console had zero errors/warnings. Backend integration independently exercises model-facing state refresh and live restriction removal through the merged context assembler.

## Source and verification scope

Combined production-source SHA-256: `ed3ce5104289553f9fdf3745f5a64279fbff0eb0cc1b77cb41209e5bbf63e708` over 1,334 sorted tracked paths under `src`, `web/src` and `agents`, hashing UTF-8 path, NUL, file bytes and NUL. Test/document changes are excluded.

No full local CI suite or hosted CI was dispatched or awaited for this integration. Live models, live OpenSandbox, external customer accounts and device-audio qualification remain separate opt-ins. No Real application, user database or deployment was restarted, converted or reset. Task-owned verification hosts/tabs are stopped after checks; disposable data remains available for inspection.
