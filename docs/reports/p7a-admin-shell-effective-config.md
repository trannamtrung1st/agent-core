# P7A — Admin shell and effective configuration

**Status:** candidate (W01 review-fix batch, round 3)  
**Baseline:** `4ab50695160462965e7e8edcff6adea005d856b5`  
**Behavior candidate SHA:** `f3988bb5a8b6de7c35f7a7bb98769fc3b00f4214`  
**Review range:** `4ab50695160462965e7e8edcff6adea005d856b5..f3988bb5a8b6de7c35f7a7bb98769fc3b00f4214`

## Scope delivered

- Owner-protected `/api/v2/admin/definitions`, `/instances`, and `/instances/{id}/effective-config` read APIs.
- Server-resolved effective model (`SessionModelBinder` + catalog), offered tools (`ToolCatalog` + configuration gate), harness references, workspace template id, and trigger/durable eligibility aligned with `OccurrenceCompatibility`.
- React Admin area at `/admin` with Chat navigation, grouped effective-config inspection, independent inventory loading with retry/unauthorized recovery, chat transport teardown on Admin entry, and return-to-last-chat reconnect.
- API, application, UI, and Playwright coverage for owner matrix, compatibility labeling, broken associations, secret sentinels, transport teardown while Admin is open, and the Chat → Admin → instance effective config → return → new turn journey (desktop and narrow).

## W01 verification (this batch)

| Check | Command | Result |
| --- | --- | --- |
| Admin API | `dotnet test tests/AgentCore.Api.Tests --filter FullyQualifiedName~AdminApiTests` | Pass (worker) |
| Admin application | `dotnet test tests/AgentCore.Application.Tests --filter FullyQualifiedName~AdminReadServiceTests` | Pass (worker) |
| Web unit | `cd web && pnpm run test --run src/features/admin` | Pass (worker) |
| Web build | `cd web && pnpm run build` | Pass (worker) |
| P7A Playwright | `cd web && pnpm exec playwright test e2e/admin-shell.spec.ts --project=synthetic` | Pass (worker) |
| Chat regression | `cd web && pnpm exec playwright test e2e/text-conversation.spec.ts --project=synthetic` | Pass (prior W01 turn) |

## Acceptance mapping (P7A)

| ID | Evidence |
| --- | --- |
| AC-P7A-01 | `/admin` route + Chat link; `web/e2e/admin-shell.spec.ts` |
| AC-P7A-02 | `AdminApiTests.Admin_definitions_require_owner_capability` |
| AC-P7A-03 | `AdminApiTests.Admin_definitions_reject_non_trusted_remote_caller` |
| AC-P7A-04 | `AdminApiTests.Admin_definitions_succeed_for_trusted_owner` |
| AC-P7A-05 | `AdminApiTests.Admin_effective_config_resolves_exact_instance_state` + resolved model/tools in `AdminReadServiceTests` |
| AC-P7A-06 | Compatibility tag in Admin UI + API `compatibility` flag |
| AC-P7A-07 | `AdminApiTests.Admin_responses_do_not_leak_secret_sentinels` |
| AC-P7A-08 | `AdminApiTests.User_catalog_agents_remain_unprotected_read` |
| AC-P7A-09 | `Admin_effective_config_returns_not_found_for_broken_definition_association` |
| AC-P7A-10 | `admin-shell.spec.ts` (teardown, return + second Synthetic turn) |

## Canonical documentation

- Architecture: [03-system-architecture.md](../03-system-architecture.md) — P7A Admin read boundary
- Backend interfaces: [04-backend-interfaces.md](../04-backend-interfaces.md) — `AdminReadService`
- Frontend: [13-frontend-implementation-spec.md](../13-frontend-implementation-spec.md) — Admin area
- Protocol: [14-api-and-realtime-protocol.md](../14-api-and-realtime-protocol.md) — `/api/v2/admin`
- Testing: [16-testing-strategy.md](../16-testing-strategy.md) — P7A matrix

## Hosted CI

Pending exact candidate SHA after W01 batch approval.

## Gaps / limitations

- Managed/durable definition sources appear in P7B; inventory currently lists built-in file definitions only.
- Hosted workflow green on the exact candidate SHA not yet recorded in this report.
