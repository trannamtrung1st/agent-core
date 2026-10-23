# Standalone Harness authority retirement — 2026-10-23

This qualifies removal of the old standalone candidate/refinement executor. The original IAM goal remains active in the [delivery ledger](multitenant-iam-delivery-2026-10-16.md).

## Implemented boundary

- Removed `HarnessManagementService`, API registration, Infrastructure factory dependency and generic `SessionToolExecutor` Harness dispatch. Removed unused HTTP DTOs and browser review contract aliases.
- Generic unowned tool offering/execution now denies every Harness command, even with caller-built Managed policy, Instance/Run/Session IDs and an explicit allowlist. Current canonical descriptors, tenant eligibility, independent delegation and protected configuration/consent/effect owners remain.
- Policy preparation/update and freeze preserve inert historical preparation and approval bytes without changing old decision statuses. Canonical configuration transactions reject creating or changing that history before committing any revision/audit.
- Removed service-bound candidate/concurrency/recovery tests and the obsolete shared-promotion method. Preserved the pure historical-candidate normalizer and extracted the disposable SQLite factory used by retained policy tests.
- Updated architecture, application/protocol, frontend, persistence and testing owners to distinguish retired orchestration from current canonical operations.

## Deliberate schema boundary

`HarnessManagementState.Preparation` and its nested Domain serialization graph remain inert. Canonical configuration observation hashes serialize the whole Instance, and exact prepared management mutations can contain this state. Dropping or rewriting its properties would invalidate previously captured hashes/plans. This module introduces no fallback serializer, migration reset, compatibility executor, automatic consent rewrite or database mutation. A coordinated schema/consent cutover is still required; serialized history is not current approval authority.

## Verification

Commands run from the repository root unless specified. All fixtures used disposable Synthetic state; no user database or Real host was changed.

| Check | Observed result |
| --- | --- |
| Serial solution build, `--no-restore --nologo -m:1 -nodeReuse:false -p:UseSharedCompilation=false` | Both builds pass; final 0 warnings/errors, 13.27 s. |
| Application `ToolRegistryPolicyTests` | 18 pass, including actual forbidden execution for all six fabricated Harness contexts. |
| Infrastructure `Canonical_configuration_commit_cannot_create_retired_Harness_preparation` | 2 pass: InMemory/SQLite reject forged history and preserve parent/security revision and audit. |
| API initial history/recovery selection | 3 pass: both-store inert history and current SQLite exact Harness consent/recovery. The history cases were extended and rechecked below. |
| API canonical Harness matrix, model instructions, self-management authority/operations and scoped configuration | 96 pass, 4 fail of 100. All 72 canonical Harness cases pass; the four failures are the obsolete occurrence fixture described below. |
| API final history update/freeze, semantic previews/source rejection, canonical management commands and pending SQLite recovery | 25 pass. |
| Current canonical Automation occurrence/admission selection | 12 pass: actual tenant model invocation into saved conversations and due-schedule edit/reopen/admission/completion, with both stores. |
| `web`: `pnpm run test --run src/services/adminApi.problem.test.ts` | 3 pass. |
| `web`: `pnpm run build` | Pass; existing Vite chunk-size advisory. |
| `web`: `pnpm exec playwright test --config playwright.tenant-chat.config.ts e2e/tenant-management-mutations.spec.ts --grep 'management.freeze.*1440\|management.policy.update.*1440'` | 4 pass: approve/reject for each command, actual saved policy/revision assertions, no flow errors. |
| Playwright MCP on a separately bootstrapped Synthetic host | Sign in; submit exact freeze task through Chat; open saved-action details; review `management.freeze`; approve and confirm; observe Configuration updated and pinned-Run message; independently read persisted Frozen=true. No browser console errors. Network showed ordinary aborted-and-reissued React reads, with successful replacements and no failed protected UI action. |

Backend test invocations used `dotnet test <project> --no-build --no-restore --nologo --filter '<selection>'`. The exact API selectors were:

```text
Initial: FullyQualifiedName~Policy_freeze_keeps_inert_historical_preparation|FullyQualifiedName~Harness_command_pending_exact_assessment_and_effect_survive
Regression: FullyQualifiedName~Canonical_Harness_commands_retain|FullyQualifiedName~Harness_instructions_use_the_exact|FullyQualifiedName~SelfManagementAuthorityTests|FullyQualifiedName~SelfManagementOperationTests|FullyQualifiedName~ScopedInstanceConfigurationTests
Final policy: FullyQualifiedName~Policy_update_and_freeze_keep_inert|FullyQualifiedName~HarnessChatAuthoringTests|FullyQualifiedName~Canonical_management_commands_reuse_exact_consent|FullyQualifiedName~Management_mutation_pending_exact_consent
Canonical occurrence: FullyQualifiedName~Tenant_model_manual_Automation_invocation_uses_the_saved_conversation|FullyQualifiedName~Tenant_automation_authoring_preserves_due_occurrence
```

Across the overlapping selections, 152 distinct backend cases pass; the four obsolete occurrence-fixture cases remain failed as disclosed above.

Local logs are `/private/tmp/harness-retirement-{build,build-r2,application,infrastructure,api-first,api-regression,api-policy,canonical-occurrence,web-tests,web-build,browser}-20261023.log` (brace notation describes separate files). Source scanning finds no remaining C# reference to the removed service or DTO file. Historical preparation status/approval record declarations remain only in the Domain serialization graph; there is no production constructor or decision executor for them.

## Remaining verification and cutover

`ScopedInstanceConfigurationTests.Fresh_occurrence_resolves_adopted_configuration_and_retains_existing_session_effort` fails in all four store/target combinations because it resolves the previously unregistered `TriggerOccurrenceRouter`. This module does not restore that authority path. The current canonical admission selection exercises the actual tenant owners separately; the historical fixture still needs retirement/replacement, and its original assertions are not claimed as passing.

MCP setup corrected two script-only assumptions (relative request URL and unavailable server-side `crypto`) before exercising the flow. An initially guessed post-action `/management-policy` read returned 404; the actual `/self-management` read confirmed Frozen=true. These were manual probe errors, not application changes or hidden passing results.

Historical schema retirement, other global/profile service/store assumptions, final tool-transport inventory and proposal-wide security, isolation, functional and UI acceptance remain open. No hosted CI, live provider call, push, deployment or milestone acceptance was performed.
