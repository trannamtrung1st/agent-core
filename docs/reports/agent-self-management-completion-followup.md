# Agent self-management focused completion review

Date: 2026-10-11. Baseline: `f966cc90b48f6a866f4d0e958bade9269ad2840e` on `main`. Verified behavior commit: `f8dc1ae274628fc5775b7845e3f49a23f63bf3f2`; checks ran on its corresponding working tree before commit. This focused follow-up addresses the six supplied review findings and supersedes the affected current-status claims in [initial verification](agent-self-management-verification.md) and [the first review](agent-self-management-review-followup.md). **Full proposal acceptance remains open.** The owner explicitly skipped live-model UAT; no real provider request was sent.

## Corrections and observable behavior

1. A committed exact diagnosed remedy retains a bounded pending-task receipt in the existing tool checkpoint. After the source Run completes, its Session mailbox can admit one fresh `ConfigurationContinuation` Run, with fresh configuration/model/catalog/budget pins, a distinct response and a source-Run dedupe key. The original input remains owned by the source Activation. Ordinary reads and the diagnosed task tool are eligible; any task write requires a new exact approval. Prior ordinary writes, uncertain effects, pending calls, cancellation, newer input, stale configuration and Off/Frozen policy prevent handoff. No second loop, scheduler, store or database migration was introduced. Both admission stores validate the owned source receipt atomically. A positional-argument error in recovered approval grants was also corrected using named response/operation/epoch arguments.
2. Goal/target inspection narrows returned views and distinguishes Definition denial, Instance capability selection, unavailable provider/host setup and missing sign-in binding. It offers only supported exact settings recipes. Remedy hashes bind the owner, Instance/policy revisions, target, goal and actual values; preview and commit recompute the recipe. Changing either goal or values fails. Host-only and unsupported goals return owner guidance; secrets/OAuth/MFA remain owner interactions.
3. `automation.preferences.inspect/patch` belongs to AutomationPreferences and reads/changes future unattended-model defaults through the existing Instance/model-catalog owner. Exact approval, revision guards, freeze and safe field-name audit apply. Ordinary reminder/task authority is independent. Immutable General Assistant v24 adds the two tools; prior seeds remain unchanged.
4. Managed SkillChanges allows create/update/enable/reset within the existing constraints. Delete and customize require exact approval even when that delegation is enabled.
5. Admin Freeze/Unfreeze submits the last persisted policy with only its lock changed. Unsaved form values remain local and available for a later Save.
6. Skill/resource creation previews show the actual target, source, absent previous content, resulting content and effect, alongside revisions and timing. Delete/customize/copy previews explain their effects. Automation preferences and binding removal use the same Instance configuration approval presentation. Exact action hashes remain enforced.

## Executed runtime evidence

The SQLite integration journey narrows `workspace.list` while retaining Definition authority, submits a user task, reviews `configuration.propose`, approves, and observes exactly two completed Runs: the original and one continuation. The second has fresh configuration, a new response ID, the original source Run identity and no re-owned user entries. It produces a grounded workspace result without another user turn. Reopening retains one user entry and does not admit a duplicate. Rejection, cancellation and Freeze before approval produce no continuation. A separate journey disposes the host while waiting for approval, reopens the same SQLite data, approves/dispatches through the existing Run control path, and completes once.

Captured requests for unrelated Hello turns on Off, Frozen and enabled owners omit management schemas/procedures. Pending-task tests reject observation-required checkpoints, pending calls, prior ordinary writes, rejected/foreign/stale receipts and management targets. The both-store admission test rejects a substituted target without changing the Session revision and deduplicates the valid continuation after reopening. Blocker integration tests verify an unavailable email provider is reported before a selection remedy and a missing browser credential binding points to the actual Credentials page. Existing settings, Skill, resource, credential, browser and durable approval suites remain regression coverage.

Synthetic Playwright verifies visible Chat approval followed by automatic workspace completion, two inspected Runs and one user entry after reload. The existing journey rejects memory disable using Escape and verifies persisted `true`, approves a fresh request and verifies persisted `false`, reads concise response settings in a fresh Run, freezes, exercises ordinary Hello, observes management suppression and explicitly unfreezes. Escape waits for the modal's opening animation to settle.

Playwright MCP separately exercised a disposable Synthetic host: persisted Assisted/Settings, edited an unsaved Managed/Skills draft, froze/unfroze, then read back unchanged persisted Assisted/Settings while the draft remained saveable. Owner/policy revisions advanced only for the exact lock operations. No related console errors or failed API requests were observed. At 1440×900, 768×900 and 390×844 the panel had no page overflow. No user database was reset or real external action executed.

## Checks

| Check | Result |
| --- | --- |
| Full `dotnet test AgentCore.sln --no-restore --nologo` | Domain 195, Application 1,677, API 499, OrderEvents 4 passed; Infrastructure 1,171 passed and one stale seed-count assertion failed; 20 opt-in skips |
| Corrected `AgentDefinitionStoreTests` | 3 passed; v24 has 90 tools including both automation preference tools |
| New both-store continuation admission test | 2 passed |
| Full Vitest on explicit Node 22, 2 workers, 60-second test timeout | 113 files / 917 tests passed |
| Final SelfManagementSection + ApprovalModal component tests | 11 passed |
| `pnpm --dir web run build` and TypeScript check | Passed; existing annotation and large-chunk warnings remain |
| Synthetic Playwright self-management, credentials and Instance Skills | 8 passed; final self-management-only rerun 2 passed |
| `git diff --check` | Passed |

The full .NET invocation's only remaining failure was its old expected tool count; its corrected file passed in the focused rerun. Together with the added store cases, 3,549 distinct default .NET tests passed across the full run and final focused checks; 20 real-provider/opt-in cases stayed skipped. Earlier seed-version expectations, customization expectations and minimal-host DI assumptions were corrected before that final full invocation.

The first two broad frontend attempts used Node 26 from the `web/` shell and failed on unavailable browser-storage globals plus rendering timeouts. The final invocation uses the absolute Node 22 executable, matching CI's major version, and the previously documented 60-second rendering allowance; assertions were retained. The initial browser rerun exposed the opening-animation keyboard race; its settled-dialog rerun passed.

Reproduction uses the existing suites and disposable SQLite/ports. From `web/`, run a verified Node 22 executable against `node_modules/vitest/vitest.mjs run --maxWorkers=2 --testTimeout=60000`. The isolated browser config imports the standard Playwright config and exports `defineConfig({...standard, webServer: standard.webServer.slice(0,2), reporter:'list'})`; it was removed after verification. Browser invocation:

```sh
CI=1 PLAYWRIGHT_FAITHFUL_MANUAL=1 PLAYWRIGHT_API_PORT=5098 PLAYWRIGHT_WEB_PORT=5188 PLAYWRIGHT_FIXTURE_PORT=5102 PLAYWRIGHT_SQLITE_PATH=/private/tmp/agent-self-management-completion-e2e-final2/synthetic.db pnpm exec playwright test --config playwright.self-management.local.config.ts e2e/self-management.spec.ts
```

## Acceptance limits

The missing automatic A15 behavior is implemented and exercised locally. A03/A04 captured unrelated requests, A12 blocker classification and A13 restart/cancellation now have additional concrete evidence. This does not establish the entire exhaustive resumed-request, approval-expiry, injection or live conversational blocker matrix in A01–A20. Required real tool-capable UAT remains pending by owner decision. No hosted CI outcome, production migration, Compose delivery or external action/model quality is inferred from these checks. P10/P11 and historical milestone freezes remain unchanged.
