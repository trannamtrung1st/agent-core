# P7F — Validation, evaluations, diff, and exact publish gate

**Status:** approved (W06 slice gate review 0101 at `03e350af1ec343c17d8486b4ffcc8fb6ecac8bb0`)

**UI batch:** `03e350af1ec343c17d8486b4ffcc8fb6ecac8bb0` (review-0036 / review 0101 PASS)

**Baseline:** `18ffecf9e660ba0772073fb63649fed960e2126f` (W05 closure)

**Foundation batch:** `1d101c54eed463bdc9d2676db76c45e31d7fc8fa` (review-0031 / review 0089 PASS)

**Resource-validation batch:** `c1c85c5d88440cd0bcc26769ecee8ddd32573d97` (review-0032 / review 0091 PASS)

**Semantic-diff batch:** `71fcc9a87fbfeda3f676e9d2697935feaa2cfcb3` (review-0033 / review 0093 PASS)

**Publish-gate batch:** `2e2e9818733efd975fd127f8375accbb15fa287c` (review-0034 / review 0095 PASS)

**Evaluation batch:** `312336416138343788dd4285ae12d16b3bd85fc7` (review-0035 / review 0098 PASS)

## Scope delivered (observed, partial)

- `AgentDefinitionDraftValidationService` exposes resolved publication validation as structured blocking findings (domain shape, provider aliases, secrets, model catalog, tools, draft knowledge/template resource bindings) with revision recheck after resource listing (409 when draft changes mid-validation).
- Owner-protected Admin HTTP: `POST /api/v2/admin/definition-drafts/{draftId}/validate` (`docs/14` route table).
- `AgentDefinitionDraftDiffService` and `GET .../definition-drafts/{draftId}/diff` with safe grouped sections against fork baseline, explicit policy/identity formatters, new-draft Added sections, and revision recheck after resource listing (409 on concurrent edit).
- `AgentDefinitionDraftPublishService` is the sole application publish command; lifecycle `CommitDraftPublicationAsync` is internal. HTTP `POST .../publish` runs resolved validation (resource bindings included), exact expected revision, and post-validation revision recheck (409 on stale revision).
- Configuration/resource `configurationFingerprint` on validation; draft evaluation scenarios (`SqliteDefinitionDraftEvaluationStore` with transactional revision bump on Sqlite profile, in-memory store with bump-before-mutate ordering), synthetic offline evaluation runner for `ToolOffered`, `ToolNotOffered`, `ResourceBound`, `TriggerSchedulePermitted`, and `ExternalActionDenied`, evaluation result provenance, and publish blocking when required eval evidence is stale or missing.
- Admin HTTP: evaluation scenario list/upsert/run/results (`docs/14`).
- Tests: `AdminDefinitionDraftEvaluationServiceTests`, `AdminDefinitionDraftDiffServiceTests`, `AdminDefinitionDraftPublishServiceTests`, `AdminApiTests` draft validate/diff/publish/evaluation coverage.
- Admin UI: Test & Publish tab on draft editor (validate, required Synthetic scenario run, safe diff, client publish eligibility aligned with revision/fingerprint evidence).

## W06 verification (partial)

| Check | Command | Result |
| --- | --- | --- |
| Draft validate API | `dotnet test tests/AgentCore.Api.Tests --filter FullyQualifiedName~Admin_definition_draft_validate` | Pass (4) at resource batch HEAD |
| Draft diff API | `dotnet test tests/AgentCore.Api.Tests --filter FullyQualifiedName~Admin_definition_draft_diff` | Pass (3) at current W06 HEAD |
| Draft diff service | `dotnet test tests/AgentCore.Application.Tests --filter FullyQualifiedName~AdminDefinitionDraftDiffServiceTests` | Pass (3) at current W06 HEAD |
| Publish gate service | `dotnet test tests/AgentCore.Application.Tests --filter FullyQualifiedName~AdminDefinitionDraftPublishServiceTests` | Pass (3) at current W06 HEAD |
| Draft publish API | `dotnet test tests/AgentCore.Api.Tests --filter FullyQualifiedName~Admin_definition_draft_publish` | Pass at publish-gate HEAD |
| Draft evaluation service | `dotnet test tests/AgentCore.Application.Tests --filter FullyQualifiedName~AdminDefinitionDraftEvaluationServiceTests` | Pass (6) at final-revise HEAD `349429d` |
| Final eval matrix regression | `dotnet test tests/AgentCore.Application.Tests --filter FullyQualifiedName~RunScenarioAsync_supports_resource_trigger_and_external_action_checks` | Pass at `349429d` |
| Draft evaluation store | `dotnet test tests/AgentCore.Infrastructure.Tests --filter FullyQualifiedName~DefinitionDraftEvaluationStoreTests` | Pass (4) at current W06 HEAD |
| Draft admin API suite | `dotnet test tests/AgentCore.Api.Tests --filter FullyQualifiedName~Admin_definition_draft` | Pass (25) at current W06 HEAD |
| Publish gate eligibility | `npm test -- --run src/features/admin/definitionDraftPublishGate.test.ts` (web) | Pass (7) |
| Publish gate panel evidence load | `npm test -- --run src/features/admin/definitionDraftPublishGatePanel.test.tsx` (web) | Pass (2) |
| Admin app publish gate wiring | `npm test -- --run src/features/admin/AdminApp.test.tsx` (web) | Pass (17) |
| Definition lifecycle Playwright | `CI=1 npx playwright test e2e/z-admin-definition-lifecycle.spec.ts` (web) | Pass (validate → eval → diff → publish journey) |
| Web production build | `npm run build` (web) | Pass |

## Final gate matrix (observed)

- Bounded Synthetic checks cover tool offer/deny, draft resource binding by logical path, user-scheduling trigger policy boundaries, and external HTTP action denial via tool policy.
- Regression: `AdminDefinitionDraftEvaluationServiceTests.RunScenarioAsync_supports_resource_trigger_and_external_action_checks`.

## Remaining (deferred to W07/W08)

- Whole-phase lifecycle automation beyond the W09 `admin-lifecycle` gate (`admin-lifecycle.spec.ts` observed in W09).
