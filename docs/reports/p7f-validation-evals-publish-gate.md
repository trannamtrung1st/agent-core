# P7F — Validation, evaluations, diff, and exact publish gate

**Status:** in progress (W06)

**Baseline:** `18ffecf9e660ba0772073fb63649fed960e2126f` (W05 closure)

**Foundation batch:** `1d101c54eed463bdc9d2676db76c45e31d7fc8fa` (review-0031 / review 0089 PASS)

**Resource-validation batch:** `c1c85c5d88440cd0bcc26769ecee8ddd32573d97` (review-0032 / review 0091 PASS)

## Scope delivered (observed, partial)

- `AgentDefinitionDraftValidationService` exposes resolved publication validation as structured blocking findings (domain shape, provider aliases, secrets, model catalog, tools, draft knowledge/template resource bindings) with revision recheck after resource listing (409 when draft changes mid-validation).
- Owner-protected Admin HTTP: `POST /api/v2/admin/definition-drafts/{draftId}/validate` (`docs/14` route table).
- `AgentDefinitionDraftDiffService` and `GET .../definition-drafts/{draftId}/diff` with safe grouped sections against fork baseline, explicit policy/identity formatters, new-draft Added sections, and revision recheck after resource listing (409 on concurrent edit).
- Tests: `AdminDefinitionDraftDiffServiceTests`, `AdminApiTests.Admin_definition_draft_validate_*`, `Admin_definition_draft_diff_*`.

## W06 verification (partial)

| Check | Command | Result |
| --- | --- | --- |
| Draft validate API | `dotnet test tests/AgentCore.Api.Tests --filter FullyQualifiedName~Admin_definition_draft_validate` | Pass (4) at resource batch HEAD |
| Draft diff API | `dotnet test tests/AgentCore.Api.Tests --filter FullyQualifiedName~Admin_definition_draft_diff` | Pass (3) at current W06 HEAD |
| Draft diff service | `dotnet test tests/AgentCore.Application.Tests --filter FullyQualifiedName~AdminDefinitionDraftDiffServiceTests` | Pass (3) at current W06 HEAD |

## Remaining (W06)

- Granular field/code findings, evaluation scenarios, fingerprint evidence, safe diff, exact-revision publish hardening, UI steps, and full gate matrix per frozen P7F contract.
