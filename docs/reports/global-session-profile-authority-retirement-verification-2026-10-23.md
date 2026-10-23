# Global Session/Profile authority retirement — 2026-10-23

This follows `9776d754` and advances the [IAM delivery ledger](multitenant-iam-delivery-2026-10-16.md). It removes global Session/Profile creation and runtime construction/push entry points. It is scoped cutover evidence; broader internal/schema reconciliation and final proposal-wide security, isolation, functional and rendered UI acceptance remain open.

## Removed authority and retained owners

- Removed the global `SessionManager` facade and its DI registration: unowned creation, catalog/history, metadata/lifecycle, model/locale, workspace and artifact commands no longer have that alternate application entry.
- Removed `LocalUserProfileService`, `ILocalUserProfileService`, `IProfileLiveUpdateNotifier` and the local service registration. No implicit global language/name seed or global Profile mutation/notification service remains.
- Removed `SessionRuntime.ApplyProfileAsync`, `ProfileUpdatedReceived` and its mailbox handler. Current original-human Profile source ownership stays with `TenantUserProfileService` / `TenantRunProfileService`; no global fan-out can replace a tenant Run's sources.
- Moved unchanged `VoiceAvailability` and the canonical factory into their own files. Removed `SessionRuntimeFactory.Create` and its raw global Session/Run/attachment/artifact, capability, browser, authority, occurrence and composer dependencies. The constructor shrinks from 30 parameters to 20; the six canonical continuation/artifact/attachment/Profile/Memory/Experience owners are required. `CreateTenant` retains its actual frame/snapshot checks, canonical storage/Run adapters, current execution authority, restricted tool executor and source services. Existing text/speech provider selection and owned recognition opening remain.
- Historical raw snapshot/Profile storage, value/provenance/JSON codecs and pure domain rules remain data contracts. This slice introduces no migration, rehash, installed-data adoption or deletion. Current tenant creation still rejects unowned saves/admission and preserves actual human/executor and persona/model pins.

## Fixture reconciliation and qualification boundaries

**56 obsolete test method bodies across 17 fixture paths** are retired. Two exclusively supporting helper files (`OwnedSessions`, `AgentRunTestDriver`) and an unused legacy Automation drain helper are removed. Exact preimage paths/hashes and method names are recorded in `/private/tmp/global-session-retirement-baseline-20261023.json` and `/private/tmp/global-session-retirement-tests-20261023.json`; original bodies remain recoverable from `9776d754`. Removed bodies are not counted as passing current qualification.

| Retired global fixture concern | Current owning evidence exercised |
| --- | --- |
| Session admission/catalog/lifecycle, revision conflicts and archived/executor rejection | `TenantSessionIntegrationTests` / `TenantSessionLifecycleTests`: authenticated human and actual executor, membership/current identity, scoped filtering, private read/manage separation, lifecycle policy, busy Run guards, prepared mutation rechecks, deletion tombstones and SQLite reopen. |
| Global local-Profile seed/mutation/live push | `TenantUserProfileTests` and authenticated User/Run Profile API cases: actual tenant/User/Account membership, no implicit seed/adoption, CAS, login/password/membership/cancellation fences, private-source rechecks, retained facts and explicit recovery. |
| Generic runtime factory, old configuration/Self-Management and Automation execution | Actual canonical HTTP-to-worker text, current claimed Core procedure/delegation policy, original-human background/completion Profile flows and the shared tenant factory. The old generic Model/Schedule journey bodies are not compatibility entry points. |
| Combined file/attachment/workspace/delete journeys | Their global facade bodies are retired. Canonical typed Session lifecycle/deletion and owning native data contracts run here; the existing artifact/attachment/workspace acceptance reports remain the owners of their fuller integration combinations. This slice does not reinterpret every historical combined Support/Compliance journey as a new passing tenant test. |

Pure runtime/model-selection, speech locale/recognizer, file-definition pinning, staged attachment/title, attach/detach ordering and host lifecycle-domain checks are preserved using literal physical state in the existing isolated `SessionRuntimeFixture`. Its small `SeedAsync` helper only constructs/saves a test snapshot; it admits no tenant Session, supplies no grants and is not a substitute for canonical IAM qualification. Native event transport fixtures seed retained Profile bytes directly rather than resolve a live global Profile service. The unused SessionManager field is removed from native lifecycle concurrency fixtures.

The actual worker regression additionally asserts removed types/methods and raw factory dependencies are absent, completes owned text under the original human/executor and checks that no global Profile was created. The signature guard runs alongside real HTTP/model execution, not as its substitute.

## Verification

| Focused local check | Observed result |
| --- | --- |
| First actual worker completion / revoked-executor refusal | **2 passed**, zero failed/skipped, **3 s**; overlaps final API group. |
| Preserved Application runtime/native fixtures | **58 passed**, zero failed/skipped, **18 s**. |
| Canonical Session/Profile storage and native lifecycle contracts | **100 passed**, zero failed/skipped, **4 m 14 s**. |
| Authenticated HTTP, real JavaScript MessagePack, Profile/background/voice and Core procedures | **56 passed**, zero failed/skipped, **3 m 27 s**. |
| Final serial solution build | **Passed**, zero warnings/errors, **18.42 s**. |

**214 distinct focused cases passed**; representative overlap is not counted twice. The first build found the unused Automation drain helper's call to the removed test driver; removing that helper resolved the sole compile error. No product runtime failure was observed. Changed-document links/fences, source retirement inventory and whitespace checks pass.

Build: `dotnet build AgentCore.sln --no-restore --nologo -m:1 -nodeReuse:false -p:UseSharedCompilation=false`. Tests use the existing project files under `tests/AgentCore.Application.Tests`, `tests/AgentCore.Infrastructure.Tests` and `tests/AgentCore.Api.Tests` with `dotnet test <project> --no-build --no-restore --nologo --filter '<filter>'`:

```text
Application: FullyQualifiedName~SessionModelSelectionTests|FullyQualifiedName~SpeechLocaleSessionTests|FullyQualifiedName~SessionRealtimeLifecycleTests|FullyQualifiedName~SessionRuntimeCompletionTests|FullyQualifiedName~AttachmentBindTests|FullyQualifiedName~BrowserLifecycleTests.End_and_dispose_|FullyQualifiedName~RoleEnvironmentTests|FullyQualifiedName~PreferredNameProvenanceTests|FullyQualifiedName~ArtifactMaterializeTests|FullyQualifiedName~SessionCatalogTests|FullyQualifiedName~AgentInstanceTests|FullyQualifiedName~ManagedInstanceP7DRegressionTests|FullyQualifiedName~OrderPlacedAdmissionTests
Infrastructure: FullyQualifiedName~TenantSessionIntegrationTests|FullyQualifiedName~TenantSessionLifecycleTests|FullyQualifiedName~TenantUserProfileTests|FullyQualifiedName~AdminLifecycleConcurrencyTests|FullyQualifiedName~UserProfilePreferencesCodecTests
API: FullyQualifiedName~Canonical_tenant_worker_completes_|FullyQualifiedName~Canonical_dispatch_rechecks_|FullyQualifiedName~Canonical_Session_HTTP_|FullyQualifiedName~Tenant_conversation_HTTP_|FullyQualifiedName~Tenant_Run_Profile_|FullyQualifiedName~Tenant_User_Profile_|FullyQualifiedName~Tenant_live_MessagePack_JavaScript_|FullyQualifiedName~Tenant_Core_procedures_use_claimed_policy_|FullyQualifiedName~Correlated_events_are_bounded_
```

Logs use `/private/tmp/global-session-retirement-` with `build-20261023.log`, `build-r2-20261023.log`, `build-final-20261023.log`, `first-runtime-20261023.log`, `application-20261023.log`, `infrastructure-20261023.log` and `api-20261023.log`. Runtime checks use Synthetic adapters and disposable stores; installed databases and Real hosts are untouched. No CI, live-provider or rendered UI acceptance was run for this backend slice. Remaining legacy-root/combination inventory and final proposal-wide acceptance are still required.
