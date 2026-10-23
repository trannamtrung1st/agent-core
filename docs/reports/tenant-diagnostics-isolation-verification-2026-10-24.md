# Tenant diagnostic and process-state isolation qualification

Date: 2026-10-24. Preimage: `2ba859e4`. Proposal-wide acceptance remains open. This report qualifies the diagnostic boundary and the specific process-state paths below.

## Fixed diagnostic disclosure paths

`HandledFailureLog` already sanitized exception content, but unexpected HTTP, worker and Runtime failures passed original exceptions into `DiagnosticLog`. Checking only the formatted message missed credentials, private URLs, provider bodies and other content carried by the exception object that a real sink renders. Three Runtime catches also logged original exceptions directly.

The existing bounded snapshot now belongs to the common `DiagnosticLog` sink. It preserves original exception types, HResult, HTTP/socket machine codes, CLR stack frames and bounded causal structure. It excludes exception messages, `Data`, arbitrary `ToString` implementations and overridden stack text. CLR stack capture avoids invoking an untrusted exception's virtual stack renderer. Aggregate limits remain eight siblings, eight nested levels and 32 total nodes. The handled-failure façade reuses this boundary; it no longer implements a separate snapshot. Disabled logs do not traverse exceptions. Trace failure marking and separate diagnostic/business ID sources remain unchanged.

The three direct Runtime catches (proactive handling, mailbox processing and attachment processing) now use the same content-free error boundary without changing their recovery. Actual authenticated tenant and Runtime-owner contexts add `TenantId` and `ActorPrincipalId` to server diagnostic records. HTTP attribution comes from the resolved membership item; Runtime attribution comes from its actual owner. Cross-tenant worker-pass failures do not invent a tenant. Neither field is added to metrics or public failure payloads.

The LLM adapter also retained regex-redacted provider-message excerpts when `LogProviderErrorMessages` was enabled. That could preserve short credentials or ordinary private user text. It now uses the shared parser's closed classified reason, such as `invalid_message_sequence`, alongside recognized codes/types. The duplicate excerpt parser and five redaction regexes are removed. The default-off verbosity option never enables raw excerpts. Response normalization, body bounds/replay, cancellation, timeouts and retry ownership remain unchanged.

No IAM permission, approval default, protected operation, schema/migration, wire failure shape, product UI or provider execution authority changed. Existing tests that explicitly expected original exception messages now require content-free causal evidence instead; their failure IDs, recovery/history and user-safe result assertions remain.

## Executed use cases

Four actual authenticated tenant Audit API cases use both stores and two failure shapes: nested HTTP/I/O failure containing URL/token/body sentinels, and an aggregate containing hostile message/stack/rendering overrides. Each returns safe `500/InternalError` with one correlated diagnostic ID. The log retains useful exception type and actual CLR call site, attributes the real tenant/principal, and leaves tenant security revision unchanged. **Every captured warning/error sink** is checked for absence of each sentinel in rendered message, exception rendering and structured fields. No outbound provider HTTP occurs.

The offline model follow-up case preserves the normalized 400 failure, model/phase/request telemetry and recognized reason while withholding image bytes, bearer/API key, address, ordinary private body text and a short secret even with verbose logging enabled. Existing model/speech transport cases preserve their diagnostic references and safe causal evidence.

The named-client fixture had one obsolete `GoogleEmailOAuth` expectation. Current body-safe clients are LLM, batch STT, TTS, Brave, Gmail and Drive. All six retain bounded shared error diagnostics. Current `TenantGoogleOAuth` intentionally has neither automatic error-body inspection nor HTTP loggers. Its new fixture sends a purpose-bearing URL and token body, observes zero automatic response reads and zero log events at all levels, then receives the original error bytes unchanged. No retired OAuth client alias is restored.

Two opt-in **actual Chromium** cases use disposable local profiles. The first separates historical native profile data from two tenants, shares persistent state only among the same Agent's Sessions, refuses other-tenant/sibling page-handle use, preserves the remaining sibling on release, reopens only the exact saved tenant/Agent/executor and confirms tenant-partitioned disk paths. Rebinding the same Agent to another tenant or executor is refused. The second rejects a substituted profile-parent symlink without changing its destination. These are native provider/profile checks, not rendered SPA or live-vendor acceptance.

## Process-local state review

The dictionary search is navigation rather than a cache count: it includes store rows, immutable allowlists, temporary values and method signatures. The following current source paths were inspected; this table does not turn static inspection into a complete collision/race acceptance claim.

| State | Key and current boundary |
| --- | --- |
| Native Browser contexts, pages, profiles and command observations | Immutable Session/Agent owner bindings include tenant/executor; conflicting or earlier unowned identity reuse is refused before cache use. Profile directories contain tenant/Agent. The two actual Chromium cases above exercise these boundaries. |
| Tenant live delivery | Sources key by `(tenant, Session)`; per-connection state is owned by the authenticated attachment. Current recipient witnesses precede delivery. [Integrated Chat/reconnect evidence](tenant-iam-integrated-ui-verification-2026-10-24.md) qualifies the source/recipient lifecycle separately. |
| Runtime State refresh throttle | Run ID stores only a bounded timestamp; `RequireModel` verifies actual frame/owner/claim before the throttle and again after observation. [Runtime State qualification](tenant-runtime-state-verification-2026-10-21.md) owns its functional matrix. |
| Connected-account synthetic state and OAuth-check gates | Mailboxes/watch arrivals use `(tenant, credential)`; OAuth check serialization uses the same owner pair. Current owning account/binding/source services remain the authorization boundary, not a cached allow decision. |
| Model clients | Route key includes catalog key, provider alias and model ID; current descriptor consistency is checked before cache lookup. These are host-owned provider routes, not tenant-owned credentials or grants. |
| Attachment extraction | Canonical `ProcessSourceAsync` validates the captured source/bytes and does not use the native `ProcessTurnAsync` cache. The native cache performs its Session-bound store lookup before ID/version reuse. |
| Built-ins and resource blobs | Built-ins are shared immutable sources; digest-addressed bytes convey no read authority. [Current catalog/source collision qualification](tenant-catalog-scheduling-consolidation-verification-2026-10-24.md) excludes retained unowned publication overlays. |
| Workspace/Artifact gates and I/O lifetimes | Global resource IDs select synchronization/lifetime state; actual owning root and exact typed source/cleanup proof select bytes. These mechanisms do not cache actor authorization. Physical store ownership/reference qualification remains in the family reports. |
| Runtime-local state and in-memory tables | Runtime collections belong to the one owned mailbox/Run. In-memory backing rows remain governed by the corresponding tenant store transaction; they are not a separate query/authorization cache. |
| Telemetry | The global timeline and duration samples are bounded process observations. `SnapshotTimeline`/`SnapshotStats` have no production reader/export route. Current metrics use their bounded non-identity labels; the selected privacy/telemetry cases below pass. Explicit local content-debug configuration is distinct from a tenant API. |

## Focused verification

**187 distinct selected cases qualified**, with zero remaining failures or skips. Repeated checks are not added to this total.

| Group | Observed result |
| --- | --- |
| Common/handled/Runtime failure diagnostics | **20 passed**, 537ms: `DiagnosticLogTests`, `HandledFailureLogTests`, `ConversationFailureDiagnosticTests`. |
| Actual tenant HTTP failure/sink isolation | **4 passed**, 17s: `Unexpected_tenant_HTTP_failure*`, both stores and both failure shapes. |
| Offline model/speech transport and classified follow-up | **5 passed**, 70ms: `ProviderTransportDiagnosticsTests` and `Rejected_follow_up_logs_only_classified_provider_reason*`. |
| Runtime recovery, attachment/mailbox, initiative, safe trace and telemetry | **103 passed**, 9s. |
| Provider inspection/replay/deadline/cancellation, real loopback and Gmail | **48 qualified**: 46 initially passed; the retired-client expectation is superseded by current Drive and OAuth qualification below. |
| Current named-client/OAuth correction group | **7 passed**, 52ms; five repeat prior positive client cases, two qualify current Drive and explicit OAuth exclusion. |
| Hosted diagnostics and shipped SQL logging | **5 passed**, 1s: `DiagnosticLoggingTests`, `LoggingConfigurationTests`. |
| Actual native profile isolation and symlink boundary | **2 passed**, 8s, no skips; `AGENTCORE_CHROMIUM_ISOLATION_TESTS=1`. |
| Final solution build | **Passed**, zero warnings/errors, 28.09s. |

All test commands use `dotnet test <project> --no-build --no-restore --nologo --filter '<selection>'`. Application's larger group selects `AttachmentProcessingRuntimeTests`, `QueuedAttachmentRuntimeTests`, `MailboxBackpressureTests`, `InitiativeTests`, `OperationalDiagnosticsSurfaceTests`, `RuntimeTelemetrySurfaceTests`, `SafeExecutionTraceTests`, `SpeechTelemetrySurfaceTests`, `ProgressTelemetrySurfaceTests`, `RunBudgetDiagnosticsTests`. Infrastructure's larger group selects `HttpErrorDiagnosticsTests`, `GmailRedactionTests`, `GmailEmailProviderTests`; its correction selects `Body_safe_provider_named_clients*` and `Current_OAuth_client_never_reads*`. Native selection is `Tenant_native_profiles_isolate_legacy_and_other_tenant_storage*` plus `Tenant_profile_parent_symlink_is_refused*`.

Build: `dotnet build AgentCore.sln --no-restore --nologo -m:1 -nodeReuse:false -p:UseSharedCompilation=false`. The initial build caught an xUnit analyzer preference for `Assert.Single(predicate)`; corrected before runtime checks. The final build includes every source/test correction. After the retired-client fixture correction, only the seven affected client cases were rerun; the other passing cases were retained. Logs use `/private/tmp/tenant-diagnostic-isolation-*20261024*.log` and `/private/tmp/tenant-process-cache-native-qualification-20261024.log`. All build/test handles are terminal. Relative link targets in the changed Markdown files and whitespace checks pass. No installed logs/data were inspected or reset, no Real host was restarted, and no provider-key call, hosted CI, push or PR was performed.

## Remaining proposal audit

Unexpected SignalR invocation/lifecycle failures still need direct logging-sink qualification: the current Hub translates known authorization/conflict/cancellation failures, while unknown faults can leave the method boundary. This report does not qualify framework-generated Hub diagnostics.

The full [store/effect matrix](tenant-store-effect-inventory-2026-10-24.md), indirect privilege reconciliation and final rendered UI/design acceptance remain tracked by the [delivery ledger](multitenant-iam-delivery-2026-10-16.md). The source review and focused results here qualify their named boundaries; they do not establish complete proposal acceptance.
