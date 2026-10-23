# Global Email authority retirement — 2026-10-23

This follows `b02a89f7` and advances the active [IAM delivery ledger](multitenant-iam-delivery-2026-10-16.md). It removes the obsolete global Email dependency chain while retaining canonical tenant source, operation and effect owners. This is scoped cutover evidence, not complete proposal acceptance.

## Retired authority and retained owners

- Removed global `EmailAccountService`, `EmailMonitoringService`, `IEmailProviderFactory`, `IEmailOAuthService`, `IEmailChangeFeed`, `AccountEmailProviderFactory`, `AccountEmailChangeFeed` and `GoogleEmailOAuthService`, their owner-selection helpers and DI registrations. Removed obsolete global OAuth results, account-inventory results and connection-test DTOs.
- Removed global `CoreEventDispatcher`. Its exact pure accepted-Core-event envelope now belongs to the current `TenantEventProcessor`; no global filter/fanout/admission/Email-authority branch remains in that class.
- `TenantCredentialOAuthService`, `TenantGoogleOAuthProvider`, canonical account read/effect services and `TenantConnectedAccountReader` retain actual tenant/login/executor/source authority, exact consent, live transport rechecks, protected-token maintenance and durable effect receipts.
- Moved the unchanged bounded Gmail HTTP retry/read methods into their sole current consumer, `GmailEmailProvider`. The Synthetic scoped adapter retains its exact callback fence in its own file. Pure monitoring options/transport failure marker, Google client configuration type and `IEmailSendPreflight` remain separate from retired authority. Existing provider protocols and payload limits are unchanged.
- `ToolConfigurationGate` now reads current `EmailTransportOptions` and the actual Synthetic/Real profile directly. Removed its global factory/provider fallback dependency. Host readiness preserves enabled/send/SMTP defaults and live option changes; canonical per-account scope/SMTP and IAM checks remain separate. Browser/Sandbox consumers only lose the obsolete constructor argument.

No schema/migration, permission, approval default, scheduler, new framework or user-data operation is introduced. Physical historical `IEmailStore`/`IEmailWatchStore` contracts and installed data remain; current tenant operations do not fall back to global source authority.

## Fixture reconciliation

**20 obsolete test method bodies across four fixture paths** are retired with these service roots. Exact baseline file hashes/method names and the retired-method list are `/private/tmp/global-email-retirement-baseline-20261023.json` and `/private/tmp/global-email-retirement-tests-20261023.json`. Removed methods are not counted as passing current qualification.

Retained independent contracts include public DNS/endpoint/TLS validation, current SQLite raw credential/ciphertext reopen, the frozen Custom email approval hash and OAuth epoch binding, malformed Google proof rejection through the canonical verifier, and public-origin configuration rejection. They prove their stated pure/physical contracts, not tenant authorization. New host-gate cases preserve the previous Real/Synthetic host flag semantics without reconstructing a global account factory. Existing canonical HTTP/model/watch/OAuth/native-protocol/effect cases supply actual tenant authority and recovery evidence.

The actual tenant connected-account HTTP journey asserts the retired types are absent from the built assemblies, then performs owned inventory/search/read and source-isolation checks on both stores. Absence checks supplement that use case; they are not sole evidence of correctness.

## Focused local verification

| Group | Observed result |
| --- | --- |
| First actual tenant HTTP inventory/search/read/isolation journey | 4 passed, zero failed/skipped, 17 s; both stores. Included in the API group below. |
| Canonical account HTTP/model exact reads/send, OAuth, Email watch and background work | **97 passed**, zero failed/skipped, 6 m 41 s. |
| Offline Gmail/IMAP/SMTP protocols, host readiness, retained pure/storage contracts and canonical tenant OAuth/native account effects | **210 passed**, zero failed/skipped, 6 m 30 s. |
| Shared browser/readiness contracts | Initial 16 passed / 5 stale unowned-execution assertions failed. The complete corrected group **passes all 21 cases**, zero failed/skipped, 52 ms. |
| Final serial solution build | Passed, zero warnings/errors, 6.09 s. |

**328 distinct focused cases qualified: 97 API + 210 Infrastructure + 21 Application.** The first four HTTP cases overlap the API group and are not added twice. Retired methods and initial failed assertions are not counted as passing current cases.

The five browser fixture corrections preserve schema/argument and active-Skill validation through the actual pure parsers/validator, while independently requiring the existing executor to reject unowned calls with zero provider dispatch. Metadata, provider-feature negotiation and the bounded low-level adapter fixture remain checked. No browser authority/policy code was changed to make these fixtures pass. API/infrastructure results were retained after these test-only corrections instead of repeating their complete groups.

Commands use `dotnet test <project> --no-build --no-restore --nologo --filter '<selection>'`:

```text
First HTTP: FullyQualifiedName~Tenant_connected_account_HTTP_reads_exact_bound_mail_or_Drive_original_without_legacy_owner_or_Agent_grants
API: FullyQualifiedName~Tenant_connected_account_|FullyQualifiedName~Model_connected_account_read_waits|FullyQualifiedName~Tenant_OAuth_HTTP|FullyQualifiedName~Tenant_email_watch|FullyQualifiedName~Model_account_actions_create_immutable_draft|FullyQualifiedName~Model_immediate_background_account_actions
Infrastructure: FullyQualifiedName~GmailEmailProviderTests|FullyQualifiedName~GmailHistoryChangeFeedTests|FullyQualifiedName~ImapSmtpEmailProviderTests|FullyQualifiedName~EmailAccountIntegrationTests|FullyQualifiedName~GoogleOAuthProofTests|FullyQualifiedName~GoogleOAuthConfigurationTests|FullyQualifiedName~EmailHostGateTests|FullyQualifiedName~EmailWatchStoreTests|(FullyQualifiedName~TenantCredentialIntegrationTests&(FullyQualifiedName~OAuth_|FullyQualifiedName~Connected_account_real_protocol|FullyQualifiedName~Account_actions_use_owned_atomic_drafts))
Application: FullyQualifiedName~Unknown_interaction_mode_does_not_become_interactive|FullyQualifiedName~NativeBrowserContractTests
Build: dotnet build AgentCore.sln --no-restore --nologo -m:1 -nodeReuse:false -p:UseSharedCompilation=false
```

Logs use `/private/tmp/global-email-retirement-` with `build-20261023.log`, `build-final-20261023.log`, `first-http-20261023.log`, `api-20261023.log`, `infrastructure-20261023.log`, `application-gates-20261023.log` and `application-gates-final-20261023.log`. Static exact-body comparison confirms the moved Core envelope and Gmail bounded HTTP helpers, allowing only visibility changes and the identical inline failure constructor. Changed-document links/fenced blocks and whitespace checks pass.

The overall goal remains active. Global Profile/Session management roots, other remaining internal authority/lifecycle/store/schema paths and final proposal-wide security, isolation, function and rendered UI acceptance still require work. This module changes no frontend and claims no new browser acceptance. No CI, live provider, user-data reset, Real restart, push or deployment was performed.
