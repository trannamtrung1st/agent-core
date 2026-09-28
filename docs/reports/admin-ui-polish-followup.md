# Admin UI polish follow-up

This report records the ad hoc review of UI shipped since the P7.5 freeze (`70a5720`) through the form-hint commit. It does not reopen P7.6 and it is not a milestone freeze. Historical freeze reports are unchanged.

Product behavior for the hints is in [Frontend Implementation](../13-frontend-implementation-spec.md). Presentation stays Ant Design v6. `.agents/context/PRODUCT.md` and `.agents/context/DESIGN.md` were left unchanged: the hints are field copy on the existing Admin form, not a new spacing, color, or component rule.

## Audited surfaces

Admin home, New definition, definition Form and Advanced JSON, capabilities and knowledge sources, resource import, validation/diff/publish, New instance, persona Form/JSON, lifecycle confirmation, memory and automation, effective configuration, managed Chat, and the shared shell at desktop and about 390px. Chat composer, session rail, Background work, and Schedules were opened as shared consumers. Chat feature sources were not edited.

## Fields

Definition form labels were already persistent. Accessible names matched those labels once; they were not doubled. Switch rows name the switch and place the visible label after the control.

| Field | What the operator sees | Stored value |
| --- | --- | --- |
| Definition ID (new definition) | Placeholder `field-guide`. Help: lowercase letters, digits, and hyphens, up to 64 characters. A non-matching id sets `aria-invalid` and keeps Create draft disabled. | Unchanged. Only the id is posted. |
| Silence threshold | Help: milliseconds, 1,000 to 120,000. | Starter number kept (observed `30000`). |
| Cooldown | Help: milliseconds, 5,000 to 600,000. | Starter number kept (observed `60000`). |
| Max consecutive proactive turns | Help: empty uses 1. | Empty stays empty. |
| Max silent evaluations | Help: empty uses 8. | Empty stays empty. |
| Max inactivity | Help: milliseconds; empty uses 900,000. | Empty stays empty. |
| Conversation language | Help: `auto`, or a BCP 47 tag such as `en`. | Starter text kept. |
| Model catalog key | Help: optional lowercase catalog key; empty sets no model default. | Empty stays empty. |
| Reasoning effort | Help: optional examples low, medium, high. | Empty stays empty. |
| Speaking rate | Help: 1 is normal speed; 0.5 to 2. | Starter `1.0` kept. |
| Provider preferences | One note: language model is required; speech aliases are required when voice is on and must stay empty when voice is off. | Aliases unchanged. |

Knowledge placeholders (`policy`, `Support policy`, `policy@demo`) and the resource path placeholder `knowledge/policy.md` stayed examples. Other labeled fields (identity, instructions, behavior selects, memory and automation switches, metadata) already had visible labels and were not given repeated generic help.

## Defects fixed

- New definition accepted only a matching id, but Create draft went disabled with no statement of the rule.
- Initiative millisecond fields showed raw numbers with no unit.
- Optional initiative bounds looked blank with no source for the empty-field default.
- Conversation language, catalog key, reasoning effort, speaking rate, and speech aliases did not state format or consequence.

## Deliberate non-changes

- Invalid Advanced JSON already shows an alert, keeps the text across tabs, and disables save and publish. Revision stays put.
- Resource preview still infers kind. An oversized file shows "File is larger than 8 MiB." and does not clear a successful bind.
- Publish confirmation names the immutable version. Archive confirmation names that new chats and triggered work stop until unarchive.
- Invalid persona JSON does not save. The visible message is the parser error.
- No horizontal overflow on the open definition draft at 390px (`scrollWidth` 390).
- Console: favicon 404 and the existing Ant Design `List` deprecation. `List` was not replaced.
- No persisted default, revision check, authorization rule, or history behavior was changed.

## Browser evidence

Profile: Synthetic (`GET /health` returned `"profile":"Synthetic"`). Disposable SQLite: `data/playwright/ui-polish-confirm/confirm.db` via an absolute connection string. `src/AgentCore.Api/data/agent-core.db` was last written at 01:18 local, before this host. Playwright MCP against `http://127.0.0.1:5173`. Product SHA at confirmation: `5ef08b0969b0c840bc6914e3a1e5579e1ed28867`.

| Viewport | Actions | Expected | Observed |
| --- | --- | --- | --- |
| 1280×900 | Chat text "Hello from the polish confirmation" | Ready, scripted reply | Ready, "Hello from synthetic." |
| 1280×900 | New definition `ui-polish-confirm`, edit instructions, save, reload | Text remains, revision advances once for that save | Instructions remained. Revision 2 after the first save, same timestamp after reload. |
| 1280×900 | Advanced JSON `{`, then Form and back | Invalid text remains, save and publish blocked, revision unchanged | Alert "Advanced JSON is invalid", both actions disabled, revision stayed 2, broken text survived the tab switch. |
| 1280×900 | Import `policy.md` and `notes.txt`, set paths, bind; then an 8 MiB+ file | Bind commits the pair; the oversized file is correctable and does not drop the bind | Preview Knowledge and Reference, "Resources bound.", then "File is larger than 8 MiB." with bind disabled and both paths still listed. |
| 1280×900 | Knowledge source `refund-policy` / `policy@demo` on `knowledge/policy.md`, save, reload | Candidate change persists; starter numbers do not reset | Identity and citation remained. Silence threshold `30000`. Catalog key empty. |
| 1280×900 | Run validation, diff, Synthetic evaluation, publish | Blockers or readiness visible; publish creates a version | "Validation snapshot", "Diff vs New", "Draft is ready for final publish.", dialog "Publish this draft?", "Published version 1." |
| 1280×900 | New instance on that publication with the definition persona | No custom persona body; persona shows the definition identity | Instance `ui-polish-confirm · v1`, name `ui-polish-confirm`, role Assistant. |
| 1280×900 | Invalid persona JSON save; open archive confirmation | Invalid JSON does not save; confirmation names the consequence | Parser message, no save success. Tooltip: new chats and triggered work stop until unarchive. |
| 1280×900 | Start managed chat for v1, send text, open Background work and Schedules | Managed identity, completed reply, drawers open and close | Ready, "Hello from synthetic.", both drawers opened. |
| 390×844 | Open the saved draft | No horizontal overflow; saved instructions and starter numbers remain | `scrollWidth` 390. Instructions unchanged. Silence threshold `30000`. Catalog key empty. |

Failed network requests observed in MCP: favicon 404. No Admin or Chat API failure on these steps.

## Real profile

`OPENROUTER_API_KEY` is unset in this environment, so a Real host was not started. `/health` was not asked to report Real. Synthetic browser evidence above covers the authoring and Chat journeys. No Synthetic Playwright project was pointed at a Real host.

## Automated checks

Working directory `web/` unless noted. Product SHA `5ef08b0` (docs in this report were written after these commands).

| Command | Result |
| --- | --- |
| `pnpm exec vitest run` focused Admin files (`definitionCandidateEditor.test.tsx`, `AdminApp.test.tsx`, `resourceImportPanel.test.tsx`, `definitionCandidate.test.ts`, `resourcePreview.test.ts`) | 5 files, 47 passed |
| `pnpm run test --run` | 69 files, 505 passed |
| `pnpm run build` | Passed (`built in 3.01s`) |
| `CI=1 PLAYWRIGHT_SQLITE_PATH=../data/playwright/p76-admin.db pnpm exec playwright test --project=p76-admin` | 1 passed (30.6s) |
| `CI=1 PLAYWRIGHT_SQLITE_PATH=../data/playwright/admin-lifecycle-isolated.db pnpm exec playwright test --project=admin-lifecycle` | 1 passed (28.0s) |
| `CI=1 PLAYWRIGHT_SQLITE_PATH=../data/playwright/ui-polish-chat.db pnpm exec playwright test e2e/text-conversation.spec.ts e2e/z-admin-managed-instance-journey.spec.ts` | 14 passed (1.9m) |
| `CI=1 pnpm exec playwright test --project=synthetic` | First run: 55 passed, 1 failed. `e2e/text-conversation.spec.ts` progress test did not find `.markdown-message strong` "Delayed" within 30s after the assistant body already matched "Order 91 is delayed". The same test alone on `data/playwright/ui-polish-progress.db` passed (15.6s). Full project rerun: 56 passed (4.1m). |
| `pnpm run test --run --maxWorkers=2` on `e2f2e04` | 69 files, 505 passed. This is the frontend command from workflow `synthetic`. |
| `dotnet test tests/AgentCore.Api.Tests/AgentCore.Api.Tests.csproj --nologo` on `e2f2e04` | Passed 240, twice. |

Backend and Compose were not part of the hint change. The API suite above was run locally after hosted run `36477563307` failed that step.

## Hosted CI

Workflow `synthetic` on push to `main`. The historical P7.6 workflow `36427670239` is not evidence for these SHAs.

| Run | SHA | Conclusion |
| --- | --- | --- |
| https://github.com/trannamtrung1st/agent-core/actions/runs/36475173201 | `5783ea820e2c0ee52f6cae9aa4a9b6f2eb4e1d0e` | failure |
| https://github.com/trannamtrung1st/agent-core/actions/runs/36477563307 | `e2f2e04061ae9b517dc52c75cdf9980328dfa157` | failure |
| https://github.com/trannamtrung1st/agent-core/actions/runs/36479010962 | `2edf832d25a3726ca360b8c0acf000b0565d0f18` | success |

Run `36475173201`: Compose smoke succeeded. Offline gates failed in "Frontend install, unit tests, and build". `instanceMemoryAutomation.test.tsx` looked up the button name "Load items" while the session-denial text was already visible and the button was still loading, so its accessible name was "loading Load items". Commit `e2f2e04` waits until that denial is visible and Load items is enabled and not loading.

Run `36477563307`: "Backend API tests" failed with exit code 1 (20:18:20Z–20:19:42Z). The same step on unchanged backend code succeeded in run `36475173201` (19:58:09Z–19:59:39Z). Public annotations do not name the test. Downloading job logs requires a signed-in repository admin (`gh` is not authenticated). Frontend and Playwright steps were skipped. Local API tests passed 240, as in the table above.

Run `36479010962` completed with conclusion success on `2edf832d25a3726ca360b8c0acf000b0565d0f18`. That SHA contains the form hints, the Load items wait, and the two failure notes above. The GitHub Actions API confirmed `status=completed` and `conclusion=success`.

## Context sync

`docs/13-frontend-implementation-spec.md` is the owner for the new field help. `docs/16`, `docs/17`, `docs/18`, and README were not changed: commands, profile setup, and the P7.6 freeze status are the same, and the implementation plan does not say this polish is still undone. PRODUCT.md and DESIGN.md were not changed.

## Remaining limitations

- Run `36477563307` failed Backend API tests without a public test name. The later run `36479010962` on `2edf832d25a3726ca360b8c0acf000b0565d0f18` succeeded. The failing test name from the earlier API step is still unknown.
- Real-profile browser use was not executed because `OPENROUTER_API_KEY` is unset.
- The first full Synthetic Playwright run failed one Chat progress assertion that passed alone and on the immediate full rerun. That assertion is outside the Admin hint change.
- Favicon 404 and the Ant Design `List` deprecation remain.
