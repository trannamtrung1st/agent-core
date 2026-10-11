# Skill invocation content verification

Local working-tree enhancement based on `main` at `bc3452de`, 2026-10-11. Existing composer polish and discovery edits were retained. Hosted CI was not awaited, as requested. This is a local enhancement, not a new milestone freeze.

## Implemented behavior

- Removing a typed slash/at-sign trigger dismisses suggestions even when Lexical leaves an empty paragraph. An explicitly opened plus picker survives clearing its search.
- One or multiple valid Skill tokens alone constitute sendable content in the frontend and Domain. Admission and Core activation retain existing authorization, canonical-key deduplication, procedure budgets and Run pins.
- One Application semantic projection preserves current invocation identity/resolved names and ordered text/references, marks old invocation facts as historical without activation, and derives readable titles and compaction input. Canonical durable text/parts and wire fingerprints are unchanged.
- Initial titles include slash context; manual renames and the 200-character limit remain. Compaction retains relevant Skill/resource meaning without opaque identity clutter or copied procedures. Privacy screening covers projected labels and canonical structured bodies; delivered assistant-text filtering remains unchanged.
- Owning architecture, implementation, frontend, protocol, persistence, testing and design documents are synchronized. No new UI kit, schema, message store or activation pipeline.

## Functional evidence

Playwright MCP on an isolated Synthetic API (`5180`) and Vite (`5273`) created an Instance and Say/Research Skills, selected the Instance through Chat, typed `/`, deleted it and observed hidden suggestions. Selecting Say alone enabled Send; sending produced `Hello from synthetic.`, a pinned `instance:say` Run, a `/say` catalog title and a readable `/Say` history chip after reload. A final fresh MCP check also used the Unicode label Nói and observed `/Nói` after reload with `/say` as the canonical-derived title. Browser console: zero errors. The user's existing local app was left running.

The runtime regressions accept one/two Skill-only inputs, observe procedures and explicit invocation context in the first model request, persist a completed user/assistant turn and canonical parts, reopen history, and send an ordinary turn. That later request retains historical Skill meaning without either procedure; the original Run's keys remain pinned. Disabled/retired input is rejected without an entry/model call. Recovered retired input remains recoverable and makes no model call.

Projection tests exercise the requested titles, canonical versus display identity, reference labels, ordering, title length, attachment context and historical attachment manifests. Compaction tests observe readable prior Say/navigation context, unchanged rows, secret-label/canonical filtering, and the existing projected-history budget. The Synthetic summary fixture proves projection/persistence mechanics; it does not assess live model summary quality.

## Local checks

- `dotnet test tests/AgentCore.Application.Tests/AgentCore.Application.Tests.csproj --filter 'FullyQualifiedName~MessagingSkillJourneyTests|FullyQualifiedName~UserMessageProjectionTests|FullyQualifiedName~ConversationCompactionTests|FullyQualifiedName~PromptContextBuilderTests' --nologo`: **32 passed**, final focused implementation.
- `dotnet test tests/AgentCore.Api.Tests/AgentCore.Api.Tests.csproj --filter 'FullyQualifiedName~SignalRMessagePackTests|FullyQualifiedName~ComposerReferenceTests' --nologo`: **39 passed**. Real JavaScript MessagePack now accepts Skill-only input, observes completed response and labeled typed history, and preserves normalized retry/conflict and locator validation.
- `pnpm run build`: **passed**. Existing Rollup annotation/chunk-size notices remain.
- `git diff --check` and changed documentation local file-target checks: **passed**.

- `pnpm exec playwright test e2e/structured-composer.spec.ts e2e/session-model.spec.ts --config /private/tmp/agent-core-composer-polish/playwright.config.ts`: **17 passed** on a fresh isolated Synthetic host. Covers one/two Skill-only first turns, meaningful catalog titles, manual rename preservation, later empty explicit sets, readable reload, Skill+text+two references+attachment, queue steering, literal paste, structured clipboard/deduplication, undo/IME, dismissal, partial-load retry and 1440/768/390 layouts. Temporary runner used no default-port server reuse.

- Additional browser regression `--grep 'disabled after selection'`: **1 passed**. Disabling a selected Skill rejects the send, retains the draft/chip and creates no user/assistant row; enabling it again sends that same Skill-only draft successfully.

- `dotnet test AgentCore.sln --nologo -m:1`: **passed**, 3,494 tests and 18 opt-in live skips: Domain 195; Application 1,666 + 7 skips; Infrastructure 1,172 + 9 skips; API 457 + 2 skips; OrderEvents 4. Log: `/private/tmp/agent-core-skill-content-backend.log`. Final semantic-projection/privacy/budget follow-ups were separately checked by the 32-test focused command above after their last small edits.
- `pnpm run test --run --maxWorkers=1`: **836 passed, 70 failed across six files** in the local pnpm runtime (Node **26.7.0**, while bare `node` is 22.18.0). Failures came from unavailable built-in Web Storage shadowing jsdom `window.localStorage`; the composer changes passed. No production/storage code was changed to accommodate this environment.
- Practical environment fallback: `env NODE_OPTIONS=--no-experimental-webstorage pnpm run test --run <files> --maxWorkers=1` passed all 70 failed cases: `api.catalog.test.ts`, `artifacts.test.ts`, `sessionModel.test.ts`, `schedules.api.test.ts` (**14**); `attachments.test.ts` (**1**); `realtime.race.test.ts` (**55**). Those three scoped runs cover every initial failure. The full 111-file suite was not redundantly rerun after the environment correction; 906 distinct frontend cases are covered by the original passing cases plus successful reruns. Local full-suite execution needs that flag under pnpm's Node 26 runtime; hosted CI uses its configured Node 22 and was not observed this turn.

Disposable native servers were stopped. Existing jsdom/Ant Design deprecation, act and CSS warnings were observed in broad unit runs; browser console checks for the affected journey remained error-free. No live provider/paid inference, physical IME, assistive-technology speech or hardware audio assessment was attempted. The targeted Synthetic journeys and existing regressions establish the affected behavior; hosted CI, Compose and unrelated milestone acceptance projects are outside this turn.

## Review closure and optional live UAT (2026-10-11)

The reviewed Skill-content enhancement is closed/frozen on behavior `21000d2111012954b36039fd5e5fe6cafa004cee`. GitHub CLI independently verified [run 38107075764](https://github.com/trannamtrung1st/agent-core/actions/runs/38107075764): all seven backend/frontend/core-browser/acceptance/Compose jobs succeeded on that exact SHA. The local observations above remain historical; this hosted closure does not cover subsequent composer auto-pagination or UAT fixture changes.

Existing Session titles remain stored as-is. Corrected generation applies to the initial title of a new Session; manual renames and historical titles are not rewritten. No rename migration, new activation path or architectural change is needed.

A repeatable opt-in `MessagingSkillJourneyTests.Hosted_skill_only_turn_and_compacted_history_retain_meaning_without_reactivation` fixture is prepared. It submits a canonical Say-only turn through SessionRuntime, sends an ordinary turn, inspects independent invocation/history context and Run pins, then seeds ordinary disposable history to reach the existing 40-entry compaction threshold. The actual compactor calls the selected hosted model; a reopened runtime must recall Say, AI engineering and .NET 10 from the resulting summary with no active Skill. Stored canonical rows and the title must remain unchanged. This is bounded runtime/compactor UAT, not a claim that 20 live conversational turns or manual browser UAT were performed.

The gate requires `AGENTCORE_SKILL_CONTENT_LIVE=1`, `OPENROUTER_API_KEY` and an explicitly configured `AGENTCORE_LLM_MODEL`. It permits at most four provider generations (1200 output tokens each, 800 for compaction), with a four-minute overall deadline and per-generation timeouts. Default suites skip it and make no hosted requests. Credentials are never written to repository files or verification logs.

Current follow-up checks: focused Application projection/Skill/context/compaction suite **33 passed, 1 live opt-in skipped**; existing MessagePack/composer-reference API integration suite **39 passed**. Live execution has not run: automatic approval review rejected sending the fixture and application prompts to OpenRouter with the stored credential without explicit payload/destination authorization. Approval has been requested. Hosted summary quality remains unverified until that run succeeds; it is optional confidence evidence, not a blocking defect or a reopened enhancement gate.


### Follow-up consistency review

The offline UAT replay reproduced an `unsafe` compaction rejection: the new fixture had not acknowledged its displayed assistant responses. The fixture now submits delivery receipts through SessionRuntime before seeding history. The same runtime/compactor/reopen path passes offline, verifies both ordinary-turn Run pins remain empty, captures summary model provenance and enforces exactly four generations. This replay validates the fixture mechanics only; it is not hosted-model quality evidence. Production compaction privacy/delivery screening was preserved.

Composer auto-pagination uses the existing shared Skill/resource list and 8px row/popup spacing. Mouse and keyboard bottom scrolling, successful append/selection, focused plus search, failed-page Retry, empty-page continuation and 1440/768/390px layouts pass all **25** `structured-composer.spec.ts` cases. Fresh Playwright MCP on isolated Synthetic ports 5086/5186 confirmed 20→21 choices with exactly two requests, editor focus retained, no Load more button and successful chip insertion; browser console errors and failed requests: **zero**.

Final affected checks: **33 Application tests passed / 1 optional live skip**, **25 focused frontend unit tests passed**, production build and `git diff --check` passed. The previous full Node 22 frontend run also passed **907 tests in 111 files** on the same production composer change. Existing API integration evidence remains **39 passed**. Review changes affect the UAT test and documentation; no further production behavior correction was found. These follow-ups require their own hosted CI and do not inherit the old exact-SHA closure.
