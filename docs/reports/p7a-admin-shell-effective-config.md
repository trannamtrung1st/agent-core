# P7A — Admin shell and effective configuration

**Status:** candidate (W01 implementation batch)  
**Baseline:** `4ab50695160462965e7e8edcff6adea005d856b5`  
**Candidate SHA:** pending commit on this branch  

## Scope delivered

- Owner-protected `/api/v2/admin/definitions`, `/instances`, and `/instances/{id}/effective-config` read APIs.
- `AdminReadService` resolves built-in definitions, lists durable instances, and projects secret-safe effective configuration (exact version/persona, tools, knowledge refs, memory/trigger policy, durable-work eligibility).
- React Admin area at `/admin` with Chat navigation, inventory views, effective-config inspection, and return-to-last-chat behavior.
- API and UI tests for owner/trusted-local enforcement, compatibility labeling, broken association, and secret sentinel absence.

## W00 baseline (pre-P7 product tree)

| Check | Command | Result |
| --- | --- | --- |
| Backend | `dotnet test AgentCore.sln --nologo` | Pass (1334 tests, 1 skipped) |
| Web unit | `pnpm install --frozen-lockfile && pnpm run test --run` | Pass (409 tests) |
| Web build | `pnpm run build` | Pass |
| Compose | `./scripts/compose-sqlite-volume.sh` | See worker verification for this batch |

Frozen workflow `36096331077` on baseline `4ab5069` remains authoritative; live `gh` auth was not re-run in this session.

## Acceptance mapping (P7A)

| ID | Evidence |
| --- | --- |
| AC-P7A-01 | `/admin` route + Chat link; `web/e2e/admin-shell.spec.ts` |
| AC-P7A-02 | `AdminApiTests.Admin_definitions_require_owner_capability` |
| AC-P7A-03 | `AdminApiTests.Admin_definitions_reject_non_trusted_remote_caller` |
| AC-P7A-04 | `AdminApiTests.Admin_definitions_succeed_for_trusted_owner` |
| AC-P7A-05 | `AdminApiTests.Admin_effective_config_resolves_exact_instance_state` |
| AC-P7A-06 | Compatibility tag in Admin UI + API `compatibility` flag |
| AC-P7A-07 | `AdminApiTests.Admin_responses_do_not_leak_secret_sentinels` |
| AC-P7A-08 | User `GET /api/v1/agents` remains unauthenticated read |
| AC-P7A-09 | `Admin_effective_config_returns_not_found_for_broken_definition_association` |
| AC-P7A-10 | `admin-shell.spec.ts` return-to-chat journey |

## Hosted CI

Pending exact candidate SHA after merge commit.
