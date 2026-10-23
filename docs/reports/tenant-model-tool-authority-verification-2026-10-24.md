# Tenant model-tool authority checkpoint — 2026-10-24

This checkpoint enumerates all **150 distinct registered tool names**, including composed family descriptors. It maps dispatch owners and qualifies the common claim fence; it does not establish proposal-wide functional acceptance. The [registry](../../src/AgentCore.Application/Tools/ToolRegistry.cs) and [dynamic boundary tests](../../tests/AgentCore.Application.Tests/TenantToolExecutorBoundaryTests.cs) own the executable set. The 104 primary ToolCatalog constants are a different measurement.

The [shared adapter](../../src/AgentCore.Application/Security/TenantToolAuthorization.cs), [Runtime worker/mailbox](../../src/AgentCore.Application/Sessions/SessionRuntime.cs) and [canonical interfaces](../04-backend-interfaces.md#model-tool-live-claim-boundary) own the behavior.

## Common model boundary

Every model-tool policy, direct execution and approval preparation resolves the actual persisted tenant Session owner and Run. Caller-built tenant/Instance/Session IDs must match. The Run must be Running, uncancelled and claimed with the exact nonempty generation. Its current persisted deadline must be in the future according to the injected TimeProvider. Store-resolved renewal avoids rejecting a valid lease because its original captured deadline passed. Current executor IAM and existing family capability, origin, policy and source checks remain required.

IAM check/effective-permission results repeat the boundary check after inspection. Expiry, cancellation, waiting approval or reclaim during inspection withholds the result and records a factual ToolBoundary denial. Runtime-owned commands cannot fall through the generic adapter into IAM parsing. A live claim is necessary; protected effects retain their existing preparation, approval, source validation, transaction and dispatch owners.

## Complete registered set

Names below are disjoint and total 150. Runtime-owned tools intentionally need not have generic SessionToolExecutor effect branches. Registration does not imply offering to every Run.

| Family | Exact registered names | Canonical dispatch/authority owner |
| --- | --- | --- |
| IAM inspection (2) | `iam.check_access`, `iam.get_effective_permissions` | TenantOperationAuthorizer; repeats the shared claim check after inspection. |
| Automation (8) | `automation.create`, `automation.delete`, `automation.disable`, `automation.enable`, `automation.inspect`, `automation.list`, `automation.run`, `automation.update` | TenantAutomationReadService / TenantAutomationMutationService and existing approval/occurrence owners. |
| Management (34) | `automation.preferences.inspect`, `automation.preferences.patch`, `configuration.inspect`, `configuration.propose`, `credentials.binding.propose`, `credentials.binding.remove`, `harness.inspect`, `harness.instructions.update`, `harness.knowledge.remove`, `harness.knowledge.upsert`, `harness.tool.configure`, `harness.tool.select`, `management.freeze`, `management.inspect`, `management.policy.update`, `resources.copy`, `resources.delete`, `resources.inspect`, `resources.list`, `resources.set_enabled`, `resources.upsert`, `settings.inspect`, `settings.list`, `settings.patch`, `settings.update_preferences`, `skills.create`, `skills.customize`, `skills.delete`, `skills.inspect`, `skills.list`, `skills.load`, `skills.reset_enabled`, `skills.set_enabled`, `skills.update` | TenantManagementOperationTarget / TenantManagementReadService; preferences use TenantPreferenceOperationService. skills.load uses the Runtime mailbox and existing exact read owner. |
| Runtime work (7) | `background.acknowledge`, `background.inspect`, `background.list`, `background.start`, `background.take`, `execution.wait`, `work.complete` | SessionRuntime mailbox and existing tenant background/completion/wait owners. |
| Application messages (1) | `app.message.send` | Registered SessionRuntime mailbox command; tenant eligibility parity remains open. |
| Context and consent (2) | `approvals.request_authorization`, `capabilities.load` | SessionRuntime mailbox; existing tenant approval/context owners. |
| Workspace (11) | `workspace.batch`, `workspace.copy`, `workspace.cwd`, `workspace.delete`, `workspace.list`, `workspace.mkdir`, `workspace.move`, `workspace.patch`, `workspace.read`, `workspace.search`, `workspace.write` | TenantWorkspaceRuntimeReadService / TenantWorkspaceMutationEffectService. |
| Attachments and Artifacts (4) | `artifacts.create`, `artifacts.create_from_workspace`, `artifacts.verify`, `attachments.read` | TenantAttachmentRuntimeContextService / TenantArtifactRuntimeReadService / TenantArtifactOperationService. |
| Continuity and Experience (9) | `agent.state.inspect`, `continuity.get`, `continuity.search`, `experience.consolidate`, `experience.recent`, `experience.record`, `experience.source`, `memory.consolidate`, `memory.forget` | TenantContinuityReadService / TenantMemoryOperationService / TenantExperienceOperationService / existing Runtime-state reader. |
| Credential discovery (1) | `credentials.list` | TenantCredentialRuntimeReadService; metadata discovery supplies no credential use authority. |
| Browser (37) | `browser.click`, `browser.close`, `browser.console_messages`, `browser.cookies`, `browser.dialog`, `browser.drag`, `browser.drop`, `browser.emulate_media`, `browser.fill_credential`, `browser.fill_form`, `browser.find`, `browser.generate_locator`, `browser.get_config`, `browser.highlight`, `browser.hover`, `browser.local_storage`, `browser.mouse`, `browser.navigate`, `browser.network_request`, `browser.network_requests`, `browser.network_state`, `browser.press_key`, `browser.resize`, `browser.route`, `browser.routes`, `browser.screenshot`, `browser.scroll`, `browser.select_option`, `browser.session_storage`, `browser.set_geolocation`, `browser.snapshot`, `browser.tabs`, `browser.type`, `browser.unroute`, `browser.upload`, `browser.verify`, `browser.wait_for` | TenantBrowserReadService / TenantBrowserEffectService and existing approved native owner. |
| Sandbox (6) | `sandbox.exec`, `sandbox.process.status`, `sandbox.process.stop`, `sandbox.profiles`, `sandbox.reset`, `sandbox.status` | TenantSandboxRuntimeReadService and existing approved native Sandbox owner. |
| Account operations (17) | `drive.accounts`, `drive.create_folder`, `drive.download`, `drive.get`, `drive.list`, `drive.move`, `drive.rename`, `drive.search`, `drive.trash`, `drive.trash_batch`, `drive.upload`, `email.accounts`, `email.create_draft`, `email.read`, `email.request_account`, `email.search`, `email.send` | TenantAccountRuntimeReadService / TenantAccountEffectService. |
| Public web and HTTP (4) | `http.request`, `web.download`, `web.fetch`, `web.search` | TenantPublicWebReadService / TenantHttpEffectService; downloads retain the existing Workspace effect owner. |
| Office and Knowledge (7) | `knowledge.retrieve`, `office.document.create`, `office.document.edit`, `office.document.inspect`, `office.spreadsheet.create`, `office.spreadsheet.edit`, `office.spreadsheet.inspect` | Existing tenant Workspace/source read and approved mutation owners; processors supply no authority. |

## Scope and remaining acceptance

The baseline had inconsistent phase/generation checks and no common deadline check: IAM inspection succeeded after actual claim expiry in both stores. The renewed-deadline regression failed before the fix. This change adds no operation, permission, approval default, schema or wire contract.

The application-message descriptor and Runtime mailbox branch exist, while the shared tenant eligibility union omits that command. Existing tenant behavior must be reconciled and functionally verified before tool parity can be accepted. This report does not retire that baseline feature. Native Browser/account/Sandbox/HTTP effects must be assessed through their protected dispatch paths.

Registered-name denial tests establish stale-context refusal, not every successful tool or private store/effect race. Existing scoped family reports own their functional evidence. Remaining proposal-wide work includes the complete permission/store/effect matrix, obsolete schema/authority reconciliation and final security/isolation/design acceptance. Inert serialized Harness preparation evidence cannot simply be dropped: it participates in historical configuration and operation hashes. No historical consent is rehashed or installed data reset here.

The current HTTP manifest contains 250 canonical pairs (232 retained + 18 additions), 228 retired pairs and 227 explicit tenant pairs; the authenticated Hub declares 14 canonical methods. The [Composer report](tenant-composer-references-verification-2026-10-24.md) owns the latest route qualification. These inventories establish boundary shape, not acceptance of every handler.

## Verification

A real expiry reproduction failed in both stores before the implementation change: IAM policy returned Allow beyond the current claim deadline. The new module has 36 infrastructure cases (16 registered-name boundary cases, 2 renewal/deadline cases, 16 inspection-race cases and 2 wrong-dispatch cases) plus 2 actual live-model API journeys. Each registered-name case exercises all 150 names and one unregistered effect through both policy and execution.

The live-model journey holds the actual Running Run, refuses malformed stale parallel calls across eight families, then releases the model. Both IAM inspections succeed through the normal Runtime loop, the Run completes, the assistant reply persists, no approval is created and no outbound HTTP occurs. Existing wait qualification preserves scheduled/manual origins, current authority, cancellation, successor generations and SQLite reopen recovery.

The complete grouped result is **966 distinct passing cases**; repeated fixture corrections are not added to that count. No UI changed; actual backend integration is the runtime verification for this module. No CI or live-provider test was requested or run. Schema/migration, permission/default and wire inventories are unchanged.


| Focused check | Final result |
| --- | ---: |
| Application tenant executor boundary and registry policy | 514 passed |
| Infrastructure affected constructor/source/approval/native SDK paths and new claim module | 407 passed |
| Actual API model IAM and conversational/independent Automation wait journeys | 45 passed |
| Serial solution build, no restore | 0 warnings / 0 errors |
| Whitespace, inventory uniqueness/count and local documentation targets | Passed |

The infrastructure selection contains 262 TenantOperationAuthorizationTests, 76 TenantDefinitionIntegrationTests, 38 TenantPreferenceOperationTests and 22 TenantRunAdmissionTests, plus 8 public-web transport and 1 trigger-store name matches. It completed in 20m25s; the API selection completed in 5m10s. This is a focused selection, not the full local CI suite.

Initial post-change execution had two unsupported-credential fixture failures: the old test called the now-supported discovery tool with no read service. It now attempts an actual unsupported credential mutation under a real claimed Run, preserving its denial/audit assertions. Two API fixture failures expected a greeting even though the actual Synthetic script returns the delayed-order reply; the final assertion verifies that reply in persisted assistant history. All corrected cases pass in the complete final groups. The initial expiry reproduction is the product defect; these later corrections do not change product behavior.

Commands/evidence:

- `dotnet build AgentCore.sln --no-restore --nologo -m:1 -nodeReuse:false -p:UseSharedCompilation=false`; final log `/private/tmp/tenant-tool-lease-final-build.log`.
- Application `dotnet test ... --no-build --no-restore --filter 'FullyQualifiedName~TenantToolExecutorBoundaryTests|FullyQualifiedName~ToolRegistryPolicyTests'`; `/private/tmp/tenant-tool-lease-application.log`.
- Infrastructure `dotnet test ... --no-build --no-restore` with affected-file method selectors plus the four new claim-boundary methods; exact selection `/private/tmp/tenant-tool-lease-regression-filter.txt`, collected set `/private/tmp/tenant-tool-lease-regression-discovery.log`, final results `/private/tmp/tenant-tool-lease-regression.log`.
- API `dotnet test ... --no-build --no-restore --filter 'FullyQualifiedName~Actual_tenant_model_IAM_tools_keep_current_claims|FullyQualifiedName~Tenant_wait_keeps_one_Run|FullyQualifiedName~Independent_Automation_wait_preserves_same_Run'`; `/private/tmp/tenant-tool-lease-api-final.log`.
- Pre-fix expiry reproduction `/private/tmp/tenant-tool-lease-repro.log`.

## Subsequent application-message qualification

The application-message gap recorded above describes this checkpoint's original state. It is superseded by the [tenant application-message module](tenant-application-messages-verification-2026-10-24.md), with 91 distinct focused cases passing. The registered set remains 150; the current dispatch owner is the existing Runtime mailbox behind tenant conversation/live-claim/current-source checks. Other remaining acceptance items above stay open.
