# Scoped Instance configuration verification

Date: 2026-10-10. Baseline: `38a610071a7a472506d98ab466ee45503f7a9466` on `main`.

Status: implemented candidate; final acceptance is pending the full regression and exact-commit hosted Synthetic gates. Historical milestone reports are unchanged and do not certify this slice. This report will record the verified behavior commit and hosted results after those gates complete.

## Implemented contract

Each new Activation resolves the exact Instance-selected Definition, sparse typed settings, current persona, Skills and scoped resource manifest from one owner revision. Admission checks that generation transactionally and freezes the effective values and immutable content hashes in `AgentRunAdmission.Configuration`, alongside existing model, Skill and execution-budget/cleanup pins. An old Session can start a new Run on an adopted version; retry, approval, wait and restart keep an already admitted Run's original configuration. Current lifecycle, capability, source and credential denial checks remain live.

Settings support explicit presence, null/false/zero, per-field clear, section reset and no-op detection. Specialized persona, unattended model, Harness and per-class execution budgets retain their existing owners. Instance capability/policy patches can narrow published authority. Local resources use `/agent/instance/resources/<path>` and inherited publications retain `/agent/resources/<path>`; origin-qualified identities prevent shadowing. Content-addressed bytes are retained conservatively, including after edits/deletes, for admitted Runs and publication/draft references.

Chat self-management commits only its own Instance with existing source, policy, exact approval and revision checks and separate durable Instance-change evidence. Explicit shared promotion selects local fields/resources, creates a reviewed draft, validates/evaluates and publishes through the existing gate. Only explicit adoption changes an Instance's selected version. Common durable forks now retain stable resource IDs.

Adoption validates preserved patches, Skills and dependencies, then atomically commits owner revision, inherited Skill initialization, Automation reconciliation and audit. Admin adds Settings, scoped Resources, inherited enablement/reset, explicit adoption preview and Run configuration provenance using the existing Ant Design v6 system.

## Durable upgrade and retention

- `20261010065035_ScopedInstanceConfiguration`: sparse settings, nullable inherited Skill enablement, Instance resource metadata/state, exact nonterminal Run reconstruction or safe failure with `configuration-migration-unavailable`. Historical booleans migrate conservatively to explicit same-value choices. Existing execution-budget storage and receipts remain unchanged.
- `20261010072141_DefinitionResourceIdentityScope`: composite `(DraftId, ResourceId)` identity allows a retained resource ID across true durable forks.
- `20261010082055_SessionReasoningPreference`: persists explicit reasoning-effort presence in `SessionSnapshots`. Historical concrete efforts are preserved conservatively because their prior representation did not distinguish user intent from a default. New default-derived model choices refresh while retaining explicit effort.

These migrations preserve owned data. Rollback requires a pre-cutover backup; they do not silently discard local configuration, retained identities or preference intent. Resource blobs are not reclaimed by these mutations; reference-aware GC remains a future operational implementation.

## Runtime read audit

| Read path | Current authority |
| --- | --- |
| Session creation/public history/header | Creation provenance; not a future-turn configuration selector. |
| Accepted user batch, fresh initiative/signal, background start/completion and occurrence admission | Current coherent Instance configuration, checked again at durable admission. |
| Prompt, policy, knowledge, Skill/capability projection and speech during execution | Admitted Run's frozen effective Definition/persona/model/catalog/resource hashes. |
| Retry/reclaim/wait/approval continuation | Same persisted Run pins and checkpoint counters; current denial gates recheck permission. |
| Explicit Session model/effort and speech locale | Session preference priority retained; default-derived model refreshed on new admission. |
| Human knowledge/resource workspace reads | Current scoped configuration; normal owned workspace/history lifecycle remains separate. |
| Scratch workspace initialization/materialization | Creation workspace provenance for one-time initialization; not permission or resource selection for a Run. |

Production DI injects the shared resolver. Optional revision-zero fixtures in isolated Application tests do not provide a production fallback for nonterminal historical Runs; missing persisted configuration fails closed.

## Verification ledger

Local checks use Synthetic and disposable SQLite files/roots under `/private/tmp`; no provider calls or user database reset occurred.

| Check | Observed result |
| --- | --- |
| Full Domain | 185 passed on the committed candidate. |
| Full Application | 1589 passed, 6 live opt-in skipped after the preference correction; focused model refresh: 7 passed. |
| Full Infrastructure | 1080 passed, 9 live opt-in skipped before the final preference column; populated scoped migration passed. The subsequent full run exposed four deliberately old-schema tests using the current EF shape. Historical projections/seeding now use only available columns; the five-case populated/rejection migration group passes. Final full gate pending. |
| Full API initial candidate | 422 passed, 2 live opt-in skipped, 5 failures: two promotion assertions omitted retained baseline resources; three recovery/timing cases. Corrected promotion checks passed; isolated recovery/approval rerun: 31 passed. Final full gate pending. |
| Order Events | 4 passed. |
| Scoped API journeys | 14 passed across InMemory/SQLite, including generation race, resource rejection, dependent Skill disable refusal, reviewed promotion and atomic Automation policy cases, and fresh occurrence admission into both an existing conversation and a new background Session. |
| Frontend | Build passed with the existing large-chunk warning. Full unit run had 826 passed and 11 failures; follow-up isolated files had timeout failures on the busy local machine. Final hosted/full gate pending; no failed check is marked accepted. |
| Synthetic Playwright | Initial broad run: 134 passed, 10 failures. Corrected rerun: 25 passed, 2 failures, subsequently corrected (retained settings disclosure and persisted reasoning preference). Final focused scoped journey: 1 passed; Session model/effort journeys: 2 passed. Final hosted/full gate pending. |
| Compose | Isolated `scoped-verification` image built and host started. A 2048 sparse token override and owner revision survived container restart on its persistent volume. Final exact-commit packaging gate pending. |
| Markdown sync | 15 changed Markdown files, 515 local links and 13 complete JSON examples checked; no link/fence/JSON errors. |

Commands: `dotnet test AgentCore.sln --no-restore --nologo --disable-build-servers -m:1 -p:UseSharedCompilation=false`; focused project filters for scoped configuration, historical migrations, model binding, Harness approval and recovery; `pnpm --dir web exec vitest run --maxWorkers=1 --minWorkers=1`; `pnpm --dir web build`; isolated `CI=1` Playwright core/Admin/Harness projects; isolated Docker Compose build/start/restart. Detailed local command logs are `/private/tmp/scoped-*.log`; these disposable logs are not portable hosted acceptance artifacts.

## Exercised user journeys

Playwright MCP against the disposable native Synthetic host created an Instance, opened Settings, entered Customize without a write, saved only `maxOutputTokens=2048`, uploaded an Instance Knowledge file and previewed its exact text. A runtime read initially failed because the Admin composer revalidated the resource-enriched Definition as an authored Definition; the corrected projection was rebuilt and the journey repeated successfully. Console/request checks distinguished that observed setup failure from the corrected flow.

Repeatable browser coverage creates an owner, saves only the changed field, causes a concurrent settings edit, verifies conflict and retained language draft, reloads/rebases, clears one field, resets the section, creates a resource, rejects a failed resource read as actionable data, retries, previews exact content, closes with focus return, checks mobile containment and deletes the resource. Existing Admin/Skills/budget/adoption/publication and Harness approval suites remain regression gates.

Backend journeys verify old Session/new Run adoption with history and explicit model retained; original resource hash/bytes survive local edits and SQLite reopen; waiting Run preserves its budget/profile/source, steps, active time and cleanup intent while a new resolver sees the adopted version/new bytes; stale configuration admission commits no Run; selected promotion excludes unselected local knowledge/settings and leaves another Instance unchanged; atomic Automation policy CAS refuses stale registration plans without changing the owner.

## Bounded visual and design evidence

Root captured Settings, Resources, Skills and resource inspection at 1440/768/390 pixels from the running Synthetic app. Files: `local/verification/scoped-resources/{settings,resources,skills,drawer}-{1440,768,390}.png` (local ignored artifacts). Captures were taken after layout settlement; mobile page width remained contained. Settings captures include the existing Execution budgets form.

A fresh files-only Impeccable reviewer scored one correction batch: stable resource keys and written enablement, Segmented inheritance and named reset, shared 48rem form bound, shared responsive detail labels/16px drawer insets/content-width Preview, and source-matched surface/design documentation. All five were resolved; no material regressions observed in confirmation captures. Ship disposition covers those visual corrections, not the functional gate.

A fresh documenter compared `.agents/context/PRODUCT.md`, `DESIGN.md`, `.impeccable/design.json/config.json`, the surface brief, changed components, shared layout helpers, CSS and AntD theme. No further changes were needed; incumbent tokens are preserved. Pre-existing incidental sidecar/prose drift was reported and not canonized.

Canonical owners updated: docs 03, 04, 05, 09, 10, 12, 13, 14, 15, 16 and 18; README summary and Admin surface/design patterns follow those owners. Historical reports and milestone freezes remain unchanged. P10/P11 remain unopened.
