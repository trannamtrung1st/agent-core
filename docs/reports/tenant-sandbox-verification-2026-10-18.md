# Tenant disposable Sandbox verification — 2026-10-18

This successor to `1f0b475f43141d88f9e5c6fee2c149434629cb0c` advances the proposal's Phase 4 Sandbox boundary from the reusable catalog to canonical disposable full/image verification and exact cleanup recovery. It does not accept the complete proposal, a milestone or live Docker compatibility. Instance selection/setup verification, Session generations/commands/process/reset/mounts and cleanup, the Sandbox frontend, remaining resource/effect families, Governance/platform, voice/rich Chat delivery, live input arbitration, autonomous origins and final obsolete-authority/schema consolidation remain open. The earlier SQLite preference reliability follow-up remains active in the [delivery ledger](multitenant-iam-delivery-2026-10-16.md).

## Implemented boundary

Canonical cookie/CSRF profile routes now provide exact/latest verification reads and explicit POST `verify` or `check-image`, using strict security/profile revision witnesses. Current exact `sandbox.read/manage` and independent scoped Credential read/bind/use precede stale-write reporting and protected source dispatch. The service reserves one ten-minute attempt/security/audit transaction before calling the existing SDK adapter outside the transaction. Receipt ownership carries the original Account/session/Principal, exact tenant/profile revision, source epochs and opaque host fingerprint. Neither profile catalog access nor compatibility evidence grants agent execution authority.

Full verification and image-only checks have distinct evidence. Only current-authority, settled full success/failure updates the existing exact observation row; full failure can invalidate prior same-revision compatibility, while image-only does not overwrite it. Accepted evidence matches tenant, profile revision, source epochs, full mode, filesystem contract and current host mapping. Active verification/cleanup blocks another attempt, including after a profile revision changes. Original-session loss, source/profile drift or expired attempt lease can retain factual bounded output but cannot promote compatibility or release that output to the retired caller. Unknown or mismatched adapter evidence becomes a safe cleanup-pending result.

Infrastructure reuses the existing disposable mount/script verification core. It checks saved attempt/current original-human authority before protected auth, mount preparation, provisioning, initialization and acceptance. Combined tenant/attempt metadata identifies disposable SDK environments; no tenant directory is inferred from model/user input. A separate bounded hosted cleanup worker consumes exact technical claims and never receives scripts, logs or human authority. It validates saved tenant/profile/attempt/revision/host/nonce and claim lease, confirms exact SDK ownership before killing, and purges only that disposable owner directory after known retirement. Late/stale/expired/foreign completions cannot settle another claim or replay initialization. Cleanup remains independent of global catalog eligibility.

Migration `20261017203239_TenantSandboxVerificationOwnership` adds exact receipt FKs, active uniqueness and indexed lease/latest witnesses, with observation references to their exact receipt and a strict tenant JSON CHECK. InMemory uses actual IAM/credential/profile locks and equivalent rules. Existing unconverted global rows remain excluded from owned evidence, and global verification APIs/store methods reject owned profiles/results. This forward working-schema migration was exercised on disposable databases only; final baseline consolidation remains open.

## Focused local evidence

| Check | Observed result |
| --- | --- |
| Infrastructure verification/source/store/cleanup regression | **118 passed**, 30.2693s: 14 new verification cases, 89 catalog/OCI/registry/store cases and 15 existing cleanup cases |
| Actual tenant SDK lost-create/logout recovery | **2 passed**, 5.4420s; both actual stores with real SDK HTTP against loopback |
| Canonical and existing Sandbox HTTP regression | **20 passed**, 24.5249s: 2 new verification, 2 catalog and 16 existing API cases |
| Documentation | 1329 local links across 14 changed/new Markdown files; zero missing targets/anchors and balanced fences; `git diff --check` passed |
| Production API build | Passed in 1.26s with zero warnings/errors |

There are **140 distinct passing cases**; the earlier two-case HTTP-only pass and initial 12-case verification pass are subsets and are not counted twice. All test handles completed, and disposable WAF/loopback hosts were disposed.

Commands run from the repository root with `--no-restore --nologo` and console normal verbosity for tests:

```sh
dotnet test tests/AgentCore.Infrastructure.Tests/AgentCore.Infrastructure.Tests.csproj --filter 'FullyQualifiedName~Tenant_sandbox_verification_|FullyQualifiedName~Sandbox_profile_|FullyQualifiedName~OciSandboxAuthorityTests|FullyQualifiedName~SandboxStoreTests|FullyQualifiedName~RegistryAuthorizationProbeTests|FullyQualifiedName~OpenSandboxCleanupTests'
dotnet test tests/AgentCore.Infrastructure.Tests/AgentCore.Infrastructure.Tests.csproj --filter 'FullyQualifiedName~Tenant_sandbox_verification_real_SDK_lost_create'
dotnet test tests/AgentCore.Api.Tests/AgentCore.Api.Tests.csproj --filter 'FullyQualifiedName~Tenant_verification_HTTP|FullyQualifiedName~Sandbox_catalog_HTTP|FullyQualifiedName~SandboxApiTests'
dotnet build src/AgentCore.Api/AgentCore.Api.csproj --no-restore --nologo
```

Executed workflows and observed outcomes:

- Actual Synthetic HTTP: create an owned profile, reject stale profile witness and unknown mode field without changing security or creating a receipt, run the actual disabled-host verifier, read exact/latest durable failure and observe VerificationFailed with unchanged configuration revision. No-store and safe projection omit internal human IDs, Credential epochs, host and cleanup proof. Anonymous, foreign and legacy global calls deny. A member with an exact read-only profile grant can inspect facts but cannot start either verification; revocation masks exact history before stale-witness reporting.
- Both stores: image-only success leaves NeedsVerification, full success supplies exact owned evidence, a later image-only result preserves that full evidence and SQLite reopen retains both receipt and observation. Changing the host mapping invalidates current compatibility. Global verification reads do not return owned evidence; audit omits bounded script output.
- Retire the original session and create a replacement during the scripted verifier. The old caller receives denial; factual result remains Retired with no compatibility promotion. Explicit fresh-session verification recovers once. Rotate a protected source Credential during dispatch: captured evidence retires and a new explicit check records the current epoch without leaking protected values to audit.
- Hold a verifier response, reject an overlapping check, expire the attempt, claim cleanup and release the late result. The exact pending receipt survives without observation promotion. Wrong host/nonce, expired and superseded cleanup claims cannot settle; renewed exact cleanup retires ownership and only an explicit new verification dispatches again. Separately, a successful result after lease expiry cannot promote even before cleanup has claimed it.
- Return foreign adapter evidence with private stdout. The service persists only sanitized cleanup-pending evidence. Running a cleanup pass retires the exact claim without a second verifier/script invocation.
- Actual SDK provisioning against loopback: inspect combined tenant/attempt metadata on the single create, retire the original human and return an unknown server failure. Exactly one owned inventory query confirms cleanup; disposable roots are purged, upstream body is withheld and the durable Retired failure never becomes accepted evidence. A fresh session can inspect the fact without replaying provisioning.
- Actual SDK cleanup against a disposable loopback server: wrong tenant/nonce/host makes zero RPC. After logout the exact technical cleanup can query its owned inventory and purge only its known disposable directory; a sibling disposable owner survives. No initialization is replayed and the exact store claim settles once.

## Review corrections and limits

The first HTTP run incorrectly assumed the shared test host disabled Sandbox; the assertion was corrected to use explicit disabled configuration. Its observed failure was safe mount preparation, not remote execution. Initial cleanup fixture runs exposed an incorrect run-store cast and then a constructor type mismatch; the unused legacy run-store dependency now has its correct isolated backing. Review added explicit lease-expiry acceptance, hostile-evidence sanitization and current host checks at both SDK cleanup and final store settlement. The initial migration draft was regenerated before any user database use; only the final migration remains. EF CLI/runtime version advice was non-failing; tooling was not upgraded.

Backend workflows use actual services/stores, WebApplicationFactory, temporary SQLite/reopen and real SDK HTTP against loopback fixtures. Successful full verification is a scripted adapter fact for application/store testing; live Docker mount attestation and initialization were not exercised and remain unverified. No frontend source/presentation changed, so no browser/Impeccable acceptance is claimed. No hosted CI, full local CI suite, live registry/provider credentials, production deployment, user-data reset or Real-host restart occurred. The [boundary inventory](multitenant-iam-boundary-inventory-2026-10-16.md#sandbox-disposable-verification-successor--2026-10-18) retains the remaining cutover obligations; the full goal remains active.
