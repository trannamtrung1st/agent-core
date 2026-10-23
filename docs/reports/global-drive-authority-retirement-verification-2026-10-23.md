# Global Drive authority retirement — 2026-10-23

This follows `47e03853` and advances the active [IAM delivery ledger](multitenant-iam-delivery-2026-10-16.md). It removes global Drive connection/account resolution while preserving the native effect ledger used by canonical tenant operations. It is scoped cutover evidence, not full proposal acceptance.

## Authority cutover

- Removed `GoogleDriveOAuthService`, `IGoogleDriveOAuthService`, `GoogleDriveProviderFactory`, `IGoogleDriveProviderFactory`, their old start/connection DTOs and DI/HTTP-client registrations. Host/options values remain pure configuration; the retired callback-path constant is removed.
- Removed `DriveService.AccountsAsync`/`ResolveAsync`, their Instance/binding/Credential/global-provider dependencies and the global recheck fallback. The same service retains the incumbent bounded native effect/reconciliation/batch ledger with operation-store, clock and optional logger dependencies.
- Every public native read/effect/inspection path now requires the internal canonical tenant authority binding before protected provider or receipt I/O. Existing inner rechecks remain; metadata reads additionally recheck before the remote request, then withhold results if the source changes afterward. Caller-built Instance/binding/Credential/provider records and an epoch alone confer no authority. Receipt Synthetic provenance derives from the accepted profile rather than a global factory.
- Canonical account read/action/effect owners continue to create the scoped transport and exact Run/root/IAM/source witness. The actual prepared operation, native effect ledger and existing owning transactions remain the same. No new scheduler, effect framework, permission, approval default or schema is introduced.
- `ToolConfigurationGate` now uses the actual tenant OAuth provider's safe descriptor for Drive readiness. Configured isolated old Email/Drive clients with no valid shared canonical client no longer advertise Drive readiness. Synthetic remains key-free; explicit host disable and current independent account policy still apply.
- Moved the unchanged bounded JSON reader into its sole current native Drive adapter. Pure host/OAuth option types remain separate from authority. Existing native transfer, protected resumable session, reconciliation, partial batch and uncertainty rules are preserved.

No installed/user data was reset, adopted or migrated. Physical native ledger/Synthetic-file storage remains beneath current tenant owners.

## Fixture reconciliation and evidence limits

**20 obsolete test method bodies across five fixture paths** are retired: 18 global-application Drive journey bodies and two global OAuth bodies. Two exclusively supporting API helper files are removed. Exact source hashes/method names and retirement manifest are `/private/tmp/global-drive-retirement-baseline-20261023.json` and `/private/tmp/global-drive-retirement-tests-20261023.json`. Removed cases are not counted as passing current qualification; their bodies remain recoverable from `47e03853`.

Native transport tests keep their existing HTTP/byte/preview/upload/Trash assertions using an explicit protocol fixture rather than global credential/OAuth setup. A 401 rejection checks the adapter's reconnect callback; canonical OAuth suites separately qualify the protected connection-state transaction. Raw persistence fixtures seed connected historical bytes directly; the seven-file ledger fixture installs an explicit test-only callback and proves physical receipt/CAS/reopen behavior. It does not qualify tenant IAM or restore a product account-selection path.

Actual tenant HTTP/model cases additionally assert global service/port types and `DriveService` resolution methods are absent, then execute the owned read/mutation workflows. New boundary cases prove no provider or receipt access from unbound records and result withholding after source revocation.

The retirement does not waive the final combination audit for the former global journeys: unavailable-parent Trash/rename/move, exact 1/7/20 manifests and stale/order/interruption variants, seven sequential uploads with MIME normalization, and a previously uncertain trashed upload followed by a fresh explicit operation. Existing canonical general action/transfer and retained native tests qualify their executed boundaries; they are not an automatic one-for-one replacement for every former global combination. This remains part of the active proposal-wide canonical parity/acceptance audit.

## Focused local verification

- First actual tenant model Drive flow: **14 passed**, zero failed/skipped, 1 m 34 s; both stores, exact approvals and original bytes.
- New tenant-required native-ledger/readiness boundaries: **16 passed**, zero failed/skipped, 32 ms.
- Grouped actual tenant API/model/OAuth/operation-read/SQLite host recovery: **59 passed**, zero failed/skipped, **6 m 59 s**.
- Grouped native Drive protocols/preview/upload/Trash, physical storage/batch recovery and canonical tenant OAuth/effect/read authority: **220 passed**, zero failed/skipped, **8 m 3 s**.

**279 distinct focused cases qualified: 59 API + 220 Infrastructure.** The first 14 model cases and 16 boundary cases overlap these groups and are not added twice. Retired global methods are not counted as passing current cases.
- Serial solution build: passed, zero warnings/errors, 12.13 s. The first build found one stale shared fixture constructor; correcting it changed no product authority rule.

Commands use `dotnet test <project> --no-build --no-restore --nologo --filter '<selection>'`:

```text
First model: FullyQualifiedName~Model_drive_actions_use_approved_metadata_and_exact_original_bytes
Boundaries: FullyQualifiedName~TenantDriveLedgerBoundaryTests|FullyQualifiedName~DriveHostGateTests
API: FullyQualifiedName~Model_drive_actions_|FullyQualifiedName~Model_drive_operation_reads_|FullyQualifiedName~Model_immediate_background_account_actions|FullyQualifiedName~Abandoned_account_dispatch_recovers_|FullyQualifiedName~Pending_operation_read_survives_|FullyQualifiedName~Tenant_connected_account_HTTP_reads_exact_bound_|FullyQualifiedName~Tenant_OAuth_HTTP
Infrastructure: FullyQualifiedName~GoogleDriveTests|FullyQualifiedName~DrivePersistenceTests|FullyQualifiedName~DriveHostGateTests|FullyQualifiedName~TenantDriveLedgerBoundaryTests|(FullyQualifiedName~TenantCredentialIntegrationTests&(FullyQualifiedName~OAuth_|FullyQualifiedName~Account_actions_|FullyQualifiedName~Account_transfers_|FullyQualifiedName~Account_abandoned_claim_|FullyQualifiedName~Account_operation_|FullyQualifiedName~Connected_account_real_protocol|FullyQualifiedName~Connected_Drive_real_protocol))
Build: dotnet build AgentCore.sln --no-restore --nologo -m:1 -nodeReuse:false -p:UseSharedCompilation=false
```

Logs use `/private/tmp/global-drive-retirement-` with `build-20261023.log`, `build-r2-20261023.log`, `first-model-20261023.log`, `boundaries-20261023.log`, `api-20261023.log` and `infrastructure-20261023.log`. Exact-body comparison confirms the moved bounded JSON reader, allowing only visibility and the identical inline failure constructor. Changed-document links/fenced blocks and whitespace checks pass.

The full goal remains active: global Profile/Session roots, other internal/lifecycle/store/schema retirement, the native combination audit above and final security/isolation/functional/rendered UI acceptance remain required. No frontend code changes or new browser acceptance is claimed. All executed fixtures are disposable/offline; no CI, hosted provider, user-data reset, Real restart, push or deployment was performed.
