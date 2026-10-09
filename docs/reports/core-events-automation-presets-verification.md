# Core Events, filters and Automation presets verification

Status: implementation complete; integrated acceptance gates are in progress on develop/branch-1. This report is not a green closure claim.

## Scope and baseline

Implementation starts at 16ed2365fb24e5c7b010df1ed6e77860afaa9862. The proposal is local/proposals/agent-core-core-events-filtering-presets-final-20261010.md. Remote main advanced to 347b8657f2c91a80b75ed5031dff73c067effcab during implementation; its model continuation, reasoning, budget UI and terminal reply-time changes are being reconciled before final verification. Historical freeze reports and published Definitions remain evidence for their original changes.

Domain, Application, Contracts, API, Infrastructure persistence/providers and the existing Admin Automation editor own the extension. General Assistant v22 and Secretary v9 opt into coreEvent; prior immutable versions retain their policy. No preset is installed automatically.

## Source and persistence decisions

The six safe event sources share the ordinary Automation occurrence and AgentRun path. Run terminal decisions and existing Session lifecycle transitions stage durable receipts in the same authoritative transaction. InstanceStore instruments persona, unattended model/effort, execution budgets and effective Harness policy; adoption emits separately. Independent continuity/Experience/Skill/credential setting stores are outside config_changed V1, and draft changes do not emit.

Core snapshots freeze on first outbox fan-out; webhook snapshots freeze at authenticated receipt. Decisions, revision, expression version/hash and dispatch survive restart. Current disable, owner/source authority and preset prerequisites stay live. Depth four and visited Automation IDs bound chains. User text cannot set trusted causation.

Migration CoreEventAutomationFilters appends columns, enum values and three tables. It preserves Schedule/webhook rows and existing webhook receipt identities. Populated prior-schema migration tests inspect nullable defaults and reopen persisted subscribers. SQLite and InMemory tests exercise terminal emission, retry exclusions, rollback, lifecycle distinction and coverage.

Buckets retain up to 24 references within a bounded payload; at most 32 are pending per Automation. Overflow and failed flushes retain source coverage and safe reason codes. Owner-checked cursor pages enumerate unfinished references, and experience.source validates exact sources before acknowledging inspected coverage. History is retained for the Instance lifetime and purged on deletion; there is no timer that deletes undelivered or unreviewed evidence.

## Filter restrictions

Jint 4.4.1 is pinned. An independent static expression grammar rejects calls, assignments, globals, arithmetic, dynamic access, prototypes, escape tricks and statement syntax before execution. Fresh private JSON realms expose no CLR grants or host callbacks. Limits are 1,024 UTF-8 source bytes, 128 nodes, depth 12, four evaluation slots, 100 ms linked deadline, 256 statements and 4 MiB engine allocation. JSON and literal numbers require safe finite decimal round-trip precision. Duplicate keys fail closed. Jint is an in-process interpreter, not an OS security boundary; see [Jint untrusted-code guidance](https://sebastienros.github.io/jint/guide/untrusted-code). Newer Jint 5 APIs are not assumed available in the pinned runtime.

## Executed feature evidence before final integration

- Real Synthetic host: two completed UserTurn Runs across different Sessions coalesce into one background review. The normal runtime calls experience.source and records two exact source Experiences. Coverage is exhausted, no Memory promotion occurs, review completion fails the UserTurn filter, and reopen creates no duplicate effect.
- Core config fan-out: owner A's frozen true expression still admits after editing the saved expression to false; a false sibling and same-key subscriber on owner B admit nothing. Read-only filter testing creates no additional occurrence. Reopen preserves decisions.
- Webhook: the unchanged authenticated eventId/data body fans out to true, false and coalesced subscribers; replay is duplicate, false is durable Filtered, and explicit grouping flushes once.
- Adoption/chain: no-op version writes emit nothing, effective 21→22 adoption emits once, depth-four and repeated Automation chains skip, and unsupported frozen expression versions fail closed.
- Chat: an explicit current-user-turn request creates an ordinary disabled review preset with source Session and immutable template provenance. Consolidation/Harness options expose missing prerequisites.
- Infrastructure: 29 focused lifecycle/filter/bucket checks passed, including both stores, final failure after transient retry, failed lifecycle CAS with no event, ambiguous JSON, unsafe precision, bounded spill/reopen and foreign-owner coverage denial.
- Browser CLI fallback: Playwright MCP is unavailable. Isolated Synthetic hosts use ports 5850/5851/5891 and disposable /tmp roots. Initial Core preset plus webhook/Run journeys passed; final integrated rerun is pending. UI shows Matched / Not matched / Error, retains drafts and preset provenance, saves disabled, creates zero Runs, preserves Escape/return focus and footer containment.
- Impeccable context launcher was attempted once; environment write/network restrictions prevented completion. Existing source-backed context and polish guidance were read directly, and DESIGN/PRODUCT/Admin surface context were synchronized.
- Captures: local/core-events-evidence/core-event-filter-1440.png, core-event-filter-768.png and core-event-filter-390.png. The final capture round will replace these at 900px height. No shared design token change was needed.
- Isolated Compose project agent-core-core-events-20261010 on port 5858 passed SQLite volume recreation with Sessions, workspaces, skills, Automations and credential key ring retained; final integrated rerun is pending.

## Final gate ledger

Full backend, frontend, build, browser, Compose and all five hosted synthetic jobs must pass on the final integrated behavior SHA. Earlier iterations found and fixed SignalInput compatibility, plain-text causation parsing, catalog version assumptions and UI test loading/focus defects. An earlier full frontend run had five Admin timeouts under competing load; isolated Admin rerun passed all 42. These earlier runs are diagnostic evidence, not exact-SHA closure.

Final behavior SHA, exact commands/counts, workflow URL and all remaining limitations will be recorded after integration and verification.
