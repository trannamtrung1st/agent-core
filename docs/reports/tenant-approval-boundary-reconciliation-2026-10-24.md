# Approval, audit, Browser and HTTP boundary reconciliation — 2026-10-24

Preimage: `39f15738`. This reviews **31 current methods** as one boundary group and consolidates the existing System-human Browser-privacy guard. It advances the original proposal’s exhaustive method review; it does not accept the still-pending specialized owner/effect families or final rendered UI/design gate.

## Change and authority dispositions

Both Browser-privacy owning stores now reuse the incumbent `RequireSystemHuman` / `RequireSystemHumanAsync` helper. It performs the same live Account/session/stamp/global-Principal and independent `system.governance.manage` checks that were copied locally. The unused pure `BrowserPrivacyRules.SystemDecision` wrapper is removed. Separate body-free audit validation, policy CAS, deployment mode/origin ceilings, atomic persistence and saved-versus-active restart behavior remain unchanged. This adds no permission, review floor, provider, route, DTO, schema or execution framework.

The [method index](tenant-store-method-source-index-2026-10-24.json) records these current source dispositions:

| Methods | Boundary / inspected owner |
| --- | --- |
| 10 | Durable approvals: actual tenant/request/root/executor reads; visibility before count/page; original-human private read/decision fences; exact action/revisions/current qualification; independent storm bounds; persisted expiry/signal/continuation dedupe |
| 7 | Approved/local/Browser effects and direct receipts: exact saved request/decision/grant/claimed Run/continuation/target; current raw/source authority and typed witnesses; atomic effect/receipt/audit/grant; once-only dispatch; abandoned recovery seals uncertainty without native replay |
| 1 | Tenant operation policy: independent governance read/manage, original human/security witness, policy/security CAS and immutable Core minimum; requester review policy never supplies executor IAM |
| 3 | Native Admin history: bounded validated summaries and operation-ID dedupe; lookup supports guarded native backing data. Unscoped List has no production caller and remains a native contract/test query; canonical private audit does not use it |
| 3 | Owned audit query/record: owner and closed System-safe filters precede totals; current original-human audit reads; exact owned Workspace/Instance/Session or actual Run/context/revision/generation for immutable bounded facts. A record never grants an effect |
| 2 | Browser host privacy: raw immutable policy data plus independent current System-human mutation owner, now sharing the existing guard |
| 2 | Browser profile reset: actual tenant/Instance/executor, original human and independent read/manage, exact native witness/epoch and unique operation at start; fresh authorization before deletion; exact factual settlement/recovery cannot initiate another reset |
| 3 | Public HTTP: exact durable dispatch reservation, fresh provider dispatch callbacks and bounded deadline; exact settlement/receipt/grant/Run; output requires fresh source authority; abandoned recovery never repeats transport I/O |

Human inspection/decision and durable execution remain distinct. An original requester logout does not revoke a valid saved decision or invent a human prerequisite for autonomous execution. Current raw permission, policy, grant expiry, exact source witnesses and execution generation still apply at their own protected boundaries. The 31 common-method dispositions do not replace the separate typed family source/effect review. Physical network address/redirect/size controls retain their transport evidence.

## Focused verification

**225 distinct cases pass, zero failures:** nine changed-path privacy cases (46 s), 59 Synthetic API/Runtime cases (5m), 145 focused Infrastructure cases (6m59s), and twelve native Chromium profile-reset cases (1m42s). All four processes are terminal with exit code 0. The initial API run recorded one opt-in skip marker; it is not counted as a pass. The complete twelve-case matrix was then executed successfully with `AGENTCORE_CHROMIUM_ISOLATION_TESTS=1`, leaving no unresolved selected skip.

The final serial solution build passes with **zero warnings/errors in 54.07 s**, log `/private/tmp/tenant-approval-boundary-build-20261024.log`:

```sh
dotnet build AgentCore.sln --no-restore --nologo -m:1 -nodeReuse:false -p:UseSharedCompilation=false
```

The changed path was checked first: `dotnet test tests/AgentCore.Infrastructure.Tests/AgentCore.Infrastructure.Tests.csproj --no-build --no-restore --nologo --filter 'FullyQualifiedName~SystemBrowserPrivacyTests'` passes **9 cases**, zero failed/skipped (46 s). Actual both-store transactions refuse retired/wrong-Principal/ordinary humans, preserve raw System authority and deployment ceilings, and prove CAS/audit/SQLite constraints. Only then were the broader groups run.

API selection covers `Canonical_durable_approval_HTTP`, `Original_conversation_receives_saved_wait`, `Audit_HTTP_`, `Tenant_Browser_profile_HTTP_reset`, `Tenant_public_http_model_requires`, `System_Browser_privacy_HTTP`, `Governance_HTTP_` and `Canonical_approval_floors_preserve`. Prefixes join with `FullyQualifiedName~` and `|`. These are actual isolated Synthetic HTTP/Runtime journeys with current cookies, CSRF, exact witnesses and owned stores. Approval-floor scenarios test real requester/executor and no-human origins; they do not apply a new mandatory review rule.

The native command is `AGENTCORE_CHROMIUM_ISOLATION_TESTS=1 dotnet test tests/AgentCore.Api.Tests/AgentCore.Api.Tests.csproj --no-build --no-restore --nologo --filter 'FullyQualifiedName~Tenant_Browser_profile_HTTP_reset'`. It exercises actual disposable Chromium contexts/profile bytes: confirmed reset preserves sibling/ephemeral contexts; stale witnesses, logout, uncertain close/deletion and repeated old operations do not repeat deletion; cold read creates no profile namespace.

The Infrastructure selection covers durable preparation/decision/expiry/visibility/limits, exact local preference effects/continuations, HTTP and Browser dispatch/recovery, profile reset, safe execution audit, native historical events and governance/current requester policy. Exact executed names and outcomes are retained in its TRX. All use `dotnet test <project> --no-build --no-restore --nologo --filter '<selection>'`; no full local or hosted CI is run.

Exact Infrastructure selection:

```text
FullyQualifiedName~Durable_approval_retains|FullyQualifiedName~Approval_expiry_persists|FullyQualifiedName~Ordinary_review_requires|FullyQualifiedName~Material_target_changes_invalidate|FullyQualifiedName~Approval_inventory_filters|FullyQualifiedName~Durable_decision_commits|FullyQualifiedName~Durable_request_creation_rechecks|FullyQualifiedName~Concurrent_same_root_preparation|FullyQualifiedName~Independent_approval_storm_limits|FullyQualifiedName~Approved_preferences_apply|FullyQualifiedName~Freeze_after_approval_revokes|FullyQualifiedName~Resumption_requires_the_pending_preference|FullyQualifiedName~Already_Allowed_auto_apply_preference|FullyQualifiedName~Saved_persona_payloads_keep|FullyQualifiedName~Tenant_http_|FullyQualifiedName~Tenant_Browser_effect_|FullyQualifiedName~Tenant_Browser_profile_|FullyQualifiedName~Actual_run_authorization_persists|FullyQualifiedName~Execution_authorization_audit_never|FullyQualifiedName~Execution_audit_store_fences|FullyQualifiedName~Execution_audit_replay|FullyQualifiedName~Empty_permission_inventory|FullyQualifiedName~Unsupported_tenant_credential_tool_is_audited|FullyQualifiedName~AdminEventStoreContractTests|FullyQualifiedName~Governance_read_is_independent|FullyQualifiedName~Stricter_policy_persists|FullyQualifiedName~Actual_requester_action_authority_changes
```

Logs: `/private/tmp/tenant-approval-boundary-{privacy,api,infra,native-api}-20261024.log`; matching `tenant-approval-boundary-*.trx` files are in each test project’s `TestResults`. No product C# changed after the passing build. Test outcomes remain separate from source-binding/hash checks.

## Current source closure

The regenerated index still resolves **92 interfaces, 544 callable methods, 547 syntax bodies (three explicit inherited defaults), 1,544 call/reference sites and 1,712 production C# files**. It records **181 reviewed / 363 pending methods**, with **49 of 180 uncalled slots reviewed / 131 pending**. Source SHA-256: `a86155bd9b9b79f28630fcd9852b959740e24b65fb0711f92b8e667d459f233c`. Nine expected generated-regex diagnostics belong only to the read-only Roslyn compilation; the real solution builds successfully. Earlier snapshots preserve their own evidence/counts.

The remaining method queue consists of Automation/events, Session/Runtime, Profile/Memory/Experience, files/Artifacts, Sandbox and Credential/account effects. The [original acceptance matrix](tenant-proposal-acceptance-matrix-2026-10-24.md) also retains final I/O/worker reconciliation, rendered keyboard/read-only/error/responsive confirmation and final delivery consolidation. No complete proposal acceptance follows from this checkpoint.

The twelve profile journeys qualify their isolated Chromium scope. Live provider/customer outcomes, final rendered UI/design, physical device/audio, multi-instance rollout and CI remain unverified. No installed-data reset, Real-host restart, push, PR or `.agents` edit occurred. The existing Impeccable context remains in place without rerunning the launcher.
