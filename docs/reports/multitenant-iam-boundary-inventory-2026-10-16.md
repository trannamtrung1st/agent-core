# Historical multi-tenant IAM boundary inventory — 2026-10-16

This is the original baseline snapshot: 231 HTTP/Hub mapping sites, 42 DbSets and 101 primary catalog constants. Its pending statuses and source line addresses are historical audit inputs, not current route, authority or acceptance claims. Baseline source addresses are retained as inert code references because files and line numbers changed during cutover.

The [delivery ledger](multitenant-iam-delivery-2026-10-16.md) owns current module evidence and remaining integration gates, including the [current route/Hub inventory checkpoint](multitenant-iam-delivery-2026-10-16.md#current-boundary-inventory-checkpoint). Repeated chronological successor notes have been consolidated there. [System Architecture](../03-system-architecture.md), [Backend Interfaces](../04-backend-interfaces.md) and [HTTP API and Realtime Protocol](../14-api-and-realtime-protocol.md) own current behavior. Exhaustive tool-dispatch/store-authority closure and proposal-wide acceptance remain required; this historical snapshot does not supply them.

## Baseline boundary ownership and required enforcement

| Family | Trusted owner / tenant | Principal / permissions | Policy and verification |
| --- | --- | --- | --- |
| HTTP tenant resources (all mapped sites below) | Tenant route + persisted resource owner; explicit scope on every ID lookup/list | Current authenticated tenant User; resource action permission | Service checks as well as route checks; 401, cross-tenant IDOR/list/count/download and revoked-role tests |
| Chat, control, audio and receipts | Connection pinned to validated tenant + Session + Instance | Current User; `agents.chat`, Session read/control as applicable | Attach and every invocation; revocation and old connection/lease checks; raw audio stays separate from mailbox |
| Definition/template catalog | Immutable platform builtin templates; tenant drafts/publications/resources | `agent-definitions.*`, `resources.*` | Templates confer no IAM; no private publication leakage |
| Credentials, Email, Drive OAuth | Credential tenant, initiating Account/session/tenant, state nonce and purpose | `credentials.manage/bind/use`, typed email/Drive action | Callback current membership/revision, separate purpose cookies, secret never in model/public metadata |
| Webhooks | Tenant-bound registered ingress + purpose token | Trusted service ingress; target agent principal at admission | No interactive-user authority from token; cross-tenant event keys and replay/dedupe negative tests |
| Agent tools (all catalog entries below) | Trusted execution context + typed concrete resource | Executing agent own IAM, capability and binding checks | Requester-aware approval, exact grants only for delegatable typed operations, fresh checks before effect |
| Scheduler / email watcher / occurrence intake | Enumerate active tenant-owned schedules/accounts/events explicitly | Trusted worker context, then actual agent SystemIdentity | No ambient default tenant or copied user cookie; tenant-bound receipts/claims/reentry |
| Background Runs / continuation / report-back | Immutable persisted tenant/executor/initiator/root/parent | Actual executor and current recipient permission | Root inherited for children; independent occurrences new root; report-back visibility checked |
| Cleanup / lifecycle / TTL / recovery | Explicit tenant+resource owner per unit | Narrow platform worker context; no broad tenant data projection | Paths, claims, tombstones and purge receipts tenant-bound; no reset of live paths |
| Browser / sandbox | Tenant + agent + Session generation; tenant-partitioned credential/profile/mount roots | Coarse browser/sandbox permissions plus existing gates | Cannot claim exact business-effect control over arbitrary shell/browser; prohibited privileged combinations documented |
| Office / workspace / attachments / artifacts | Tenant + instance/session/source owner and recipient | `workspaces.*`, `resources.*`, attachment/session permissions | Path isolation, canonical file metadata and download/preview authorization; stale references and other-tenant IDs denied |
| Platform / Account / health / static SPA | Account or explicitly System scope; health/static have no private data | System operations explicitly assigned; Account self-service | Platform operator cannot read tenant-private data; public health redacted; SPA does not authenticate private API |
| Audit, logs, caches and telemetry | Tenant/actor attached; shared metadata explicitly read-only | `security.audit.read` or aggregate system diagnostics | No secret-bearing body/query logging, cross-tenant cache collisions or private metric labels |

## Baseline HTTP and Hub mapping sites

These route expressions, source addresses and statuses were captured before canonical cutover. They preserve the original audit inputs; they do not describe current mappings or accepted behavior. Canonical transport contracts belong to [HTTP API and Realtime Protocol](../14-api-and-realtime-protocol.md). OAuth callbacks, Synthetic routes and hooks retain purpose-specific validation requirements.

| Source | Method | Source route expression | Receiver | Target owner | Baseline status |
| --- | --- | --- | --- | --- | --- |
| `src/AgentCore.Api/AdminAutomationEndpoints.cs:23` | GET | `"/core-event-types"` | `group` | Tenant resource | Pending cutover |
| `src/AgentCore.Api/AdminAutomationEndpoints.cs:33` | POST | `"/filter-test"` | `group` | Tenant resource | Pending cutover |
| `src/AgentCore.Api/AdminAutomationEndpoints.cs:40` | GET | `"/{automationId:guid}/unreviewed-sources"` | `group` | Tenant resource | Pending cutover |
| `src/AgentCore.Api/AdminAutomationEndpoints.cs:46` | GET | `"/deliveries"` | `group` | Tenant resource | Pending cutover |
| `src/AgentCore.Api/AdminAutomationEndpoints.cs:48` | GET | `"/presets"` | `group` | Tenant resource | Pending cutover |
| `src/AgentCore.Api/AdminAutomationEndpoints.cs:52` | GET | `""` | `group` | Tenant resource | Pending cutover |
| `src/AgentCore.Api/AdminAutomationEndpoints.cs:84` | POST | `""` | `group` | Tenant resource | Pending cutover |
| `src/AgentCore.Api/AdminAutomationEndpoints.cs:86` | PUT | `"/{automationId:guid}"` | `group` | Tenant resource | Pending cutover |
| `src/AgentCore.Api/AdminAutomationEndpoints.cs:88` | DELETE | `"/{automationId:guid}"` | `group` | Tenant resource | Pending cutover |
| `src/AgentCore.Api/AdminAutomationEndpoints.cs:90` | POST | `"/{automationId:guid}/run"` | `group` | Tenant resource | Pending cutover |
| `src/AgentCore.Api/Security/SystemBrowserPrivacyEndpoints.cs:13` | GET | `"/api/v2/system/browser/privacy"` | `app` | System governance | [Converted System owner](system-browser-privacy-verification-2026-10-20.md) |
| `src/AgentCore.Api/Security/SystemBrowserPrivacyEndpoints.cs:15` | PUT | `"/api/v2/system/browser/privacy"` | `app` | System governance | [Converted System owner](system-browser-privacy-verification-2026-10-20.md) |
| `src/AgentCore.Api/AdminCredentialEndpoints.cs:16` | GET | `"/email-accounts/{credentialId:guid}/monitoring"` | `group` | Tenant resource | Pending cutover |
| `src/AgentCore.Api/AdminCredentialEndpoints.cs:18` | PUT | `"/email-accounts/{credentialId:guid}/monitoring"` | `group` | Tenant resource | Pending cutover |
| `src/AgentCore.Api/AdminCredentialEndpoints.cs:20` | POST | `"/email-accounts/{credentialId:guid}/monitoring/check"` | `group` | Tenant resource | Pending cutover |
| `src/AgentCore.Api/AdminCredentialEndpoints.cs:22` | GET | `"/email-accounts/oauth/configuration"` | `group` | Tenant resource | Pending cutover |
| `src/AgentCore.Api/AdminCredentialEndpoints.cs:23` | GET | `"/email-accounts/{credentialId:guid}/connection"` | `group` | Tenant resource | Pending cutover |
| `src/AgentCore.Api/AdminCredentialEndpoints.cs:24` | POST | `"/email-accounts/{credentialId:guid}/oauth/start"` | `group` | Tenant resource | Pending cutover |
| `src/AgentCore.Api/AdminCredentialEndpoints.cs:32` | POST | `"/email-accounts/{credentialId:guid}/connection/check"` | `group` | Tenant resource | Pending cutover |
| `src/AgentCore.Api/AdminCredentialEndpoints.cs:34` | POST | `"/email-accounts/{credentialId:guid}/oauth/disconnect"` | `group` | Tenant resource | Pending cutover |
| `src/AgentCore.Api/AdminCredentialEndpoints.cs:36` | POST | `"/email-accounts"` | `group` | Tenant resource | Pending cutover |
| `src/AgentCore.Api/AdminCredentialEndpoints.cs:38` | PATCH | `"/email-accounts/{credentialId:guid}"` | `group` | Tenant resource | Pending cutover |
| `src/AgentCore.Api/AdminCredentialEndpoints.cs:43` | POST | `"/email-accounts/{credentialId:guid}/test"` | `group` | Tenant resource | Pending cutover |
| `src/AgentCore.Api/AdminCredentialEndpoints.cs:48` | GET | `"/email-send-operations/{draftId}"` | `group` | Tenant resource | Pending cutover |
| `src/AgentCore.Api/AdminCredentialEndpoints.cs:55` | GET | `"/credentials"` | `group` | Tenant resource | Pending cutover |
| `src/AgentCore.Api/AdminCredentialEndpoints.cs:56` | GET | `"/credentials/{credentialId:guid}"` | `group` | Tenant resource | Pending cutover |
| `src/AgentCore.Api/AdminCredentialEndpoints.cs:57` | PATCH | `"/container-registry-credentials/{credentialId:guid}"` | `group` | Tenant resource | Pending cutover |
| `src/AgentCore.Api/AdminCredentialEndpoints.cs:60` | POST | `"/container-registry-credentials"` | `group` | Tenant resource | Pending cutover |
| `src/AgentCore.Api/AdminCredentialEndpoints.cs:63` | POST | `"/credentials"` | `group` | Tenant resource | Pending cutover |
| `src/AgentCore.Api/AdminCredentialEndpoints.cs:64` | PATCH | `"/credentials/{credentialId:guid}"` | `group` | Tenant resource | Pending cutover |
| `src/AgentCore.Api/AdminCredentialEndpoints.cs:66` | PUT | `"/credentials/{credentialId:guid}/value"` | `group` | Tenant resource | Pending cutover |
| `src/AgentCore.Api/AdminCredentialEndpoints.cs:68` | DELETE | `"/credentials/{credentialId:guid}"` | `group` | Tenant resource | Pending cutover |
| `src/AgentCore.Api/AdminCredentialEndpoints.cs:70` | GET | `"/agent-instances/{instanceId:guid}/credential-bindings"` | `group` | Tenant resource | Pending cutover |
| `src/AgentCore.Api/AdminCredentialEndpoints.cs:71` | POST | `"/agent-instances/{instanceId:guid}/credential-bindings"` | `group` | Tenant resource | Pending cutover |
| `src/AgentCore.Api/AdminCredentialEndpoints.cs:73` | DELETE | `"/agent-instances/{instanceId:guid}/credential-bindings/{bindingId:guid}"` | `group` | Tenant resource | Pending cutover |
| `src/AgentCore.Api/AdminCredentialEndpoints.cs:75` | POST | `"/agent-instances/{instanceId:guid}/browser-profile/reset"` | `group` | Tenant resource | Pending cutover |
| `src/AgentCore.Api/AdminEndpoints.cs:113` | GET | `"/definitions"` | `group` | Tenant resource | Pending cutover |
| `src/AgentCore.Api/AdminEndpoints.cs:121` | GET | `"/definitions/{definitionId}/versions/{version:int}"` | `group` | Tenant resource | Pending cutover |
| `src/AgentCore.Api/AdminEndpoints.cs:139` | GET | `"/instances"` | `group` | Tenant resource | Pending cutover |
| `src/AgentCore.Api/AdminEndpoints.cs:147` | GET | `"/authoring-options"` | `group` | Tenant resource | Pending cutover |
| `src/AgentCore.Api/AdminEndpoints.cs:150` | DELETE | `"/definitions/{definitionId}"` | `group` | Tenant resource | Pending cutover |
| `src/AgentCore.Api/AdminEndpoints.cs:195` | GET | `"/tools"` | `group` | Tenant resource | Pending cutover |
| `src/AgentCore.Api/AdminEndpoints.cs:205` | GET | `"/events"` | `group` | Tenant resource | Pending cutover |
| `src/AgentCore.Api/AdminEndpoints.cs:218` | GET | `"/instances/{instanceId:guid}/effective-config"` | `group` | Tenant resource | Pending cutover |
| `src/AgentCore.Api/AdminEndpoints.cs:235` | POST | `"/agent-instances"` | `group` | Tenant resource | Pending cutover |
| `src/AgentCore.Api/AdminEndpoints.cs:273` | PATCH | `"/agent-instances/{instanceId:guid}/persona"` | `group` | Tenant resource | Pending cutover |
| `src/AgentCore.Api/AdminEndpoints.cs:302` | DELETE | `"/agent-instances/{instanceId:guid}"` | `group` | Tenant resource | Pending cutover |
| `src/AgentCore.Api/AdminEndpoints.cs:333` | PATCH | `"/agent-instances/{instanceId:guid}/lifecycle"` | `group` | Tenant resource | Pending cutover |
| `src/AgentCore.Api/AdminEndpoints.cs:370` | GET | `"/agent-instances/{instanceId:guid}/active-version/preview"` | `group` | Tenant resource | Pending cutover |
| `src/AgentCore.Api/AdminEndpoints.cs:376` | PATCH | `"/agent-instances/{instanceId:guid}/active-version"` | `group` | Tenant resource | Pending cutover |
| `src/AgentCore.Api/AdminEndpoints.cs:408` | GET | `"/agent-instances/{instanceId:guid}/learned-memory"` | `group` | Tenant resource | Pending cutover |
| `src/AgentCore.Api/AdminEndpoints.cs:427` | GET | `"/agent-instances/{instanceId:guid}/learned-memory/{memoryId:guid}"` | `group` | Tenant resource | Pending cutover |
| `src/AgentCore.Api/AdminEndpoints.cs:434` | DELETE | `"/agent-instances/{instanceId:guid}/learned-memory/{memoryId:guid}"` | `group` | Tenant resource | Pending cutover |
| `src/AgentCore.Api/AdminEndpoints.cs:460` | GET | `"/execution-budget-limits"` | `group` | Tenant resource | Pending cutover |
| `src/AgentCore.Api/AdminEndpoints.cs:461` | GET | `"/agent-instances/{instanceId:guid}/execution-budgets"` | `group` | Tenant resource | Pending cutover |
| `src/AgentCore.Api/AdminEndpoints.cs:477` | POST | `"/agent-instances/{instanceId:guid}/execution-budgets"` | `group` | Tenant resource | Pending cutover |
| `src/AgentCore.Api/AdminEndpoints.cs:491` | POST | `"/agent-instances/{instanceId:guid}/unattended-model"` | `group` | Tenant resource | Pending cutover |
| `src/AgentCore.Api/AdminEndpoints.cs:518` | POST | `"/agent-instances/{instanceId:guid}/learned-memory/reset"` | `group` | Tenant resource | Pending cutover |
| `src/AgentCore.Api/AdminEndpoints.cs:558` | GET | `"/definition-drafts"` | `group` | Tenant resource | Pending cutover |
| `src/AgentCore.Api/AdminEndpoints.cs:566` | GET | `"/definition-drafts/{draftId:guid}"` | `group` | Tenant resource | Pending cutover |
| `src/AgentCore.Api/AdminEndpoints.cs:582` | POST | `"/definition-drafts"` | `group` | Tenant resource | Pending cutover |
| `src/AgentCore.Api/AdminEndpoints.cs:600` | POST | `"/definition-drafts/new"` | `group` | Tenant resource | Pending cutover |
| `src/AgentCore.Api/AdminEndpoints.cs:617` | POST | `"/definition-drafts/fork"` | `group` | Tenant resource | Pending cutover |
| `src/AgentCore.Api/AdminEndpoints.cs:643` | PUT | `"/definition-drafts/{draftId:guid}"` | `group` | Tenant resource | Pending cutover |
| `src/AgentCore.Api/AdminEndpoints.cs:666` | DELETE | `"/definition-drafts/{draftId:guid}"` | `group` | Tenant resource | Pending cutover |
| `src/AgentCore.Api/AdminEndpoints.cs:683` | POST | `"/definition-drafts/{draftId:guid}/validate"` | `group` | Tenant resource | Pending cutover |
| `src/AgentCore.Api/AdminEndpoints.cs:699` | GET | `"/definition-drafts/{draftId:guid}/evaluation-scenarios"` | `group` | Tenant resource | Pending cutover |
| `src/AgentCore.Api/AdminEndpoints.cs:715` | PUT | `"/definition-drafts/{draftId:guid}/evaluation-scenarios"` | `group` | Tenant resource | Pending cutover |
| `src/AgentCore.Api/AdminEndpoints.cs:752` | DELETE | `"/definition-drafts/{draftId:guid}/evaluation-scenarios/{scenarioId}"` | `group` | Tenant resource | Pending cutover |
| `src/AgentCore.Api/AdminEndpoints.cs:771` | POST | `"/definition-drafts/{draftId:guid}/evaluation-scenarios/{scenarioId}/run"` | `group` | Tenant resource | Pending cutover |
| `src/AgentCore.Api/AdminEndpoints.cs:789` | GET | `"/definition-drafts/{draftId:guid}/evaluation-results"` | `group` | Tenant resource | Pending cutover |
| `src/AgentCore.Api/AdminEndpoints.cs:805` | GET | `"/definition-drafts/{draftId:guid}/diff"` | `group` | Tenant resource | Pending cutover |
| `src/AgentCore.Api/AdminEndpoints.cs:821` | POST | `"/definition-drafts/{draftId:guid}/publish"` | `group` | Tenant resource | Pending cutover |
| `src/AgentCore.Api/AdminEndpoints.cs:842` | GET | `"/definitions/{definitionId}/publications"` | `group` | Tenant resource | Pending cutover |
| `src/AgentCore.Api/AdminEndpoints.cs:851` | POST | `"/definitions/{definitionId}/publications/{version:int}/deprecate"` | `group` | Tenant resource | Pending cutover |
| `src/AgentCore.Api/AdminEndpoints.cs:874` | GET | `"/definition-drafts/{draftId:guid}/resources"` | `group` | Tenant resource | Pending cutover |
| `src/AgentCore.Api/AdminEndpoints.cs:890` | POST | `"/definition-drafts/{draftId:guid}/resources/content"` | `group` | Tenant resource | Pending cutover |
| `src/AgentCore.Api/AdminEndpoints.cs:911` | PUT | `"/definition-drafts/{draftId:guid}/resources/batch"` | `group` | Tenant resource | Pending cutover |
| `src/AgentCore.Api/AdminEndpoints.cs:944` | PUT | `"/definition-drafts/{draftId:guid}/resources"` | `group` | Tenant resource | Pending cutover |
| `src/AgentCore.Api/AdminEndpoints.cs:973` | DELETE | `"/definition-drafts/{draftId:guid}/resources/{resourceId:guid}"` | `group` | Tenant resource | Pending cutover |
| `src/AgentCore.Api/AdminEndpoints.cs:996` | GET | `"/definition-drafts/{draftId:guid}/resources/{resourceId:guid}/content"` | `group` | Tenant resource | Pending cutover |
| `src/AgentCore.Api/AdminEndpoints.cs:1019` | GET | `"/definitions/{definitionId}/publications/{version:int}/resources"` | `group` | Tenant resource | Pending cutover |
| `src/AgentCore.Api/AdminWebhookEventEndpoints.cs:16` | GET | `"/catalog"` | `group` | Tenant resource | Pending cutover |
| `src/AgentCore.Api/AdminWebhookEventEndpoints.cs:17` | GET | `"/catalog/builtin/{key}"` | `group` | Tenant resource | Pending cutover |
| `src/AgentCore.Api/AdminWebhookEventEndpoints.cs:19` | GET | `""` | `group` | Tenant resource | Pending cutover |
| `src/AgentCore.Api/AdminWebhookEventEndpoints.cs:31` | GET | `"/{eventId:guid}"` | `group` | Tenant resource | Pending cutover |
| `src/AgentCore.Api/AdminWebhookEventEndpoints.cs:45` | POST | `""` | `group` | Tenant resource | Pending cutover |
| `src/AgentCore.Api/AdminWebhookEventEndpoints.cs:47` | PUT | `"/{eventId:guid}"` | `group` | Tenant resource | Pending cutover |
| `src/AgentCore.Api/AdminWebhookEventEndpoints.cs:49` | POST | `"/{eventId:guid}/rotate"` | `group` | Tenant resource | Pending cutover |
| `src/AgentCore.Api/AdminWebhookEventEndpoints.cs:51` | POST | `"/{eventId:guid}/revoke"` | `group` | Tenant resource | Pending cutover |
| `src/AgentCore.Api/AgentInstanceResourceEndpoints.cs:13` | GET | `""` | `g` | Tenant resource | Pending cutover |
| `src/AgentCore.Api/AgentInstanceResourceEndpoints.cs:14` | GET | `"/{key}"` | `g` | Tenant resource | Pending cutover |
| `src/AgentCore.Api/AgentInstanceResourceEndpoints.cs:16` | GET | `"/{key}/content"` | `g` | Tenant resource | Pending cutover |
| `src/AgentCore.Api/AgentInstanceResourceEndpoints.cs:24` | POST | `""` | `g` | Tenant resource | Pending cutover |
| `src/AgentCore.Api/AgentInstanceResourceEndpoints.cs:25` | POST | `"/{key}/copy"` | `g` | Tenant resource | Pending cutover |
| `src/AgentCore.Api/AgentInstanceResourceEndpoints.cs:27` | PATCH | `"/{key}"` | `g` | Tenant resource | Pending cutover |
| `src/AgentCore.Api/AgentInstanceResourceEndpoints.cs:28` | PUT | `"/{key}/enabled"` | `g` | Tenant resource | Pending cutover |
| `src/AgentCore.Api/AgentInstanceResourceEndpoints.cs:30` | DELETE | `"/{key}/enabled-override"` | `g` | Tenant resource | Pending cutover |
| `src/AgentCore.Api/AgentInstanceResourceEndpoints.cs:32` | DELETE | `"/{key}"` | `g` | Tenant resource | Pending cutover |
| `src/AgentCore.Api/AgentInstanceSettingsEndpoints.cs:13` | GET | `""` | `group` | Tenant resource | Pending cutover |
| `src/AgentCore.Api/AgentInstanceSettingsEndpoints.cs:14` | GET | `"/{section}"` | `group` | Tenant resource | Pending cutover |
| `src/AgentCore.Api/AgentInstanceSettingsEndpoints.cs:16` | PATCH | `"/{section}"` | `group` | Tenant resource | Pending cutover |
| `src/AgentCore.Api/AgentInstanceSkillEndpoints.cs:16` | GET | `""` | `g` | Tenant resource | Pending cutover |
| `src/AgentCore.Api/AgentInstanceSkillEndpoints.cs:18` | GET | `"/{key}"` | `g` | Tenant resource | Pending cutover |
| `src/AgentCore.Api/AgentInstanceSkillEndpoints.cs:19` | POST | `""` | `g` | Tenant resource | Pending cutover |
| `src/AgentCore.Api/AgentInstanceSkillEndpoints.cs:20` | PATCH | `"/{key}"` | `g` | Tenant resource | Pending cutover |
| `src/AgentCore.Api/AgentInstanceSkillEndpoints.cs:21` | PUT | `"/{key}/enabled"` | `g` | Tenant resource | Pending cutover |
| `src/AgentCore.Api/AgentInstanceSkillEndpoints.cs:22` | DELETE | `"/{key}/enabled-override"` | `g` | Tenant resource | Pending cutover |
| `src/AgentCore.Api/AgentInstanceSkillEndpoints.cs:23` | DELETE | `"/{key}"` | `g` | Tenant resource | Pending cutover |
| `src/AgentCore.Api/AgentInstanceSkillEndpoints.cs:24` | POST | `"/{key}/customize"` | `g` | Tenant resource | Pending cutover |
| `src/AgentCore.Api/AgentWorkspaceEndpoints.cs:15` | GET | `""` | `home` | Tenant resource | Pending cutover |
| `src/AgentCore.Api/AgentWorkspaceEndpoints.cs:26` | GET | `"content"` | `home` | Tenant resource | Pending cutover |
| `src/AgentCore.Api/AgentWorkspaceEndpoints.cs:38` | GET | `"metadata"` | `home` | Tenant resource | Pending cutover |
| `src/AgentCore.Api/AgentWorkspaceEndpoints.cs:48` | DELETE | `"content"` | `home` | Tenant resource | Pending cutover |
| `src/AgentCore.Api/ArtifactEndpoints.cs:17` | GET | `""` | `group` | Tenant resource | Pending cutover |
| `src/AgentCore.Api/ArtifactEndpoints.cs:33` | GET | `"page"` | `group` | Tenant resource | Pending cutover |
| `src/AgentCore.Api/ArtifactEndpoints.cs:47` | GET | `"{artifactId:guid}"` | `group` | Tenant resource | Pending cutover |
| `src/AgentCore.Api/ArtifactEndpoints.cs:65` | GET | `"{artifactId:guid}/content"` | `group` | Tenant resource | Pending cutover |
| `src/AgentCore.Api/AttachmentEndpoints.cs:21` | GET | `""` | `group` | Tenant resource | Pending cutover |
| `src/AgentCore.Api/AttachmentEndpoints.cs:39` | POST | `""` | `group` | Tenant resource | Pending cutover |
| `src/AgentCore.Api/AttachmentEndpoints.cs:82` | GET | `"{attachmentId:guid}"` | `group` | Tenant resource | Pending cutover |
| `src/AgentCore.Api/AttachmentEndpoints.cs:102` | GET | `"{attachmentId:guid}/content"` | `group` | Tenant resource | Pending cutover |
| `src/AgentCore.Api/AttachmentEndpoints.cs:124` | POST | `"{attachmentId:guid}/materialize"` | `group` | Tenant resource | Pending cutover |
| `src/AgentCore.Api/AttachmentEndpoints.cs:142` | DELETE | `"{attachmentId:guid}"` | `group` | Tenant resource | Pending cutover |
| `src/AgentCore.Api/AttachmentEndpoints.cs:161` | POST | `"stage"` | `group` | Tenant resource | Pending cutover |
| `src/AgentCore.Api/BackgroundSessionEndpoints.cs:19` | GET | `"/api/v2/agent-instances/{instanceId:guid}/sessions"` | `app` | Tenant resource | Pending cutover |
| `src/AgentCore.Api/BackgroundSessionEndpoints.cs:30` | GET | `"/api/v2/agent-instances/{instanceId:guid}/sessions/{sessionId:guid}"` | `app` | Tenant resource | Pending cutover |
| `src/AgentCore.Api/BackgroundSessionEndpoints.cs:41` | GET | `""` | `owned` | Tenant resource | Pending cutover |
| `src/AgentCore.Api/BackgroundSessionEndpoints.cs:57` | GET | `""` | `instanceRuns` | Tenant resource | Pending cutover |
| `src/AgentCore.Api/BackgroundSessionEndpoints.cs:67` | GET | `"/{runId:guid}"` | `instanceRuns` | Tenant resource | Pending cutover |
| `src/AgentCore.Api/BackgroundSessionEndpoints.cs:74` | GET | `"/background"` | `sessions` | Tenant resource | Pending cutover |
| `src/AgentCore.Api/BackgroundSessionEndpoints.cs:82` | POST | `"/continue-in-chat"` | `sessions` | Tenant resource | Pending cutover |
| `src/AgentCore.Api/BackgroundSessionEndpoints.cs:91` | GET | `"/agent-runs"` | `sessions` | Tenant resource | Pending cutover |
| `src/AgentCore.Api/BackgroundSessionEndpoints.cs:100` | GET | `"/agent-runs/{runId:guid}"` | `sessions` | Tenant resource | Pending cutover |
| `src/AgentCore.Api/BackgroundSessionEndpoints.cs:103` | POST | `"/agent-runs/{runId:guid}/cancel"` | `sessions` | Tenant resource | Pending cutover |
| `src/AgentCore.Api/BackgroundSessionEndpoints.cs:112` | POST | `$"/agent-runs/{{runId:guid}}/approvals/{{approvalId:guid}}/{action}"` | `sessions` | Tenant resource | Pending cutover |
| `src/AgentCore.Api/ComposerEndpoints.cs:12` | GET | `"/api/v2/agent-instances/{instanceId:guid}/composer"` | `app` | Tenant resource | Pending cutover |
| `src/AgentCore.Api/ContinuityEndpoints.cs:25` | GET | `"/maintenance"` | `group` | Tenant resource | Pending cutover |
| `src/AgentCore.Api/ContinuityEndpoints.cs:27` | PUT | `"/maintenance"` | `group` | Tenant resource | Pending cutover |
| `src/AgentCore.Api/ContinuityEndpoints.cs:34` | GET | `"/experience"` | `group` | Tenant resource | Pending cutover |
| `src/AgentCore.Api/ContinuityEndpoints.cs:36` | PUT | `"/experience/configuration"` | `group` | Tenant resource | Pending cutover |
| `src/AgentCore.Api/ContinuityEndpoints.cs:44` | POST | `"/experience/checkpoints"` | `group` | Tenant resource | Pending cutover |
| `src/AgentCore.Api/ContinuityEndpoints.cs:53` | PUT | `"/experience/{experienceId:guid}"` | `group` | Tenant resource | Pending cutover |
| `src/AgentCore.Api/ContinuityEndpoints.cs:62` | POST | `"/experience/reset"` | `group` | Tenant resource | Pending cutover |
| `src/AgentCore.Api/DriveEndpoints.cs:14` | GET | `"/integrations/google-drive/options"` | `root` | Tenant resource | Pending cutover |
| `src/AgentCore.Api/DriveEndpoints.cs:16` | GET | `"/accounts"` | `g` | Tenant resource | Pending cutover |
| `src/AgentCore.Api/DriveEndpoints.cs:17` | GET | `"/accounts/{accountRef}/files"` | `g` | Tenant resource | Pending cutover |
| `src/AgentCore.Api/DriveEndpoints.cs:19` | GET | `"/accounts/{accountRef}/files/{fileId}"` | `g` | Tenant resource | Pending cutover |
| `src/AgentCore.Api/DriveEndpoints.cs:20` | GET | `"/accounts/{accountRef}/files/{fileId}/content"` | `g` | Tenant resource | Pending cutover |
| `src/AgentCore.Api/DriveEndpoints.cs:25` | GET | `"/accounts/{accountRef}/files/{fileId}/office-preview"` | `g` | Tenant resource | Pending cutover |
| `src/AgentCore.Api/DriveEndpoints.cs:31` | GET | `"/accounts/{accountRef}/operations/{operationId:guid}"` | `g` | Tenant resource | Pending cutover |
| `src/AgentCore.Api/EmailSelectionEndpoints.cs:19` | GET | `""` | `group` | Tenant resource | Pending cutover |
| `src/AgentCore.Api/EmailSelectionEndpoints.cs:38` | POST | `"/decision"` | `group` | Tenant resource | Pending cutover |
| `src/AgentCore.Api/GoogleDriveOAuthEndpoints.cs:21` | POST | `"/google-drive"` | `g` | Tenant OAuth initiator/state | Pending cutover |
| `src/AgentCore.Api/GoogleDriveOAuthEndpoints.cs:22` | GET | `"/{id:guid}/drive-connection"` | `g` | Tenant OAuth initiator/state | Pending cutover |
| `src/AgentCore.Api/GoogleDriveOAuthEndpoints.cs:23` | POST | `"/{id:guid}/drive-connect"` | `g` | Tenant OAuth initiator/state | Pending cutover |
| `src/AgentCore.Api/GoogleDriveOAuthEndpoints.cs:27` | POST | `"/{id:guid}/drive-check"` | `g` | Tenant OAuth initiator/state | Pending cutover |
| `src/AgentCore.Api/GoogleDriveOAuthEndpoints.cs:28` | POST | `"/{id:guid}/drive-disconnect"` | `g` | Tenant OAuth initiator/state | Pending cutover |
| `src/AgentCore.Api/GoogleDriveOAuthEndpoints.cs:29` | GET | `Root + "/callback"` | `app` | Tenant OAuth initiator/state | Pending cutover |
| `src/AgentCore.Api/GoogleDriveOAuthEndpoints.cs:30` | GET | `Root + "/synthetic"` | `app` | Tenant OAuth initiator/state | Pending cutover |
| `src/AgentCore.Api/GoogleDriveOAuthEndpoints.cs:35` | POST | `Root + "/synthetic"` | `app` | Tenant OAuth initiator/state | Pending cutover |
| `src/AgentCore.Api/GoogleEmailOAuthEndpoints.cs:16` | GET | `root + "/callback"` | `app` | Tenant OAuth initiator/state | Pending cutover |
| `src/AgentCore.Api/GoogleEmailOAuthEndpoints.cs:17` | GET | `root + "/synthetic"` | `app` | Tenant OAuth initiator/state | Pending cutover |
| `src/AgentCore.Api/GoogleEmailOAuthEndpoints.cs:25` | POST | `root + "/synthetic"` | `app` | Tenant OAuth initiator/state | Pending cutover |
| `src/AgentCore.Api/HarnessManagementEndpoints.cs:15` | GET | `""` | `management` | Tenant resource | Pending cutover |
| `src/AgentCore.Api/HarnessManagementEndpoints.cs:20` | PUT | `""` | `management` | Tenant resource | Pending cutover |
| `src/AgentCore.Api/HarnessManagementEndpoints.cs:35` | GET | `""` | `group` | Tenant resource | Pending cutover |
| `src/AgentCore.Api/HarnessManagementEndpoints.cs:37` | PUT | `"/policy"` | `group` | Tenant resource | Pending cutover |
| `src/AgentCore.Api/HarnessManagementEndpoints.cs:46` | POST | `"/prepare"` | `group` | Tenant resource | Pending cutover |
| `src/AgentCore.Api/HarnessManagementEndpoints.cs:52` | POST | `"/propose-shared"` | `group` | Tenant resource | Pending cutover |
| `src/AgentCore.Api/HarnessManagementEndpoints.cs:58` | POST | `"/verify"` | `group` | Tenant resource | Pending cutover |
| `src/AgentCore.Api/HarnessManagementEndpoints.cs:64` | POST | `"/cancel"` | `group` | Tenant resource | Pending cutover |
| `src/AgentCore.Api/HarnessManagementEndpoints.cs:70` | POST | `"/approvals/{approvalId:guid}"` | `group` | Tenant resource | Pending cutover |
| `src/AgentCore.Api/HarnessManagementEndpoints.cs:76` | POST | `"/publish-adopt"` | `group` | Tenant resource | Pending cutover |
| `src/AgentCore.Api/HookEndpoints.cs:9` | POST | `"/api/v1/hooks/{eventKey}"` | `app` | Tenant webhook registration/token | Pending cutover |
| `src/AgentCore.Api/HostSessionEndpoints.cs:17` | POST | `""` | `group` | Tenant resource | Pending cutover |
| `src/AgentCore.Api/HostSessionEndpoints.cs:61` | GET | `"{sessionId:guid}"` | `group` | Tenant resource | Pending cutover |
| `src/AgentCore.Api/HostSessionEndpoints.cs:85` | POST | `"{sessionId:guid}/lifecycle"` | `group` | Tenant resource | Pending cutover |
| `src/AgentCore.Api/LegacySessionEndpoints.cs:17` | GET | `"{sessionId:guid}"` | `group` | Tenant resource | Pending cutover |
| `src/AgentCore.Api/LegacySessionEndpoints.cs:36` | GET | `"{sessionId:guid}/messages"` | `group` | Tenant resource | Pending cutover |
| `src/AgentCore.Api/LegacySessionEndpoints.cs:62` | DELETE | `"{sessionId:guid}"` | `group` | Tenant resource | Pending cutover |
| `src/AgentCore.Api/OfficePreviewEndpoints.cs:17` | GET | `"/file-preview/options"` | `group` | Tenant resource | Pending cutover |
| `src/AgentCore.Api/OfficePreviewEndpoints.cs:18` | GET | `"/sessions/{sessionId:guid}/attachments/{attachmentId:guid}/office-preview"` | `group` | Tenant resource | Pending cutover |
| `src/AgentCore.Api/OfficePreviewEndpoints.cs:26` | GET | `"/sessions/{sessionId:guid}/artifacts/{artifactId:guid}/office-preview"` | `group` | Tenant resource | Pending cutover |
| `src/AgentCore.Api/OfficePreviewEndpoints.cs:33` | GET | `"/agent-instances/{instanceId:guid}/workspace/office-preview"` | `group` | Tenant resource | Pending cutover |
| `src/AgentCore.Api/OfficePreviewEndpoints.cs:42` | GET | `"/admin/agent-instances/{instanceId:guid}/resources/{key}/office-preview"` | `group` | Tenant resource | Pending cutover |
| `src/AgentCore.Api/OfficePreviewEndpoints.cs:49` | GET | `"/admin/definition-drafts/{draftId:guid}/resources/{resourceId:guid}/office-preview"` | `group` | Tenant resource | Pending cutover |
| `src/AgentCore.Api/ProfileEndpoints.cs:16` | GET | `""` | `group` | Authenticated Account profile | Pending cutover |
| `src/AgentCore.Api/ProfileEndpoints.cs:31` | PATCH | `""` | `group` | Authenticated Account profile | Pending cutover |
| `src/AgentCore.Api/Program.cs:144` | HUB | `"/hubs/session");` | `app` | Tenant Session/agent | Pending cutover |
| `src/AgentCore.Api/Program.cs:170` | GET | `"/health"` | `app` | Public health/static or tenant agent catalog | Pending cutover |
| `src/AgentCore.Api/Program.cs:177` | GET | `"/api/v1/agents"` | `app` | Tenant definition catalog | Pending cutover |
| `src/AgentCore.Api/Program.cs:183` | GET | `"/api/v1/agents/{agentId}"` | `app` | Tenant definition catalog | Pending cutover |
| `src/AgentCore.Api/RuntimeStateEndpoints.cs:11` | GET | `"/agent-instances/{instanceId:guid}/state"` | `admin` | Tenant resource | Pending cutover |
| `src/AgentCore.Api/SandboxEndpoints.cs:18` | GET | `"/sandboxes"` | `admin` | Tenant resource | Pending cutover |
| `src/AgentCore.Api/SandboxEndpoints.cs:22` | GET | `"/sandbox-profiles/{id}"` | `admin` | Tenant resource | Pending cutover |
| `src/AgentCore.Api/SandboxEndpoints.cs:24` | PUT | `"/sandbox-profiles/{id}"` | `admin` | Tenant resource | Pending cutover |
| `src/AgentCore.Api/SandboxEndpoints.cs:47` | POST | `"/sandbox-profiles/{id}/verify"` | `admin` | Tenant resource | Pending cutover |
| `src/AgentCore.Api/SandboxEndpoints.cs:56` | POST | `"/sandbox-profiles/{id}/check-image"` | `admin` | Tenant resource | Pending cutover |
| `src/AgentCore.Api/SandboxEndpoints.cs:62` | POST | `"/agent-instances/{instanceId:guid}/sandbox/verify"` | `admin` | Tenant resource | Pending cutover |
| `src/AgentCore.Api/SandboxEndpoints.cs:74` | GET | `"/agent-instances/{instanceId:guid}/sandbox"` | `admin` | Tenant resource | Pending cutover |
| `src/AgentCore.Api/SandboxEndpoints.cs:90` | GET | `""` | `group` | Tenant resource | Pending cutover |
| `src/AgentCore.Api/SandboxEndpoints.cs:92` | GET | `"/executions/{executionId:guid}"` | `group` | Tenant resource | Pending cutover |
| `src/AgentCore.Api/SandboxEndpoints.cs:94` | POST | `"/executions/{executionId:guid}/stop"` | `group` | Tenant resource | Pending cutover |
| `src/AgentCore.Api/SandboxEndpoints.cs:96` | POST | `"/reset"` | `group` | Tenant resource | Pending cutover |
| `src/AgentCore.Api/SessionAutomationEndpoints.cs:19` | GET | `""` | `group` | Tenant resource | Pending cutover |
| `src/AgentCore.Api/SessionAutomationEndpoints.cs:44` | POST | `"/{triggerId:guid}/cancel"` | `group` | Tenant resource | Pending cutover |
| `src/AgentCore.Api/SessionCatalogEndpoints.cs:18` | POST | `"/api/v1/local/owner-capability"` | `app` | Tenant resource | Pending cutover |
| `src/AgentCore.Api/SessionCatalogEndpoints.cs:35` | GET | `"/api/v2/models"` | `app` | Tenant resource | Pending cutover |
| `src/AgentCore.Api/SessionCatalogEndpoints.cs:38` | GET | `"/api/v2/agent-instances"` | `app` | Tenant resource | Pending cutover |
| `src/AgentCore.Api/SessionCatalogEndpoints.cs:75` | GET | `""` | `group` | Tenant resource | Pending cutover |
| `src/AgentCore.Api/SessionCatalogEndpoints.cs:101` | DELETE | `""` | `group` | Tenant resource | Pending cutover |
| `src/AgentCore.Api/SessionCatalogEndpoints.cs:118` | POST | `""` | `group` | Tenant resource | Pending cutover |
| `src/AgentCore.Api/SessionCatalogEndpoints.cs:154` | GET | `"{sessionId:guid}"` | `group` | Tenant resource | Pending cutover |
| `src/AgentCore.Api/SessionCatalogEndpoints.cs:178` | POST | `"{sessionId:guid}/rename"` | `group` | Tenant resource | Pending cutover |
| `src/AgentCore.Api/SessionCatalogEndpoints.cs:200` | POST | `"{sessionId:guid}/archive"` | `group` | Tenant resource | Pending cutover |
| `src/AgentCore.Api/SessionCatalogEndpoints.cs:218` | POST | `"{sessionId:guid}/deactivate"` | `group` | Tenant resource | Pending cutover |
| `src/AgentCore.Api/SessionCatalogEndpoints.cs:236` | POST | `"{sessionId:guid}/unarchive"` | `group` | Tenant resource | Pending cutover |
| `src/AgentCore.Api/SessionCatalogEndpoints.cs:252` | POST | `"{sessionId:guid}/reopen"` | `group` | Tenant resource | Pending cutover |
| `src/AgentCore.Api/SessionCatalogEndpoints.cs:275` | POST | `"{sessionId:guid}/lifecycle"` | `group` | Tenant resource | Pending cutover |
| `src/AgentCore.Api/SessionCatalogEndpoints.cs:303` | POST | `"{sessionId:guid}/speech-locale"` | `group` | Tenant resource | Pending cutover |
| `src/AgentCore.Api/SessionCatalogEndpoints.cs:322` | POST | `"{sessionId:guid}/model"` | `group` | Tenant resource | Pending cutover |
| `src/AgentCore.Api/SessionCatalogEndpoints.cs:346` | GET | `"{sessionId:guid}/knowledge/{identity}"` | `group` | Tenant resource | Pending cutover |
| `src/AgentCore.Api/SessionCatalogEndpoints.cs:364` | DELETE | `"{sessionId:guid}"` | `group` | Tenant resource | Pending cutover |
| `src/AgentCore.Api/StateIsolationConfigurationEndpoints.cs:17` | GET | `""` | `group` | Tenant resource | Pending cutover |
| `src/AgentCore.Api/StateIsolationConfigurationEndpoints.cs:18` | POST | `"/preset"` | `group` | Tenant resource | Pending cutover |
| `src/AgentCore.Api/StateIsolationConfigurationEndpoints.cs:20` | POST | `"/preview"` | `group` | Tenant resource | Pending cutover |
| `src/AgentCore.Api/StateIsolationConfigurationEndpoints.cs:22` | POST | `"/apply"` | `group` | Tenant resource | Pending cutover |
| `src/AgentCore.Api/WorkspaceEndpoints.cs:18` | GET | `""` | `group` | Tenant resource | Pending cutover |
| `src/AgentCore.Api/WorkspaceEndpoints.cs:40` | GET | `"content"` | `group` | Tenant resource | Pending cutover |
| `src/AgentCore.Api/WorkspaceEndpoints.cs:67` | PUT | `"content"` | `group` | Tenant resource | Pending cutover |

## Baseline relational store inventory

All rows below require an explicit tenant key and scoped APIs, including indexed child rows and JSON identity validation. Account/authentication and immutable builtin templates are the only explicit global identity/configuration exceptions. OwnerCapabilities is removed, not migrated into IAM. InMemory implementations require the same constraints.

| Set | Entity | Target ownership / query | Baseline status |
| --- | --- | --- | --- |
| `Sessions` | `SessionRecord` | Tenant key, scoped read/write/list and tenant-bound references | Pending cutover |
| `Snapshots` | `SnapshotRecord` | Tenant key, scoped read/write/list and tenant-bound references | Pending cutover |
| `Entries` | `EntryRecord` | Tenant key, scoped read/write/list and tenant-bound references | Pending cutover |
| `Profiles` | `ProfileRecord` | Account-owned profile; remove local singleton | Pending cutover |
| `OwnerCapabilities` | `OwnerCapabilityRecord` | Remove table and store | Pending cutover |
| `Attachments` | `AttachmentRecordRow` | Tenant key, scoped read/write/list and tenant-bound references | Pending cutover |
| `MessageAttachments` | `MessageAttachmentRow` | Tenant key, scoped read/write/list and tenant-bound references | Pending cutover |
| `Artifacts` | `ArtifactRecordRow` | Tenant key, scoped read/write/list and tenant-bound references | Pending cutover |
| `StructuredMemories` | `StructuredMemoryRecord` | Tenant key, scoped read/write/list and tenant-bound references | Pending cutover |
| `AgentInstances` | `AgentInstanceRecord` | Tenant key, scoped read/write/list and tenant-bound references | Pending cutover |
| `CoreEventBuckets` | `CoreEventBucketRecord` | Tenant key, scoped read/write/list and tenant-bound references | Pending cutover |
| `CoreEvents` | `CoreEventRecord` | Tenant key, scoped read/write/list and tenant-bound references | Pending cutover |
| `CoreEventDeliveries` | `CoreEventDeliveryRecord` | Tenant key, scoped read/write/list and tenant-bound references | Pending cutover |
| `AutomationTriggers` | `AutomationTriggerRecordEntity` | Tenant key, scoped read/write/list and tenant-bound references | Pending cutover |
| `Automations` | `AutomationRecord` | Tenant key, scoped read/write/list and tenant-bound references | Pending cutover |
| `WebhookEvents` | `WebhookEventRecord` | Tenant key, scoped read/write/list and tenant-bound references | Pending cutover |
| `ExternalEvents` | `ExternalEventRecord` | Tenant key, scoped read/write/list and tenant-bound references | Pending cutover |
| `ExternalEventDeliveries` | `ExternalEventDeliveryRecord` | Tenant key, scoped read/write/list and tenant-bound references | Pending cutover |
| `TriggerOccurrences` | `TriggerOccurrenceRecord` | Tenant key, scoped read/write/list and tenant-bound references | Pending cutover |
| `BackgroundCompletionReceipts` | `BackgroundCompletionReceiptRecord` | Tenant key, scoped read/write/list and tenant-bound references | Pending cutover |
| `Activations` | `ActivationRecord` | Tenant key, scoped read/write/list and tenant-bound references | Pending cutover |
| `AgentRuns` | `AgentRunRecord` | Tenant key, scoped read/write/list and tenant-bound references | Pending cutover |
| `ActivationSourceEntries` | `ActivationSourceEntryRecord` | Tenant key, scoped read/write/list and tenant-bound references | Pending cutover |
| `AgentDefinitionDrafts` | `AgentDefinitionDraftRecord` | Tenant key, scoped read/write/list and tenant-bound references | Pending cutover |
| `AgentDefinitionPublications` | `AgentDefinitionPublicationRecord` | Tenant key, scoped read/write/list and tenant-bound references | Pending cutover |
| `AgentDefinitionDraftResources` | `AgentDefinitionDraftResourceRecord` | Tenant key, scoped read/write/list and tenant-bound references | Pending cutover |
| `AgentDefinitionDraftEvaluationScenarios` | `AgentDefinitionDraftEvaluationScenarioRecord` | Tenant key, scoped read/write/list and tenant-bound references | Pending cutover |
| `AgentDefinitionDraftEvaluationResults` | `AgentDefinitionDraftEvaluationResultRecord` | Tenant key, scoped read/write/list and tenant-bound references | Pending cutover |
| `AgentDefinitionPublicationResources` | `AgentDefinitionPublicationResourceRecord` | Tenant key, scoped read/write/list and tenant-bound references | Pending cutover |
| `AdminEvents` | `AdminEventRecord` | Tenant key, scoped read/write/list and tenant-bound references | Pending cutover |
| `BrowserPrivacy` | `BrowserPrivacyRecord` | Tenant key, scoped read/write/list and tenant-bound references | Pending cutover |
| `Credentials` | `CredentialRecord` | Tenant key, scoped read/write/list and tenant-bound references | Pending cutover |
| `AgentCredentialBindings` | `AgentCredentialBindingRecord` | Tenant key, scoped read/write/list and tenant-bound references | Pending cutover |
| `Experiences` | `ExperienceRecord` | Tenant key, scoped read/write/list and tenant-bound references | Pending cutover |
| `IdentityMaintenanceSettings` | `IdentityMaintenanceSettingsRecord` | Tenant key, scoped read/write/list and tenant-bound references | Pending cutover |
| `ExperienceSettings` | `ExperienceSettingsRecord` | Tenant key, scoped read/write/list and tenant-bound references | Pending cutover |
| `AgentDefinitionSkillStates` | `AgentDefinitionSkillStateRecord` | Tenant key, scoped read/write/list and tenant-bound references | Pending cutover |
| `AgentInstanceSkills` | `AgentInstanceSkillRecord` | Tenant key, scoped read/write/list and tenant-bound references | Pending cutover |
| `AgentInstanceResources` | `AgentInstanceResourceRecord` | Tenant key, scoped read/write/list and tenant-bound references | Pending cutover |
| `AgentDefinitionResourceStates` | `AgentDefinitionResourceStateRecord` | Tenant key, scoped read/write/list and tenant-bound references | Pending cutover |
| `EmailWatches` | `EmailWatchRecord` | Tenant key, scoped read/write/list and tenant-bound references | Pending cutover |
| `EmailWatchEvents` | `EmailWatchEventRecord` | Tenant key, scoped read/write/list and tenant-bound references | Pending cutover |

## Baseline tool/effect inventory

| Catalog tool | Authority boundary | Status |
| --- | --- | --- |
| `credentials.list` | Actual agent principal + tenant-owned target + catalog operation permission; capability/policy remain additional gates | Pending cutover |
| `knowledge.retrieve` | Actual agent principal + tenant-owned target + catalog operation permission; capability/policy remain additional gates | Pending cutover |
| `attachments.read` | Actual agent principal + tenant-owned target + catalog operation permission; capability/policy remain additional gates | Pending cutover |
| `workspace.cwd` | Actual agent principal + tenant-owned target + catalog operation permission; capability/policy remain additional gates | Pending cutover |
| `workspace.read` | Actual agent principal + tenant-owned target + catalog operation permission; capability/policy remain additional gates | Pending cutover |
| `workspace.list` | Actual agent principal + tenant-owned target + catalog operation permission; capability/policy remain additional gates | Pending cutover |
| `drive.accounts` | Actual agent principal + tenant-owned target + catalog operation permission; capability/policy remain additional gates | Pending cutover |
| `drive.list` | Actual agent principal + tenant-owned target + catalog operation permission; capability/policy remain additional gates | Pending cutover |
| `drive.search` | Actual agent principal + tenant-owned target + catalog operation permission; capability/policy remain additional gates | Pending cutover |
| `drive.get` | Actual agent principal + tenant-owned target + catalog operation permission; capability/policy remain additional gates | Pending cutover |
| `drive.create_folder` | Actual agent principal + tenant-owned target + catalog operation permission; capability/policy remain additional gates | Pending cutover |
| `drive.upload` | Actual agent principal + tenant-owned target + catalog operation permission; capability/policy remain additional gates | Pending cutover |
| `drive.download` | Actual agent principal + tenant-owned target + catalog operation permission; capability/policy remain additional gates | Pending cutover |
| `drive.rename` | Actual agent principal + tenant-owned target + catalog operation permission; capability/policy remain additional gates | Pending cutover |
| `drive.move` | Actual agent principal + tenant-owned target + catalog operation permission; capability/policy remain additional gates | Pending cutover |
| `drive.trash` | Actual agent principal + tenant-owned target + catalog operation permission; capability/policy remain additional gates | Pending cutover |
| `drive.trash_batch` | Actual agent principal + tenant-owned target + catalog operation permission; capability/policy remain additional gates | Pending cutover |
| `office.document.create` | Actual agent principal + tenant-owned target + catalog operation permission; capability/policy remain additional gates | Pending cutover |
| `office.document.inspect` | Actual agent principal + tenant-owned target + catalog operation permission; capability/policy remain additional gates | Pending cutover |
| `office.document.edit` | Actual agent principal + tenant-owned target + catalog operation permission; capability/policy remain additional gates | Pending cutover |
| `office.spreadsheet.create` | Actual agent principal + tenant-owned target + catalog operation permission; capability/policy remain additional gates | Pending cutover |
| `office.spreadsheet.inspect` | Actual agent principal + tenant-owned target + catalog operation permission; capability/policy remain additional gates | Pending cutover |
| `office.spreadsheet.edit` | Actual agent principal + tenant-owned target + catalog operation permission; capability/policy remain additional gates | Pending cutover |
| `workspace.write` | Actual agent principal + tenant-owned target + catalog operation permission; capability/policy remain additional gates | Pending cutover |
| `workspace.patch` | Actual agent principal + tenant-owned target + catalog operation permission; capability/policy remain additional gates | Pending cutover |
| `workspace.search` | Actual agent principal + tenant-owned target + catalog operation permission; capability/policy remain additional gates | Pending cutover |
| `workspace.mkdir` | Actual agent principal + tenant-owned target + catalog operation permission; capability/policy remain additional gates | Pending cutover |
| `workspace.copy` | Actual agent principal + tenant-owned target + catalog operation permission; capability/policy remain additional gates | Pending cutover |
| `workspace.delete` | Actual agent principal + tenant-owned target + catalog operation permission; capability/policy remain additional gates | Pending cutover |
| `workspace.batch` | Actual agent principal + tenant-owned target + catalog operation permission; capability/policy remain additional gates | Pending cutover |
| `workspace.move` | Actual agent principal + tenant-owned target + catalog operation permission; capability/policy remain additional gates | Pending cutover |
| `artifacts.create` | Actual agent principal + tenant-owned target + catalog operation permission; capability/policy remain additional gates | Pending cutover |
| `artifacts.create_from_workspace` | Actual agent principal + tenant-owned target + catalog operation permission; capability/policy remain additional gates | Pending cutover |
| `artifacts.verify` | Actual agent principal + tenant-owned target + catalog operation permission; capability/policy remain additional gates | Pending cutover |
| `sandbox.profiles` | Actual agent principal + tenant-owned target + catalog operation permission; capability/policy remain additional gates | Pending cutover |
| `sandbox.exec` | Actual agent principal + tenant-owned target + catalog operation permission; capability/policy remain additional gates | Pending cutover |
| `sandbox.status` | Actual agent principal + tenant-owned target + catalog operation permission; capability/policy remain additional gates | Pending cutover |
| `sandbox.process.status` | Actual agent principal + tenant-owned target + catalog operation permission; capability/policy remain additional gates | Pending cutover |
| `sandbox.process.stop` | Actual agent principal + tenant-owned target + catalog operation permission; capability/policy remain additional gates | Pending cutover |
| `sandbox.reset` | Actual agent principal + tenant-owned target + catalog operation permission; capability/policy remain additional gates | Pending cutover |
| `web.search` | Actual agent principal + tenant-owned target + catalog operation permission; capability/policy remain additional gates | Pending cutover |
| `web.fetch` | Actual agent principal + tenant-owned target + catalog operation permission; capability/policy remain additional gates | Pending cutover |
| `web.download` | Actual agent principal + tenant-owned target + catalog operation permission; capability/policy remain additional gates | Pending cutover |
| `http.request` | Actual agent principal + tenant-owned target + catalog operation permission; capability/policy remain additional gates | Pending cutover |
| `email.accounts` | Actual agent principal + tenant-owned target + catalog operation permission; capability/policy remain additional gates | Pending cutover |
| `email.request_account` | Actual agent principal + tenant-owned target + catalog operation permission; capability/policy remain additional gates | Pending cutover |
| `email.search` | Actual agent principal + tenant-owned target + catalog operation permission; capability/policy remain additional gates | Pending cutover |
| `email.read` | Actual agent principal + tenant-owned target + catalog operation permission; capability/policy remain additional gates | Pending cutover |
| `email.create_draft` | Actual agent principal + tenant-owned target + catalog operation permission; capability/policy remain additional gates | Pending cutover |
| `email.send` | Actual agent principal + tenant-owned target + catalog operation permission; capability/policy remain additional gates | Pending cutover |
| `demo.sensitive_action` | Actual agent principal + tenant-owned target + catalog operation permission; capability/policy remain additional gates | Pending cutover |
| `automation.create` | Actual agent principal + tenant-owned target + catalog operation permission; capability/policy remain additional gates | Pending cutover |
| `automation.list` | Actual agent principal + tenant-owned target + catalog operation permission; capability/policy remain additional gates | Pending cutover |
| `automation.inspect` | Actual agent principal + tenant-owned target + catalog operation permission; capability/policy remain additional gates | Pending cutover |
| `automation.run` | Actual agent principal + tenant-owned target + catalog operation permission; capability/policy remain additional gates | Pending cutover |
| `automation.enable` | Actual agent principal + tenant-owned target + catalog operation permission; capability/policy remain additional gates | Pending cutover |
| `automation.disable` | Actual agent principal + tenant-owned target + catalog operation permission; capability/policy remain additional gates | Pending cutover |
| `automation.update` | Actual agent principal + tenant-owned target + catalog operation permission; capability/policy remain additional gates | Pending cutover |
| `automation.delete` | Actual agent principal + tenant-owned target + catalog operation permission; capability/policy remain additional gates | Pending cutover |
| `capabilities.load` | Actual agent principal + tenant-owned target + catalog operation permission; capability/policy remain additional gates | Pending cutover |
| `skills.load` | Actual agent principal + tenant-owned target + catalog operation permission; capability/policy remain additional gates | Pending cutover |
| `app.message.send` | Actual agent principal + tenant-owned target + catalog operation permission; capability/policy remain additional gates | Pending cutover |
| `browser.navigate` | Actual agent principal + tenant-owned target + catalog operation permission; capability/policy remain additional gates | Pending cutover |
| `browser.snapshot` | Actual agent principal + tenant-owned target + catalog operation permission; capability/policy remain additional gates | Pending cutover |
| `browser.close` | Actual agent principal + tenant-owned target + catalog operation permission; capability/policy remain additional gates | Pending cutover |
| `browser.tabs` | Actual agent principal + tenant-owned target + catalog operation permission; capability/policy remain additional gates | Pending cutover |
| `browser.screenshot` | Actual agent principal + tenant-owned target + catalog operation permission; capability/policy remain additional gates | Pending cutover |
| `browser.find` | Actual agent principal + tenant-owned target + catalog operation permission; capability/policy remain additional gates | Pending cutover |
| `browser.click` | Actual agent principal + tenant-owned target + catalog operation permission; capability/policy remain additional gates | Pending cutover |
| `browser.hover` | Actual agent principal + tenant-owned target + catalog operation permission; capability/policy remain additional gates | Pending cutover |
| `browser.drag` | Actual agent principal + tenant-owned target + catalog operation permission; capability/policy remain additional gates | Pending cutover |
| `browser.drop` | Actual agent principal + tenant-owned target + catalog operation permission; capability/policy remain additional gates | Pending cutover |
| `browser.type` | Actual agent principal + tenant-owned target + catalog operation permission; capability/policy remain additional gates | Pending cutover |
| `browser.fill_form` | Actual agent principal + tenant-owned target + catalog operation permission; capability/policy remain additional gates | Pending cutover |
| `browser.select_option` | Actual agent principal + tenant-owned target + catalog operation permission; capability/policy remain additional gates | Pending cutover |
| `browser.press_key` | Actual agent principal + tenant-owned target + catalog operation permission; capability/policy remain additional gates | Pending cutover |
| `browser.upload` | Actual agent principal + tenant-owned target + catalog operation permission; capability/policy remain additional gates | Pending cutover |
| `browser.fill_credential` | Actual agent principal + tenant-owned target + catalog operation permission; capability/policy remain additional gates | Pending cutover |
| `browser.wait_for` | Actual agent principal + tenant-owned target + catalog operation permission; capability/policy remain additional gates | Pending cutover |
| `browser.dialog` | Actual agent principal + tenant-owned target + catalog operation permission; capability/policy remain additional gates | Pending cutover |
| `browser.resize` | Actual agent principal + tenant-owned target + catalog operation permission; capability/policy remain additional gates | Pending cutover |
| `browser.console_messages` | Actual agent principal + tenant-owned target + catalog operation permission; capability/policy remain additional gates | Pending cutover |
| `browser.mouse` | Actual agent principal + tenant-owned target + catalog operation permission; capability/policy remain additional gates | Pending cutover |
| `browser.highlight` | Actual agent principal + tenant-owned target + catalog operation permission; capability/policy remain additional gates | Pending cutover |
| `browser.emulate_media` | Actual agent principal + tenant-owned target + catalog operation permission; capability/policy remain additional gates | Pending cutover |
| `browser.get_config` | Actual agent principal + tenant-owned target + catalog operation permission; capability/policy remain additional gates | Pending cutover |
| `browser.set_geolocation` | Actual agent principal + tenant-owned target + catalog operation permission; capability/policy remain additional gates | Pending cutover |
| `continuity.search` | Actual agent principal + tenant-owned target + catalog operation permission; capability/policy remain additional gates | Pending cutover |
| `agent.state.inspect` | Actual agent principal + tenant-owned target + catalog operation permission; capability/policy remain additional gates | Pending cutover |
| `continuity.get` | Actual agent principal + tenant-owned target + catalog operation permission; capability/policy remain additional gates | Pending cutover |
| `memory.consolidate` | Actual agent principal + tenant-owned target + catalog operation permission; capability/policy remain additional gates | Pending cutover |
| `memory.forget` | Actual agent principal + tenant-owned target + catalog operation permission; capability/policy remain additional gates | Pending cutover |
| `experience.consolidate` | Actual agent principal + tenant-owned target + catalog operation permission; capability/policy remain additional gates | Pending cutover |
| `experience.recent` | Actual agent principal + tenant-owned target + catalog operation permission; capability/policy remain additional gates | Pending cutover |
| `background.list` | Actual agent principal + tenant-owned target + catalog operation permission; capability/policy remain additional gates | Pending cutover |
| `background.inspect` | Actual agent principal + tenant-owned target + catalog operation permission; capability/policy remain additional gates | Pending cutover |
| `background.take` | Actual agent principal + tenant-owned target + catalog operation permission; capability/policy remain additional gates | Pending cutover |
| `background.acknowledge` | Actual agent principal + tenant-owned target + catalog operation permission; capability/policy remain additional gates | Pending cutover |
| `execution.wait` | Actual agent principal + tenant-owned target + catalog operation permission; capability/policy remain additional gates | Pending cutover |
| `background.start` | Actual agent principal + tenant-owned target + catalog operation permission; capability/policy remain additional gates | Pending cutover |
| `work.complete` | Actual agent principal + tenant-owned target + catalog operation permission; capability/policy remain additional gates | Pending cutover |

## Baseline non-route entry points and storage locations

`SessionHost` / `SessionManager`, `SessionRuntime` tool dispatch, `AgentRunAdmissionFactory`, `AgentRunCoordinator`, `AgentRunDispatcher` / hosted worker, `TriggerSchedulerHostedService`, `EmailWatchHostedService`, `BackgroundOccurrenceIntakeHostedService`, `AttachmentTtlHostedService`, `WorkspaceCleanupHostedService`, `SessionShutdownHostedService`, `OpenSandboxHostedService` and lifecycle mutators are explicit owners of ingress/reentry/cleanup obligations. Their calls require a trusted tenant/executor context; HTTP middleware alone cannot protect them.

`PersistenceOptions` currently locates SQLite `data/agent-core.db`, attachment/workspace/artifact/definition-resource roots and credential key ring. Browser options and Sandbox allocation/mount records add external state. `data/` and any configured Real roots are **user data, not disposable**. No reset is authorized here. Tests must allocate unique temporary roots/databases; only those positively identified resources can be deleted. Bootstrap/reset procedures must include OAuth state, browser state, credentials, schedules, unfinished effects and canonical files, not just SQLite rows.

Baseline source counts: 231 HTTP/Hub mapping sites, 42 DbSets, 101 primary catalog constants. Browser/Skill/management-generated interfaces still require runtime registry coverage; this source list alone is not a final completeness assertion.
