# Retained Harness history and canonical authority — 2026-10-24

This follows [standalone Harness authority retirement](harness-authority-retirement-verification-2026-10-23.md). It qualifies retained historical data inside actual tenant-owned SQLite Instances, rather than treating a retired serializer type as an executable subsystem. It does not introduce a migration, serializer fallback, consent rewrite or new authority owner. The broader IAM goal remains active.

## Ownership and retention decision

The current `HarnessManagementJson` column still stores canonical policy, sparse overrides, freeze state and factual partial assessments. The Policy component's freeze/source/tool-eligibility fields remain live configuration. Existing full-policy Instance choices are conservatively retained when sparse overrides are absent; materializing those choices for a later patch does not grant IAM, introduce newly inherited delegation or execute an action. Removing that Policy component would change current behavior.

`Preparation` and its nested authoring operation/approval/publication graph are different: they are inert serialized history. A source census finds the retired record types declared only in Domain. The only explicit production member accesses to `.Preparation` are the canonical configuration guards that reject creation/mutation and preserve existing bytes. The historical admin event-kind summary is display metadata, not an executor. Current services, tools, transport and recovery use first-class tenant requests, exact operations, decisions, grants, receipts and Runs.

Whole-Instance observation hashes and exact prepared management mutations can serialize this graph. Dropping the fields, applying JsonIgnore or rewriting old statuses would change the normal canonical bytes, including already-saved hashes. No isolated physical erasure is justified by this audit. Keeping the hash-bound historical evidence does not preserve the retired preparation executor. A future physical-erasure requirement would need explicit schema/consent reconciliation; it is not implemented by silently rehashing saved decisions or resetting installed data.

This resolves the privacy/authority question for retained Harness history. It does not claim physical graph removal, acceptance of all other transitional schema, or proposal-wide completion.

## Actual tenant matrix

Sixteen SQLite scenarios cross two current commands (`harness.instructions.update`, `management.policy.update`), retained Pending/Approved history, present/missing current `agents.update`, and new approval/rejection. Pending history uses sparse current configuration; Approved history additionally uses retained full-policy choices with absent overrides. Both remain owned by the actual tenant/Agent.

Each scenario uses canonical login, Agent creation, human delegation and separate executor-role assignment. Before model work it seeds retained data directly into the disposable SQLite row as an installed-data precondition; public/canonical writers are not used to create old history. The sentinel includes private historical draft content and unrelated historical preparation/approval IDs.

Twelve real human read routes must return 200 with none of those private bytes/IDs: Agent list/detail, editor, configuration controls, settings, self-management, versions/adoption preview, isolation, budgets and Skill/Resource editors. The historical approval ID is refused by current detail/decision endpoints with the incumbent 403 unavailable-resource behavior. This is not treated as a current request or proof of prior consent.

The real model reads current Harness/management information and proposes a current exact change. It always waits for a new canonical request. With its own current action permission that request is Review; without it the request is ExceptionalGrant. An old Approved sidecar supplies neither permission nor review. Current approval detail/inbox and all model messages exclude the private sentinel.

A new authorized decision then applies exactly the current instruction/policy change once, or withholds it on rejection. The old preparation bytes, statuses, IDs and hashes remain byte-for-byte unchanged after normal serialization. The current request's action hash also remains unchanged through settlement. Instance revision advances only for the newly approved change; actual mode/instructions match the decision. No outbound provider HTTP occurs.

Instruction authoring retains its existing implementation boundary: the canonical owner operation is `settings.patch`, with the exact semantic `harness.instructions.update` witness. Policy changes use their own canonical management operation. The test verifies the typed witness instead of inventing a parallel Harness effect owner.

## Additional focused evidence

Existing both-store tests reject creating/changing historical preparation in canonical configuration transactions without any revision/audit mutation. Existing unowned native precondition fixtures verify private history preservation across policy/freeze edits; the sparse/full-policy test proves freeze and conservative choices still constrain newer Definition defaults. The current exact Harness consent test, pending SQLite host recreation and schema/guard preservation checks remain separate evidence. These checks do not turn unowned fixtures into executable tenant authority.

Initial compilation corrected an approval-status enum reference. The first eight-case integration fixture expected 404 for historical IDs; the current canonical API uniformly returns 403, and the fixture was aligned. The expanded matrix then passed all eight policy cases; eight instruction cases stopped at a fixture assertion expecting the semantic tool in the canonical owner slot. They were corrected to inspect the existing typed Harness witness. No production behavior was changed for these fixture corrections.

## Focused results

**24 distinct cases passed**, without counting corrected fixture reruns twice.

| Check | Result |
| --- | ---: |
| Actual owned SQLite history matrix: two current commands, Pending/Approved, present/missing primary permission, approve/reject | 16 passed |
| Existing native history policy/freeze preservation, both stores | 2 passed |
| Sparse/full conservative policy and freeze semantics | 1 passed |
| Current exact instruction consent/effect | 1 passed |
| Pending current Harness consent/assessment/effect across SQLite host recreation and refused downgrade | 1 passed |
| Canonical history creation/mutation rejection, both stores | 2 passed |
| Current Harness migration schema/guard preservation | 1 passed |
| Serial solution build, no restore | 0 warnings / 0 errors |
| Affected-document local targets, whitespace and current source census | Passed |

The original API group passed five existing cases and exposed eight unavailable-ID fixture expectations. The expanded matrix passed all eight management-policy scenarios; the eight instruction cases stopped at the semantic/owner-slot assertion. Only those corrected instruction cases were rerun, passing 8/8. The three passing infrastructure cases and five passing existing API cases were not repeated after test-only corrections.

- Build: `dotnet build AgentCore.sln --no-restore --nologo -m:1 -nodeReuse:false -p:UseSharedCompilation=false`; `/private/tmp/tenant-harness-history-final-build.log`.
- Retained history: `dotnet test tests/AgentCore.Api.Tests/AgentCore.Api.Tests.csproj --no-build --no-restore --filter FullyQualifiedName~Retained_Harness_history_stays_private_and_cannot_replace_current_tenant_consent`; `/private/tmp/tenant-harness-history-api-final.log` records the eight passing policy cases and corrected instruction assertions. The final instruction-only selection added `&DisplayName~harness.instructions.update`; `/private/tmp/tenant-harness-history-instructions-final.log`.
- Existing API selection: exact method-name filters for native inert-history preservation, sparse inheritance/freeze, exact instruction consent and pending SQLite Harness recovery; `/private/tmp/tenant-harness-history-api.log` retains their five passing cases and the initial corrected ID expectations.
- Infrastructure: `FullyQualifiedName~Canonical_configuration_commit_cannot_create_retired_Harness_preparation|FullyQualifiedName~Harness_command_migration_preserves_prior_effect_schema_and_guards`; `/private/tmp/tenant-harness-history-infra.log`.
- Static census: `rg` for the four retired record types and `.Preparation` member references under `src/`, alongside the existing retired-executor inventory. Static search supplements the executed consent/projection tests; it does not replace them.

 Browser/UI, physical erasure, live external effects and the complete permission/store/effect acceptance matrix are not claimed by this backend module.
