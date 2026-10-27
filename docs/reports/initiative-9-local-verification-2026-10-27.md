# Initiative 9 — Task delegation and communication delivery

Implemented and reviewed locally on 2026-10-27–28, from baseline `0ca5eac3b56d7d9e1788b63d14785a205a55cdfc`. The later owner TODO-only commit `3ff0c63a0bac31b978bbfea18802dd22e286bed8` is preserved as the delivery parent. Verification ran on the source delivered with this report. This is focused local delivery evidence, not acceptance of a new MVP milestone, hosted deployment, live providers or physical devices. The [adopted proposal](../proposals/initiative-9-task-delegation-communication.md) owns scope; the [source audit](initiative-9-source-audit-2026-10-27.md) records removal and replacement dispositions.

## Delivered behavior

A durable Task binds an actual claimed Agent Run or authenticated Human command to an independently authorized worker. The worker owns its principal, Session, Activation, configuration, Space/Environment, model and bounded execution allowance. Human commands create no fictitious source Run. Same-Agent work follows the same Task path. Dependencies create no speculative worker graph; ready dependencies admit once. Exact current Review/ExceptionalGrant uses the existing approval transaction and freezes the assignment. Cooperation defaults off.

Task correspondence has exactly source/worker roles with actual sender attribution. Human guidance is visibly human. Direct Agent threads have exactly two Agent principals; humans can inspect within current scope and cannot post as an Agent. Optional recipient wake uses its own ordinary Run, current capabilities and bounded hop/rate/concurrency limits. There is no additional execution engine, group conversation or external A2A protocol.

Saved terminal Run facts determine Task outcome across approval successors. Unknown non-replayable effects stay NeedsAttention. Exactly one Task notice exists even when no worker was admitted. Acknowledgment, optional parent-owned report admission/completion and independent Goal verification are separate facts. Reports reserve eight steps and 60 seconds from the same root allowance and cannot redelegate. The enforced resource dimensions are steps and active execution time, with a separate absolute deadline; provider-token pricing and monetary accounting are not implemented or claimed.

Chat has a scoped Tasks drawer, exact delegation/Review, messages, cancellation/replacement and explicit uncertain-request readback. Agent Activity has assigned Tasks and direct correspondence; Settings has opt-in Cooperation. Worker/result/Space/Environment/Artifact metadata is independently filtered. Existing ordered Session Hub hints carry authorized IDs/revisions only and cause scoped HTTP refetch. Reads remain authoritative after reconnect or revocation.

General Assistant v35 uses `tasks.*` and `agent-messages.*`. The five predecessor child tools, child approval decoders, model take/usage-acknowledgment, old wait selectors and dormant consumption state machine are removed. Runtime v21–34 definitions are retired; their 14 exact baseline files are archived as test fixtures. Independent Automation destination reports retain their existing owner and receipt semantics.

## Focused verification

The runs below overlap. They are not a summed test total. Logs are local `/private/tmp/i9-*.log` files; test source and representative UI captures are committed. Initial failures and their resolutions are recorded below rather than counted as passing runs.

| Area | Observed result |
| --- | --- |
| Task domain | `final-domain`: **10 passed**, including closed source/assignment, reserved consent identities, immutable terminal facts, retention tombstones, off-by-default cooperation and invalid depth. |
| Closed Application contracts | `final-application`: **27 passed**. Earlier affected Task/wait/native capability projection selection: **81 passed**. |
| Real cooperation/transactions | `final-cooperation-transactions`: **68 passed**, InMemory/SQLite: actual same/cross-Agent model delegation, model/Human consent, exceptional grants, independent reviewers, explicit Task-scoped reads, notice discovery/acknowledgment, direct messages/wakes and independent scheduled/manual roots. |
| Root allowance | `nested-root-budget`: **2 passed**, three actual delegation edges share one frozen root across SQLite reopening; no fourth child and no allowance reset. Parallel fan-out cases in the cooperation group admit two workers and refuse a third. |
| Concurrent exact admission | `concurrent-task-admission`: **2 passed**, separately prepared requests race through the actual stores: one created graph and one replay. |
| Direct privacy | `direct-third-agent-privacy`: **2 passed**; a third real Agent with broad message permission sees no thread and cannot read its exact ID. The actual two-hop wake, pair/hour cap, restart and retired-thread generation cases remain passing. |
| Task/Goal independence | `final-goal-task-independence`: **6 passed**, actual worker question/guidance/completion, report/inbox-only, acknowledgment, dependencies/reopen/Temporary retirement. The independently accepted Goal retains its revision, Active status and Pending checkpoint with no evidence after Task success and acknowledgment. |
| Cutover, retention and waits | `final-cutover-retention-wait`: **30 passed**, current privacy/retirement, real predecessor migration, strict tool/payload retirement, Task waits and independent report-source/inventory cases. |
| Receipt retirement | `consumption-removal-regression`: **18 passed**, removed consumption model/ports plus independent root-bound report, predecessor and empty-migration parity. `consumption-final-migration-reports`: **29 passed**, including actual predecessor refusal for statuses 1/2 and current independent report Review/receipt/replay cases. |
| Native file/Artifact effects | **24 scenarios qualified in split runs**: first 20 passed/4 scoped-fixture failures, then the corrected four passed. Both-store native transaction boundaries separately passed **8** same/cross-Agent × workspace mutation/Artifact publication cases. Physical files, actual consent/effect owners and exact publication receipts were used. |
| Native Chromium | **21 scenarios qualified in split runs**: 19 passed and two capability-fixture failures, then both corrected cases passed. Includes actual disposable Chromium, protected navigation/read/capture/transfer, rejection/cancellation/current capability and SQLite host recreation. No skipped Chromium case is counted. |
| Sandbox operations | **12 native-owner scenarios passed** after correcting their final Task read URL. They exercise the production Sandbox effect/read owners against a controlled local SDK peer: approve/reject/cancel/uncertain/provision/inspect/stop/reset/profiles/status/process boundaries. This is not real container execution. |
| Retained native source authority | `retained-sandbox-current-authority`: **3 passed**, completed command/inspection observations become unreadable after the real worker permission is revoked; saved Task success and terminal identity remain factual. Final command/workspace guard is also covered by the HTTP/native recheck. Earlier live/terminal compound Session/Space/workspace group: **12 passed**. |
| Shutdown and request isolation | `task-shutdown-lifecycle-final`: **7 passed**: an unregistered-but-retiring Runtime blocks host disposal until final release; registry, SQLite account discovery and no-report cleanup cases pass. |
| HTTP/wire/independent roots | `final-http-wire-native-sources`: **10 passed**, authenticated Task HTTP, both-store real JavaScript MessagePack/WebSockets/reconnect, request-isolation wire, duration waits/independent Automation, retained Sandbox command. |
| Strict HTTP input | `final-closed-http-sandbox`: **2 passed** after duplicate-property rejection. Actual duplicate objective/security-revision fields return 400 and create no Task; ordinary admission/recovery still works. |
| Retired model tools | `task-wait-handoff-cutover`: **12 passed**: all five old calls against the actual executor on both stores admit no Task/worker; actual worker approval successor waits resume only after factual final completion. |
| Frontend | `consumption-removal-ui-final`: **39 passed** across Task Center and Background Work after removing the obsolete handled-Run shortcut/test. The final entire changed approval-inspector file passed **134/134** (`final-approval-inspector`), resolving the six earlier fixture expectation failures; the intermediate affected selection passed 10. Realtime/context consumer selection passed **25**. Skipped unrelated tests are not counted. |
| Static/build | TypeScript and the production frontend build passed; API builds passed. Existing Rollup/chunk-size warnings and a jsdom Ant Design textarea-height warning remain; native affected layouts were checked separately. `git diff --check`, documentation and frozen-fixture checks are recorded in the final audit below. |

## Actual browser journeys

Playwright MCP operated the running Synthetic API/SPA with disposable SQLite and protected roots, no provider credentials. The source Human Session `b5d64396-800b-4813-bef7-b7baa2e66fad` assigned Task `01a17f76-7ea0-7468-9f3f-987b2fd0ee88` to Riley (`01a17f75-c26f-754a-8804-2047f7175ea1`). Cooperation was observed off, enabled and saved through Settings. The form showed the actual worker Space, Scripted Alpha model and 32-step/240-second ceiling.

The real worker asked a durable question and suspended. Its Human guidance POST committed on the server, then its response was deliberately aborted. The UI retained uncertainty and disabled further mutation until **Read saved request** recovered the exact saved receipt without another POST. Exactly one HumanGuidance message exists at sequence 2 after the Agent question. Worker Run `01a17f76-7ea0-7f84-aa24-05bb8708fa84` completed under the target principal in a distinct worker Session. Human source Run and worker parent Run remained null. Acknowledgment was performed separately; inbox-only admitted no report.

A second assignment required real `delegate_task` Review. The UI displayed the frozen target/Space/Environment/model/budget/deadline, approved that action through the existing inspector and inspected the admitted worker. Task `01a17f7d-00e7-7cc4-9323-a11da1108682` succeeded with the selected Private Space and Reviewed local profile. Environment values were never exposed. An actual Riley Chat Run sent a direct Agent message to Alex through `agent-messages.send`; Activity displayed the two-Agent thread without a Human composer.

The final Background Work consumer check also observed a real independently admitted Automation result and Reported destination status. Eighteen Tab moves stayed in its drawer, Escape returned focus to its opener, and the owned fixture schedule was then disabled. The retired handled-result shortcut is absent; existing Activity owns Run inspection.

For stale-response privacy, an already executed message response was held while subsequent Task reads returned a controlled 403. The drawer retired its private scope; releasing the old response did not restore content or controls. This is an intentional transport fault fixture, not a claim of browser-driven IAM revocation. Real IAM revocation is exercised by backend service/native tests.

Native browser layouts were measured and captured at **320/390/768/1024/1440** with no horizontal drawer overflow. Repeated forward/reverse Tab cycles, Escape and opener restoration passed for Tasks, Goals, Files, Sandbox and Background Work. Reduced motion was enabled. A separate context used **720×500 CSS pixels at DPR 2**, producing a 1440×1000 capture for equivalent 200% reflow. Native browser-menu zoom itself was not exercised.

Representative committed captures: [desktop Task](assets/initiative-9/task-current-reviewed-1440.png), [390px Task](assets/initiative-9/task-current-reviewed-390.png), [communication](assets/initiative-9/communication-1440.png), [200% equivalent reflow](assets/initiative-9/task-200-percent-equivalent.png), [denied late reply](assets/initiative-9/task-denied-late-reply-1440.png), [independent report consumer](assets/initiative-9/consumer-background-work-final-1440.png). The full five-width/state/consumer capture set remains under ignored `.impeccable/review/i9-current/`.

## Design and self-review

Pre-code Task and communication surface contracts were recorded after the fresh-source audit, using the incumbent Ant Design operational UI. No separate human design sign-off is claimed. Impeccable context ran once. The independent finish reviewer first requested settled captures and a correctly sized reflow capture; those were supplied. Its material findings were missing worker Space/Environment in Task detail and an undersized desktop direct-thread selector. Both were corrected and scored resolved. The final reviewer disposition was **`ship` for those two scored fixes**, with no visible regression from that batch; it did not independently certify every backend or whole-initiative requirement.

The documenter returned **No changes** after checking the context/design files, theme/layout owners, both surface briefs and affected components. Current UI reuses the established dark/blue semantic palette, system type scale, token spacing, 640px/full-narrow drawers, direct Ant Design controls and shared keyboard handling. Preexisting design-sidecar inspection metadata drift and two older orphaned briefs were not canonized or repaired.

Self-review also corrected current model-message/cancel Review enforcement, report inventory filtering before count/page, actual-source retention, terminal native source rechecks, Task settlement in request isolation, shutdown retirement and dormant completion consumption. The source audit maps removed child tests to exact current replacements; removed methods are not presented as skipped or passing tests.

## T01–T40 evidence map

Rows identify the exercised owners and practical limits, not 40 new disjoint test runs. Current authority is repeated at preparation, transaction, execution and read boundaries; existing file/Artifact/Evaluation/Goal owners remain independent.

| Requirement | Current evidence |
| --- | --- |
| T01 | Actual claimed same-Agent model delegation in `Cooperation_actual_model_delegates_or_sends_as_its_claimed_Agent`; distinct worker Session, one graph. |
| T02 | Cross-Agent model/Human cases and native workers verify actual target principal/configuration/Space/model. |
| T03 | Closed Application contracts, Task HTTP and transaction forgery cases reject caller identity/root/Run witnesses before mutation. |
| T04 | Default-off target refusal, current admission/configuration checks and stale Space Membership capture refusal; no alternate fallback is admitted. Lifecycle eligibility uses the incumbent active-Agent boundary. |
| T05 | Prepared-admission generation/lease/cancellation/revoked/capability cases; current worker native capability loss also refuses use. |
| T06 | Exact configuration/assignment hash and current model/Space rechecks; stale prepared configuration is rejected. |
| T07 | HTTP independently removes worker execution authority; both-store secondary/primary/Review transaction cases refuse without orphan graph. |
| T08 | Scheduled/manual actual Automation delegation preserves null scheduled requester or actual invoker; worker execution authority is separate. |
| T09 | Exact model/Human Review, independent reviewer, role loss and exceptional grant cases; protected native rejection/cancel/revocation journeys retain existing downstream guards. |
| T10 | Store/HTTP same-key replay, uncertain saved-guidance readback and immutable admission receipt. |
| T11 | Changed objective/target/cancel intent conflicts without a changed saved effect. |
| T12 | Separately prepared concurrent store admissions produce one graph/receipt in both stores, including actual SQLite CAS/uniqueness. |
| T13 | Atomic Task/worker/receipt transaction negatives, SQLite reopen, claim recovery and stale writer refusal; no logical identity replacement. |
| T14 | Two-worker fan-out/third refusal, three-edge root-budget proof, invalid depth and separate report reservation. Steps/time/deadline are enforced; no financial/token ledger. |
| T15 | Successful prerequisite admits the dependent once; failed/cancelled prerequisite blocks without worker graph; blocked notice has no invented Run. |
| T16 | Actual claimed worker question, Human guidance and Agent correspondence have immutable sender/sequence and current read filtering. |
| T17 | Two-Agent direct case, third claimed Agent denial, HTTP anonymous/foreign-tenant and exact-ID privacy. |
| T18 | Saved message key, repeated delivery/reconcile and SQLite recreation preserve one logical message/wake; ordered reconnect hints confer no authority. |
| T19 | Default-off/direct opt-in, active-thread exclusion, two-hop/eight-per-hour and durable refused/pending delivery facts. |
| T20 | Actual Task wait/question/completion/timeout/cancel/reopen cases keep one Run/response/attempt identity and fenced wake. |
| T21 | Actual `work.complete` and exact approval-successor chain; initial approval pause does not prematurely settle a Task. |
| T22 | Both-store cross-Agent report cases admit and complete a parent-principal Run with exact Task source and reserved budget. |
| T23 | Retired/deleted source, stale parent configuration, absent worker and current source authority withhold report; no injected placeholder Run. |
| T24 | Metadata-only observer gets no worker/result/Run/context; source-scoped file fixtures keep transcript/Artifact bytes independently denied. |
| T25 | Final six-case Goal-independence assertion retains accepted Goal revision, Active state, Pending checkpoint and no evidence after Task success/report/ack. |
| T26 | Same/cross-Agent file/Artifact transaction tests plus actual runtime publication consent, changed bytes, exact receipts and current source revocation. |
| T27 | Dependency/queued cancellation, actual native cancellation and indeterminate single-dispatch effects preserve honest outcomes/attention. |
| T28 | HTTP/UI replacement creates a new key/Task ID with exact `retryOf`; original terminal facts remain. |
| T29 | Actual SQLite store/host recreation for Tasks, messages, consent, waits and native Browser; shutdown waits for final Runtime release. |
| T30 | Saved/Temporary source retirement, physical content/intent tombstones, inherited wake privacy, thread generations and UI late-reply retirement. |
| T31 | Both-store actual model calls for all five retired names, old-version refusal, closed approval/wait/origin negatives and frozen fixture archives. |
| T32 | Independent Automation destination Review/report/receipt/restart, actual scheduled/manual delegation and duration/wire regressions; existing event/manual origin identity remains unchanged. |
| T33 | Capacity refusal before graph mutation; exact scoped filtered Task/message pages, observer privacy, multi-page UI reload and pre-count independent report filtering. No load benchmark is claimed. |
| T34 | Actual protected physical file/Artifact/Chromium use with worker identity; production Sandbox owner against controlled SDK peer. Real container qualification remains separate. |
| T35 | Actual MCP assignment→question→lost-reply readback→completion→inspection/ack, real Review and direct Agent communication. |
| T36 | All five widths, equivalent 200% CSS reflow, reduced motion, repeated keyboard/Escape/opener checks; browser-menu zoom and manual screen-reader/device checks unrun. |
| T37 | Delayed saved response after controlled denied refresh remains retired; RTL tests cover tenant/Session changes, loaded pages and late callbacks. |
| T38 | Shared drawer consumers exercised in MCP; Task/Background/approval/transport regressions pass. Physical voice/audio was not rerun. |
| T39 | Actual SessionGoals predecessor upgrade/refusal, matching SQLite backup, empty down/up/schema parity, populated Task downgrade refusal and retired receipt-state refusal. No customer reset. |
| T40 | Canonical owners/status/API/tool/migration/UI copy synchronized; removal audit and independent finish reviewer/documenter outcomes recorded with their actual scope. |

## Cutover and qualification limits

Stop writers and back up SQLite plus matching Workspace/Artifact/Attachment/resource/key roots before the offline migration. `20261027060000_TaskDelegation` follows `20261027040000_SessionGoals`; startup refuses automatic upgrade. Installed old child graphs, old consent/waits/consumption, retired capabilities or General Assistant v21–34 require deliberate disposition. The migration does not infer cross-Agent lineage or silently delete customer state. Populated Task/consent/receipt evidence refuses a lossy downgrade; restore the coordinated backup. See [persistence](../15-persistence-and-configuration.md#initiative-9--task-delegation-and-communication) and [operations](../17-observability-and-operations.md#initiative-9--task-delegation-and-communication).

No user database was reset. Only owned disposable Synthetic fixtures and roots were created. Hosted CI was neither dispatched, rerun nor awaited. Full CI suites, live providers, deployment, production capacity/load, real Docker/OpenSandbox container execution, GPU/physical audio/device certification, browser-menu zoom and manual screen-reader qualification were not run. These are explicit qualification limits; none is represented as passing or as original MVP milestone acceptance.

## Final source audit

The final receipt/migration group passed **29/29**, followed by **3/3** final actual-predecessor tests for retired statuses and an orphaned saved consumption token. Final API Task HTTP and shutdown cases passed **2/2** in the four-case selection; its two independent manual-report fixtures initially lacked `spaces.read`. After correcting only that explicit fixture grant, the independent manual/scheduled/source-Review/creation-Review/SQLite-restart selection passed **10/10** (`final-independent-automation-report`). No production permission was weakened. The entire changed approval-inspector file passed **134/134**. The final Task/Background UI selection passed **39/39**; the obsolete handled-Run test was deleted with its unsupported shortcut.

The final copied-metadata Review recheck passed **2/2** (`final-task-metadata-review-recheck`). Agent read Review hides copied Agent names/IDs. Source-Session read Review already denies the whole tool response through notice discovery; the initial assertion expected partial metadata and was corrected to this stronger boundary, with policy restored before testing independent Task/message denial.

TypeScript, the production frontend build, Playwright test discovery and staged whitespace checking passed. The 24 changed Markdown files passed relative-link/fragment, fence and complete-JSON checks with no new or preexisting issue in that checked set. All seven runtime Definition JSON files parse; all 14 retired built-in fixtures match their original baseline bytes. No new TODO/FIXME/HACK, skipped test or unimplemented branch was found in the staged production changes.

The delivered code fingerprints are Git subtree object IDs, independent of this report's own commit hash:

| Source subtree | Git tree |
| --- | --- |
| `src` | `04124ed0c885d32273fe560ba21c53674d1f5afa` |
| `tests` | `b81eb3fc4e79f98861276c123a52a0a1cb75edda` |
| `web` | `8792d4ca12b777aa0ac385c37c230a8da1aacbc8` |
| `agents` | `757b112907f1b7ea71044fa47554e1e7de1bf278` |

D0/D1 source/design work, B1–B5 implementation, F1/F2 UI and R removal/documentation have the focused evidence recorded above. Final local qualification is bounded by the explicit T34/T36/T38 external/runtime limits; this report does not claim unrun native container, browser-menu zoom, physical voice, hosted or production acceptance. No full CI suite was run.

Owned Synthetic API/Vite verification processes were stopped after browser checks; the disposable evidence roots were retained.
