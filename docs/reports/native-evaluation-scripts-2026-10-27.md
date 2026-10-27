# Executable Sandbox Scripts — local delivery verification

Date: 2026-10-27. Baseline: `a213ec2d92ab4a316d4c41280b2152755321c552`.

Initiative 7 is ready for closure for the requested local implementation scope. Native Sandbox Scripts execute through the existing OpenSandbox adapter, produce trusted structured findings and retain no-replay cleanup/recovery ownership. Initiative 8 — Session Goals may consume these immutable references through the existing owners. No new application milestone, hosted CI or production certification is asserted.

## Implementation and ownership

- Domain: immutable Script configuration gains saved Sandbox profile, command timeout and permitted evidence kind. Findings reference a distinct `EvaluationSandboxExecution`.
- Application: `EvaluationSandboxAuthority`, `EvaluationSandboxRules` and the evaluation service bind one attempt/check to exact Script/Suite/source/profile/input identities and an incumbent exact Sandbox Review. The tenant Run owner now actually supplies the evaluation service to its existing coordinator and disposes the attached sweep on shutdown.
- Infrastructure: the existing `OpenSandboxService` performs create, mount attestation, non-root command execution, storage enforcement and generation retirement. Both IAM stores implement atomic native reservation, CAS lifecycle changes and technical unavailable settlement. A forward migration adds the native table and extends guarded approvals without rewriting historical evidence.
- API/UI: safe native evidence and cancellation endpoints; existing Script/Suite editors, approval inbox and evidence drawer expose configuration, exact Review, execution, cancellation, immutable results and recovery. Ant Design v6 and existing spacing/draft/feedback owners remain unchanged.
- Deployment: the compatibility server accepts only exact evaluation mounts with matching identity metadata and advertises `evaluationIsolationFencing`. Older hosts fail closed with actionable upgrade evidence.

## Authority and security decisions

A saved Script or Evaluation Attempt grants no native authority. Manual evaluation keeps the original authenticated human session as a witness; terminal hooks keep trusted source provenance. The source Instance's agent principal is independently authorized as executor. Both actor and executor need current evaluation, Script, Sandbox and source read permissions. Registry use requires current credential-use authority. Exact existing `ApprovalKind.Review` is always required and never supplies an exceptional grant or missing execution permission. Current reviewer qualification and independent-review policy are checked again.

The operation pins exact Script ID/version/hash, Suite/check, source Run revision/fingerprint, profile revision, input kind/digest and fresh execution/claim identity. Dispatch and conclusive settlement recheck original human login in the store transaction as well as current tenant security revision. Watchers recheck current authority during execution. Source Run/Session IDs on the approval are read witnesses only: native signals never wait, resume or mutate that historical Run, and the old live-Run command path rejects the native marker.

The provider's generation/session metadata carries the fresh execution GUID solely for retirement correlation. No Session, Activation or AgentRun is created. No source Run claim, tool receipt, credentials or effect identity is borrowed. No API-host shell, separate scheduler, alternate engine or Profile-verification executor is introduced.

Only bounded immutable JSON (64 KiB UTF-8) is mounted read-only at `/home/input.json`; `/working` is disposable scratch. Original working directories, Spaces, private mounts, attachments and secrets are not mounted. Egress is denied. Profile initialization scripts do not run. CPU is capped at one, memory at 512 MiB and scratch at 16 MiB, each also capped by the selected profile. Commands run for 1–30 seconds; the profile must support the 120-second remote lifetime. Storage enforcement remains sampled rather than a kernel quota.

## Supported contract and exclusions

Manual, AfterRun and OnFailure native Scripts assess readable terminal Runs. Inputs are metadata, exact saved Run output, or pinned admitted task input. Missing/unreadable historical input yields Inconclusive; current workspaces are never substituted. BeforeRun and Publication native configuration is explicitly rejected.

Confirmed exit 0 yields Passed and confirmed nonzero yields Failed only with current authority and confirmed termination/cleanup. Timeout, cancellation, lost result, revoked authority, interrupted dispatch or unconfirmed cleanup yields Inconclusive. Evidence retains safe identities, exit, duration, reason and bounded-output digest; raw logs and settled input JSON are not retained. A passing command assesses only its saved assertion.

Durable reservation precedes effects. `Reserved → Dispatching` CAS owns one create/command path. Duplicate requests converge; unknown dispatch is never replayed. Restart retires the original identity. Cleanup retries remain durable and cannot promote unknown findings to Passed. When current source/login authority is unavailable at finding publication, technical recovery can record only Indeterminate/Inconclusive.

## Executed checks

All commands below are focused local checks. Optional GitHub Actions and the full CI suite were not dispatched or run.

| Check | Observed result |
| --- | --- |
| `dotnet build src/AgentCore.Api/AgentCore.Api.csproj --no-restore --nologo --disable-build-servers -m:1 -p:UseSharedCompilation=false` | Passed, zero warnings/errors |
| Infrastructure filter `Native_evaluation_executes_exact_reviewed_script`, opt-in against actual local OpenSandbox | 2 passed: InMemory and SQLite |
| Infrastructure filter `Evaluation_sandbox_migration` | 1 passed; real legacy Review, Script, trigger/index and FK preservation |
| Focused unified evaluation/hook/migration regression matrix | 41 passed |
| Application filters `EvaluationBindingValidationTests` and `AgentRunCoordinatorTests` | 22 passed |
| API filter `Owned_resource_upload_evaluate_publish_and_read_survive_fork_without_hash_or_tenant_bypass` | 1 passed |
| `npm run test -- --run src/features/access/useEvaluationLaunch.test.tsx` | 5 passed |
| `npm run build` in `web/` | Passed; existing large-chunk advisory remains |
| Python `test_sandbox_compatibility.py` / `test_sandbox_retirement.py` | 10 / 11 passed |
| Playwright MCP, actual disposable Synthetic API with SQLite and native Sandbox | Create, configure, Review, execute, inspect and cancellation journeys passed |

Native test opt-in uses `AGENTCORE_OPENSANDBOX_TESTS=1` and trusted `AGENTCORE_OSB_PROOF_URL`, `AGENTCORE_OSB_PROOF_KEY`, `AGENTCORE_OSB_PROOF_MOUNT_ROOT`, `AGENTCORE_OSB_PROOF_IMAGE`. Keys were read from the existing local deployment without printing their values. The workload used installed immutable image `sha256:985c3477b1bcf54385a33c048e465ed6bf7f590d09e9dd4bbfae06d62af91561` on the authenticated local server. Each fixture had its own UUID mount root. Profile verification qualified setup only; native findings came from independently reviewed Script commands.

### Native scenarios, on both stores

| Action and expected behavior | Observed |
| --- | --- |
| Review exact manual Script and execute exit 0 | Passed, exit 0, confirmed retirement |
| Create a newer Script/Suite while old terminal hooks retain v1 pins | AfterRun and OnFailure both Passed on their exact old references; required verification became Verified |
| Execute intentional `exit 7` | Failed, exit 7, confirmed retirement |
| Require missing historical Run output | Inconclusive, actionable missing-input reason, no native execution |
| Repeat request and reenter execution | Same attempt returned; settled dispatch denied, no second command |
| Supply the historical source claim before independent Review | Denied |
| Invoke through a foreign tenant | Denied |
| Revoke executor authority after approval but before dispatch | Inconclusive, no dispatch claim or provider create; input cleared |
| Request with no executor authority | Inconclusive, no native admission |
| Inspect isolation with fail-fast assertions | Read-only input, fresh scratch, source sentinel inaccessible/unchanged, Docker socket absent, provider API-key variables absent, external TCP connection denied |
| Run two native operations concurrently | Both Passed, distinct provider identities and scratch, confirmed cleanup |
| Exceed one-second command limit | Inconclusive, confirmed retirement |
| Cancel after real command starts | Inconclusive/Cancelled, confirmed retirement |
| Crash after real command completes but before result/cleanup commit; reopen service owner and expire claim | Same durable execution/claim, exactly one command, Interrupted/Inconclusive, confirmed retirement and cleared input |
| Sign out original human before settlement | Native Inconclusive; attempt Indeterminate/Inconclusive, confirmed retirement |
| Inspect factual source after all observations | Source remains Failed at its original revision; original workspace sentinel unchanged |

### Actual browser evidence

The app ran on task-owned ports 5098/5198, with Synthetic providers, a disposable SQLite database and the existing authenticated native Sandbox server. No hosted inference was used. Supporting profile/agent/IAM fixture data was prepared through existing HTTP endpoints; Script creation, Suite/check creation, Script versioning, evaluation request, exact Review, evidence inspection and cancellation were performed through visible controls. The cancellation Suite v2 pin was fixture setup through the existing Suite API.

The real UI journey produced Passed attempt `01a17bcf-2917-73fe-8783-77ae06c37c9d`, execution `01a17bcf-2a22-7761-99fb-d6df5dab0c04`: exact Script/Suite v1, exit 0, 3406 ms, termination confirmed. Source Run `01a17bc2-0810-7c2d-9f7c-0d3bfc20edb2` remained Completed at revision 5 with its original Synthetic response. Creating Script v2 retained the selected profile/input and saved its 20-second limit.

The running v2 cancellation journey produced attempt `01a17bd5-9f63-7591-a104-458c59e2275a`, execution `01a17bd5-9f69-72fb-bac5-84dbcd713431`: UI cancellation requested, then Cancelled/Inconclusive, unknown exit, 2372 ms and confirmed termination. An execution that finished before a later cancellation retained its saved Passed result; UI feedback now distinguishes that race.

Desktop 1440×1000 and narrow 390×844 evidence views were inspected visually and through geometry checks; final narrow document and drawer widths were both 390 with no horizontal overflow. Keyboard focus remained within the drawer and Escape closed it. Empty catalogs, disabled unsupported hooks, preserved version drafts, loading and unavailable-service feedback were exercised. A deliberate 600 ms/403 native-read fault displayed Loading access then the permission-denied message; removing the fault and refreshing restored real saved evidence. An earlier genuine uncertain result remained Inconclusive after refresh without replay. No runtime JavaScript error remained on the successful journey; StrictMode/navigation aborts and intentionally injected failure requests were distinguished from application failures. Actual browser zoom and physical-device certification were not asserted.

## Review findings fixed

1. Extended the SQLite approval constraint/Space guards with a closed native purpose, preserving all legacy guards/indexes and exact payloads; corrected the readonly enum wire representation.
2. Connected the tenant execution facade to the existing attached evaluation sweep; approvals now continue in the actual host rather than only direct service tests.
3. Fixed native approval Script-reference projection to plain text and retained requester/reviewer attribution; linked approval details back to exact evaluation evidence.
4. Corrected dispatch revalidation to tolerate its own provider-ID persistence while retaining immutable identity, claim, phase and cancellation fences.
5. Added transactional original-login checks, initial-finding readiness, both-store admission-capacity consistency and bounded pending-authority rechecks.
6. Kept timeout/unknown/cleanup results fail-closed, made cancellation settlement races converge safely, and preserved technical unavailable settlement after logout.
7. Disabled BeforeRun in native authoring, used generic Script/Pending labels, and avoided showing ordinary active-environment cleanup ownership as a recovery warning.
8. Strengthened native proof scripts with fail-fast assertions and real network/environment checks. Harmless public image signing keys and provider configuration were not mistaken for injected credentials.

## Migration, documentation and readiness

Migration `20261027020000_EvaluationSandboxExecutions` preserves current durable data. Stop writers and back up the installed database/protected configuration before upgrade; `migrate-evaluations` accepts the UnifiedEvaluations predecessor. Restore the matching binary/database backup on failure. Native evidence prevents lossy downgrade. Deploy/reload the updated compatibility server before native execution; recheck active workloads first. The existing local server was restarted once with explicit user authorization after authenticated inventory and daemon checks both reported zero workloads.

Canonical architecture/interfaces, backend/frontend/API, persistence, testing/operations, README, roadmap, implementation plan and TODO are synchronized. Historical verification reports are unchanged. No shared visual tokens or second UI kit were introduced, so the design-system presentation contract is unchanged.

Review verdict: requirements, ownership, current authority, isolation, lifecycle, installed-data preservation, responsive UI, bounded evidence/audits and local verification are satisfied for this scope. Remaining limits are the explicitly rejected hooks/input sources, incumbent sampled storage enforcement, trusted local Docker/OpenSandbox deployment, optional CI, production rollout and broader live-provider/device certification. These do not represent deferred native execution work in Initiative 7.
