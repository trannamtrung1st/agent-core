# Unified composer verification

Implemented on `develop/branch-1` after the main sync at `db2afbbd`. Implementation revision: `5d0412b0` (`Implement unified composer with explicit Skills and scoped references`); this report update changes documentation only. This report does not infer hosted acceptance from earlier milestones.

The production composer uses Lexical 0.52.0 as headless input infrastructure and direct Ant Design v6 presentation. `/` selects effective Skill keys for one user-turn Run; `@` selects owned read-only evidence; Add content opens the same controllers and attachment picker. All six resource categories have native bounded resolvers. The existing Session mailbox, Run admission, SignalR transport, capability authority and background lifecycle remain owners of their behavior.

## Acceptance evidence

| Criteria | Exercised outcome |
| --- | --- |
| AC-01 | Synthetic text conversation, stream, refresh, Stop, pending voice and disconnect regressions pass. |
| AC-02–04 | Chromium keyboard selection inserts two same-name Skills with distinct keys, plus two references. Duplicate canonical keys normalize once. The first Synthetic model request contains both explicit procedures; next plain turn does not retain them; zero model `skills.load` calls. |
| AC-05–06 | Fresh catalog validation rejects missing selections and Always+explicit aggregate overflow on both stores. Accepted retired input recovered after ACK emits recoverable ComposerInputUnavailable, preserves user intent and makes no model call. Existing model-load behavior remains covered by Skill activation suites. |
| AC-07–09 | Native integration resolves home files, regular Sessions, original Background Work task/result and Session Artifacts. 51 artifact choices page without duplicates; wrong Session/artifact pairs and foreign Instance/profile references are denied. Home mutation returns stale; large bodies are metadata-only. Reference reads leave source revision, origin, surfaces and Run count unchanged. |
| AC-10,19 | Malicious source text is projected as User evidence with a System lower-trust boundary. Explicit procedures project with ModelSupportsTools=false while request.Tools stays empty. Selection never changes Definition grants. |
| AC-11–12 | Typed queue snapshots survive mutation of original arrays and rejected steering. Lost ACK reconnect replays the same event ID and parts while preserving a newer typed draft. Real MessagePack accepts normalized adjacent-text retries, rejects changed-part retries, unknown fields, fallback mismatch and Skill-only turns. |
| AC-13 | Browser reload restores four chips from typed history. InMemory/SQLite entry reopen and immutable Run retry/reopen preserve canonical parts, source order, active keys and reference evidence. Legacy null-parts history remains readable. |
| AC-14–15 | Existing Synthetic refresh, detached continuation, Stop, manual pause/Resume, queued attachment and fake-device AudioWorklet capture/reconnect pass. New-chat identity selection clears typed draft scope. Attachment-only transport omits empty editor parts. Frontend image/model capability regression suites pass. |
| AC-16 | Chromium tests exercise token undo/redo, select-all deletion, literal external paste, internal multiline structured copy/paste, foreign-Instance paste downgrade, Vietnamese composition Enter guard and Shift+Enter. Accessible listbox/result announcements and focus return are present; physical IME and assistive-technology speech are separate boundaries below. |
| AC-17 | Plus menu selects the same canonical reference types. Completed Background Work Add to chat invokes insertion only, with no Continue or conversation navigation call. |
| AC-18 | Functional picker assertions and actual MCP captures at 1440/768/390 verify bounded placement, caret anchoring, reachable Send/Model/Voice controls and 44px narrow picker targets. Fresh finish review resolved resize anchoring and icon/target issues. |
| AC-20 | Two accepted inputs retain source order and unioned explicit Skills in one immutable Run admission across InMemory/SQLite claim, retry and reopen. |

## Executed checks

Node 22.18.0 was used for pnpm and .NET wire tests that spawn Node. Synthetic tests used temporary SQLite/attachment/workspace/artifact roots and separate native ports; no user catalog was reset.

- `dotnet test AgentCore.sln --no-restore`: Domain 191, Application 1651, Infrastructure 1154 and OrderEvents 4 passed; paid/live/container checks skipped by their opt-in gates. Initial API run had an old payload-reflection assertion and a loaded-run approval timeout; payload expectation was updated, isolated regressions passed and the full API rerun passed 456 with 2 live skips.
- Final full Domain: 194 passed. Final full Application: 1653 passed, 7 opt-in skips. Full Infrastructure: 1154 passed, 15 opt-in skips; final focused persistence/admission/native workspace checks 46 passed.
- Final focused API/reference/MessagePack/background checks: 43 passed after original objective projection and normalized retry assertions; prior full API rerun 456 passed.
- `pnpm run test --run --maxWorkers=2`: 110 files / 898 tests passed. Final affected frontend suite 109 passed plus one newly added replay test initially used the wrong retry trigger; corrected reconnect test and drawer cases 60 passed, final queue 28 passed. Added tests are verified by those focused reruns.
- `pnpm run build`: passed; Vite reports its existing large-chunk advisory (about 2.4 MB main bundle, 741 KB gzip with Lexical).
- Isolated standard Playwright configuration: 21 of 23 passed, including all 6 new composer cases, ordinary text, attachments and both fake-device capture cases. The two Cmd/Control steering checks retained textarea/DOM-text assumptions; adapted headless-editor assertions and final focused rerun: 2 of 2 passed.
- Playwright MCP: created an active Instance and Skill, selected an explicit Skill, sent task+chip, inspected reference-only response, reloaded typed history, exercised paused Resume, picker category/keyboard navigation and responsive captures. Expected canonical history and Synthetic replies were observed.
- Impeccable detect: no findings. Required fresh reviewer and documenter completed the bounded extension finish workflow. DESIGN.md, design.json and the chat surface brief match the finished editor and incumbent AntD tokens.
- `git diff --check`: passed.

Final composer-only Chromium rerun: **7 of 7 passed**, adding real typed queued steering with an unchanged newer draft and literal Escape continuation. The final empty-parts attachment-only regression also passed.

Local disposable captures are `.impeccable/review/desktop.png`, `user-768.png`, `mobile.png`; repeatable E2E captures live under the ignored Playwright test-results directory. No generated screenshots or temporary port configuration are committed.

## Verification boundaries

Hosted CI, Docker Compose deployment/restart, paid-provider behavior, physical microphone/headset quality, native Vietnamese input-method operation, browser UI zoom and spoken screen-reader output were not run for this initiative. Deterministic Synthetic, real MessagePack, actual Chromium interactions, programmatic composition guards, accessible roles and temporary SQLite reopen establish the local feature behavior. Existing unrelated milestone freezes and hosted claims remain historical; P10/P11 are unopened.

## Consistency review follow-up (2026-10-11)

Review of `develop/branch-1` after `2c3af5f8` found and corrected three behavior gaps in `c9cb061a`:

- Internal paste deduplicated its payload but not existing editor chips, leaving two visible Skill chips for one canonical activation. The Chromium regression reproduced two chips before the fix. Paste now deduplicates against retained chips after removing the replacement selection; selecting and replacing the same Skill remains supported.
- Reconnect reconciliation compared only fallback text and could erase a newer draft with the same reference locator but a different selected revision. It now also compares label-free structured parts before clearing the restored draft. A lost-ACK regression verifies the newer revision survives without retransmission.
- Client validation accepted empty UUIDs and empty UUID Skill keys. Core accepted a selected revision on a Skill reference despite the protocol's immutable Skill locator rule. Client/Core validation now follows the existing protocol; unit tests and the real MessagePack scenario reject these invalid inputs.

Executed locally with Node 22 and disposable Synthetic SQLite roots:

- Full Domain suite: 195 passed.
- Focused frontend parts/queue regressions: 33 passed. Broader frontend services, Composer and ChatApp suites: 20 files / 197 tests passed.
- API ComposerReferenceTests and SignalRMessagePackTests: 38 passed. Final wire rerun after adding the Skill revision rejection: 37 passed.
- Full composer Chromium file: 8 passed. Final duplicate/replacement paste regression rerun: 1 passed, including one visible history chip and one admitted Skill activation.
- Playwright MCP: selected Review, pasted the existing chip, confirmed one visible chip, replaced the entire draft with that same Skill plus new text, sent it and observed a completed Synthetic reply and exactly one explicit Skill key in Run inspection. Reload retained the chip and reply. Product requests succeeded; an exploratory request used an incorrect `/runs` path and returned 404 before being corrected to the documented `/agent-runs` route. Reloaded console had no errors.
- Frontend build passed, retaining Vite's existing large-chunk advisory. `git diff --check` passed.

This follow-up verifies the affected local behavior; the hosted/manual verification boundaries above still apply.

The follow-up fetched and merged the new `origin/main` commit `6ef26d82` as `d10310c9`. The sole conflict was in `docs/16-testing-strategy.md`; both branches' acceptance guidance was retained, including main's diagnostic budget and SQLite writer checks. Infrastructure dependency registration retains both the composer service and the configured SQLite busy timeout. The merged frontend build and `DefinitionDraftEvaluationStoreTests` passed (10 tests, including real SQLite lock contention).

Combined-branch Chromium verification (`pnpm exec playwright test --project=synthetic structured-composer.spec.ts diagnostic-details.spec.ts`): **18 passed**, including the eight-step terminal-finalization check, diagnostic copy/reload, both clipboard cases and typed queue steering. No required local follow-up check remains failing. Broader hosted/paid/manual gates and the unrelated secretary journey were not rerun in this bounded review.

## PR #8 hosted verification repair

The first PR run on `af83c5c1` passed acceptance and Compose, but Infrastructure stopped with one timeout in `BrowserPrivacyTests.Closed_shadow_child_frames_fail_closed_without_pixels` (Protected): 1161 passed, 15 opt-in skips. The fixture's iframe `onload` had completed, but Playwright's child-frame lifecycle notification wait timed out. The fixture now waits for the child document's complete ready state and body directly; the Protected/Unmasked assertions still require `target_denied` and no image bytes. Local `dotnet test tests/AgentCore.Infrastructure.Tests --no-restore --filter FullyQualifiedName~BrowserPrivacyTests` passed all 13 tests. The updated PR head requires a fresh complete hosted run before merge; the original failed run remains historical evidence.

The superseded browser logs also exposed four textarea-value assertions, a textarea-disabled assertion and a submission expectation invalidated by client-side input limits. Workspace/capability tests now assert contenteditable text and read-only semantics while retaining their execution assertions. The over-limit test verifies rejection before submission and successful draft recovery; a separate disabled-after-selection Skill case preserves real recoverable server ACK/detail coverage. Background Work setup waits for conversation finalization and the drawer's actual list response before its empty-state assertion. Local repair run: 9 passed; final validation/background rerun: 4 passed, including the new stale-Skill recovery case. These are fixture/expectation corrections; no production behavior or test timeouts were weakened.
