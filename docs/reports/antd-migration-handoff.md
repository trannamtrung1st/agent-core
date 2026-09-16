# Ant Design v6 migration handoff

Run: `run-20260916T173700-eb3104`. Baseline HEAD: `22552308d9bbb7bb1b5552870dbdbe45a89ead4a`. Presentation-only; no push or hosted-provider spend.

Canonical UI decision: [Technology Decisions — Ant Design v6](../10-technology-decisions.md#decision-ant-design-v6-as-mvp-generic-ui-system). Screens and behavior: [Frontend Implementation](../13-frontend-implementation-spec.md). Lightweight visuals: [DESIGN.md](../../.agents/context/DESIGN.md).

## Batch-to-commit mapping

| batch_id | plan_items | commit | checks |
| --- | --- | --- | --- |
| batch-01 | item-8d2ac9f4aef1 | `82fb8a78b4b5cc4ce8d354cc13df57c5edc12ea2` | Unit 110; build; CI=1 e2e 8. Playwright roots under `local/tdp-workspace` rejected by persistence guard; rerun used `/tmp`. |
| batch-02 | item-08aba1004657 | `07b9e68d1ac7e1363d57cd5c367144e2d487eb04` | Frozen-lockfile; unit 108; build; text-conversation E2E; MCP identity Sam + start. |
| batch-03 | item-40aac7bafca5 | `bc9f77d6fd5a2ca1abbac34f24a3fc8b891dcc0a` | Unit 109; build; text-conversation E2E; MCP desktop catalog + 390px drawer. |
| batch-04 | item-0faca7b0ae47, item-fd7331b90119 | `b720b978eca3a88e7c715bc8a8b790be7fc5118b` | Unit 112; build; CI=1 e2e 8 including fake-device voice; MCP Hello → Hello from synthetic. |
| batch-05 | item-cf45d8b6ad35 | `2940a19ba0f656e886a8762fc5d8b211c5bd5613` | Unit 112; build; CI=1 e2e 8; MCP send/narrow; fonts/plates gone. |
| batch-06 | item-5f342d4db1e6 | `23032593dca5ed8e4d669a5deb2925c7f7f9aaa3` | Frozen-lockfile; unit 112; build; CI=1 e2e 8; MCP catalog/composer/attach/pending-voice/End/narrow. |

## Final commands (batch-06)

From `web/` with `Persistence__Provider=InMemory` and `/tmp` attachment/workspace/artifact roots; `CI=1` for Playwright:

| Command | Result |
| --- | --- |
| `pnpm install --frozen-lockfile` | Pass (lockfile up to date). |
| `pnpm run test --run` | Pass — 19 files / 112 tests. |
| `pnpm run build` | Pass — `dist/assets/index-B__LShDd.css` 1.67 kB. |
| `CI=1 pnpm run test:e2e` | Pass — 8 tests / 18.1s, including fake-device capture, duplex, interrupt, playback, reconnect. |

Baseline (same suites at `22552308`): unit 110 passed, build passed, CI=1 e2e 8 passed. Later unit count rose with presentation tests (108→112), not from failing baselines. No backend `src/**` or `tests/AgentCore.*` diff vs baseline.

## Protected-path audit

`git diff 22552308..HEAD -- src tests/AgentCore.*`: empty.

`git diff 22552308..HEAD -- web/src/services web/src/state web/src/audio web/src/features/chat/statusLabel.ts web/src/features/chat/sanitizedMarkdown.ts`: empty.

Frontend edits are product-component presentation plus `app.css` / `main.tsx` composition. `antd` 6.6.4; no `@ant-design/icons`, Pro, or wrappers.

## Phases 0–10

| Phase | Status | Evidence |
| --- | --- | --- |
| 0 Baseline | Complete | `local/tdp-workspace/evidence/.../baseline/` |
| 1 Root | Complete | ConfigProvider + Ant `App`; batch-02 |
| 2 Identity | Complete | AntD Select; custom Select deleted; batch-02 |
| 3 Shell | Complete | Layout/Sider/Drawer; batch-03 |
| 4 Transcript | Complete | Empty/Spin/Tag around `HistoryEntry[]`; batch-04 |
| 5 Composer | Complete | TextArea/Button/Alert/Progress; hidden file input; batch-04 |
| 6 Retirement | Complete | `styles.css`, fonts, plates deleted; batch-05 |
| 7 DESIGN.md | Complete | Short AntD policy; batch-05 |
| 8 Impeccable | Complete | `design.json` / surface / Martian exception reset; batch-05 |
| 9 Frontend skill | Complete | Direct AntD; nonvisual rules kept; batch-05 |
| 10 Docs/handoff | Complete | This report; docs/10 verified; docs/13/11/18, README, AGENTS, docs-consistency skill |

## Section 16 criteria

| ID | Status | Evidence |
| --- | --- | --- |
| AC-01 | Pass | `web/package.json` `antd` ^6.6.4; `main.tsx` ConfigProvider |
| AC-02 | Pass | IdentityPicker, ChatApp, SessionRail, Hud, Transcript, Composer import `antd` |
| AC-03 | Pass | Layout sider desktop; Drawer `Open sessions` at 390px |
| AC-04 | Pass | Unit 112 + e2e 8 + MCP catalog/send/attach; batch-07 live paused delete and drop/paste |
| AC-05 | Pass | Protected-path audit empty vs baseline |
| AC-06 | Pass | No `web/src/components/Select.tsx` |
| AC-07 | Pass | `web/src/styles.css` deleted |
| AC-08 | Pass | Martian fonts and plates deleted |
| AC-09 | Pass | Active guidance is AntD; Pixel names only as bans or historical rationale; remaining `/docs` “Post-MVP planned until verified” headings are unrelated |
| AC-10 | Pass | `.agents/context/DESIGN.md` short AntD policy |
| AC-11 | Pass | `.impeccable/design.json` and ChatApp surface describe AntD; detector exception removed |
| AC-12 | Pass | `.agents/skills/frontend/SKILL.md` |
| AC-13 | Pass | docs/10 verified; docs/13 no longer “planned” or pre-migration custom UI |
| AC-14 | Pass | Catalog, voice preflight, attachments, HUD precedence unchanged in docs/13 |
| AC-15 | Pass | No AppButton wrappers; `app.css` layout/overflow/preview/hidden input |
| AC-16 | Pass | Frozen-lockfile, unit, build, e2e |
| AC-17 | Pass | Retirement search + this docs pass; no live fallback theme |

## Browser matrix

Prefer Playwright MCP on isolated Synthetic `http` profile (`/health` `Synthetic`). Fake-device voice uses CLI E2E (MCP has no fake-device flags).

| Row | How exercised | Observed |
| --- | --- | --- |
| Empty catalog | MCP | `No sessions yet.` then populated after start |
| Identity select | MCP | Combobox → Sam — Customer support representative |
| Start / New chat | MCP | Start conversation → Ready; New chat returns to picker |
| Switch/open | MCP | Click `Support notes` restored Hello from synthetic. |
| Rename | MCP | Title Save → rail `Support notes` |
| Archive / unarchive | MCP | Archive then Show archived then Unarchive |
| Ended locked | MCP | After End, row `Support notes … · Ended` disabled (no reopen actions) |
| Delete cancel | MCP | Delete then Cancel; row remained |
| Delete confirm | MCP (batch-07, isolated Synthetic InMemory) | Started conversation (Alex), queued attachments, **New chat** left catalog row `New chat … · Paused` with identity picker visible. First Confirm delete while catalog still held revision 1 after two attachment POSTs returned 409 `Unable to delete the session` — not counted as success. Toggled **Show archived** to refresh the catalog, then **Delete → Confirm delete** issued `DELETE /api/v2/sessions/0828f1b3-…?expectedRevision=3` **204**. Rail text: `Sessions / New chat / Show archived / No sessions yet.` Zero `.session-row` nodes. Ended rows remain non-deletable per docs/13. Unit tests and the Open/stale-revision 409 are not used as pass evidence. |
| Load more | Skipped with data rationale | Catalog fetch `limit: 50`. Isolated InMemory MCP catalog stayed well under one page (`hasMore` false; Load more control not shown). Button exists when `hasMore` (`SessionRail.tsx`). Creating 51 sessions is catalog-capacity work, not UI-system proof. |
| Send text / stream | MCP + e2e | You — Hello / Sam — Hello from synthetic. |
| Enter send | MCP | `line1` sent via Enter |
| Shift+Enter | MCP | Message value kept `keep` plus newline JSON; no send |
| Disabled until ready | MCP | Send disabled with empty composer; enabled after file ready |
| Transcript + composer | MCP desktop and 390px | Composer Message/Attach/Send/Voice/End remain |
| File select | MCP | Attach chooser → `antd-notes.txt` chip |
| Drag/drop / paste | MCP (batch-07) on running Synthetic composer | Playwright MCP `drop` of `drop-notes.txt` onto `form.composer` produced pending chip **drop-notes.txt** (`Remove drop-notes.txt`) and `POST …/attachments` 201. File paste into **Message**: in-page `paste` with `clipboardData.files` (`paste-notes.txt`) joined the same queue (chips `drop-notes.txt`, `paste-notes.txt`; second `POST …/attachments` 201). Native Playwright `dispatchEvent('paste')` without `clipboardData.files` did not queue a file and is not cited as the pass. Composer unit tests are extra coverage, not the matrix close. |
| Upload retry/remove | Unit + MCP remove in batch-04/e2e | e2e `Remove notes.txt`; retry in Composer tests |
| Attachment-only send | MCP + e2e | Transcript link `antd-notes.txt` / `notes.txt` |
| Historical file access | MCP + e2e | Authenticated chip/link, not public Image URL |
| Voice start / pending cancel | MCP | HUD `Starting voice…`; `Cancel voice` visible; cancel returns Ready. MCP has no fake device (same as batch-04). |
| Mute / duplex / interrupt / playback / reconnect / voice-to-text | CLI fake-device e2e | `voice-duplex`, `voice-interrupt`, `voice-playback`, `voice-capture` (8/8) |
| End | MCP | Composer End → picker; Ended row |
| Markdown / unknown / interrupted | Unit | `Transcript.test.tsx`, `sanitizedMarkdown.test.ts` |
| Empty/loading/error | Unit + MCP | Empty copy; Spin connecting tests; capability/catalog errors in rail tests |
| Connection retry | e2e text-conversation | Disconnect → Reconnecting |

**Narrow 390×844:** `Open sessions` drawer (Close works); Start conversation; Hello → Hello from synthetic.; Message still visible.

**Console (batch-07):** pre-existing `favicon.ico` 404; one stale-revision delete 409 then successful 204; a TypeError from the failed clipboard-less paste dispatch (handler read `clipboardData.files`). No plates/fonts requests.

## Whole-output review revision (batch-07)

Findings `wo-001` / `wo-002` / `wo-003` (`review-whole-output-01-fs-01`). Host: `dotnet run --launch-profile http` with `Persistence__Provider=InMemory` and disposable `/tmp` attachment/workspace/artifact roots; Vite `127.0.0.1:5173`; `/health` `{"profile":"Synthetic"}`. Playwright MCP against that app.

- **Paused delete:** catalog empty after Confirm delete (204, revision 3).
- **Drop/paste:** both files in `Pending attachments` and both upload POSTs 201.
- **Commit mapping:** batch-06 cell is `23032593dca5ed8e4d669a5deb2925c7f7f9aaa3` (matches prior `batch-06/commit-mapping.md`). This evidence-revision commit is mapped in run evidence `batch-07/commit-mapping.md` after `git commit` so the report does not embed a pre-commit placeholder.

Skills this revision: develop, document, frontend, testing.

**MCP Voice gap:** no fake-device; capture/playback/interrupt/reconnect proven by `CI=1` Playwright, not MCP audio.

## Proposal §15 checklist

Covered in the matrix table. Load more skipped with the page-size rationale above. Attachment drag/drop and paste rows are closed by batch-07 MCP on the running Synthetic composer, not by `Composer.test.tsx`.

## Retirement-match dispositions (final)

See [batch-05 retirement-search](../../local/tdp-workspace/evidence/antd-migration/run-20260916T173700-eb3104/batch-05/retirement-search.md) (workspace evidence; not committed). Committed outcomes: no `styles.css`, no Martian fonts/plates, no custom Select, DESIGN.md/Impeccable/frontend skill are AntD. This item resolved remaining canonical docs so they no longer prescribe avoiding component frameworks or Pixel Dialogue Field as the live UI. Historical rationale in docs/10 may name Pixel Dialogue Field as the retired cost. TDP `inputs/**` remain immutable snapshots.

## Skills applied

- `.agents/skills/develop/SKILL.md`
- `.agents/skills/document/SKILL.md`
- `.agents/skills/docs-consistency/SKILL.md`
- `.agents/skills/architecture/SKILL.md`
- `.agents/skills/frontend/SKILL.md`
- `.agents/skills/testing/SKILL.md`
- `.agents/skills/impeccable/SKILL.md` (operate / AntD baseline; Pixel anti-reference)

## Residual

Manual headset/speaker quality remains opt-in with live keys (unchanged MVP policy). No deployment or push.
