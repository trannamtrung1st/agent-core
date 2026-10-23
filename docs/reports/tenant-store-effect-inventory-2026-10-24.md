# Tenant store/effect source inventory — 2026-10-24

Source: `33ab693b`. This fills the current source-census gap left by the historical 42-DbSet snapshot. It is an ownership/evidence index for proposal sections 10, 12 and 16, not a new authorization layer, schema change or final acceptance claim. [Architecture](../03-system-architecture.md), [application ports](../04-backend-interfaces.md), [protocol](../14-api-and-realtime-protocol.md) and [persistence](../15-persistence-and-configuration.md) remain normative.

## Census and limits

- **81 mapped tables/entities** in the current EF snapshot, including entities configured without a DbSet property. Counting the 42 public DbSets misses ownership, identity, approval and other configured tables.
- **93 public store interfaces**, each assigned exactly once to one of **15 owner families**. The machine-readable index records every declaration and every matching production source reference. References are navigation aids, not proof that each call is reachable or authorized.
- **10 registered hosted entries** and **20 external/private-I/O boundaries** are indexed below. Boundary categories group related methods; they are not a claim that only twenty effect methods exist.
- **96 non-Designer migration-directory source files**: 95 migration implementations plus one metadata helper, from `20261015154322_CanonicalFilesystemBaseline` through `20261023190000_TenantPublicHttp`, remain explicit in the index. EF discovers 95 migration IDs; `20261021214545_TenantExperienceConsent.Metadata.cs` is a partial-class SQL-resource accessor, not a separate migration. The index distinguishes IDs, implementations and support. No migration is removed, replayed or rewritten here.
- Existing [HTTP](http-authority-cutover-verification-2026-10-21.md), [Hub](hub-authority-cutover-verification-2026-10-21.md), [150-name tool](tenant-model-tool-authority-verification-2026-10-24.md) and [78-permission](tenant-approval-defaults-audit-2026-10-24.md) inventories cover different boundaries. Their scopes and dates remain independent.

The [source index](tenant-store-effect-source-inventory-2026-10-24.json) contains all 81 entity/table names, 93 store symbols/declaration paths/source references, owner/enforcement/evidence grouping, migration paths, hosted sources and I/O port files. It is a dated census, not a second contract catalog. An ID, digest, raw store, provider or DI registration never supplies actor authority.

## Catalog/planner follow-up

The [catalog/scheduling consolidation](tenant-catalog-scheduling-consolidation-verification-2026-10-24.md) supersedes the dated composite-catalog and global admission/helper references in this census. It fixes built-in Resource collisions with retained unowned rows and qualifies all 241 private HTTP method/path pairs and 227 tenant pairs on both stores. The 93 interfaces remain; a syntax-based follow-up counts 548 declared methods without double-counting inheritance. Neither number establishes complete method-level authorization acceptance. The original machine-readable source census remains tied to its recorded commit.

## Persistence ownership and existing evidence

Each table and public store port appears in exactly one family below. “Physical” or historical stores remain non-authorizing data mechanisms behind the current owner or isolated retained-data tests; their presence is not a compatibility API. Existing reports describe observed scenarios on their recorded commits. This inventory adds no new passing runtime result.

### identity-iam

**Owner:** IdentityService; TenantAccessService; IamAuthorizer; SystemOperatorService.

Global Account/authentication and explicit System scope are separate from tenant membership. Current principal, owning resource, scope/role validity, last-owner protections, revision and audit are checked by the current service/store transaction.

**Mapped tables:** `Accounts`, `AuthenticationSessions`, `Tenants`, `TenantUsers`, `Principals`, `SystemIdentities`, `AccessRoles`, `AccessRolePermissions`, `AccessRoleAssignments`, `TenantInvitations`, `IdentityBootstrap`, `AuthorizationAudit`.

**Store ports:** `IIamStore`, `ISystemAdministrationStore`, `ISystemOperatorStore`.

**Existing evidence:** [tenant-resource-scope-verification-2026-10-16](tenant-resource-scope-verification-2026-10-16.md), [tenant-system-audit-verification-2026-10-21](tenant-system-audit-verification-2026-10-21.md).

### definitions

**Owner:** TenantDefinitionService; isolated Synthetic evaluation runner.

TenantAgentDefinitions is the mutable ownership root. Drafts/publications/resources/evaluation join the actual root; exact child/source reads and independent mutation/publish/evaluate permissions precede I/O and commit. Approved immutable built-ins are read-only sources. Global authoring actors are retired.

**Mapped tables:** `TenantAgentDefinitions`, `AgentDefinitionDrafts`, `AgentDefinitionPublications`, `AgentDefinitionDraftResources`, `AgentDefinitionPublicationResources`, `AgentDefinitionDraftEvaluationScenarios`, `AgentDefinitionDraftEvaluationResults`.

**Store ports:** `IAgentDefinitionAdminStore`, `IDefinitionDraftEvaluationStore`, `IDefinitionResourceContentStore`, `IAgentDefinitionResourceAdminStore`, `ITenantDefinitionStore`.

**Existing evidence:** [global-definition-authority-retirement-verification-2026-10-23](global-definition-authority-retirement-verification-2026-10-23.md), [tenant-definition-resource-iam-verification-2026-10-17](tenant-definition-resource-iam-verification-2026-10-17.md).

### agent-configuration

**Owner:** TenantAgentService; TenantAgentConfigurationService; TenantRunConfigurationResolver.

SystemIdentity binds the actual tenant/Instance/executor. Parent and child IAM, source reads, current revisions, Definition maxima, sparse overrides, freeze and pinned Run policy remain independent. Retained Harness history is inert hash-bound data, not consent authority.

**Mapped tables:** `AgentInstances`, `AgentInstanceSkills`, `AgentInstanceResources`, `AgentDefinitionSkillStates`, `AgentDefinitionResourceStates`.

**Store ports:** `IAgentInstanceStore`, `IStateIsolationConfigurationStore`, `ITenantAgentStore`, `ITenantManagementStore`.

**Existing evidence:** [global-instance-configuration-authority-retirement-verification-2026-10-23](global-instance-configuration-authority-retirement-verification-2026-10-23.md), [tenant-instance-child-iam-verification-2026-10-17](tenant-instance-child-iam-verification-2026-10-17.md), [tenant-harness-history-verification-2026-10-24](tenant-harness-history-verification-2026-10-24.md).

### session-runtime

**Owner:** TenantSessionService; TenantRunService; TenantSessionRuntimeHost; SessionRuntime.

TenantSessions joins physical Session/Instance/executor. Current human login/membership/chat/read/manage permissions and trusted claimed Runtime frames fence reads/admission/input/foreground/receipts/voice. One mailbox owns live state. Raw global Session/Profile actors and factory fallback are retired.

**Mapped tables:** `TenantSessions`, `Sessions`, `SessionSnapshots`, `ConversationEntries`, `Activations`, `AgentRuns`, `ActivationSourceEntries`, `BackgroundCompletionReceipts`.

**Store ports:** `IAgentRunStore`, `ITenantConversationStore`, `ITenantDeliveryReceiptStore`, `ITenantRunStore`, `ITenantCompletionRuntimeStore`, `ITenantRuntimeStore`, `ITenantRuntimeStateStore`, `ITenantSessionForegroundStore`, `ITenantRuntimeInputStore`, `ITenantSessionStore`, `ITenantVoiceModeStore`.

**Existing evidence:** [global-session-profile-authority-retirement-verification-2026-10-23](global-session-profile-authority-retirement-verification-2026-10-23.md), [tenant-run-admission-verification-2026-10-17](tenant-run-admission-verification-2026-10-17.md), [tenant-background-runtime-verification-2026-10-20](tenant-background-runtime-verification-2026-10-20.md), [tenant-application-messages-verification-2026-10-24](tenant-application-messages-verification-2026-10-24.md).

### approval-operation

**Owner:** TenantApprovalService; TenantApprovedOperationService; typed operation owners.

Actual saved Run preparation binds exact tenant/executor/arguments/source and current primary/secondary authority. Review and eligible ExceptionalGrant are distinct. Decision CAS, fresh human qualification, root dedupe, grant validity and receipt/dispatch fences constrain continuation; unknown effects are not replayed.

**Mapped tables:** `TenantApprovalRequests`, `TenantApprovalDecisions`, `TenantOperationGrants`, `ApprovalProcessedSignals`, `TenantOperationPolicies`, `TenantOperationReceipts`, `TenantCredentialCheckDispatches`.

**Store ports:** `ITenantApprovalStore`, `ITenantDirectOperationStore`, `ITenantApprovedOperationStore`, `ITenantOperationPolicyStore`.

**Existing evidence:** [tenant-durable-approval-verification-2026-10-17](tenant-durable-approval-verification-2026-10-17.md), [tenant-approval-loop-verification-2026-10-17](tenant-approval-loop-verification-2026-10-17.md), [tenant-approval-defaults-audit-2026-10-24](tenant-approval-defaults-audit-2026-10-24.md), [tenant-readback-consolidation-verification-2026-10-23](tenant-readback-consolidation-verification-2026-10-23.md).

### automation-event

**Owner:** TenantAutomationService; TenantAutomationMutationService; TenantEventProcessor; TenantWebhookService; TenantEmailMonitoringService.

Actual tenant/executor owns Automation, trigger, occurrence and event rows. Human or no-human source attribution, accepted lineage, source/destination authority, semantic intent, current claims and atomic dedupe are explicit. Worker enumeration supplies no ambient human or default tenant.

**Mapped tables:** `Automations`, `AutomationTriggers`, `TriggerOccurrences`, `WebhookEvents`, `ExternalEvents`, `ExternalEventDeliveries`, `CoreEvents`, `CoreEventBuckets`, `CoreEventDeliveries`, `EmailWatches`, `EmailWatchEvents`.

**Store ports:** `ICoreEventStore`, `IEmailWatchStore`, `IExternalEventStore`, `ITenantAutomationMutationStore`, `ITenantAutomationReadStore`, `ITenantAutomationStore`, `ITenantEmailWatchStore`, `ITenantEventStore`, `ITenantWebhookStore`, `ITriggerStore`.

**Existing evidence:** [tenant-event-automation-verification-2026-10-21](tenant-event-automation-verification-2026-10-21.md), [tenant-webhook-verification-2026-10-21](tenant-webhook-verification-2026-10-21.md), [tenant-automation-authoring-verification-2026-10-22](tenant-automation-authoring-verification-2026-10-22.md), [tenant-automation-presets-verification-2026-10-22](tenant-automation-presets-verification-2026-10-22.md).

### credential-account

**Owner:** TenantCredentialService; TenantCredentialBindingService; TenantCredentialOAuthService; TenantConnectedAccountReader; TenantAccountRuntimeReadService; TenantAccountEffectService.

TenantCredentials owns physical protected credentials and account profiles; binding also fixes tenant/Instance/executor. Purpose, live scopes/binding epoch/current identity/source reads, exact compound consent and secret maintenance/dispatch receipts fence access. OAuth binds original login, tenant, purpose and nonce. Raw global adapters exclude owned rows.

**Mapped tables:** `TenantCredentials`, `Credentials`, `AgentCredentialBindings`, `EmailAccountProfiles`, `DriveAccountProfiles`, `EmailAccountSelections`, `EmailDrafts`, `EmailSendOperations`, `DriveOperations`, `SyntheticDriveFiles`, `CredentialOAuthAttempts`.

**Store ports:** `ICredentialStore`, `IAgentCredentialBindingStore`, `IDriveOperationStore`, `ISyntheticDriveStore`, `IEmailStore`, `ITenantAccountOperationStore`, `ITenantConnectedAccountReadStore`, `ITenantCredentialBindingStore`, `ITenantCredentialCheckStore`, `ITenantCredentialOAuthStore`, `ITenantCredentialStore`.

**Existing evidence:** [global-credential-authority-retirement-verification-2026-10-23](global-credential-authority-retirement-verification-2026-10-23.md), [global-email-authority-retirement-verification-2026-10-23](global-email-authority-retirement-verification-2026-10-23.md), [global-drive-authority-retirement-verification-2026-10-23](global-drive-authority-retirement-verification-2026-10-23.md), [tenant-account-action-model-ui-verification-2026-10-20](tenant-account-action-model-ui-verification-2026-10-20.md).

### profile-memory-experience

**Owner:** TenantUserProfileService; TenantRunProfileService; TenantMemoryService; TenantMemoryManagementService; TenantContinuityReadService; TenantExperienceOperationService.

Actual tenant/User/Account and Agent/source ownership controls independent current reads/manage. Per-Run pin/opt-in/isolation can narrow sources. Learning/forget/consolidation/review use their own exact operation rules. Historical global state tables are not canonical tenant source fallbacks.

**Mapped tables:** `TenantUserProfiles`, `TenantLearnedMemories`, `TenantExperiences`, `UserProfiles`, `StructuredMemories`, `Experiences`, `ExperienceSettings`, `IdentityMaintenanceSettings`.

**Store ports:** `IExperienceStore`, `IStructuredMemoryStore`, `ITenantContinuityStore`, `ITenantExperienceStore`, `ITenantExperienceManagementStore`, `ITenantExperienceReviewStore`, `ITenantLearnedMemoryStore`, `ITenantMemoryManagementStore`, `ITenantMemoryOperationStore`, `ITenantPreferenceOperationStore`, `ITenantRunProfileStore`, `ITenantUserProfileStore`.

**Existing evidence:** [global-memory-service-retirement-verification-2026-10-23](global-memory-service-retirement-verification-2026-10-23.md), [global-session-profile-authority-retirement-verification-2026-10-23](global-session-profile-authority-retirement-verification-2026-10-23.md), [tenant-user-profile-verification-2026-10-21](tenant-user-profile-verification-2026-10-21.md), [tenant-memory-management-verification-2026-10-22](tenant-memory-management-verification-2026-10-22.md), [tenant-experience-maintenance-verification-2026-10-22](tenant-experience-maintenance-verification-2026-10-22.md).

### files-artifacts

**Owner:** TenantWorkspaceReadService; TenantWorkspaceMutationService; TenantArtifactService; TenantAttachmentService; typed source/publication/cleanup owners.

Actual tenant/Instance/Session resolves canonical roots and source bindings. Read/manage/publish/recipient authority remains separate; current witnesses surround byte I/O. Physical content/digest keys grant no access. Cleanup requires exact saved ownership/reference/lifetime proof, not absence from an unowned lookup.

**Mapped tables:** `Artifacts`, `Attachments`, `MessageAttachments`.

**Store ports:** `IAgentInstanceWorkspaceStore`, `IArtifactStore`, `ITenantArtifactCleanupStore`, `ITenantArtifactOperationStore`, `ITenantArtifactStore`, `ITenantAttachmentStore`, `ITenantAttachmentMutationStore`, `ITenantAttachmentCleanupStore`, `ITenantResourceSourceContentStore`, `ITenantWorkingCleanupStore`, `ITenantWorkspaceArtifactSourceStore`, `ITenantAttachmentArtifactSourceStore`, `ITenantWorkspaceMutationObservationStore`, `ITenantWorkspaceMutationStore`, `ITenantWorkspaceOperationStore`, `ITenantWorkspaceReadStore`.

**Existing evidence:** [global-resource-workspace-authority-retirement-verification-2026-10-23](global-resource-workspace-authority-retirement-verification-2026-10-23.md), [tenant-workspace-effects-verification-2026-10-19](tenant-workspace-effects-verification-2026-10-19.md), [tenant-attachment-context-verification-2026-10-19](tenant-attachment-context-verification-2026-10-19.md), [tenant-workspace-artifact-source-verification-2026-10-19](tenant-workspace-artifact-source-verification-2026-10-19.md), [tenant-artifact-cleanup-verification-2026-10-19](tenant-artifact-cleanup-verification-2026-10-19.md), [tenant-composer-references-verification-2026-10-24](tenant-composer-references-verification-2026-10-24.md).

### sandbox

**Owner:** TenantSandboxProfileService; TenantSandboxVerificationService; TenantSandboxControlService; TenantSandboxApprovedOperationService; OpenSandboxService.

Actual owned profile/image and verified setup precede claimed Session environment/lease/generation/process/workspace dispatch. Human reset/Stop and model command/read/consent are separate owners. Cleanup preserves uncertainty and requires reference/quiescence proof; external shell identity is a coarse privileged capability.

**Mapped tables:** `TenantSandboxProfiles`, `TenantSandboxVerificationAttempts`, `SandboxProfiles`, `SandboxProfileVerifications`, `SandboxInstanceVerifications`, `SandboxSessionLeases`, `SandboxExecutions`.

**Store ports:** `ISandboxStore`, `ITenantSandboxProfileStore`, `ITenantSandboxRuntimeStore`, `ITenantSandboxVerificationStore`.

**Existing evidence:** [global-sandbox-authority-retirement-verification-2026-10-23](global-sandbox-authority-retirement-verification-2026-10-23.md), [tenant-sandbox-command-approval-verification-2026-10-18](tenant-sandbox-command-approval-verification-2026-10-18.md), [tenant-sandbox-recovery-verification-2026-10-18](tenant-sandbox-recovery-verification-2026-10-18.md).

### browser

**Owner:** TenantBrowserEffectService; TenantBrowserReadService; TenantBrowserRetirementService; TenantBrowserProfileService; SystemBrowserPrivacyService; NativePlaywrightBrowser.

Actual tenant/Agent/Session/executor/profile/handle ownership and current claim, target/origin, binding/read/consent proofs fence native use. Persistent directories are tenant/Agent partitioned. Privacy administration and explicit human profile reset grant no model effect authority. Coordinate/geolocation are unavailable in the tenant adapter.

**Mapped tables:** `BrowserPrivacy`.

**Store ports:** `IBrowserPrivacyStore`, `ISystemBrowserPrivacyStore`, `ITenantBrowserProfileStore`.

**Existing evidence:** [tenant-browser-ownership-verification-2026-10-18](tenant-browser-ownership-verification-2026-10-18.md), [tenant-browser-retirement-verification-2026-10-20](tenant-browser-retirement-verification-2026-10-20.md), [system-browser-privacy-verification-2026-10-20](system-browser-privacy-verification-2026-10-20.md), [tenant-native-browser-journeys-verification-2026-10-23](tenant-native-browser-journeys-verification-2026-10-23.md).

### audit

**Owner:** AuthorizationAuditQueryService; SystemAdministrationService; bounded redacted diagnostics.

Current human tenant/security.audit.read or explicit aggregate System authority selects authorized audit facts. Global Admin history remains historical data; it is not the tenant audit API. Logs/metrics cannot confer private access or carry protected payloads.

**Mapped tables:** `AdminEvents`.

**Store ports:** `IAuthorizationAuditQueryStore`, `IAdminEventStore`, `ITenantAuthorizationAuditStore`.

**Existing evidence:** [tenant-system-audit-verification-2026-10-21](tenant-system-audit-verification-2026-10-21.md), [admin-memory-automation-retirement-verification-2026-10-23](admin-memory-automation-retirement-verification-2026-10-23.md).

### cleanup-isolation

**Owner:** TenantAgentDecommissionService; TenantAgentDecommissionCleanup; TenantRequestIsolationService; tenant file/Browser retirement owners.

Exact original-human or trusted cleanup proof, current source/security revisions, causal-child accounting and host quiescence constrain logical deletion and physical retirement. Cleanup cannot adopt foreign resources or call absence in an unowned store abandonment.

**Mapped tables:** No additional table; this family coordinates existing owned roots..

**Store ports:** `ITenantAgentDecommissionStore`, `ITenantRequestIsolationStore`.

**Existing evidence:** [global-lifecycle-deletion-retirement-verification-2026-10-24](global-lifecycle-deletion-retirement-verification-2026-10-24.md), [tenant-agent-decommission-verification-2026-10-20](tenant-agent-decommission-verification-2026-10-20.md), [tenant-isolation-configuration-verification-2026-10-21](tenant-isolation-configuration-verification-2026-10-21.md).

### raw-session-template

**Owner:** Canonical tenant Runtime adapters; immutable approved built-in templates; retained physical store primitives.

IMemoryStore is physical storage behind the canonical tenant adapter, not a public authorizer. Canonical Runtime source is tenant-bound. IBuiltInAgentDefinitionStore is approved read-only shared configuration. Unowned snapshots/Profile/test primitives cannot admit a current tenant Run or become a private fallback.

**Mapped tables:** No additional table; this family coordinates existing owned roots..

**Store ports:** `IAgentDefinitionStore`, `IBuiltInAgentDefinitionStore`, `IMemoryStore`, `IAttachmentStore`.

**Existing evidence:** [global-session-profile-authority-retirement-verification-2026-10-23](global-session-profile-authority-retirement-verification-2026-10-23.md), [global-definition-authority-retirement-verification-2026-10-23](global-definition-authority-retirement-verification-2026-10-23.md).

### public-http

**Owner:** TenantHttpEffectService; TenantPublicWebReadService.

Public research/downloads use bounded network/DNS/redirect/content policies and current task/source controls. General public HTTP mutation additionally requires the exact current operation/consent and durable dispatch/receipt boundary; no bound private account or raw provider fallback.

**Mapped tables:** No additional table; this family coordinates existing owned roots..

**Store ports:** `ITenantHttpOperationStore`.

**Existing evidence:** [tenant-public-http-verification-2026-10-23](tenant-public-http-verification-2026-10-23.md), [tenant-public-web-read-verification-2026-10-23](tenant-public-web-read-verification-2026-10-23.md).

## Worker/recovery ingress

| Registered entry | Current source/boundary |
| --- | --- |
| [OpenSandboxHostedService](../../src/AgentCore.Api/OpenSandboxHostedService.cs) | Refreshes readiness and reconciles durable Sandbox evidence; never admits a model task. |
| [TenantRequestIsolationHostedService](../../src/AgentCore.Api/TenantRequestIsolationHostedService.cs) | Pages exact tenant request owners; canonical host reservation settles each current quiescent Session. |
| [SessionShutdownHostedService](../../src/AgentCore.Api/SessionShutdownHostedService.cs) | Drains the same canonical TenantSessionRuntimeHost; no independent registry/authority. |
| [WorkspaceCleanupHostedService](../../src/AgentCore.Api/WorkspaceCleanupHostedService.cs) | Calls exact Agent decommission, Browser retirement, working-file, Artifact and Attachment cleanup owners. Saved proofs survive commit-to-filesystem gaps. |
| [TriggerSchedulerHostedService](../../src/AgentCore.Api/TriggerSchedulerHostedService.cs) | Calls TenantAutomationService and TenantEventProcessor; current source/owner admission replaces global live-event authority. |
| [EmailWatchHostedService](../../src/AgentCore.Api/EmailWatchHostedService.cs) | Uses TenantEmailMonitoringService and current bound account/watch authority; no global author or Profile fallback. |
| [AgentRunHostedService](../../src/AgentCore.Api/AgentRunHostedService.cs) | Processes current pending approvals, owned credential/Browser/workspace/account/HTTP receipts and ITenantRunExecution; only exact saved operations may resume. |
| [NativePlaywrightBrowser](../../src/AgentCore.Infrastructure/Browser/NativePlaywrightBrowser.cs) | Native process/profile lifecycle implements the current binding/target/retirement contracts. Possessing its instance does not authorize a model operation. |
| [TenantSandboxVerificationCleanupWorker](../../src/AgentCore.Infrastructure/Security/TenantSandboxVerificationCleanupWorker.cs) | Retires saved technical verification claims independently of catalog/readiness; unknown files/initialization are not repeated. |
| [CredentialOAuthExpiryWorker](../../src/AgentCore.Infrastructure/Security/CredentialOAuthExpiryWorker.cs) | Only expires private OAuth attempts; carries no copied human or effect grant. |

## External effects and private I/O

| Boundary | Port files | Current enforcement |
| --- | --- | --- |
| Language-model generation | [LanguageModel.cs](../../src/AgentCore.Application/Ports/LanguageModel.cs) | Canonical Runtime uses its pinned configuration/current execution/source fence. Isolated Definition evaluation is a separately authorized Synthetic use case. |
| Speech recognition and synthesis | [Speech.cs](../../src/AgentCore.Application/Ports/Speech.cs) | Owned voice lease/resources, normalized callbacks, response/utterance/generation retirement and received/heard delivery; raw audio bypasses the domain mailbox. |
| Browser commands and observations | [Browser.cs](../../src/AgentCore.Application/Ports/Browser.cs), [TenantBrowser.cs](../../src/AgentCore.Application/Ports/TenantBrowser.cs) | Current exact native owner/handle/target/origin and command/read/consent gates. Arbitrary browser interaction is coarse privileged external access, not proof of a typed business action. |
| Browser password resolution | [Browser.cs](../../src/AgentCore.Application/Ports/Browser.cs), [TenantBrowser.cs](../../src/AgentCore.Application/Ports/TenantBrowser.cs) | Bound Password is resolved inside trusted Core/native target fencing; no secret/callback in model arguments or receipts. |
| Browser profile reset and retirement | [TenantBrowserProfile.cs](../../src/AgentCore.Application/Ports/TenantBrowserProfile.cs), [TenantBrowser.cs](../../src/AgentCore.Application/Ports/TenantBrowser.cs) | Original-human reset or saved exact retirement proof, current references and provider observation; cannot grant model command authority. |
| Sandbox setup/image verification | [TenantSandboxVerification.cs](../../src/AgentCore.Application/Ports/TenantSandboxVerification.cs), [SandboxImageAuthority.cs](../../src/AgentCore.Application/Ports/SandboxImageAuthority.cs) | Owned verified candidate, registry identity and technical cleanup claim; network/image access does not grant model shell execution. |
| Sandbox command/process/reset | [TenantSandboxRuntime.cs](../../src/AgentCore.Application/Ports/TenantSandboxRuntime.cs), [SandboxService.cs](../../src/AgentCore.Application/Ports/SandboxService.cs) | Actual tenant/Session environment lease/generation, exact command/control proof and bounded factual receipts. Shell identity remains coarse; uncertainty is not success. |
| Sandbox file materialization | [TenantSandboxRuntime.cs](../../src/AgentCore.Application/Ports/TenantSandboxRuntime.cs) | Core chooses workload identity and exact eligible owned source paths before provider dispatch; no arbitrary host-root selection. |
| Workspace file/structure changes | [TenantWorkspaceMutations.cs](../../src/AgentCore.Application/Ports/TenantWorkspaceMutations.cs), [TenantWorkspaceOperations.cs](../../src/AgentCore.Application/Ports/TenantWorkspaceOperations.cs) | Canonical resolved owner/path, exact source/read/write/consent witnesses and filesystem safety; truthful partial/unknown outcomes. |
| Attachment ingestion | [Stores.cs](../../src/AgentCore.Application/Ports/Stores.cs), [TenantAttachments.cs](../../src/AgentCore.Application/Ports/TenantAttachments.cs) | Actual Session binding, bounded type/size/parser, current original human or trusted Runtime mutation witness; no private file adoption by ID. |
| Office inspect/render/export | [Office.cs](../../src/AgentCore.Application/Ports/Office.cs), [TenantWorkspaceArtifactSources.cs](../../src/AgentCore.Application/Ports/TenantWorkspaceArtifactSources.cs) | Current source reads and immutable output/source provenance. Processing a file grants no Artifact publication/recipient permission. |
| Artifact publish/delete/blob delivery | [TenantArtifacts.cs](../../src/AgentCore.Application/Ports/TenantArtifacts.cs), [TenantArtifactOperations.cs](../../src/AgentCore.Application/Ports/TenantArtifactOperations.cs), [TenantArtifactCleanup.cs](../../src/AgentCore.Application/Ports/TenantArtifactCleanup.cs) | Source verification, independent create/read/recipient permissions and exact cleanup proof surround physical byte I/O. |
| Public search | [PublicWeb.cs](../../src/AgentCore.Application/Ports/PublicWeb.cs), [PublicNetworkPolicy.cs](../../src/AgentCore.Application/Ports/PublicNetworkPolicy.cs) | Typed public research owner applies current eligibility and bounded provider/DNS/redirect/content rules. |
| Public fetch and download | [PublicWeb.cs](../../src/AgentCore.Application/Ports/PublicWeb.cs), [PublicNetworkPolicy.cs](../../src/AgentCore.Application/Ports/PublicNetworkPolicy.cs) | Current task/destination and network/source witnesses; published/downloaded bytes retain actual source evidence. |
| General public HTTP | [PublicWeb.cs](../../src/AgentCore.Application/Ports/PublicWeb.cs), [TenantHttpOperations.cs](../../src/AgentCore.Application/Ports/TenantHttpOperations.cs) | Current http.request permission, exact sensitive operation consent, bounded public network rules, dispatch and recovery receipt; no private credential borrowing. |
| Email read/draft/send | [Email.cs](../../src/AgentCore.Application/Ports/Email.cs), [TenantConnectedAccounts.cs](../../src/AgentCore.Application/Ports/TenantConnectedAccounts.cs), [TenantAccountOperations.cs](../../src/AgentCore.Application/Ports/TenantAccountOperations.cs) | Actual owned binding/purpose/scopes and current read/compound exact send consent; provider draft possession grants no authority. |
| Drive read/transfer/mutation | [Drive.cs](../../src/AgentCore.Application/Ports/Drive.cs), [TenantConnectedAccounts.cs](../../src/AgentCore.Application/Ports/TenantConnectedAccounts.cs), [TenantAccountOperations.cs](../../src/AgentCore.Application/Ports/TenantAccountOperations.cs) | Current owned account/source/target authority and exact compound operation; changed/revoked scope blocks old preparation. |
| Credential test, OAuth exchange/refresh/disconnect | [TenantCredentialChecks.cs](../../src/AgentCore.Application/Ports/TenantCredentialChecks.cs), [TenantCredentialOAuth.cs](../../src/AgentCore.Application/Ports/TenantCredentialOAuth.cs) | Current original login/tenant/purpose/state/binding epoch and exact saved dispatch; expiry can only retire proofs. |
| Protected value persistence | [Credentials.cs](../../src/AgentCore.Application/Ports/Credentials.cs) | Credential protector/keys are physical mechanisms; current service owner decides use. No secret-bearing preview/log/model projection. |
| Private source/context reads | [TenantResourceSourceContent.cs](../../src/AgentCore.Application/Ports/TenantResourceSourceContent.cs), [TenantWorkspaceReads.cs](../../src/AgentCore.Application/Ports/TenantWorkspaceReads.cs), [TenantContinuity.cs](../../src/AgentCore.Application/Ports/TenantContinuity.cs) | Current exact source ownership/read rights and source-specific continuity around I/O; Composer Evidence does not activate private Skill procedures. |

## Files, catalogs and process-local state

The [diagnostic/process-state follow-up](tenant-diagnostics-isolation-verification-2026-10-24.md) records the selected current cache key/owner call chains, actual native tenant-profile isolation and the common content-free exception sink correction. It supersedes the prior raw-exception logging behavior; its 187 scoped cases do not establish complete method/effect acceptance.

- Workspace directories use the actual Instance/Session physical layout. `FileAgentInstanceWorkspaceStore.Tenant` resolves the supplied tenant/Instance/Session against current tenant stores before selecting that layout. A TenantId path segment is not substituted for this ownership proof.
- Persistent Browser profiles explicitly use `tenants/{tenant}/agents/{agent}` under the configured profile root. Provider epoch, executor/owner identity, current profile/reset/command observations and no-link checks constrain reuse and deletion.
- Definition-resource blobs are immutable digest-addressed physical bytes. The digest is neither a private source ID nor a read grant. Current typed resource ownership/binding/read checks select eligible bytes; `ReadVerifiedResourceSourceAsync` also applies bounded no-follow/hard-link and hash/size validation.
- Attachment, Artifact, working-file and native retirement use their separate exact owner/reference proofs. `PersistenceOptions` declares the configured database, blob/workspace and protection-key roots; none was inspected as user data, reset or modified by this audit.
- Built-in Definition templates, model catalog metadata and immutable capability descriptors are shared read-only configuration. Tenant selections, resource/Skill catalogs, bound account metadata and current Runtime/Browser/Sandbox state retain actual owner/source checks. Current source references locate the caches and projections; their complete collision/race qualification remains part of final acceptance.
- Tenant audit and explicit aggregate System diagnostics have separate authority. Existing structured safe-log/diagnostic mechanisms remain; a source census alone cannot establish every emitted field is non-sensitive. No provider/log export was performed.

## Verification and remaining acceptance work

Static checks enumerate every snapshot Entity→ToTable pair and every public interface whose name ends in Store under Application/Ports, assert unique complete family assignment, validate declaration/reference/migration/worker/port/report paths, and parse the saved JSON. All named owner classes in the index resolve in current source. Changed-document links/fragments and fences plus unstaged/staged whitespace checks pass.

This is documentation-only source/evidence indexing. No application build, fresh database migration, integration/browser test, provider call, CI or installed-data reset is counted for this change. The 81 entities are the current EF snapshot census, not a fresh SQLite physical-schema observation. Existing reports retain their exact observed results and limitations.

The inventory makes the remaining final audit finite but does not close it: verify method-level current call chains and negative evidence for each family (including indirect native privileges, cache collisions, audit payloads and cleanup), reconcile source/model/migration state against a disposable current database, finish safe consolidation and current interface documentation, then execute the proposal-wide security/isolation/function and rendered UI/design acceptance matrix. Existing old owner-route refusal, removed actor names and inert historical rows must be assessed separately; no legacy authority is accepted merely because a row or interface remains.

The original goal stays active. No current application behavior, permission default, approval lifetime, schema, migration, serializer, UI/design source or agent tooling changed.

## Executed follow-up

[Canonical schema and cross-family boundary qualification](tenant-schema-boundary-verification-2026-10-24.md) now supplies fresh physical SQLite/model/startup and 135 distinct focused case results. It clarifies the 95 migration IDs versus 96 non-Designer source files. The source census above remains separate from that measured scope and from final acceptance.
