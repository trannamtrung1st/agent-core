# Semantic Automation authorization verification — 2026-10-16

## Scope and decision

The owner requested a complete semantic cutover after repeated Automation authorization failures. Earlier wording bypasses and a classifier that saw only Create/Update could neither verify the actual proposed task nor provide durable recovery. This successor removes production natural-language authorization matching rather than extending its vocabulary.

The agent interprets requests in the model's supported languages and submits complete structured arguments. Independent verification compares the current user request with the exact action, owned current target, revision, schedule/defaults, instructions, execution destination, delivery and enabled state. Core independently enforces trusted current-user provenance, ownership, permissions, live policy and structural validation. Create/update/delete/enable/disable and Run now share this contract. List/inspect results, historical messages, Memory, retrieved text, initiative and Automation occurrences cannot independently grant mutation authority.

Current clarification can refer to prior conversation: the runtime supplies at most eight prior nonstreaming turns within 12,000 serialized characters as Evidence, excluding the current source turn. Assistant text uses existing delivered/heard-prefix projection. Draft arguments are retained for one eligible next turn as Evidence; Core never merges them or interprets interval corrections. The agent must propose complete fields. Missing weekly weekday/time or timezone is a structural scheduling issue, separate from authorization.

Real verification retains the selected catalog route, Core/Runtime/Evidence hierarchy, supported reasoning effort, strict single-decision JSON and completed-terminal requirement. Budgets remain 512 tokens, then at most one fresh 1024-token recovery only after confirmed LengthLimit, within one shared 30-second/caller deadline. Partial Allow, incomplete streams, malformed/oversized/channel-incompatible output and provider failures cannot authorize effects. Logs contain bounded route/outcome metadata, never user text, action JSON, credentials or raw provider output.

Deny/Ambiguous require clarification. Unavailable/invalid verification retains the exact action in the existing durable Run approval workflow. Explicit owner UI approval binds its fingerprint and response; owner, source, Session, revision and live policy are rechecked. Changed actions invalidate approval. Reject, expiry, user stop and supersession produce no mutation. Reconnect and host restart retain the same proposal without another verifier call. Creation still grants no future Memory or other effect permissions.

No new scheduler, approval subsystem, table or migration is introduced. Existing Automation records and policy services remain. Automation-specific approval limits are 65,536 UTF-8 prepared-argument bytes and 131,072 preview characters; generic and Drive limits are unchanged. Preparation also bounds exact verification JSON to 64,000 characters and checks the serialized complete preview before persistence. Long non-English text is rendered inert and readable through the existing Ant Design modal.

## Verification

All checks were local and key-free. Backend commands used `TMPDIR=/private/tmp DOTNET_CLI_USE_MSBUILD_SERVER=0 dotnet test <project> --no-restore -m:1 -p:UseSharedCompilation=false --nologo --filter '<focused classes>' -- RunConfiguration.TestSessionTimeout=<60000–120000>`. Local test-runner sockets required sandbox escalation. No hosted CI or live model calls were made.

| Project/check | Focus | Result |
| --- | --- | --- |
| `tests/AgentCore.Domain.Tests` | Automation/Drive approval bounds, AgentRun contracts | 38 passed |
| `tests/AgentCore.Application.Tests` | TriggerCommandAuthorizer, ModelTriggerCommandAuthorizer, TriggerSchedule*, ScheduleDraftAdmission, TriggerDurablePolicy, TriggerOccurrenceRouting, ToolApproval, ToolPolicy | 147 passed |
| `tests/AgentCore.Infrastructure.Tests` | AutomationActivationTool, AutomationAuthorizationProvider, HostedTriggerTool, ResponsesTransport | 83 passed |
| `tests/AgentCore.Api.Tests` | AutomationSemanticAuthorization, AutomationAuthorizationJourney, AutomationActivationJourney, MultiTriggerAutomationJourney; Reliability negation/follow-up, persistence/execution and ambiguous-diagnostic cases | 87 passed |
| API supplemental integration | Pending host-restart approval; ChatAutomationJourney, AutomationDestinationJourney, CoreEventAutomationJourney, TriggerScheduleApi | 33 passed; restart case overlaps the final 87-case run |
| `web`: `pnpm exec vitest run src/features/chat/ApprovalModal.test.tsx` | Complete inert create/Run now previews, explicit decisions and existing approval consumers | 25 passed |
| `web`: `pnpm run build` | TypeScript and Vite production build | Passed; existing large-chunk advisory remains |
| Documentation/source consistency | Relative links, new anchors, balanced fences, retired-authorizer reference audit, `git diff --check` | Passed |

Runtime scenarios exercised real Session Runtime/services with temporary SQLite, not only compilation: daily 09:00 Vietnam-time background creation with quiet delivery; Allow/Deny/Ambiguous; invalid/unavailable verification with approval/rejection; no record before decision; one verifier call; exact retained preview on reconnect/restart; no reclassification on approved restart; task/time/timezone/destination/delivery/requirement mutation; Session/response/source/provenance mismatch; target revision changes; expiry/user stop; complete long Japanese instructions beyond the generic preview bound; Run now admission; explicit disabled creation; and unavailable structured timezones rejected before classification. Existing daily/weekly offline Real-transport journeys exercise catalog routing and truncation recovery on Chat Completions and Responses. Scheduler journeys verify execution, preserved tasks/destinations, occurrence dedupe and SQLite reopen. Current Vietnamese timezone clarification receives bounded prior user Evidence; an explicit Deny fixture still saves nothing. Unacknowledged assistant prose stays excluded.

Initial failures identified obsolete grammar/draft-merging expectations, an assertion for `stale` rather than `conflict`, and expectations for classifier errors after an explicit UI rejection. Those fixtures now use typed verifier decisions and the existing `rejected` result. The final exact-action review also corrected regular Schedule/Webhook creation ignoring explicit `enabled:false`, aligned trigger-collection default-state previews with their disabled authoring default, and moved actual timezone availability validation before verification (the new regression initially caught the unavailable-zone proposal reaching classification). A frontend build caught an unsupported Testing Library `exact` option; it was removed and both unit tests and build passed. A separate nine-case state/preset/collection run passed before the final focused API run. The preview-bound review added a genuine long-content runtime regression. No failed checks remain in the focused scope.

### Browser interaction

Playwright MCP exercised a disposable Synthetic API on `127.0.0.1:5680` and Vite on `127.0.0.1:5683`, backed by `/private/tmp/agent-core-semantic-authorization-ui-20261016/verification.db`. Configuration selected `Synthetic__AutomationAuthorizationDecision=ClassifierUnavailable`; `[test:automation-approval]` supplied a complete deterministic agent proposal. This setting is read only in Synthetic and is not a Real authorization bypass.

1. Created a disposable General Assistant v32 Instance and opened Chat. The preview displayed daily 09:00, `Asia/Ho_Chi_Minh`, the exact instructions, background execution and `reportBack:false`. Reject left the Automation drawer empty.
2. Submitted again, reloaded while awaiting approval, recovered the pending preview and approved. The drawer contained exactly one Automation. Admin readback showed the same task, daily 09:00 Vietnam timezone, Active status and Chat user-request provenance.
3. Checked 1440/768/390px layouts. Desktop/tablet modal width was 720px; at 390px it was 374px, with a 326px details region and no document/details horizontal overflow. Approve/Reject stayed reachable through the existing scrolling layout. Captured and inspected `/private/tmp/agent-core-semantic-authorization-ui-20261016/semantic-automation-approval-desktop.png` through MCP as local evidence.
4. Reloaded only the owned disposable API with the final implementation and reopened retained data. A fresh failure preview included source identity and proposed enabled/default fields. Reject preserved the one previously approved Automation, with no duplicate.
5. Inspected console/network. Relevant model, Run and Automation reads returned 200/204. The only flow console warning was the existing Ant Design `List` deprecation in the unchanged Automation drawer. An early setup request with stale local owner capability recovered through the normal capability bootstrap. No approval-flow transport errors were observed.

Early exploratory Synthetic requests used unrelated/incomplete old agent scripts and did not establish success; the complete marker above was used for the verified flow. Browser navigation was reset to `about:blank`; only verification-owned listeners were stopped. The user's existing host, Sessions and database were not restarted or changed.

## Retired behavior and replacement coverage

| Retired behavior | Current contract and coverage |
| --- | --- |
| HeuristicTriggerCommandAuthorizer, TimedScheduleIntent; clear-wording bypass | Every complete proposal reaches the independent exact-action verifier; multilingual/request-kind fixtures and offline Real route tests |
| ScheduleContinuationLanguage and interval text parsing/automatic argument merge | One-turn draft Evidence, complete agent-submitted correction, missing-field structural failures and stale referent revision conflicts |
| Bare yes/no pending-proposal execution | Durable exact owner UI approval; changed-action/source/Session/response fences, rejection, expiry, stop, reconnect and host restart |
| AutomationActivation word/cadence matching and Run now regex | Agent-owned list/inspect/reference interpretation; semantic target/change verification plus deterministic owned ID/revision, terminal-state, grant and live-policy tests |

Timezone alias normalization remains structural validation of a supplied timezone field; it does not inspect user messages or grant authorization. Synthetic phrase selectors choose offline agent scripts only; semantic decisions are explicit typed fixtures independent of text.

## Boundaries and deployment

This is focused request completion, not milestone acceptance. CI, full solution/frontend/E2E suites, Docker qualification and paid/live-provider semantic quality checks were not requested and were not run. Offline multilingual fixtures prove transport, evidence, gating and recovery behavior; they do not claim measured linguistic accuracy for a live selected model. Semantic safety still depends on that independent model assessment, while deterministic policy and exact UI approval provide separate enforcement/recovery.

Updated code requires an intentional restart of the user's backend to become active. This task restarts only its disposable verification host. No production Automation was created on the user's behalf. Historical verification reports remain evidence for their original versions.

Canonical owners: [Architecture](../03-system-architecture.md#semantic-automation-authorization-cutover-2026-10-16), [Interfaces](../04-backend-interfaces.md#automation-semantic-authorization-boundary), [Decision](../10-technology-decisions.md#language-independent-automation-authorization-2026-10-16), [Backend](../12-backend-implementation-spec.md#timeouts-and-retry-policy), [Protocol](../14-api-and-realtime-protocol.md#existing-automation-activation-tool-contract), [Configuration](../15-persistence-and-configuration.md#semantic-automation-verification-configuration-and-recovery), [Testing](../16-testing-strategy.md#explicit-greeting-scheduling-authorization-regression), [Operations](../17-observability-and-operations.md#automation-authorization-classifier-diagnostics), [Plan](../18-implementation-plan.md#semantic-automation-authorization-successor-2026-10-16).
