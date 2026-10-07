# Agent Instance Skills migration verification

Date: 2026-10-07. Requested scope: `local/proposals/agent_core_instance_skills_full_migration_proposal.md`. Candidate: working tree based on `e7fb4a62` (uncommitted implementation; no published behavior SHA yet).

## Status

Implementation is present as a single architectural cutover. All local backend/frontend/browser/persistence regression gates are green. Hosted Synthetic/Compose on the final published behavior SHA has not run; proposal acceptance criterion 20 and final closure remain pending. Historical P8/P8.5/P9.7 reports retain their original evidence and do not verify this migration.

## Implemented contracts

- Immutable reusable Definition procedures require explicit `Always`/`OnDemand` and `DefaultEnabled`. Instances retain stable-id enabled choices, including dormant ids, and own independently editable Guid-keyed procedures with revision, author, timestamps and copy provenance.
- Create/version transitions reconcile Definition state atomically with the owner and optional history. The combined enabled Always procedure budget is checked before committing a version change. Customization atomically creates a local copy and disables its source; Definition resource bindings reject copying. Same names never shadow.
- One resolver emits enabled origin-qualified keys. Every accepted user turn freezes the complete catalog; durable Work serializes equivalent catalog, active keys and load count before generation. Recovery/load/projection use the snapshot. Ordinary writes affect later executions. Always starts active; OnDemand requires `skills.load`. Limits remain four keys per call, two calls and 8000 active procedure characters, with no global count ceiling.
- Ordinary `skills.list/inspect/create/update/set_enabled/delete/customize` use existing Definition authorization, trusted current-instance context and one shared service for model and owner HTTP writes. Schemas reject model owner arguments; requirements never grant capabilities. Definition content is read-only from an instance. Archived owners allow inspection only.
- Harness retains knowledge, instructions and tool proposals. Its Skill scope, mutation descriptors, schemas, pipelines and UI controls are removed. Keyword selection and the unused pre-request repinning path are removed. Current execution/storage fields are `PinnedSkillCatalog` and `ActiveSkillKeys`.
- Admin instance detail has separate Definition/Instance collections with explicit copy explanation, warnings, retained conflict drafts, confirmation, responsive tables and accessible revealed content. Definition authoring uses the new explicit shape, preserving invalid/missing values for validation instead of silently inferring them.
- SQLite migration `20261007114116_InstanceSkillsCutover` adds owned state/local tables with scalar bounded content, owner foreign keys and revision concurrency. Hard deletion removes both collections. No backfill, dual reads, aliases or hidden conversion are added.

## Acceptance map

| Proposal criteria | Evidence owner |
| --- | --- |
| 1–5: UI, reusable/local ownership, version independence, explicit copy | `InstanceSkillsSection.test.tsx`, `instance-skills.spec.ts`, `SkillActivationTests`, owner API journey |
| 6–10: effective resolution, complete pins, explicit projection, canonical loading | `EffectiveSkillCatalogResolver`, `ConversationTurnExecution`, `DynamicSkillActivationTests`, `SkillPinStoreTests`, durable recovery integration |
| 11–13: authorized requirements and self-management, denied owner path | `DynamicSkillActivationTests`, `AgentInstanceSkillTests`, restricted Synthetic browser journey |
| 14–17: Harness removal, direct cutover, fresh data, shared write rules | cleanup search, EF migration/model check, common `AgentInstanceSkillService`, canonical docs |
| 18: store parity, rollback, reopen and deletion | InMemory/SQLite Skill contract cases and `AdminLifecycleDeletionTests` |
| 19: bounded UI finish | MCP desktop/mobile actions, four valid captures, fresh finish review and documentation handoff |
| 20: regression and exact-SHA hosted verification | local results below; hosted gate pending |

## Executed behavior

MCP exercised the running native Synthetic API/Vite app: create an Accounting procedure, inspect Definition content, Customize Record lookup and observe its source disabled plus an independent provenance-labelled copy, run Accounting in Chat, reload persisted state and inspect at 390×844. New/inspection headings received focus and scrolled into view; archived local content remained inspectable with an enabled Close action and opener focus restoration. The page had no horizontal overflow. No related console or network errors occurred; the favicon 404 was unrelated.

The repeatable Skills browser journey additionally exercised authorized Chat self-creation followed by a later-turn load, Admin edit, disable/reload, later-turn absence, confirmed deletion and restricted Customer Support denial. A separate UI journey published two versions, upgraded and rolled back the same instance: Definition procedure/version changed, its disabled choice remained, and same-name local content remained independent. The three journeys passed in 33.2 seconds on disposable SQLite.

Durable integration loaded an Instance procedure, checkpointed before a transient model failure, changed that procedure and created another Skill, recovered the same Work item, and observed only the old pinned content. Fresh Work saw the new metadata and loaded the updated procedure. SQLite reopen retains complete local procedures and active/load state. Store parity tests reject a combined Always-budget upgrade while retaining the original owner version/revision, state rows and local content.

Compose recreated an isolated Synthetic container and verified exact local Skill readback, source disabled state, revision, author/timestamps and customization provenance, alongside existing workspace/Automation/credential/result survival. It exposed a write/read timestamp precision mismatch; Skill writes now use the same millisecond precision as SQLite. The rerun passed. The project was `agent-core-instance-skills-check`, port 5114; its container/network and disposable volume were removed after verification. The native MCP API/Vite hosts were also stopped. The normal demo project and unrelated hosts were untouched.

## Checks

| Check | Result |
| --- | --- |
| Final `dotnet test AgentCore.sln --no-restore --nologo` | Passed: Domain 150, Application 1302, Infrastructure 814, API 360, OrderEvents plugin 4; 2630 passed, 13 opt-in skips, zero failed |
| `pnpm --dir web run test --run --maxWorkers=1` | Passed: 94 files, 712 tests, zero failed, 610.60 seconds; unchanged test timeouts |
| `pnpm --dir web run build` | Passed; existing large bundle warning (about 2 MB minified) |
| Affected Skills/Harness/messaging CLI journeys | Earlier affected run: 10 passed; subsequent instance upgrade/rollback journey: 3 passed |
| Full Synthetic/browser-stt/browser-browser projects | Passed: 110 tests, 10.3 minutes; isolated API/Vite ports 5104–5106 / 5194–5196 and fresh SQLite |
| Isolated browser acceptance projects | Passed: faithful-manual 1 (1.3m), admin-lifecycle 1 (26.7s), p76-admin 1 (28.7s), p97-harness 6 (35.3s), p9899-continuity 2 (41.3s), p910-continuity-maintenance 1 (32.8s), secretary-demo 4 (49.2s); 16 total, separate invocations/databases/server lifecycles |
| Isolated `scripts/compose-sqlite-volume.sh` | Passed, including exact Skill survival assertions |
| EF `migrations has-pending-model-changes` | Passed: no pending model changes |
| Markdown links/fences/complete JSON examples and `git diff HEAD --check` | Passed: 15 changed Markdown files, 464 local links/anchors, 15 complete JSON blocks; no diff whitespace errors |
| Hosted `.github/workflows/synthetic.yml` | Pending publication of the final candidate and its exact-SHA run |

The earlier frontend command started before the final malformed-input handling was edited; its cached module disagreed with a newly added test. Three existing large Definition-editor tests also exceeded 30 seconds while broad jobs overlapped. The focused editor rerun passed all 21 tests; the final full rerun passed all 712. Both retain the existing timeouts and assertions. An existing API reattach test observed the completion event before mailbox cleanup; its assertion now waits on the internal mailbox barrier. All four focused reattach tests pass. These failed runs are not counted as green gates.

## UI finish evidence

[Desktop](assets/instance-skills-desktop.png), [mobile collection](assets/instance-skills-mobile.png), [mobile editor](assets/instance-skills-editor-mobile.png), [archived mobile inspection](assets/instance-skills-archived-mobile.png).

The fresh finish reviewer required revealed-panel focus/scroll/return focus and usable archived inspection. Both findings were scored resolved; final disposition was `ship` for those scored fixes. Successful Save restores the opener after busy/loading clear and has a regression assertion. The documenter checked the inherited Ant Design v6 components, panel CSS, compact tables, form width and token spacing, preserving the incumbent visual system. Pre-existing tooling-context drift (Definition-only wording in PRODUCT.md and an older tab inventory in DESIGN.md) was reported, without an unrelated design-system rewrite. `/docs` remains authoritative.

## Cleanup and remaining gate

Current source/test/fixture/UI cleanup searches are clean for `ActivationKeywords`, `DeterministicSkillSelector`, `MaxActiveSkills`, removed Harness Skill tools/scope, old active-id fields and Definition-only prompt builders. Historical EF migrations and explicitly superseded reports retain their original names/evidence. Remaining `SkillList` reads belong to Definition authoring/validation, initial state/version reconciliation, read-only review/copy, or the effective resolver. The executor's no-store guard permits only truly empty Definition catalogs in standalone fixtures and rejects nonempty Skills without initialized owner storage; it does not load procedures from the Definition as a fallback.

Verification uses fresh disposable SQLite/project data. Old demo publications/executions are intentionally unsupported by this cutover and must be reset before that old data is reused. No live-provider calls or user database resets were used for acceptance evidence. The skipped tests are Application 1, Infrastructure 9 and API 3 opt-in checks; the default key-free suites have no failures.

Final closure requires all local gates green, then the final candidate committed/published and all required hosted Synthetic jobs green on that exact behavior SHA. Local evidence alone does not mark the proposal complete.
