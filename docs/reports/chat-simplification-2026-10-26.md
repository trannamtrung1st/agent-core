# Message, Execution and Chat Simplification

Implementation baseline: `ed73a7762e4818a765438b236184824c9b9b4971` on `develop/branch-1`. The implementation uses one coordinated schema/API/browser cutover. The exact implementation and main-integration commits are recorded below after creation. Evidence is local and Synthetic; it does not renew historical milestone or deployment acceptance.

## Dependency and removal inventory

| Existing owner/symbol | Readers/writers and decision | Verification |
| --- | --- | --- |
| Session → Activation → AgentRun | Domain, coordinator, tenant stores, runtime and Activity; retain the canonical execution model. No separately persisted Turn entity found. | Existing admission/runtime/SQLite integration tests |
| `ActivationKind.UserTurn`, `IsPromptTurn`, `_turnGeneration` | Admission causation, context projection and live response supersession; retain distinct semantics. | Context, interruption and wire regressions |
| `SessionStatus`, `SessionLifecycleStatus` | Transport attachment versus business outcome; retain both projections. | HTTP/history/ready agreement |
| `LifecycleTransitionSource.Legacy`, `RequestEndAsync` | Runtime end, lifecycle parser and tests; replace internal end provenance with Host, retire privileged Legacy parser/enum. Historical stored Legacy denotes that internal host end only; migration must reject other uses. | Lifecycle policy, migration/reopen |
| `work.complete` | Background outcome/checkpoint contract; retain unchanged. | Background/report-back regressions |
| `CompletionEvaluator` | Implicit agent closure must not bypass explicit completion capability. | Explicit completion/denial tests |
| Session row/snapshot, tenant governance | Extend existing owners with immutable retention and pinned policy; no shadow Session/Turn or deletion ledger. | InMemory/SQLite parity, expiry/CAS |
| Tenant Session delete and cleanup workers | Retain tombstone, grant retirement, memory redaction and source cleanup as the only deletion sink. | Due scan/manual deletion recovery |
| Composer upload/admission | Reuse existing pending file upload and ACK ownership for explicit plain-text conversion. | Exact UTF-8, rejection/retry and browser journeys |

## Verification ledger

All commands ran from the repository root unless `web/` is shown. .NET commands used `--no-restore -m:1 -nodeReuse:false -p:UseSharedCompilation=false --nologo`. These checks were selected for the affected behavior; the full CI suite was not run.

| Check / exact selector | Observed result |
| --- | --- |
| `dotnet build src/AgentCore.Api/AgentCore.Api.csproj` | Passed, 0 warnings/errors; latest API test build also compiles the final host recovery changes. |
| Infrastructure `TenantChatRetentionTests\|TenantSessionLifecycleTests\|TenantSessionIntegrationTests` | 40 passed: both stores, exact deadline, no saved catalog entry, stale retirement CAS, factual cancellation/uncertain-effect preservation, Saved pin and terminal due, lifecycle and reopen. |
| Infrastructure `TenantChatRetentionTests\|ChatRetentionMigrationTests` after adding private file coverage | 11 passed: bound Attachment/Artifact lookup and content unavailable at exact due before scan; existing byte cleanup is idempotent; migration preserves history and known Host provenance, rejects unknown Legacy source and lossy tenant-policy downgrade. |
| Infrastructure `FrozenMigrationMetadataTests\|CanonicalModelIdentityMigrationTests\|CanonicalSchemaAcceptanceTests` | 44 passed: historical trigger ownership (188 guards before main synchronization), canonical identity migration and current physical/model schema parity. |
| Application `SessionCompletionIntentTests\|SessionRuntimeCompletionTests\|LifecycleTransition\|SessionRuntimeInterruption\|SessionRuntimeTool` | 12 passed: reason-only/forged arguments, current claim/Session/foreground policy, checkpoint intent recovery and current durable final response required. |
| Application `VoiceRealtimeRegressionTests\|SpeechPlaybackTests\|SessionRuntimeLifecycleRoutingTests\|ContextAssemblerTests\|QueuedAttachmentRuntimeTests\|ToolExecutionBudgetTests` | 71 passed: retained interruption, response lifecycle, playback/heard boundaries, context and queued-file behavior. |
| API `Canonical_Session_HTTP_pins_authenticated_creator` | 1 passed: existing authenticated creator/executor, CSRF, foreign tenant, stale revision, lifecycle/delete and retired route behavior. |
| API `Explicit_agent_completion\|Tenant_live_MessagePack_JavaScript` | Final run: 6 passed (both stores): actual worker default-Off/Allowed completion, durable final history, late-input rejection and real Node SignalR/MessagePack auth, reconnect, switch, logout and receipts with zero provider HTTP. |
| `web/`: `pnpm exec vitest run src/features/chat/Composer.test.tsx src/features/chat/ChatApp.test.tsx src/features/chat/longText.test.ts src/services/messageParts.test.ts src/services/realtime.tenant.test.ts src/services/realtime.race.test.ts src/services/realtime.queue.test.ts src/services/realtime.protocol.test.ts src/features/access/TenantGovernance.test.tsx` | 186 passed in 9 files before the final extra expiry race case. |
| `web/`: focused Composer/Governance/realtime tenant+races recheck | 110 passed, including the added expiry/late rejected ACK/late ready case and governance save/stale/reader behavior. |
| `web/`: final ChatApp + realtime tenant recheck | 42 passed after expiry unmounts private file, sandbox, approval and background details; late history requests are invalidated and rejected ACK cannot restore private drafts. |
| `web/`: `pnpm run build` | Passed TypeScript and production Vite build. Existing large-chunk warning remains; no new bundling architecture. |
| `web/`: `TENANT_CHAT_API_PORT=5098 TENANT_CHAT_WEB_PORT=5188 CI=1 pnpm exec playwright test --config playwright.tenant-chat.config.ts e2e/chat-retention.spec.ts` | Final run: 2 passed (38.7s), disposable SQLite/Synthetic host and real browser MessagePack. |

For the .NET rows, the complete command is `dotnet test tests/AgentCore.<Application|Infrastructure|Api>.Tests/AgentCore.<Application|Infrastructure|Api>.Tests.csproj --filter 'FullyQualifiedName~<selector>|FullyQualifiedName~<selector>...'` plus the common flags above. Individual selectors in the table are exact test-name substrings, not shell alternation without the `FullyQualifiedName~` prefix. Documentation checks: 17 changed Markdown files have balanced fences, five new relative links resolve, the added complete JSON example parses, and `git diff --check` passes. Production searches found no `LifecycleTransitionSource.Legacy`, Legacy parser alias or independently persisted Turn class/table.

Final raw logs and captures are local under `local/verification/chat-simplification/`; they are intentionally ignored and are not deployment artifacts.

## Observed Synthetic journeys

1. Create Saved on the selected Agent/Private Space, send normal text and receive the Synthetic answer. Completion delegation starts Off. Enable the exact Session policy, send the completion script, observe the final answer before the Agent Completed note. Composer disappears; direct reload retains final text and factual reason. HTTP tests reject a subsequent input. The browser policy mutation can correctly receive stale-revision conflict while a prior receipt settles; the repeatable journey reattaches before taking new policy witnesses rather than replaying that mutation.
2. Select Temporary, observe the 24-hour/external-effects disclosure and send normal text. Paste an oversized literal Unicode/emoji/CRLF/code-fence draft. Send is disabled. Explicit Attach long text receives a deliberate first-upload 503, keeps the original, retries, shows a text-file tile and short request, then admits the file once. Protected original download matches the exact `Buffer.from(original, 'utf8')` bytes. The chat is absent from the catalog and works by its direct link before expiry.
3. Open Temporary files, advance only the browser's projected deadline, and observe immediate empty transcript/draft/files, closed file drawer, no composer, and the expired notice. This exercises browser reaction; separate FakeTimeProvider store/service tests prove server expiry at the exact authoritative boundary before scanner execution. A late rejected admission/ready/history callback cannot resurrect content.
4. In Playwright MCP, save governed Saved retention as seven days and read it back with the incremented security revision. Existing Session pin remains unchanged in store tests after changing the tenant default. Permission, stale and save feedback are exercised in focused component tests.
5. MCP layout review: Completed Chat at 1440×900, 768×800, 390×720, 320×640 and 390×420; governance at 1440×900 and 320×640; active Temporary at 320×640. No horizontal overflow; New chat remains within each tested viewport. At 200% CSS scaling with reduced-motion emulation, terminal content and New chat remain reachable and unclipped. This is browser layout evidence, not physical-device certification. Current MCP console: zero errors; aborted requests were navigation/expiry cancellations, with normal protected requests returning 200 before the projected expiry.

## Corrections made during verification

- Publish terminal completion only after the durable final-response commit; ordinary persistence callbacks intentionally skip ended Sessions, so this path has its own existing-runtime persistence kind.
- Preserve exact pasted bytes by handling an empty Lexical root without inserting an extra paragraph newline.
- Clear expiry state before waiting for transport shutdown; invalidate history, pending send/voice state and private drawers. Ignore late ready/ACK callbacks.
- Add current due-time guards to SQLite Attachment/Artifact ownership as well as their public service fences, matching InMemory behavior.
- Use native SQLite column removal for the guarded retention downgrade so external trigger owners retain `Sessions`; preserve historical migration files. Guard both Session retention and tenant policy against lossy downgrade.
- Remove stale owner-capability localStorage setup from affected realtime fixtures and explicitly authorize the exact Agent Private Space in new Synthetic/MessagePack fixtures. No product permission is auto-granted.

## Operations and limits

Ship backend, migration and browser together. Back up supported database, keys and byte roots; migrate a copy and use the existing explicit upgrade/startup procedure. Saved defaults are pinned at creation; Temporary is fixed creation +24h. Logical access expires immediately; physical cleanup is eventual. InFlight/Indeterminate effects retain evidence after factual Run cancellation clears the claim and settles completion accounting; physical retirement waits for factual reconciliation, and external Space/email/service effects remain independently owned. Recovery handles one failed due Session without starving later targets.

Hosted CI/full solution tests, live-provider calls, production deployment/restore and physical headset/microphone checks were not requested or run. Synthetic runtime/voice regression evidence does not certify audible quality. The two pre-existing orphan Impeccable briefs for removed AdminApp/ApprovalModal surfaces were reported by tooling; this task updates only the affected current Chat/governance briefs. Historical unscoped browser fixtures and reports remain historical, outside the canonical tenant journeys exercised here.

## Source and main integration

Baseline: `ed73a7762e4818a765438b236184824c9b9b4971`. Implementation commit and subsequent main synchronization are recorded in the final verification follow-up. No supported migration history was removed, no provider credentials were used, and no hosted Actions run was dispatched.


## Main synchronization qualification

Implementation `642a754e` was committed and pushed to `develop/branch-1` before fetching main. Main advanced to `0ac18c7e` (`ffe4c9b9` Self-Management implementation plus its evidence commit). The merge retains both features: README and design context keep both summaries; the tenant-chat configuration includes both new retention journeys and main's management-read suite. Automatic merges retain main's explicit Private Space fixture authority, sparse policy guards, current Synthetic management-task selection, approval checks and shared SelfManagementFields presentation.

`20261026010000_CanonicalSelfManagement` precedes `20261026080000_ChatRetention`; the retention upgrade/refusal test now targets that actual predecessor. The combined current schema has 190 triggers, including both features' guards. No historical migration was rewritten. A final retirement check now uses incumbent factual cancellation to clear a quiescent uncertain Run's claim and settle accounting while preserving its InFlight/Indeterminate disposition and private evidence; it neither claims the external effect succeeded nor deletes its evidence.

Post-merge checks:

- Domain: `ManagementPolicyRestrictionTests|SessionLifecycle|AgentRun`, 68 passed.

- Infrastructure: `TenantChatRetentionTests|ChatRetentionMigrationTests|SelfManagement_one_time|SelfManagement_sqlite|FrozenMigrationMetadataTests|CanonicalSchemaAcceptanceTests`, 63 passed; final uncertain-claim retirement recheck `TenantChatRetentionTests`, 8 passed.
- API: `Explicit_agent_completion|Tenant_live_MessagePack_JavaScript|SelfManagementAuthorityTests|Canonical_Session_HTTP_pins_authenticated_creator`, 17 passed.
- Browser: the four files `chat-retention.spec.ts`, `tenant-chat.spec.ts`, `tenant-management-reads.spec.ts`, `tenant-management-mutations.spec.ts` selected with `--grep 'Saved completion|Temporary long text|management read consent approve 1440|management command settings.patch approve 1440|tenant text, rendered|local queue drains|tenant Steer'`, 7 passed (3.1m). Same `TENANT_CHAT_API_PORT=5098 TENANT_CHAT_WEB_PORT=5188 CI=1 pnpm exec playwright test --config playwright.tenant-chat.config.ts` prefix as above.
- Frontend: ChatApp, Composer, TenantAgentControls, TenantGovernance, realtime tenant/race selected 146 cases. Four files passed 132 cases on the combined run. Two Admin cases failed under concurrent build/test load (one 30s timeout and one transient modal visibility assertion); isolated `--maxWorkers=1 --no-file-parallelism` recheck passed all 14 cases in those two files. No assertion or product behavior was weakened. `pnpm run build` passed typecheck and Vite (31.16s); existing chunk-size and third-party PURE-annotation warnings remain.

The merged source commit is recorded by the final documentation follow-up so it identifies all behavior changes without a self-referential commit hash. These tests qualify the combined local implementation; hosted CI, production restore/deployment, live providers and physical audio remain unrun.
