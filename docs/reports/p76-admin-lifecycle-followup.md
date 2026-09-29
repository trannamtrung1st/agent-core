# P7.6 Admin lifecycle follow-up

This report records a post-freeze Admin cleanup. It does not move the P7.6 freeze.

P7.6 remains frozen on `17d89ae`. Hosted workflow `36427670239` is green on that SHA. See [P7.6 closure](p7.6-freeze-candidate.md).

## What changed

- Admin home inventory includes draft-only logical definitions. Published definitions stay one row per logical id and show draft and version counts.
- `New Definition` is rejected with 409 when the id is already a built-in version, durable publication, or draft. After a successful durable-definition delete, the id can be reused.
- Managed instance delete is allowed only after archive, and only when the instance is not a compatibility row and has no live session, active identity-user memory, trigger registration, trigger occurrence, work item, approval, or conversation execution. The delete does not cascade. Admin history records `InstanceDeleted`.
- Durable-only logical definition delete purges drafts, draft resources, evaluation evidence, publications, and publication bindings in one transaction when a revision witness matches and nothing still references the definition. Built-in definitions are refused. Published versions are still deprecated individually. Admin history records `DefinitionDeleted`.
- Definition model and provider fields use `GET /api/v2/admin/authoring-options`. Voice selects speech aliases only when the server reports one recognizer and one synthesizer. Advanced JSON does not insert aliases on save.
- Folder import treats the selected folder as the package root and infers kind from the remaining top-level directory. Re-import still does not replace an existing path.

## Local evidence

| Check | Result |
| --- | --- |
| `dotnet test tests/AgentCore.Infrastructure.Tests --filter FullyQualifiedName~AdminLifecycleDeletionTests` | Pass, 12 (InMemory and SQLite: safe delete, stale witness, referenced definition, archived delete, active/compatibility/stale instance, referenced instance) |
| `dotnet test tests/AgentCore.Application.Tests --filter FullyQualifiedName~AdminDefinitionNewDraftTests` | Pass, 4 |
| `dotnet test tests/AgentCore.Api.Tests --filter FullyQualifiedName~Admin_authoring_options_and_draft_only_definition_lifecycle` | Pass, 1 (authoring options, draft-only inventory, 409 duplicate and built-in, delete, inventory disappearance, id reuse) |
| `pnpm exec vitest run` definition candidate, resource preview, and definition editor | Pass, 17 |
| `pnpm exec vitest run src/features/admin/AdminApp.test.tsx` | Pass, 32 |
| `CI=1 pnpm exec playwright test e2e/p76-admin-journey.spec.ts` | Pass, 1. The Playwright runner started Synthetic. The journey created a definition, edited Form and Advanced JSON, published, and opened a managed chat |
| `dotnet test AgentCore.sln` | Pass. Domain 103; Application 821 passed / 1 skipped; API 243; Infrastructure 524 passed / 7 skipped. Skips are existing opt-in hosted checks |

Hosted Synthetic for this follow-up was not run. Compose was not run. The historical P7.6 hosted gate stays the evidence for `17d89ae` only.

## Not in this follow-up

- No generic cascade delete.
- No Delete Version.
- No implicit bulk-import replacement.
- No P8 provider framework.
