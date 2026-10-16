# Multi-tenant IAM and durable approvals delivery ledger

Owner request: implement the final proposal at `local/proposals/agent-core-multitenant-iam-approval-final-proposal.md` with the develop skill. Baseline: `38b8288230854b9044b69100c4f99bb4c259d2d1`; clean checkout. No CI requested. Existing Real application/data are not disposable and must remain untouched. No application milestone acceptance is implied.

## Status and sequence

| Phase | Deliverable / gate | Current status |
| --- | --- | --- |
| 0 | Source/route/store/effect inventory and ownership | Baseline inventoried; [boundary obligations](multitenant-iam-boundary-inventory-2026-10-16.md); no unowned boundary category |
| 1 | Full UX contract and internal review before UI implementation | [UX specification](../19-multi-tenant-iam-ux.md) complete; internally reviewed against incumbent layouts and pinned brief; foundation UI evidence is in Phase 7; full successor browser acceptance remains pending |
| 2 | Identity/authentication + canonical persistence + same-Account two-tenant negative tests | Identity/store/HTTP foundation implemented and focused tests passing; full resource ownership and Hub authentication integration still pending |
| 3 | One scoped IAM engine, live authority, human-only reserve and service integration | Closed catalog, builtin/custom roles, ownership-validated member/role/registered-agent/definition resource grants and access services implemented; actual resource/tool/background service integration pending |
| 4 | Every tenant resource/store/effect cut over with negative tests | Canonical actual Agent Instance creation/read/inventory/archive/restore transactions implemented and locally verified; canonical tenant definition draft/publication authoring and Instance creation from owned publications implemented; resource/evaluation child APIs implemented; tenant UI, other Instance/resource/effect boundaries pending |
| 5 | Trusted Run executor/requester/root lineage and inspection tools | Pending |
| 6 | Durable approval/outbox/claim/revalidation/exact grants and bounded loops | Pending |
| 7 | Canonical tenant UI/auth/account/IAM/approvals/Chat | Owner-prioritized foundation UI implemented and locally verified for auth/account/Members/Roles/Audit; Agent Access, resource access, durable Approvals and tenant-aware Chat remain pending |
| 8 | Remove old authority, canonical reset/bootstrap docs, focused security/browser checks and Impeccable/design sync | Pending |

Do not mark the goal complete or describe the application as multi-tenant until all protected boundaries, durable approval execution and designed UI journeys are verified. Intermediate source is not an authorized production release. No live storage reset, deployment/restart or CI has occurred.

## Chosen contracts

Backend term Tenant, UI Organization. Global human Account → unique tenant User → Principal; tenant Agent Instance → own SystemIdentity → Principal. Explicit System/Tenant/Resource assignment scopes. No implicit platform superuser, requester-role intersection, temporary roles, Task table or owner-token fallback. Secure same-origin HttpOnly human sessions plus CSRF; purpose-specific OAuth/webhook authentication stays separate. Canonical tenant routes `/t/{tenantId}/…` and `/api/v2/tenants/{tenantId}/…`. Authoritative UX owns screen structure and states.

## Verification record

Initial inspection only: `git status --short` clean; HEAD matches inspected proposal; route/store/tool source inventory generated. Impeccable context loaded successfully. No runtime acceptance or test result is claimed at this stage.

## Identity foundation evidence and remaining gate

- `dotnet build src/AgentCore.Api/AgentCore.Api.csproj --no-restore --nologo --disable-build-servers -p:UseSharedCompilation=false -m:1 -v minimal`: passed, zero warnings/errors at initial HTTP foundation. Subsequent focused test builds compile their affected source.
- `dotnet test tests/AgentCore.Infrastructure.Tests/AgentCore.Infrastructure.Tests.csproj --filter FullyQualifiedName~IamIdentityIntegrationTests --no-restore --nologo --disable-build-servers -p:UseSharedCompilation=false -m:1 -v minimal`: initial six workflows passed on both stores, including migrated SQLite/reopen. Expanded password/stamp and global Account suspension coverage passes: **8/8**, zero skips, both stores, generated migrations/reopen.
- `dotnet test tests/AgentCore.Api.Tests/AgentCore.Api.Tests.csproj --filter FullyQualifiedName~IdentityAndTenantAccessTests --no-restore --nologo --disable-build-servers -p:UseSharedCompilation=false -m:1 -v minimal`: five full Synthetic HTTP/startup workflows and a separate real identity-composition SQLite host-restart workflow passed (**6/6**). Missing bootstrap configuration failed startup; a valid old owner capability did not authenticate canonical Account APIs; missing CSRF denied login; valid login issued Secure/HttpOnly/SameSite Strict cookie; invitation registration joined only A; B reads and system escalation failed; removal blocked the next tenant request; logout cookie replay returned 401. A first assertion expecting only `no-store` was corrected to test the semantic NoStore flag because cookie middleware also correctly adds `no-cache`.
- Initial VSTest execution was blocked by sandbox loopback socket restrictions (`SocketException (13)`); the same focused commands ran with approved local execution. The first shared-compiler build stalled; the independent build with shared compilation disabled passed. No hosted CI, provider calls or user-host restart occurred.

The foundation's canonical `/api/v2/auth`, `/account`, `/system`, `/tenants/{id}` namespaces use cookie authentication/CSRF and current membership. **Old resource routes, owner capability, local profile, tool/Hub admission and live approvals have not yet been cut over.** Do not deploy this intermediate source or claim tenant isolation. New startup requires explicit identity bootstrap; existing user databases are not reset or upgraded silently. The intermediate IdentityFoundation migration must be consolidated with the final full tenant schema before delivery. Global human Account principals exist for Account/system attribution; registration assigns them no platform role.

## Continuation notes

Next required work is the tenant ownership/resource integration needed to finish Phase 2 and proceed through Phases 3–8. Do not skip the full proposal, claim completion, or treat the currently passing identity tests as Session/Hub/tool isolation. The owner subsequently prioritized frontend polish; the implemented foundation UI and measured limitations are recorded in the [access UI report](access-ui-polish-2026-10-16.md). The complete UX contract remains in docs/19. Existing owner resources remain unscoped and old live approval authority still exists. The full boundary source inventory retains its baseline obligations rather than claiming conversion.

Foundation code lives in Domain/Security, Application/Security + Ports/Iam, Infrastructure/Security, Api/Security. New identity services have no ASP.NET/EF dependencies in Application/Domain; human authentication and endpoint mapping are API concerns. Main DbContext adds the normalized identity model and an intermediate migration; all test databases are new temporary paths. Cookie key ring is isolated under configured credential roots. Do not use the Real application's populated database for tests or a reset.

Working native check uses `--disable-build-servers -p:UseSharedCompilation=false -m:1` because an initial default shared-compiler build stalled. VSTest loopback needs approved local execution under the sandbox. That initial foundation checkpoint made no frontend execution claim; the later owner-prioritized pass below owns browser evidence. CI remains unrequested. Final source still needs canonical baseline consolidation, obsolete authority removal, bootstrap/launcher/test fixture updates across supported profiles and bounded Impeccable/browser acceptance.

Final foundation rerun: **8/8 Infrastructure scenarios** and **6/6 API scenarios**, zero skips. `git diff --check` and relative-file-link/fence validation across fourteen changed Markdown documents pass. This is a verified local checkpoint, not full proposal completion or milestone acceptance.

## Owner-prioritized frontend pass

The owner requested frontend/Impeccable consistency after foundation commit `baf9a3e7`. Auth/account/organization selection, member/invitation access, role authoring/inspection and audit now have real canonical UI routes, responsive/keyboard handling and focused browser evidence. [Verification](access-ui-polish-2026-10-16.md) distinguishes this delivered slice from the original proposal's unimplemented resource/agent/Hub/approval requirements. The original goal remains active; no insecure intermediate deployment or full tenant-isolation claim is authorized.

## Resumed ownership enforcement

After the owner-prioritized UI/review checkpoint `587806f9`, resource-scoped IAM checks, grants and access inspection now prove target ownership from current tenant security records. Fabricated/cross-tenant scopes and mismatched operation families fail closed. Registered Agent Instance IDs cannot span tenants, enforced in both stores and by a global SQLite unique index. The [resource-scope verification](tenant-resource-scope-verification-2026-10-16.md) records current evidence. This is a dependency toward the full resource cutover, not a substitute for it: the subsequent canonical Agent Instance create/read/lifecycle transaction is recorded below; delete/decommission, definitions/publications and all other owning stores/HTTP/tool/Hub paths still require conversion. Unsupported resource families have no permissive fallback. The active goal remains the full proposal.


## Actual tenant Agent Instance transactions

The canonical tenant-agent service now creates the existing Instance and initial Skill state with its unique backing SystemIdentity/Principal, tenant security revision and audit atomically. Scoped inventory filters before count/order/page; cross-tenant targets deny. Archive/restore atomically changes Instance lifecycle and agent identity status while retaining IDs/grants. Global Account suspension is rechecked at commit; stale security/Instance witnesses do not leave an orphan or success audit. A restrictive backing foreign key and matching InMemory retention prevent identity-only registration and unscoped backing deletion. Old lifecycle writes reject owned roots. [Focused evidence](tenant-agent-lifecycle-verification-2026-10-16.md) records current local tests.

This remains a partial resource cutover. Ownership is normalized through SystemIdentity rather than a second Instance store; all other access paths still need to use that ownership. Old Instance settings/resource/reads and owner APIs, the remaining definition child/UI/runtime boundaries, delete/decommission, Sessions/Runs/Hub/tool admission, trusted execution lineage, durable approvals and tenant-aware agent/Chat/approval UI remain pending. Lifecycle policy/Sandbox reconciliation follows the durable transaction; full worker interruption and tenant isolation are not accepted by these tests. The goal remains active for the full proposal; no user-data reset, deployment/restart or CI is authorized by this checkpoint.


## Tenant definition ownership and publication

After `9c33627e`, canonical definition create/read/edit/publish/fork/deprecate uses a real tenant-owned root over existing authoring records. Definition scopes resolve through that owner, including checks inside assignment transactions. Global stores exclude owned drafts/publications/resource/evaluation content. Publication preserves candidate validation, required evidence and resource-byte gates, immutable prior versions and atomic consume/security/audit semantics; Account suspension and competing CAS are fenced. Canonical Instance creation can pin an exact active owned publication with separate agent-create and definition-read authority. [Focused evidence](tenant-definition-verification-2026-10-16.md) records runtime/store results.

The subsequent canonical resource/evaluation child API implementation is recorded below. Still required: builtin-template fork content, draft/logical deletion, complete tenant authoring UI, previews/standalone resource IAM, and tenant-aware runtime definition/resource resolution. Other Instance settings/resources/decommission, all Session/Run/Hub/tool/background effect boundaries, trusted execution lineage, durable approvals, old authority removal, final baseline consolidation and full UI acceptance remain pending. Unscoped definition readers intentionally cannot resolve tenant publications; this is fail-closed intermediate source, not full application deployment acceptance. The original full goal remains active.


## Tenant definition resource and evaluation workflows

Canonical child APIs now upload bytes or reuse an owned draft binding, mutate bounded resource batches/scenarios atomically with draft/security CAS and audit, download owned exact-version content, and execute the existing bounded offline evaluator outside persistence locks. A caller cannot attach another tenant's content by hash. Evaluation completion rechecks its exact draft/scenario/fingerprint, current actor/Account and revision; commit order makes later failure supersede an earlier same-timestamp pass. Saved-content mutations also require read authority, closing mutation-only response disclosure. [Focused verification](tenant-definition-children-verification-2026-10-16.md) records current evidence.

This advances the full definition cutover, without claiming the entire proposal. Tenant-aware editor/child UI, builtin-template fork content, draft/logical deletion, standalone resource IAM/previews and runtime resolution remain pending, alongside other Instance/resource stores, all Session/Run/Hub/tool/background effects, trusted execution lineage, durable approvals, old authority removal, final baseline consolidation and full designed UI acceptance. No user data/reset/restart/deployment or CI was performed. The original goal remains active.
