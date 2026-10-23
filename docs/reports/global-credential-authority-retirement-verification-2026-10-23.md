# Global credential and browser-profile authority retirement

Date: 2026-10-23. Preimage: `fbf3efd7`. This is a scoped continuation of the active tenant cutover, not full proposal acceptance.

## Result

Removed global `CredentialService`, `ICredentialResolver` and `AgentBrowserProfileService`, including their DI registrations. These actors had no current production callers beyond registration. The existing credential/binding DTOs and identical pure projection moved to `CredentialViews.cs`; current `TenantCredentialService` and `TenantCredentialBindingService` use that mapper. `TenantBrowserProfileService` remains the owner of protected reset and uncertainty receipts.

`SandboxImageAuthority` no longer accepts or looks up a global `ICredentialStore`. Unowned profiles with a registry Credential cannot resolve tagged or digest sources, release a secret or reach disposable verification, even when physical Credential and current-version verification records remain. Tenant source preparation still uses the original human proof, tenant Credential owner, current source scope and revision/secret epochs. Public/local native image mechanics remain available for isolated adapter qualification. No schema, permission classification, approval default, protected payload or hash-bound preparation format changed.

Physical Credential/binding stores remain serialization and cleanup adapters with owned-row fences. The Drive persistence tests seed physical rows explicitly; they qualify connection/receipt bytes, CAS and reopen, rather than actor authorization. Current IAM store tests retain owned-row exclusion, mutation refusal, retained binding protection and reopen checks. Native Chromium sink checks verify wrong/stale target refusal before resolver invocation, child reflection and screenshot masking, actual local login, profile reopen, another native owner and targeted reset.

## Fixture reconciliation

Thirteen methods with 23 declared cases, including four opt-in model benchmark cases, were retired from six paths. Original bodies remain in `fbf3efd7`:

| Former fixture | Current qualification and limits |
| --- | --- |
| Three `SystemCredentialTests` global lifecycle/discovery/policy methods | Current owned lifecycle/binding, typed HTTP validation, metadata consent/recreation and native value/origin/metadata rules. Large-page model discovery still requires final canonical qualification. |
| `BrowserCredentialSinkTests.Existing_password_uses_current_binding_reflects_safely_and_unbind_preserves_login` | Separate native wrong/stale-target, redaction/screenshot/login checks and canonical model consent/current-authority fill. Native callbacks grant no tenant authority. |
| `OciSandboxAuthorityTests.Protected_registry_scope_rotation_disable_delete_and_verification_epoch_survive_store_reopen` | Owned source scope, epoch, verification/recovery tests; new both-store retained unowned-record refusal. |
| `RegistryAuthorizationProbeTests.Credential_rotated_during_manifest_probe_fences_pending_disposable_provisioning` | Current tenant source preparation and verification epoch/refusal tests. Native registry transport challenge/redirect tests remain. |
| Six `BrowserModelCompatibilityTests` methods and one `ExecutionBudgetChromiumTests` method | Their unowned Runtime/global Credential execution is retired. Current metadata discovery/review/recreation and native password consent are exercised through real HTTP/model/Chromium boundaries. Full no-Skills two-origin SSO and long protected-login/48-step reserved-cleanup journeys are still required canonical acceptance work. Old paid benchmarks do not qualify the current tenant implementation. |

A remaining mixed native profile fixture expected an unowned detached `SessionToolExecutor` to navigate into a login wall. The current tenant fence correctly refused that call. Its native portion now directly verifies reopen, isolation and reset; the obsolete detached assertion is removed without changing product admission. This is one refined native fixture in addition to the thirteen retired methods.

## Verification

There are **182 distinct passing selected cases** after superseding the obsolete mixed-fixture failure:

| Check | Observed result |
| --- | --- |
| Initial affected-use-case check | 7 passed: both-store owned lifecycle/binding and retained unowned registry refusal, plus real native password sink. These cases also occur in the grouped run. |
| Grouped Infrastructure credential/registry/source/verification/persistence/native cases | Initially 146 passed and the mixed native/unowned-detached fixture failed. Final native group: 8 passed, including its corrected successor; seven repeat previously passing cases. Thus all 147 final selected cases are qualified. |
| Credential HTTP lifecycle/typed validation and metadata exact-review/recreation | 13 passed. Two opt-in Browser declarations were initially skipped and then run explicitly below. |
| `AGENTCORE_CHROMIUM_ISOLATION_TESTS=1` HTTP/model password and profile reset | 22 passed, zero skipped, both stores. Actual native fill, redaction and post-resolution authority refusal; reset/cold/stale/uncertain/close/logout outcomes. |
| Serial solution build | Initial: 53.54 seconds; final: 10.28 seconds. Both zero warnings/errors. |
| Static review | Exact retired-symbol production/test search empty; identical safe projection extraction; whitespace and seven changed-document local-link checks pass. |

All tests use `dotnet test --no-build --no-restore --nologo` against the applicable Infrastructure or API project. The Infrastructure filter selects `OciSandboxAuthorityTests`, `RegistryAuthorizationProbeTests` excluding `Live_`, `SystemCredentialTests`, `DrivePersistenceTests`, `Legacy_credential_binding_paths_`, `Sandbox_profile_`, `Tenant_sandbox_verification_`, `BrowserCredentialSinkTests`, `Owned_credential_lifecycle_` and `Owned_bindings_share_`. The final repeat selects only `BrowserCredentialSinkTests`. API filters select `Owned_HTTP_credential_lifecycle_`, `Exact_HTTP_credential_grants_`, `Typed_HTTP_profiles_`, `Credential_metadata_model_waits_`, `Credential_metadata_pending_review_`, `Tenant_Browser_model_protected_fill_` and `Tenant_Browser_profile_HTTP_reset_`; the last two require the explicit disposable Chromium opt-in.

The final build uses `dotnet build AgentCore.sln --no-restore --nologo -m:1 -nodeReuse:false -p:UseSharedCompilation=false`. Logs and exact preimage/method inventories use `/private/tmp/credential-actor-retirement-*20261023*` and `/private/tmp/credential-actor-retirement-*-inventory.json`. All build/test handles are terminal. Temporary databases, native profiles and keys are test-owned. No installed database reset, Real-host restart, provider-key use, deployment, Docker run, CI, push or PR is part of this slice.

## Remaining goal

The complete proposal remains open: reconcile remaining Admin/configuration/tool/store authority roots and safe schema consolidation; finish canonical SSO, large-page discovery and long cleanup-budget qualification; then perform the final proposal-wide security, isolation, functional and rendered UI acceptance pass. Historical MVP freezes remain unchanged.
