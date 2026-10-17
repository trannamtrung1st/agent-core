# Tenant operation authorization prerequisite — 2026-10-17

Baseline: `dec8e8286034866ceef756241a80a1452e2b70cf`. This successor implements current Run-bound IAM/review eligibility, persisted stricter tenant policy and the shared tool inspection boundary. The full proposal remains active. It does not enable tenant model/worker execution, typed protected effects or first-class durable approval requests/decisions/grants.

## Delivered behavior

`TenantOperationAuthorizer` resolves actual saved Run/Session/executor/requester references and current active tenant/Instance/Principal plus original requester Session read/chat. It composes the existing IAM evaluator; requester action rights influence review without replacing executor rights. Human-only operations deny. Missing nondelegatable authority denies. Missing delegatable agent authority requires a qualifying human for exceptional eligibility. Agent-owned action authority selects ordinary review through current policy. Qualification checks current active human underlying action plus approve; exceptional eligibility additionally requires grant-exception. Independent review excludes the original requester. No eligible reviewer yields a stable denial.

Core approval metadata classifies read permissions as ReadOnly/None; ordinary mutations as Mutation/WhenRequesterLacksPermission; delete/send/execute and non-read Credential/Browser/Sandbox permissions as Sensitive/Always. Tenant governors can select an equally strict or stronger mode and independent review, or return their own stricter setting to the Core minimum. They cannot edit human-only/delegatable catalog meaning. Policies are loaded with current tenant security, not frozen into Run authority.

`TenantOperationPolicyService` and its owning port require original current human/session and tenant governance. Policy/security witnesses and safe audit commit together; logout without a tenant security change still fences a prepared write. The new same-tenant policy table has composite identity, restrictive ownership, concurrency revision and value/length checks. Aggregate freeze enforces the closed catalog and Core minimum. Both stores retain policy through unrelated resource writes; SQLite reopen retains it.

The production `SessionToolExecutor` factory injects the shared tenant boundary. Async policy and actual execution resolve retained owning Session roots before legacy isolation/management/provider services. Owned Sessions require matching server Tenant/Session/Instance/Run references and current authority; hiding the frame cannot select a global fallback. Current supported paths are `iam.check_access` and `iam.get_effective_permissions`. Their bounded strict arguments cannot select identity, policy or grants. Results expose only safe decision/permission/resource references, no role assignments, other principal inventories or secrets. Effective inspection excludes possible exceptional authority. Unsupported tenant effects fail closed even with an old-style exact-tool approval object. Decisions are current inspection results, not approval records, effect capability tokens or one-time grants.

IAM tool descriptors are context-only and conditionally offered for trusted matching tenant execution context with a tools-capable model. This registration does not wire tenant worker dispatch or inject the remaining Runtime judgment projection. Legacy non-tenant tool/runtime behavior retains its existing focused regression evidence; obsolete global authority remains scheduled for final removal.

## Executed verification

| Check | Result and observed behavior |
| --- | --- |
| Initial integrated shared executor flow | 2 passed through actual saved tenant Runs and both stores. Missing agent authority returns exceptional eligibility despite an Owner requester; an exact agent grant allows read; revocation changes the next check immediately. Only current executor-granted permissions appear in effective results; forged principal and tenant frames deny. |
| `TenantOperationAuthorizationTests` | 14 passed. None/WhenRequesterLacksPermission/Always outcomes, requester rights without role merging, ordinary versus exceptional eligibility, human-only/nondelegatable denial, independent active human qualification, underlying permission revocation, requester suspension, policy minimum/CAS/reopen, preservation through an unrelated Instance write, and policy logout/revoke/competition/cancellation races without false audit. |
| Infrastructure filter `TenantOperationAuthorizationTests\|TenantRunAdmissionTests\|IamIdentityIntegrationTests\|Tenant_Session_resolves_owned_publication\|CanonicalStartupMigrationTests\|CanonicalSchemaRefusalTests` | 63 passed. Includes the final bounded IAM parser, affected identity/scoped-role/Run/owned-publication and canonical schema/refusal regressions. |
| Final boundary/sensitive-authority check | 2 passed. The shared tenant executor rejects an old-style matching tool/action-hash approval object before legacy service access. Missing coarse browser authority cannot be exceptionally delegated; a current agent-owned browser permission still requires ordinary review under the Sensitive minimum. No browser effect is executed. |
| Application filter `ToolRegistryPolicyTests\|AgentRunDurabilityTests\|SessionRuntimeCompletionTests\|BackgroundCompletion` | 69 passed. Context-only IAM offers reject absent/mismatched Session and non-tools contexts. Existing Synthetic user/child/report, retry and completion/deadline workflows remain functional. This is existing execution regression evidence, not tenant model execution. |
| API filter `TenantSessionAccessTests` | 3 integrated Synthetic HTTP journeys passed. Governance cookie/CSRF, Core-minimum and invalid-mode denial, policy/security stale witnesses, ignored actor/tenant/catalog JSON fields, same-Account separate-tenant policy, anonymous/old-token denial and safe catalog metadata. Existing Run/lifecycle journeys also prove the production DI executor rejects an unframed owned Session. |
| API build and EF model | Build passed with zero warnings/errors. Final `has-pending-model-changes --no-build` with Infrastructure project/startup reports no model changes. Existing EF tools 10.0.5/runtime 10.0.12 version warning remains; generation/comparison succeed. |
| Documentation validation | 12 Markdown files, 800 local links, 212 fragment anchors, balanced fences and 20 complete JSON examples checked without errors. `git diff --check` passed. |

An early persistence test attempted to rename a Session with queued work and correctly hit the existing `session_in_use` guard. The test now verifies policy retention through a separate Instance creation; production lifecycle guards were preserved. Inspection uses the canonical owning-resource/IAM paths and no hosted provider. All runtime/store execution used disposable fixtures.

## Follow-up consistency review

Review baseline: `fdf3ca961f281b34af6656ebacc1bc13ed150f59`. The review found that legacy approval preparation could run before the shared execution gate. A regression reproduced the defect with both stores: an actual tenant Session received a legacy Synthetic email preview containing a private draft recipient. Both cases failed before the correction (`/private/tmp/tenant-approval-preparation-repro.log`).

The common preparation dispatcher and direct Email, Drive, management and maintenance preparation methods now check tenant ownership before preview/service/provider reads. Automation preparation uses the same fence. Missing tenant frames still resolve the actual retained Session owner; explicit tenant frames deny even without the boundary service. The management dispatcher carries its actual Session ID to the direct method. No tenant approval record or effect authority is created.

The corrected integrated regression passed for both stores. Missing, matching, tenant-less and forged tenant frames produced `forbidden` / `tenant_effect_unavailable`, no preparation and zero observed provider draft reads. Drive, management, maintenance, generic filesystem and Automation paths reached that fence before their service-specific checks. The same draft still prepared through the legacy path with exactly one observed draft read. This is preparation-boundary evidence, not tenant execution or durable approval acceptance.

Final focused checks (all `dotnet test`, `--no-restore --nologo`):

| Project and `FullyQualifiedName~` filters | Result |
| --- | --- |
| Infrastructure: `TenantOperationAuthorizationTests\|EmailAccountIntegrationTests` | 35 passed, including 2 new both-store preparation cases; `/private/tmp/tenant-approval-preparation-regression.log`. The command also named `DriveToolApprovalTests`, which matches no current test class; actual Drive coverage is the API journey below. |
| Application: `ToolActionPreparationTests\|EmailToolTests\|ToolRegistryPolicyTests\|AgentRunDurabilityTests\|SessionRuntimeCompletionTests\|BackgroundCompletion` | 88 passed; `/private/tmp/tenant-approval-review-application.log`. |
| API: `TenantSessionAccessTests\|SelfManagementOperationTests\|SelfManagementRemediationTests\|SelfManagementCompletionTests\|DriveApprovalJourneyTests\|IdentityMaintenanceJourneyTests` | 40 passed; `/private/tmp/tenant-approval-review-api.log`. Synthetic HTTP journeys preserve legacy exact approval, management/remediation, Drive target validation and identity-maintenance workflows alongside canonical tenant policy/Run/lifecycle checks. |

Documentation checks passed for the four changed Markdown files: 237 local links, 66 fragment anchors, balanced fences and two complete JSON examples. `git diff --check` passed. No application UI changed in this correction. No milestone acceptance or full-proposal completion is claimed.

## Remaining full goal

Tenant worker claim/dispatch/recovery, Runtime mailbox/Hub/audio and trusted requester judgment wiring, typed per-effect target/capability/credential/recipient revalidation, autonomous/continuation ingress and other owning resource/effect families remain required. First-class durable ApprovalRequest/Decision/OperationGrant, outbox/continuation, exact action/hash/witness binding, claim/consume/reconciliation and root-level storm suppression remain pending. The current eligibility check creates no pending approval and executes no protected operation. Governance UI, complete tenant Chat/Access/Approval UI, Sandbox/decommission, old-authority/schema removal, reset/bootstrap consolidation and complete security/browser/visual acceptance remain open.

No user-data reset, deployment/host restart, CI, hosted provider call or milestone acceptance was requested or performed.
