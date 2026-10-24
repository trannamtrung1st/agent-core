# Tenant IAM and Account original-human authority — 2026-10-24

Preimage: `785d7939`. This completes the next coherent security-owner module; the original proposal’s remaining method/effect and rendered design gates stay active.

## Observed failure and change

The actual authenticated Owner HTTP journey logged out its original session immediately before a role create entered `IIamStore.TrySaveTenantAsync`. Before the fix, it returned **201 Created**, where the regression expected refusal. The deliberately failed baseline is `/private/tmp/tenant-iam-human-baseline-20261024.log` and `tests/AgentCore.Api.Tests/TestResults/tenant-iam-human-baseline-20261024.trx`; it is not passing evidence.

Canonical member/invitation/role/assignment handlers now derive the original human server-side and pass it through the existing Application owner to the security transaction. Both stores validate the original tenant Account/session/User/Principal under the owning lock/transaction, beside active actors, exact security revision, real resource references and final-Owner checks. Authenticated invitation acceptance instead proves the existing Account Principal/session before creating that Account’s tenant User. It cannot borrow another Account or invent an existing tenant membership. Public registration retains its exact invitation-proof path and issues no login.

Password changes retain explicit current-password verification and security-stamp CAS, adding original-login validation inside the Account transaction. Account metadata/membership reads repeat the original Account/session/Principal after I/O. Tenant access, member labels, invitations, roles, permission catalog and self/other checks repeat the original human and unchanged security revision after capture. A retired login returns 403 `initiator_unavailable` without releasing the captured projection or committing the command; its next cookie request returns 401. A fresh login can explicitly retry current witnesses. Password recovery must supply the then-current password, rather than replaying stale authentication material.

The physical Account/session/Principal validator is extracted unchanged from the existing System administration helper. System administration/operator permissions remain independently mandatory. The unused production `AccountAdministrationService` and DI registration are removed; its unchanged principal-only native fixture helper moves into Infrastructure tests. Raw native bootstrap/setup/data contracts confer no HTTP authority. No permission classification, approval default, autonomous Run authority, provider, DTO, route, schema or frontend source changes.

## Verification

**158 distinct cases pass, zero failures/skips:** 92 new HTTP race/recovery cases (5m20s), 41 existing HTTP/restart/System regressions (2m40s), and 25 both-store identity/privacy regressions (1m4s). All three processes are terminal with exit code 0. The intentionally failed one-case baseline is separate and is not included in this passing total.

The final serial solution build passes with **zero warnings/errors in 49.89 s**. Its log is `/private/tmp/tenant-iam-human-build-20261024-r2.log`. The initial build exposed an Application-internal authorizer method used from API; the correction keeps the public API authorizer call and final original-human/revision fence. That failed build is not counted as passing evidence. No product C# changed after the final passing build.

The new group uses real HTTP/cookie/CSRF, actual shipped composition and isolated InMemory/SQLite stores. Its decorators forward actual identity proof unchanged and schedule actual logout/password rotation at the final pre-transaction boundary or after source capture. Private temporary roots/keys/databases are disposed; user hosts and data are untouched.

- **40 mutation journeys:** ten operations × both stores × logout/password rotation: invite/revoke, member suspend/remove, role create/update, assign/revoke, authenticated acceptance and password change. Each compares the saved tenant graph/audit and the Account value produced by the interleaved retirement; refusal causes no additional change. Explicit fresh-login retry succeeds once; password retry additionally proves the intended new password works.
- **52 read journeys:** eleven surfaces × both stores × both retirements, plus late Account-label capture for ordinary/searched member lists. Captured private member/role, access decisions, Account metadata and membership lists are withheld. Explicit fresh-login read succeeds without changing tenant state or audit.

Reproduce with the serial solution build and `dotnet test <project> --no-build --no-restore --nologo --filter '<selection>'`:

| Project / selection | Qualified scope |
| --- | --- |
| API: `FullyQualifiedName~IamHumanAuthorityTests` | New direct race/refusal/recovery group |
| API: `FullyQualifiedName~IdentityAndTenantAccessTests\|FullyQualifiedName~IdentitySqliteRestartTests\|FullyQualifiedName~System_administration_\|FullyQualifiedName~System_operator_` | Existing identity/CSRF/invitations/two-tenant/resource grants, SQLite cookie restart, System catalog/status/operator permissions, forgery/CAS/rollback/final operator and original-human retirement |
| Infrastructure: `FullyQualifiedName~IamIdentityIntegrationTests\|FullyQualifiedName~SystemBrowserPrivacyTests` | Both-store bootstrap/identity/Owner races and isolation; shared System-human helper/privacy transaction, raw-permission and deployment-ceiling regression |

Logs: `/private/tmp/tenant-iam-human-{cases,api-regressions,infra-regressions}-20261024.log`; corresponding `tenant-iam-human-*.trx` files live in each project’s `TestResults` directory. Source navigation/hash checks are recorded separately from runtime cases.

## Source reconciliation and remaining scope

The [method index](tenant-store-method-source-index-2026-10-24.json) adds **23 dispositions**: sixteen trusted identity/security data slots and seven canonical System administration/operator slots. Account lookup/password verification, native one-use bootstrap, no-role registration, current session/stamp validation, owner-qualified security reads/writes, active membership filtering and bounded native audit are traced to their current consumers. System collection reads expose safe metadata under original-human/current System permission; four distinct System mutations use exact current role/audit, applicable target stamp/revision or new-root uniqueness, and final-operator/Owner guards. Platform grants still imply no tenant-private access. Source dispositions are not universal positive-effect proofs.

Current totals are **92 interfaces, 544 callable slots, 547 syntax bodies (three explicit inherited defaults), 1,544 call/reference sites and 1,712 production C# files**. **150 slots are reviewed and 394 remain; 48 of 180 uncalled slots are reviewed, leaving 132.** Source SHA-256: `1a4d949f379c9b49cce0acb617a11ecb3b91a21ed1856d436039f4873c73273e`. The read-only Roslyn analysis retains nine expected missing generated-regex diagnostics; the actual solution builds successfully. Earlier reports retain their source hashes/counts.

The [original acceptance matrix](tenant-proposal-acceptance-matrix-2026-10-24.md) still owns full closure. No final rendered UI/design, live provider/customer, physical device/audio, multi-instance or CI acceptance is claimed. No installed-data reset, Real-host restart, push, PR or `.agents` edit occurred; the Impeccable launcher was not rerun.
