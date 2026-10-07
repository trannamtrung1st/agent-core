# HTTP API and Realtime Protocol

## Capability authoring and loading contracts

`GET /api/v2/admin/tools` retains legacy-selectable exact `toolNames`, compatibility `maxToolAllowlistEntries:null`, and a `capabilities` catalog of name/category/summary/tags/discoverable/defaultProjectionClass/configured. The full `capabilities` catalog includes context-only names for exact new-mode authorization snapshots and identifies them separately. Legacy `toolNames` excludes context-owned grants as before. No secret configuration values are returned.

New candidate environment accepts `capabilities.mode` Selected or All, exact `resolvedCapabilities`, optional fingerprint, and `projection.alwaysCapabilities`. Server draft saves and publications resolve All against the trusted registry and write the fingerprint. Published runtime authority always uses the stored explicit names. `toolAllowlist` remains the compatibility input; new-mode grants cannot use both representations. Findings identify authorization/projection fields and duplicate, unregistered, unauthorized-projection or context-only errors. Missing optional configuration may be authorized in new mode. Candidate persistence rejects documents over 1 MiB with `candidate/document_too_large`.

```json
{
  "capabilities": { "mode": "Selected", "resolvedCapabilities": ["capabilities.load", "workspace.read", "workspace.write"] },
  "projection": { "alwaysCapabilities": ["workspace.read"] }
}
```

This snippet is the new environment fragment. All authoring uses `mode: "All"` and an explicit resolved array; the server replaces that array with the current exact registry snapshot before saving/publishing.

The existing model tool/result protocol carries `capabilities.load` with `{query,limit?}`; results provide loaded name/summary pairs, alreadyProjected names and an empty unavailable list. Provider adapters receive real schemas only on continuation. No new SignalR DTO/event, version or public load-state endpoint is introduced. Discovery context and execution-time policy remain authoritative.


## Managed workspace refinement HTTP and tool contracts

The current capability catalog and structured findings are specified above. Historical workspace authoring used the former 32-grant bound; that count ceiling has been removed.

For a pinned new-mode managed Session, workspace GET list/content and PUT content accept `/home` and `/working`; relative HTTP paths resolve from `/home` independently of runtime cwd. Legacy paths remain unchanged for old definitions. PUT keeps the raw byte body and Content-Type; optional `expectedRevision`/`expectedSha256` query values guard existing durable replacement. It cannot mutate ended/archived owners. HTTP retain/checkout remain compatibility endpoints and are not new model tools. Artifacts remain Session scoped and newly published bytes are independent of home ItemIds.

The normal model tool/approval protocol carries one `workspace.cwd` get/set and direct home write/patch. New-mode destructive approval details display concrete public paths resolved against the trusted Session cwd; the action hash and executable arguments still bind the original exact call. Compatibility previews retain their historical scratch scope. Individual cross-root copy accepts source/destination and optional durable destination revision/hash; whole-tree tokens apply to same-scope home restructuring. Cross-root move and mixed-scope batch fail closed. No SignalR DTO/event, protocol version, structural HTTP endpoint or public cwd-setting endpoint is added. See [tool semantics](12-backend-implementation-spec.md#agent-workspace-v2-tools-and-policy); earlier filesystem sections describe the compatibility contract.


This document owns wire protocol version 1. Contracts contains closed typed DTOs; Api validates/maps them. C# property names may be PascalCase, but JSON uses camelCase and MessagePack uses explicit string keys in camelCase. Configure these independently: System.Text.Json settings do not configure MessagePack. Serialize GUIDs as lowercase dashed strings and timestamps as UTC ISO-8601 strings, avoiding cross-language Guid/date extensions. Integers are nonnegative and <= JavaScript Number.MAX_SAFE_INTEGER; rotate/end before overflow. Unknown type/version is rejected; do not deserialize arbitrary CLR type names.

## HTTP

REST handles creation, discovery, history, state and terminal ending. Live user text, audio and playback use SignalR. All examples are JSON, including the JSON-equivalent view of MessagePack messages. Nullable fields are serialized explicitly as null, except optional HTTP validation errors/extensions. Content-Type is application/json. IDs below are illustrative; creation generates fresh IDs.

**P7 Admin (observed):** Owner-protected `/api/v2/admin/...` routes in the table below are trusted-local HTTP only; they do not attach a Session Runtime or enter the SignalR mailbox. Managed user chat still uses `POST /api/v2/sessions` with `agentInstanceId` to pin definition version and persona into the session snapshot. History, effective-config, validation, diff, and event list responses use allowlisted projections (no secrets or raw provider credentials). Owner-protected draft read, evaluation scenario, and resource content routes return editing payloads the Admin UI needs; learned-memory and automation surfaces still redact sensitive bodies. See [Backend Implementation](12-backend-implementation-spec.md#p7b-definition-lifecycle-observed).

| Method | Request | Success | Errors |
| --- | --- | --- | --- |
| GET /api/v1/agents | No body | 200 list below | 503 definitions unavailable |
| GET /api/v1/agents/{agentId} | Optional `version` positive integer; latest if absent | 200 public descriptor | 404 unknown ID/version |
| POST /api/v1/sessions | Create body below | 201 session view; Location=/api/v1/sessions/{id} | 400 validation; 404 agent; 409 VoiceUnavailable when initial mode is voice but voice is not available; 503 storage |
| GET /api/v1/sessions/{sessionId} | No body | 200 session view | 404 unknown |
| GET /api/v1/sessions/{sessionId}/messages | `limit` 1..100; optional `after` or `before` (not both) | 200 history page | 400 invalid cursor; 404 unknown |
| DELETE /api/v1/sessions/{sessionId} | No body | 204 after terminal save; repeated known end also 204 | 404 unknown; 503 durable save failed |
| GET /health | No body | 200 health below | 503 if store/definition startup failed |
| GET /api/v2/admin/definitions | Owner capability; trusted local caller | 200 logical-definition inventory. Published and built-in versions stay one row per version (`builtIn` or `durable`; `published` or `deprecated`). A definition that has drafts and no version is one `draft` / `draftOnly` row. Every item includes `draftCount` and `displayName` | 401 capability; 403 non-local |
| GET /api/v2/admin/authoring-options | Owner capability; trusted local caller | 200 read-only authoring choices: language-model, speech-recognizer, and speech-synthesizer aliases; speech defaults only when that list has one alias; language-model default `primary-llm` when present, otherwise the single alias; model catalog entries with supported reasoning efforts; interruption classifiers | 401 capability; 403 non-local |
| DELETE /api/v2/admin/definitions/{definitionId} | Owner capability; trusted local caller; `{ drafts: [{ draftId, revision }], publications: [{ version, metadataRevision }] }` must match the current durable authoring state. Purges drafts, draft resources, draft evaluation evidence, durable publications, and publication bindings for a durable-only definition. Keeps admin history. Does not delete instances, sessions, or other execution state | 204 | 400 built-in definition; 401 capability; 403 non-local; 404 nothing durable to delete; 409 witness is stale or the definition is still referenced |
| GET /api/v2/admin/definition-drafts | Owner capability; trusted local caller | 200 draft summaries | 401 capability; 403 non-local |
| GET /api/v2/admin/definition-drafts/{draftId} | Owner capability; trusted local caller | 200 draft with candidate JSON | 401 capability; 403 non-local; 404 |
| POST /api/v2/admin/definition-drafts | Owner capability; trusted local caller; `{ definitionId, candidate }` creates a `New` draft | 201 draft | 400 validation; 401 capability; 403 non-local; 409 the id is already a built-in version, durable publication, or draft |
| POST /api/v2/admin/definition-drafts/new | Owner capability; trusted local caller; `{ definitionId }` only | 201 draft whose candidate is the server starter and whose source kind is `New` | 400 validation; 401 capability; 403 non-local; 409 the id is already a built-in version, durable publication, or draft |
| POST /api/v2/admin/definition-drafts/fork | Owner capability; trusted local caller; `{ definitionId, sourceVersion, sourceKind }` (`ForkBuiltIn` / `ForkDurable`) | 201 draft | 400 validation; 401 capability; 403 non-local; 404 |
| PUT /api/v2/admin/definition-drafts/{draftId} | Owner capability; trusted local caller; `{ expectedRevision, candidate }` | 200 draft | 400 validation; 401 capability; 403 non-local; 404; 409 stale revision |
| DELETE /api/v2/admin/definition-drafts/{draftId}?expectedRevision= | Owner capability; trusted local caller; permanently removes unpublished draft state and its resource/evaluation records; immutable publications remain | 204 removed | 401 capability; 403 non-local; 404; 409 stale revision |
| POST /api/v2/admin/definition-drafts/{draftId}/validate | Owner capability; trusted local caller | 200 validation result (`draftRevision`, `configurationFingerprint`, `hasBlockingFindings`, `findings[]`) | 401 capability; 403 non-local; 404; 409 draft changed during validation |
| GET /api/v2/admin/definition-drafts/{draftId}/evaluation-scenarios | Owner capability; trusted local caller | 200 draft evaluation scenarios | 401 capability; 403 non-local; 404 unknown draft |
| PUT /api/v2/admin/definition-drafts/{draftId}/evaluation-scenarios | Owner capability; trusted local caller; `{ expectedRevision, scenarioId, title, prompt, requirementLevel: Required\|Advisory, checkType, toolName? }` (atomic upsert + revision bump) | 200 scenario | 400 validation; 401 capability; 403 non-local; 404; 409 stale revision |
| DELETE /api/v2/admin/definition-drafts/{draftId}/evaluation-scenarios/{scenarioId}?expectedRevision= | Owner capability; trusted local caller | 204 removed | 401 capability; 403 non-local; 404 unknown draft or scenario; 409 stale revision |
| POST /api/v2/admin/definition-drafts/{draftId}/evaluation-scenarios/{scenarioId}/run | Owner capability; trusted local caller | 200 synthetic evaluation result with revision/fingerprint provenance | 401 capability; 403 non-local; 404; 409 draft changed during evaluation |
| GET /api/v2/admin/definition-drafts/{draftId}/evaluation-results | Owner capability; trusted local caller | 200 recorded evaluation results | 401 capability; 403 non-local; 404 unknown draft |
| GET /api/v2/admin/definition-drafts/{draftId}/diff | Owner capability; trusted local caller | 200 safe grouped diff (`baselineKind`, `baselineVersion`, `sections[]`) | 401 capability; 403 non-local; 404 |
| POST /api/v2/admin/definition-drafts/{draftId}/publish | Owner capability; trusted local caller; `{ expectedRevision }` | 200 publication summary (`metadataRevision` included); successful publication atomically removes the source draft and its unpublished resources/evaluation rows | 400 validation or stale/missing required eval evidence; 401 capability; 403 non-local; 404; 409 stale revision |
| GET /api/v2/admin/definitions/{definitionId}/publications | Owner capability; trusted local caller | 200 durable publication summaries | 401 capability; 403 non-local |
| POST /api/v2/admin/definitions/{definitionId}/publications/{version}/deprecate | Owner capability; trusted local caller; `{ expectedMetadataRevision }` | 200 publication summary | 400 built-in; 401 capability; 403 non-local; 404; 409 stale metadata |
| GET /api/v2/admin/definitions/{definitionId}/versions/{version}?sourceKind=ForkBuiltIn\|ForkDurable | Owner capability; trusted local caller; explicit source distinguishes built-in and durable rows sharing a version | 200 full definition candidate in the same JSON shape as draft `candidate`; read-only inspection creates no draft or admin event | 400 invalid source; 401 capability; 403 non-local; 404 source/version missing |
| GET /api/v2/admin/instances | Owner capability; trusted local caller | 200 instance inventory (id, definition, version, lifecycle, compatibility, persona name, timestamps) | 401 capability; 403 non-local |
| GET /api/v2/admin/instances/{instanceId}/effective-config | Owner capability; trusted local caller | 200 allowlisted effective configuration: resolved catalog model, offered tools, harness refs, knowledge backing path (`resourcePath` when set, otherwise `knowledge/{identity}`), workspace template id, policies, durable-work eligibility, and the stored unattended catalog key and reasoning effort (no secrets). The projection is read-only | 401 capability; 403 non-local; 404 unknown instance or missing exact definition version |
| GET /api/v2/admin/tools | Owner capability; trusted local caller | 200 registered Definition-grant tool names for draft allowlist editing; context-owned Continuity, Experience, Harness and execution capabilities are excluded | 401 capability; 403 non-local |
| GET /api/v2/admin/events | Owner capability; trusted local caller; optional query `targetType`, `targetId`, `limit` | 200 append-only admin events (safe summary metadata only) | 401 capability; 403 non-local |
| POST /api/v2/admin/agent-instances | Owner capability; trusted local caller; `{ definitionId, version, persona? }` exact durable publication. Omit or null `persona` stores the definition identity (`personaSource` Default). A present persona is validated and stored as Custom on the inserted instance, with that source and fingerprint on the single `ManagedInstanceCreated` summary | 201 managed instance (`compatibility=false`) | 400 validation; 401 capability; 403 non-local; 404 |
| PATCH /api/v2/admin/agent-instances/{instanceId}/persona | Owner capability; trusted local caller; JSON `{ expectedRevision, expectedPersonaRevision, persona }` typed identity (Form/JSON share schema) | 200 instance summary | 400 validation; 401 capability; 403 non-local; 404; 409 stale revision |
| PATCH /api/v2/admin/agent-instances/{instanceId}/lifecycle | Owner capability; trusted local caller; `{ expectedRevision, lifecycle }` (`Active` / `Archived`) | 200 instance summary | 400 validation; 401 capability; 403 non-local; 404; 409 stale revision |
| DELETE /api/v2/admin/agent-instances/{instanceId} | Owner capability; trusted local caller; `{ expectedRevision }`. Managed archived instances only. Commits removal of the instance row and home metadata with `InstanceDeleted`, then purges home bytes. A post-commit cleanup failure can return 500 while the instance remains deleted; startup/five-minute recovery retries inaccessible leftover bytes from the committed receipt. Does not delete sessions, learned memory, triggers, occurrences, work items, approvals, or conversation executions | 204 | 400 compatibility instance or instance is not archived; 401 capability; 403 non-local; 404; 409 stale revision or the instance is still referenced |
| PATCH /api/v2/admin/agent-instances/{instanceId}/active-version | Owner capability; trusted local caller; `{ expectedRevision, version }` exact durable publication version | 200 instance summary | 400 validation; 401 capability; 403 non-local; 404; 409 stale revision |
| GET /api/v2/admin/agent-instances/{instanceId}/learned-memory | Owner capability; trusted local caller; query `scope` (`Session` \| `IdentityUser` \| `User`); `sessionId` required for `Session` | 200 bounded active items with safe provenance; `IdentityUser`/`User` return 200 empty list when effective retrieval is disabled | 400 invalid scope or missing/invalid `sessionId` for `Session`; 401 capability; 403 non-local, compatibility instance, Session scope when pinned session memory disabled, or session not owned by trusted profile; 404 instance or session |
| DELETE /api/v2/admin/agent-instances/{instanceId}/learned-memory/{memoryId} | Owner capability; trusted local caller; query `scope`, `sessionId` when `scope=Session`, `confirm=true` | 204 | 400 validation (`scope`, missing `sessionId` for `Session`, `confirm` not true); 401 capability; 403 non-local, compatibility instance, effective policy denial on mutation, or session profile mismatch; 404 instance, session, or memory item |
| POST /api/v2/admin/agent-instances/{instanceId}/learned-memory/reset | Owner capability; trusted local caller; `{ scope, sessionId?, confirm: true }` (`sessionId` required when `scope=Session`) | 200 `{ scope, itemsRemoved }` | 400 validation (`scope`, `confirm`, `sessionId`); 401 capability; 403 non-local, compatibility instance, effective policy denial, or session profile mismatch; 404 instance or session |
| GET /api/v2/admin/agent-instances/{instanceId}/connection | Owner capability; trusted local caller | 200 connection summary without cookies, tokens, a token hash, a profile path, or a webhook credential. JSON `null` when that instance exists and has no connection row | 401 capability; 403 non-local; 404 instance |
| POST /api/v2/admin/agent-instances/{instanceId}/connection/connect | Owner capability; trusted local caller; `{ displayName, baseUrl }` absolute http(s) origin without credentials | 200 connection summary. `Connected` only after the trusted origin is observed past the login wall | 400 validation; 401 capability; 403 non-local; 404 instance |
| POST /api/v2/admin/agent-instances/{instanceId}/connection/reauthenticate | Owner capability; trusted local caller. Same observation path as connect from `NeedsReauthentication`, `Connecting`, or `Unavailable` | 200 connection summary | 400 validation; 401 capability; 403 non-local; 404 instance or connection |
| POST /api/v2/admin/agent-instances/{instanceId}/connection/mark-connected | Owner capability; trusted local caller. Re-observes the sign-in URL. Operator confirmation alone does not mark a login wall connected | 200 connection summary | 400 the page could not be observed; 401 capability; 403 non-local; 404 instance or connection |
| POST /api/v2/admin/agent-instances/{instanceId}/connection/open-browser | Owner capability; trusted local caller. Opens the application sign-in URL in that instance's browser and records the observed status | 200 connection summary | 401 capability; 403 non-local; 404 instance or connection |
| POST /api/v2/admin/agent-instances/{instanceId}/connection/revoke | Owner capability; trusted local caller. Sets `NotConnected` and does not delete the profile | 200 connection summary | 401 capability; 403 non-local; 404 instance or connection |
| POST /api/v2/admin/agent-instances/{instanceId}/connection/reset-profile | Owner capability; trusted local caller. Closes the live persistent context, deletes only that instance profile directory, and sets `NotConnected` | 200 connection summary with status detail `profile_reset` | 400 the saved sign-in could not be cleared; 401 capability; 403 non-local; 404 instance or connection |
| GET /api/v2/admin/event-sources | Owner capability; trusted local caller | 200 `{ items }` of source id, display name, kind, public source key, status, and revision. No token and no token hash | 401 capability; 403 non-local |
| POST /api/v2/admin/event-sources | Owner capability; trusted local caller; `{ displayName }`. Creates an active Webhook source. The raw token is in this response only | 200 `{ sourceId, sourceKey, token, status }` | 400 validation; 401 capability; 403 non-local |
| POST /api/v2/admin/event-sources/{sourceId}/rotate | Owner capability; trusted local caller. Replaces the hash immediately and returns the new raw token once. The public source key stays | 200 `{ sourceId, sourceKey, token, status }` | 401 capability; 403 non-local; 404 source |
| POST /api/v2/admin/event-sources/{sourceId}/revoke | Owner capability; trusted local caller. Clears the hash and sets `Revoked`. Historical events stay | 200 source summary | 401 capability; 403 non-local; 404 source |
| GET /api/v2/admin/agent-instances/{instanceId}/event-subscriptions | Owner capability; trusted local caller | 200 `{ items }` of registration id, source id, event type, status, and revision | 401 capability; 403 non-local; 404 instance |
| POST /api/v2/admin/agent-instances/{instanceId}/event-subscriptions | Owner capability; trusted local caller; `{ sourceId, eventType }` with `eventType` `order.placed`. Repeating the same pair returns the existing registration | 200 subscription summary | 400 the agent cannot subscribe or the event type is not allowed; 401 capability; 403 non-local; 404 instance or source |
| POST /api/v1/hooks/{sourceKey} | Bearer token for that Event Source. JSON body at most 8 KiB: `eventId`, `type` (`order.placed`), optional `occurredAt`, and `data.orderReference` only. Does not run the agent, browser, or router. The body does not list subscribers | 202 `{ eventId }` after the External Event commits; 200 `{ eventId }` when that source event was already admitted, including when no agent is subscribed | 400 malformed, unknown, unsupported, or oversized payload; 401 missing, wrong, or revoked bearer |
| GET /api/v2/admin/agent-instances/{instanceId}/automation/registrations | Owner capability; trusted local caller | 200 owned active/suspended registrations with safe schedule/provenance fields | 401 capability; 403 non-local or compatibility instance; 404 instance |
| POST /api/v2/admin/agent-instances/{instanceId}/automation/registrations/{registrationId}/cancel | Owner capability; trusted local caller; `{ expectedRevision, confirm: true }` | 200 cancelled registration (`Active` or `SuspendedPolicy`) | 400 validation (body, `confirm`, revision, or terminal/non-cancellable status); 401 capability; 403 non-local or compatibility instance; 404 instance or registration (including other owner); 409 stale `expectedRevision` or registration already completed/cancelled |
| POST /api/v2/admin/agent-instances/{instanceId}/unattended-model | Owner capability; trusted local caller; `{ expectedRevision, catalogKey, reasoningEffort }`. A blank catalog key clears the instance unattended default | 200 instance summary including `unattendedModelCatalogKey` and `unattendedReasoningEffort` | 400 unknown model or effort; 401 capability; 403 non-local or compatibility instance; 404 instance; 409 stale revision |
| POST /api/v2/admin/agent-instances/{instanceId}/automation/registrations/{registrationId}/model | Owner capability; trusted local caller; `{ expectedRevision, catalogKey, reasoningEffort }`. A blank catalog key uses the unattended or conversation default. The registration JSON includes `modelSource`: `Trigger override`, `Unattended default`, or `Conversation default` | 200 registration summary | 400 unknown model, effort, or inactive registration; 401 capability; 403 non-local or compatibility instance; 404 instance or registration; 409 stale revision |
| GET /api/v2/agent-instances | Owner capability | 200 active managed instances for User new-chat (`instanceId`, `definitionId`, `activeVersion`, persona `name`/`role`, exact-version `voiceAvailable`/`language`) | 401 capability; 403 non-local |
| GET /api/v2/admin/definition-drafts/{draftId}/resources | Owner capability; trusted local caller | 200 draft resource bindings (metadata only) | 401 capability; 403 non-local; 404 |
| POST /api/v2/admin/definition-drafts/{draftId}/resources/content | Owner capability; trusted local caller; raw body with `Content-Type` and bounded size | 201 stored content hash and byte length | 400 oversize/empty; 401 capability; 403 non-local |
| PUT /api/v2/admin/definition-drafts/{draftId}/resources | Owner capability; trusted local caller; JSON `{ expectedRevision, resourceId?, logicalPath, kind, mediaType, contentSha256, byteLength }` to bind or replace one draft resource | 200 draft resource row | 400 path/kind/secret; 401 capability; 403 non-local; 404; 409 stale revision |
| PUT /api/v2/admin/definition-drafts/{draftId}/resources/batch | Owner capability; trusted local caller; `{ expectedRevision, items[] }` of already-stored `{ logicalPath, kind, mediaType, contentSha256, byteLength }`. One transaction. Success returns the new revision and the complete draft resource list ordered by logical path, including rows that were already bound. An invalid item, collision, limit failure, or stale revision leaves rows and the revision unchanged | 200 `{ revision, items }` | 400 validation; 401 capability; 403 non-local; 404; 409 stale revision |
| DELETE /api/v2/admin/definition-drafts/{draftId}/resources/{resourceId}?expectedRevision={n} | Owner capability; trusted local caller; `expectedRevision` query parameter | 200 removed draft resource row | 401 capability; 403 non-local; 404; 409 stale revision |
| GET /api/v2/admin/definition-drafts/{draftId}/resources/{resourceId}/content | Owner capability; trusted local caller | 200 resource bytes | 401 capability; 403 non-local; 404 |
| GET /api/v2/admin/definitions/{definitionId}/publications/{version}/resources | Owner capability; trusted local caller | 200 immutable publication bindings | 401 capability; 403 non-local; 404 |
| GET /api/v2/sessions/{sessionId}/triggers | Owner capability; optional `limit` (minimum 1, clamped to 100) and `before` (registration ID) | 200 newest-first safe schedule page for the session's Agent Instance and trusted profile; omitted pagination preserves the full-list contract | 400 invalid query; 401 capability; 404 session or foreign/unknown cursor |
| POST /api/v2/sessions/{sessionId}/triggers/{triggerId}/cancel | `{ "expectedRevision": n }` | 200 updated safe schedule | 400 invalid revision; 401 capability; 404 session, guessed id, or other instance; 409 stale revision |
| GET /api/v2/sessions/{sessionId}/work-items | Optional `limit` (default 50, minimum 1, clamped to 100), `before` (work-item ID), and `attentionOnly` | 200 newest-first safe page for the session's Agent Instance and trusted profile. `attentionOnly=true` selects completed attention results. Each item includes `attentionRequired`, `attemptCount`, and `maxAttempts` | 400 invalid query; 401 capability; 404 session or foreign/unknown cursor |
| GET /api/v2/sessions/{sessionId}/work-items/{workItemId} | No body | 200 safe detail, including `attentionRequired`, `attemptCount`, and `maxAttempts` | 401 capability; 404 session, other owner, or unknown id |
| GET /api/v2/sessions/{sessionId}/work-items/{workItemId}/result | No body | 200 semantic result, including `attentionRequired` | 401 capability; 404 when no result exists, including not-yet-complete and other owner |
| POST /api/v2/sessions/{sessionId}/work-items/{workItemId}/cancel | `{ "expectedRevision": n }` | 200 cancelled, or the same cancelled item | 400 terminal work; 401 capability; 404 other owner; 409 stale revision |
| POST /api/v2/sessions/{sessionId}/work-items/{workItemId}/approvals/{approvalId}/approve | `{ "expectedRevision", "expectedApprovalRevision", "actionHash" }` | 200 queued resume of the same item | 401 capability; 404 other owner; 409 stale revision or altered hash |
| POST /api/v2/sessions/{sessionId}/work-items/{workItemId}/approvals/{approvalId}/reject | same body | 200 queued resume without dispatch | 401 capability; 404 other owner; 409 stale revision or altered hash |

Operational list pages retain the `{ items }` envelope. Ordering is descending immutable creation time, then descending ID; `before` is exclusive and owner scoped. Clients request 21 rows for a 20-row UI page, use the extra row only to detect more data, and send the last displayed ID as the next cursor. Read acknowledgements are browser-local UI state and do not mutate work records.

Work-item JSON uses status tokens `queued`, `running`, `needsApproval`, `retrying`, `completed`, `failed`, and `cancelled`. List and detail include origin, optional recorded Schedule/Thought `intent`, progress, failure summary, `attemptCount`, `maxAttempts`, and, while approval is pending, `approvalId`, `approvalRevision`, `actionHash`, and `approvalPreview`. A terminal failed item may include optional `diagnosticId`. They omit evidence JSON, checkpoint payload, prepared action JSON, and result text. The result route returns only `workItemId`, `text`, `completedAt`, and `attentionRequired`. No new SignalR message carries durable work. A paused or ended session can still list and decide its owner's work.

Agent list (GET detail returns one item with the same fields):

```json
{"agents":[{"id":"compliance","version":1,"name":"Jordan","role":"Compliance reviewer","description":"Review simulated policy questions without changing live records.","voiceAvailable":true,"language":"en"},{"id":"customer-support","version":1,"name":"Sam","role":"Customer support representative","description":"Resolve a simulated support issue.","voiceAvailable":true,"language":"en"},{"id":"examiner","version":1,"name":"Alex","role":"Speaking examiner","description":"Practice a speaking examination.","voiceAvailable":true,"language":"en"},{"id":"general-assistant","version":1,"name":"Riley","role":"General assistant","description":"Open-ended chat for harness and contract checks.","voiceAvailable":true,"language":"auto"}]}
```

`language` is the conversation language policy (`auto` or a fixed BCP-47-like language tag), not the speech locale; see `capabilities.speechLocale.effective` on `session.ready` for STT/TTS.

Expose only descriptors, not systemInstructions, full definitions, provider configuration or credentials. `voiceAvailable` is the public formula in [Interfaces](04-backend-interfaces.md#independent-speech-ports): Voice.Enabled and structurally resolvable STT and TTS paths on the effective speech plan (including resolvable Browser/client-transport paths). Synthetic server-audio and Browser client transports both count as available when selected and resolvable. Text-only definitions return false without failing backend startup. Gated or unknown hosted speech paths stay unresolved and advertise `voiceAvailable: false`. `session.ready` repeats the same flag on the agent descriptor. For managed sessions, the `session.ready` agent `name`/`role`/`description` project the session snapshot's pinned persona (when present), not the live instance catalog, so reopened historical chats keep their creation-time identity in the header. GET session and catalog items expose the same pinned `agentName` and `agentRole` (definition identity when no persona is pinned) so a paused or ended header matches the chat list.

POST request:

```json
{"agentId":"examiner","agentVersion":1,"mode":"text"}
```

`agentId` required; `agentVersion` optional, resolved to the latest and then pinned. `mode` is optional and defaults to `text`; allowed values `text|voice`. Mode is the session's **current** interaction mode, not a second conversation. Voice on create is rejected with `VoiceUnavailable` when `voiceAvailable` is false. No automatic opening greeting: ready waits for user input or an eligible initiative trigger. The voice-call button issues `session.mode.set` on the **same** session after attach, after local [audio preflight](13-frontend-implementation-spec.md#voice-preflight); when no session exists yet, the first Voice click creates/attaches a text session, reads `session.ready` transports, then preflights. It must not create a fresh unrelated voice session or drop text history. Do not send `audio.input` until Mode is voice.

201 / GET session response:

```json
{"sessionId":"873f07d1-e264-4c81-a31b-7e59e940b842","agentId":"examiner","agentVersion":1,"mode":"text","pendingMode":null,"status":"created","createdAt":"2026-09-15T00:00:00.000Z","updatedAt":"2026-09-15T00:00:00.000Z","lastEntrySequence":0,"activeResponseId":null,"protocolVersion":1,"pauseReason":null,"lifecycleStatus":"active","speechLocale":{"effective":"en","source":"agentDefault","override":null}}
```

status is `created|attached|paused|ending|ended`; additive `lifecycleStatus` is `active|paused|completed|expired|cancelled|ended`. Purpose metadata and completion-authority policy are not public. `pendingMode` is `text|voice` while a mode change is queued, otherwise null. `pauseReason` is null unless `status` is `paused`, then one of `manual|inactivity|silentEvaluation|initiative|disconnected|recovered|persistence` (extensible string). Semantic pauses (`manual|inactivity|silentEvaluation|initiative|persistence`) require explicit `POST .../reopen` before attach and refresh `lastUserActivityAt`. Transport pauses (`disconnected|recovered`) resume through attach/reconnect alone and preserve `lastUserActivityAt`. activeResponseId is required and nullable. GET reads the current runtime projection when active, durable snapshot otherwise. Session summary is server-only and never appears on this view. Initial mode is honored only after attach; creation performs no provider calls. The browser opens semantic pauses read-only until the user resumes; transport pauses reconnect automatically from the catalog or deep link.

History response (all listed entry fields required; nullable responseId/sourceEventId):

```json
{"items":[{"entryId":"019944af-0000-7000-8000-000000000010","sequence":1,"sourceEventId":"019944af-0000-7000-8000-000000000010","role":"user","text":"Please explain.","responseId":null,"status":"completed","deliveryMode":"text","heardTextEndExclusive":15,"receivedTextEndExclusive":15,"createdAt":"2026-09-15T00:00:01.000Z"},{"entryId":"019944af-0000-7000-8000-000000000011","sequence":2,"sourceEventId":null,"role":"assistant","text":"There are three points.","responseId":"019944af-0000-7000-8000-000000000012","status":"interrupted","deliveryMode":"voice","heardTextEndExclusive":9,"receivedTextEndExclusive":23,"createdAt":"2026-09-15T00:00:02.000Z"}],"nextAfter":2,"hasMore":false,"hasOlder":false,"nextBefore":null}
```

Roles `user|assistant|applicationMessage`; an `applicationMessage` stores visible text only, has no `speechText`, and uses heard length 0. It is not a prompt turn. Chat groups it before the assistant entry with the same `responseId`. Clients apply assistant text, speech, blocks, terminal metadata, and response-scoped failure to that assistant entry only. A failed entry's `failure` object may include allowlisted `failureReason`, `providerResponseChannel`, `protocolRepair` (`attempted`), and `protocolRepairOutcome` (`succeeded`, `failed`, or `cancelled`). Older rows omit them. An application message changes through `session.entry.upsert` or an authoritative snapshot. statuses `completed|interrupted|failed|streaming`; deliveryMode `text|voice` is the mode in which that entry was produced (prompt builder: text assistant entries use received prefix; voice assistant entries use heard prefix). A pending local user entry is reconciled by sourceEventId (the original user.text eventId or voice utteranceId); assistant entries are reconciled by responseId. Streaming and interrupted assistant text is limited to ReceivedTextEndExclusive so superseded generated tails stay hidden. A durably Completed or Failed assistant entry exposes its full stored terminal display text even when the browser disconnected before its final receipt; ReceivedTextEndExclusive remains the conservative delivery coordinate and does not become hearing evidence. Additive optional `speechText` is the persisted public spoken projection when it was stored on the envelope; omit or null when absent. Additive optional `memoryReceipts` is controller admission metadata for that assistant entry (`outcome`, `operation`, `subject`, optional `scopes` listing memory layers successfully established or confirmed by this admission (e.g. `session`, `identityUser`, `user`), `presentation` `indicator|explicit`, `label`). It is omitted when every receipt is silent. It is not part of `text` or `speechText`. `indicator` is a quiet confirmation such as Remembered or Forgotten. `explicit` is the visible failure of a user-explicit save or delete. Agent-inferred outcomes are not projected. The first-party UI shows it only when it meaningfully differs from display text: **Speech text** for text-delivered secondary speech, **Spoken** reserved for Voice delivery. Same-mode or omitted speech does not duplicate display. Public heardTextEndExclusive is clamped to the projected text length; the internal heard offset remains available for model context even if a last text receipt was lost. History is ordered by stable entry sequence; a streaming entry is updated in place, so reconnect fetches a fresh snapshot, not merely entries after its old sequence. Omit `after` and `before` for the newest page; `before` requests the immediately older page; `after` remains a forward cursor. `before`+`after` is rejected. Newest and backward pages take `limit+1` internally, return items ascending, and set `hasOlder` plus `nextBefore`. `after` is an entry sequence, not an event cursor. No persisted PCM or speculative partial user transcript appears here.

**Follow-on P1 observed (frozen on `dceaccb`; see [TODO.md](../TODO.md)):** Protocol-v1 `status` stays `created|attached|paused|ending|ended`; additive `lifecycleStatus` is observed on session views, catalog items, `session.ready`, and `session.state.changed`. Advisory RequestComplete publishes `session.completion.intent`. Purpose/deadline/completion policy stay private on ordinary public projections; trusted-host `/api/v2/host/sessions` is the create/configuration surface. First-party lifecycle is always User authority. Additive `speechLocale` on session views and `session.ready` capabilities exposes `{effective, source, override}` independently of the public agent `language`. Browser STT uses `speechLocale.effective`; Browser TTS fails closed when no compatible voice exists. Hosted adapter locale hints are observed. POST `/api/v2/sessions/{id}/speech-locale` and create `speechLocale` are the session override; the first-party Speech locale Select is observed. Real Chrome 153 `fr-FR` Browser STT/TTS smoke is observed. See [Technology Decisions](10-technology-decisions.md#decision-provider-neutral-effective-speech-locale).

**P2D session model (observed):** Public session views and `session.ready` capabilities.model expose `{catalogKey, displayName, selectionSource, reasoningEffort, modelId}` without secrets. `selectionSource` records **model origin** (`systemDefault` vs `user`), not who last changed reasoning effort or other settings. GET `/api/v2/models` is the safe catalog. Create/mutate accept only catalog-level `key`/`reasoningEffort`; Default resolves to a concrete selection at persist time. Catalog `providerAlias` must be `primary-llm` until multi-provider routing exists. Live mutation persist-before-use; `SessionBusy` (409) while generating or while a prior model-selection persist is still in flight.

Health: `{"status":"healthy","profile":"Synthetic","protocolVersion":1}`. Check local startup/SQLite access, not remote LLM billing or an active model call. Use built-in ASP.NET Core OpenAPI at /openapi/v1.json in development; no admin UI required.

Validation/provider errors use Problem Details on HTTP:

```json
{"type":"about:blank","title":"Invalid session request","status":400,"code":"ValidationError","detail":"mode must be text or voice.","traceId":"demo-trace","errors":{"mode":["Unsupported value."]}}
```

errors is optional and contains only safe field-level messages. Never include provider response bodies, stack traces or keys.

## Hub and encoding

Endpoint `/hubs/session`; MessagePack Hub Protocol v1 is the canonical application encoding (distinct from SignalR's own protocol version). `protocolVersion` stays 1. One connection attaches one session and a session has one owning connection. The hub validates, associates the connection lease, and forwards to application services. Runtime logic stays outside hub methods. Audio DTOs use `byte[]`/Uint8Array encoded as MessagePack binary. MessagePack is the default. `VITE_AGENTCORE_REALTIME_PROTOCOL=json` is a diagnostic transport on the same hub, handlers, and lifecycle; JSON byte arrays arrive as base64 and are decoded at the client boundary. Unset or any other value stays MessagePack. Vite reads the variable when the SPA is started or built from `web/` env or the Vite shell; repo-root `.env` does not apply to native Vite. Restart Vite after changing the variable; a browser refresh alone is insufficient. Changing an ASP.NET or container environment variable after that build does not switch the transport. There is no `/hubs/session-json` hub and no second lifecycle. Local JSON inspection:

```bash
cd web
VITE_AGENTCORE_REALTIME_PROTOCOL=json pnpm dev
```

The same value may be placed in gitignored `web/.env.local` (see `web/.env.example`). A JSON diagnostic image passes the variable as a Docker build arg (`docker compose build --build-arg VITE_AGENTCORE_REALTIME_PROTOCOL=json` then `docker compose up`); the default image build leaves it unset. The browser cannot set `diagnosticId`, `correlationId`, or `causationId`; the server rejects client-supplied correlation and causation.

```csharp
// AgentCore.Contracts representative boundary signatures, not runtime ports.
public sealed record ClientEnvelope(int ProtocolVersion, string SessionId,
    string EventId, long Sequence, string? Timestamp, string? ResponseId,
    string? AttachmentId, string Type);
public sealed record InputAudioDto(int ProtocolVersion, string SessionId,
    string AttachmentId, string StreamId, long FrameSequence,
    long SampleOffset, byte[] Data);
public sealed record OutputAudioDto(int ProtocolVersion, string SessionId,
    string AttachmentId, string ResponseId, long FrameSequence,
    long SampleOffset, bool IsFinal, byte[] Data);
```

Envelope record describes common metadata; each command has a statically typed payload DTO following the table, not an arbitrary dictionary. Implement SignalR hub `Attach(AttachCommand)`, `SendText(UserTextCommand)`, `CancelResponse(CancelResponseCommand)`, `SetMode(SetModeCommand)`, `SpeechStarted(SpeechStartedCommand)`, `SpeechEnded(SpeechEndedCommand)`, `SpeechEvidence(SpeechEvidenceCommand)`, `SendAudio(InputAudioDto)`, `PlaybackStarted(PlaybackCommand)`, `PlaybackProgress(PlaybackCommand)`, `PlaybackCompleted(PlaybackCommand)`, `PlaybackStopped(PlaybackCommand)`, `ResponseReceived(ResponseReceiptCommand)`, `SetMuted(MuteCommand)`, `EndSession(EndCommand)`. All return `Task<CommandAck>` except SendAudio returns Task after local ingress admission. Browser uses ordered `connection.send("SendAudio", dto)` calls, not a round-trip `invoke` for every frame; audio rejection is reported through SessionEvent error. CommandAck is `{eventId,accepted,error}` with error nullable; a non-null error uses the category/code/message/fatal/retryAfterMs payload from the error table. Accepted user text means its user entry and `ConversationTurnExecution` ownership are durable; it does not mean the response is complete.

The client registers `SessionEvent` for typed server envelope maps and `AudioOutput` for OutputAudioDto. Hub sends one event argument, not positional payload fields. Include serialization fixtures to verify exact keys/casing/binary bytes. Use WebSocket transport and MaximumParallelInvocationsPerClient=4 so an audio method cannot monopolize control handling. Audio ingress uses immediate bounded TryWrite and reports overflow; it never waits for STT/network work. Control sequencing still comes from the browser connection service and mailbox admission. Configure maximum receive message size 32 KiB and reject text >8,000 UTF-16 code units or frame >1,920 bytes at application validation.

## Envelope requirements and ordering

| Field | Client control | Server control |
| --- | --- | --- |
| protocolVersion | Required integer 1, including attach | Required 1 |
| sessionId | Required UUID | Required UUID |
| eventId | Required unique client UUID, reused for exact retry | Required new server UUID |
| sequence | Attach=0; subsequent controls start at 1 per attachment | Starts at 1 for ready, increases per attachment at actual send |
| timestamp | Optional UTC ISO string; advisory only | Required server TimeProvider UTC ISO string |
| correlationId | **Omitted.** If present, ProtocolError; never copied into EventContext | Required; server-stamped. Root may use the accepted client eventId as a correlation seed |
| causationId | **Omitted.** If present, ProtocolError; never copied into EventContext | Nullable; immediate producing event |
| responseId | Required for playback/receipt and `agent.response.cancel`; null otherwise | Required for all agent.*, playback.*, null for session/transcript/error unless response-scoped |
| attachmentId | Null on attach; required subsequently | Required after attach; protocol rejection may be null |
| type / payload | Required discriminant and typed payload | Required discriminant and typed payload |

`responseId` is the generation identity; there is no competing generationId. Transcript payloads require utteranceId. Audio deliberately omits eventId/correlation/timestamp/global sequence to keep high-frequency frames separate; required fields are exactly the audio DTO fields above. Correlate audio via responseId on output or streamId on input. Frame sequences/sample offsets provide audio ordering. Audio output identity is verified against the response lifecycle control stream, never inferred from arrival time.

Control sequences increase in send order, not when work was produced. Each live attachment assigns the next control sequence and finishes that `SessionEvent` send before assigning another, including when tool progress and model output publish together. Priority stop can bypass queued audio/text without producing descending counters. Sender drops stale response data before assigning its control sequence. Only `playback.stop`/`playback.gain`/error/lifecycle controls may bypass queued deltas; `agent.text.completed` and successful `agent.response.completed` are ordering fences and wait for all preceding text/audio sends for that response. Client ignores duplicate server control sequences; a gap forces resynchronization (stop output, close the connection, then reconnect and reattach). Audio has independent counters. Buffer unknown-response audio at most 60 ms until `agent.response.started` arrives, otherwise reject; never play unknown identity. Control sender must send `agent.response.started` before submitting that response's first audio.

Client serializes control calls in its connection service; API accepts increasing sequence values, tolerates gaps caused by a rejected command, rejects backwards values except exact known eventId retry. Store dedupe outcomes for the attachment (maximum 1,024 controls; reject older unknown retries as StaleCommand). Repeated eventId with different payload is ProtocolError, including a changed `user.text.behavior`. A new attachment resets command sequencing. Text eventId is also stored as ConversationEntry sourceEventId; a retry after reconnect is deduplicated by that ID with fresh attachment/sequence metadata. Keep at most one unacknowledged text submission in the browser; audio and speech boundaries are never replayed. Non-text commands are not retried across attachment changes. `agent.response.cancel` is accepted when the expected `responseId` is still active or already terminal for this session (idempotent); it is rejected as StaleCommand when a newer response is live, and as ValidationError when the id is missing, malformed, or unknown. Accepted user.text is persisted before success ACK. `behavior=queue` while a response is live does not supersede it. The shipped first-party web client uses a local pending-send FIFO instead of `behavior=queue` during live responses; row Steer uses `behavior=interrupt`, `agent.response.completed` dequeues the FIFO head with the default non-interrupt path, and explicit Stop (`userStop`) does not dequeue queued drafts.

Example attach command (sent to Attach as one MessagePack map):

```json
{"protocolVersion":1,"sessionId":"873f07d1-e264-4c81-a31b-7e59e940b842","eventId":"019944af-0000-7000-8000-000000000020","sequence":0,"timestamp":"2026-09-15T00:00:00.100Z","responseId":null,"attachmentId":null,"type":"session.attach","payload":{"lastServerSequence":null}}
```

## Client events

Every control uses metadata above plus payload below. Empty payload is `{}`.

| Logical type | Hub method | Exact payload fields |
| --- | --- | --- |
| session.attach | Attach | lastServerSequence: integer\|null (diagnostic) |
| session.mode.set | SetMode | mode: text\|voice |
| user.text | SendText | text: string <=8,000 (blank allowed only with bindable `attachmentIds`); attachmentIds?: UUID[] max 10; behavior?: `queue`\|`interrupt` (omit=`interrupt`; unknown values are rejected) |
| agent.response.cancel | CancelResponse | empty `{}`; top-level `responseId` is the expected generation to stop (required UUID; creates no user entry) |
| agent.approval.respond | RespondApproval | approvalId: UUID, decision: `approve`\|`reject`; top-level `responseId` is the owning live generation (required UUID) |
| user.speech.started | SpeechStarted | streamId: UUID, utteranceId: UUID, sampleOffset: integer, activityScore: number 0..1 |
| user.speech.ended | SpeechEnded | streamId, utteranceId, sampleOffset: integer, durationMs: nonnegative number |
| client.speech.evidence | SpeechEvidence | kind: `started`\|`partial`\|`final`\|`ended`\|`failed`; utteranceId: UUID; revision?: integer >=0; text?: string <=8,000; confidence?: number 0..1; activityScore?: number 0..1; durationMs?: nonnegative number |
| audio.input | SendAudio | Dedicated InputAudioDto, no envelope |
| playback.started | PlaybackStarted | consumedSamples: 0, textEndExclusive: integer (clientSpeech: 0) |
| playback.progress | PlaybackProgress | consumedSamples: integer (clientSpeech: 0), textEndExclusive: integer |
| playback.completed | PlaybackCompleted | consumedSamples: total (clientSpeech: 0), textEndExclusive: integer |
| playback.stopped | PlaybackStopped | consumedSamples: final actual position (clientSpeech: 0), textEndExclusive: integer |
| response.received | ResponseReceived | textEndExclusive: highest rendered UTF-16 **display** offset; blockIds?: string[] |
| session.mute | SetMuted | muted: boolean |
| session.end | EndSession | reason: userEnded |

Playback textEndExclusive is the speech-coordinate offset of synthesized speech (display text when `reply.speech` is absent). Display receipts stay on `reply.text` and must not be copied onto heard. Send `response.received` when either the rendered display offset or the delivered `blockIds` set advances; a block that becomes visible without more display text still needs a receipt so reconnect can keep `DisplayDelivered`. Receipt/progress values must be <= emitted display/speech/samples and monotonic. Ignore playback.started/progress/completed/stopped and text receipts after response supersession or disconnect for state/history/context. A stopped acknowledgement may record diagnostic stop latency only. Completed successful responses may still accept a final text receipt to acknowledge already delivered text; this cannot authorize more output. When output transport is `clientSpeech`, every playback acknowledgement uses `consumedSamples=0`; `textEndExclusive` is the only heard coordinate (`started` must be 0, `progress` is clamped to already emitted speech-text, `completed` equals the final length only after `speech.output.completed`, `stopped` is the last trusted boundary). Assistant completion waits for that completed ACK. Explicit Stop leaves unaccepted first-party drafts in the browser queue, but already server-accepted `behavior=queue` turns keep durable ownership and dispatch after the interrupted response terminalizes. When muting, client sends ended boundary before mute; unmute produces session.ready-like stream data via session.state.changed with a fresh streamId before accepting PCM. Stream IDs are server-issued on ready/unmute/recovery and after a max-utterance restart. `client.speech.evidence` is admitted only for an attached unmuted Voice session whose input transport is `clientTranscript`; Voice listening on that path does not open an `ISpeechRecognizer` session, and PCM frames are ignored. Map kinds onto the existing speech evidence path: one durable final per utterance, ephemeral partials, final-before-ended, and `failed` as RecognitionFailed without a user turn. Protocol v1 remains additive: `audio.input` and server-audio PCM output DTOs are unchanged.

Speech boundaries include sampleOffset in the same stream coordinate as audio. Audio ingress defers a boundary until preceding samples are admitted; application receives speech observation promptly but recognizer flush waits for the ordered marker. This avoids separate hub invocation ordering corrupting STT buffering. One browser audio producer sends ordered frames; duplicates are ignored, gaps fail the stream.

## Server events

| Type | Required payload fields |
| --- | --- |
| session.ready | mode, pendingMode: text\|voice\|null, status, lifecycleStatus, agent descriptor, streamId: UUID\|null, audioFormat, capabilities, lastEntrySequence, history: entry array (latest 50, public projection; on reattach the active Streaming row includes the already-published display prefix so its length equals the next `agent.text.delta.textStart`, while `receivedTextEndExclusive` remains the durable receipt boundary), hasOlderHistory: bool, historyBeforeSequence: long\|null (pagination cursor for `GET /messages?before=` when more than 50 entries exist; null when `hasOlderHistory` is false), activeResponseId and conversationExecutionId: UUID\|null when accepted conversational execution survives reattach, outputState, pendingApproval: same bounded fields as `agent.approval.requested` plus `responseId` when a tool approval wait is still open (null otherwise) |
| transcript.partial | utteranceId, revision: integer, text |
| transcript.final | utteranceId, text, entryId: UUID, entrySequence: integer |
| transcript.discarded | utteranceId |
| agent.response.started | entryId: UUID, entrySequence: integer, trigger: userTurn\|longSilence\|environmentUpdate\|unfinishedInteraction\|scheduledOccurrence\|applicationEvent, conversationExecutionId: UUID\|null (present for accepted user conversational execution) |
| agent.progress | kind: preparing\|readingAttachments\|runningTool\|waitingExternal\|finalizing, state: started\|updated\|completed\|failed, operationId?: UUID, message?: trusted bounded status string. A `browser.*` tool uses kind `runningTool` with message `Using browser…`. The owning `responseId` is on the envelope, not nested in the payload. Transient live status only: never `session.ready` history, GET `/messages`, TTS/`clientSpeech` input, provider reasoning, or durable assistant text. Runtime-generated `operationId` is not a provider tool-call id. `session.state.changed.outputState` is unchanged and remains additive. |
| agent.approval.requested | approvalId: UUID, operationId: UUID, toolName: string, effect: readOnly\|write\|sensitiveWrite\|destructive, summary: bounded safe string, details: bounded string map, expiresAt: ISO-8601 timestamp. Owning `responseId` is on the envelope. Live-session only; not durable history. `session.ready` may replay the same bounded payload (including `responseId`) while an approval wait is still open after reattach. No credentials, provider bodies, or raw binary. |
| agent.speech.projection | mode: same\|custom\|none, text: accepted playback projection for the live response (Voice); published when validated speech is ready (`custom`) or when `same` completion fallback is applied; never raw markers or native JSON; not a separate history entry. Clients must not treat `same`/`none` projections as public semantic `speechText` (only `custom` maps to history `speechText`) |
| agent.text.delta | text, textStart: UTF-16 offset of **display** text |
| agent.text.completed | textLength: integer (display) |
| agent.block.upsert | blockId, kind: markdown\|attachment\|artifact\|unknown, text, fallbackText, attachmentId?, artifactId? |
| audio.output | Dedicated OutputAudioDto |
| speech.output.segment | segmentIndex: integer starting at 0, textStart: UTF-16 **speech-text** offset, text: already-speakable string, voiceHint, language, speakingRate; `responseId` is on the envelope |
| speech.output.completed | textEndExclusive: integer in speech-text coordinates; distinct from `agent.response.completed` |
| playback.gain | gain: 0.2\|1.0, rampMs: 20, candidateId: UUID |
| playback.stop | reason: interrupted\|disconnected\|ended\|providerFailed\|audioFailed\|modeChange |
| agent.response.interrupted | reason: userBargeIn\|newText\|userStop\|disconnected\|ended\|modeChange, heardTextEndExclusive: integer, speechText?: string\|null |
| agent.response.completed | status: completed\|failed, heardTextEndExclusive: integer, finishReason: lengthLimit\|contentFiltered\|null, speechText?: string\|null, memoryReceipts?: array of `{outcome, operation, subject, scopes?: string[], presentation: indicator\|explicit, label}` or null, effectReceipts?: array of `{tool, status, label}` or null. `scopes` lists memory layers covered by the admission outcome (e.g. `session`, `identityUser`, `user`); omit when none apply. Memory receipts and effect receipts are not appended to display or speech text. An effect receipt is runtime copy for a committed write (`browser.close` → `Browser closed`, `email.send` → `Email sent`) and stays visible when the follow-up reply fails.
| session.state.changed | status, lifecycleStatus, mode: text\|voice, pendingMode: text\|voice\|null, inputState, outputState, muted: boolean, streamId: UUID\|null |
| session.entry.upsert | One history item, same fields as `session.ready` history and `GET /messages`, including `role: applicationMessage`. Envelope `responseId` is the owning response. Published on the existing hub when a new application message is admitted. A duplicate or cancelled send does not publish a second item. Not spoken. |
| session.completion.intent | reason, advisory: boolean |
| error | category, code, message, fatal: boolean, retryAfterMs: integer\|null, diagnosticId: UUID string\|null, optional allowlisted failureReason and providerResponseChannel |

For the active response, the browser appends a delta when `textStart` equals its current UTF-16 display length. It may accept a matching overlap and append only the new suffix. A forward offset gap is not terminal and must not be ignored indefinitely: reattach and use the authoritative active prefix from `session.ready`, then continue at the next delta offset. While that response remains live, the reattached browser resumes `response.received` updates.

For transcript.final, the server publishes only after the user entry is durably saved; entryId and entrySequence identify the committed turn and gate client history. Ignored or discarded recognition finals emit transcript.discarded instead (clear ephemeral live transcript only; no durable user entry). `agent.response.started` identifies the new assistant entry even before its first periodic checkpoint.

session.ready audioFormat is `{encoding:"pcm_s16le",sampleRateHz:24000,channels:1,frameDurationMs:20}` in voice, null in text. The agent descriptor includes additive `language` (conversation language policy: `auto` or a fixed BCP-47-like language tag). capabilities is `{stt:{streamingAudio,partialTranscripts,speechBoundaryEvents,cancellation,transport:"serverAudio"|"clientTranscript"},tts:{streamingAudio,timingMarks,cancellation,voiceSelection,speakingRate,supportedFormats,transport:"serverAudio"|"clientSpeech"},bargeInPolicy:"semantic"|"speechAndFinal"|"speechActivity"|"none",speechLocale:{effective,source:"sessionOverride"|"agentDefault"|"providerFallback",override}}`. `speechLocale.effective` is the resolved BCP-47-like speech locale for STT/TTS and may differ from agent `language` (for example `auto` conversation policy with provider-fallback speech locale). `transport` fields are additive under protocol v1 and may be ignored by older clients. Existing STT/TTS booleans remain for `serverAudio` so current server-audio clients keep working. For `clientTranscript`, voice-mode `session.ready` advertises the effective client facts (`partialTranscripts=true`, `speechBoundaryEvents=true`, `streamingAudio=false`); do not send all-false STT flags merely because no backend `ISpeechRecognizer` exists. Do not omit or invert `voiceAvailable` to force old clients to refuse Browser hosts. Capabilities contain **effective** adapter facts plus those transports; they never include secrets, endpoints or adapter names. Speech booleans are false and supportedFormats is empty in text mode. In voice mode supportedFormats lists canonical encoding/sampleRateHz/channels records that a server-audio adapter can produce. Ready may identify one durable accepted conversational execution and its live response so attaching can observe—not restart—that work. Other interrupted responses never resume. Ready history items use the public fields from GET `/messages` (including `deliveryMode`, both text-end offsets, optional `speechText`, and optional `failure` on a failed assistant row) plus only the active Streaming prefix exception described above; it is not a persistence snapshot and must not include session summary. `failure` is `{ diagnosticId, correlationId: string|null, category, code }` and is omitted when the row has no failure reference. Legacy failed rows stay failed with `failure` null.

Example server control (other events use identical metadata with their table payload):

```json
{"protocolVersion":1,"sessionId":"873f07d1-e264-4c81-a31b-7e59e940b842","attachmentId":"019944af-0000-7000-8000-000000000001","eventId":"019944af-0000-7000-8000-000000000014","sequence":4,"timestamp":"2026-09-15T00:00:02.100Z","correlationId":"019944af-0000-7000-8000-000000000010","causationId":"019944af-0000-7000-8000-000000000013","responseId":"019944af-0000-7000-8000-000000000012","type":"agent.text.delta","payload":{"text":"Hello","textStart":0}}
```

Terminal interruption uses `agent.response.interrupted`, never a later `agent.response.completed` event. Failed response uses error then `agent.response.completed(status=failed)`; it does not also emit interrupted. On any terminal failed/interrupted event client tombstones the response and flushes playback. On success client retains text and rejects additional output. `agent.text.completed` does not imply audio has finished.

## Connection lifecycle

Session IDs act as local/demo bearer capabilities: use cryptographically random UUIDv4 session IDs (122 random bits), never sequential IDs. Production event/response IDs use UUIDv7. `IIdGenerator.NewSessionId()` and `NewId()` are specified in the C# port so deterministic tests cover both. Do not log session URLs publicly. This is not a full authentication system. **Planned until verified:** post-MVP trusted-local owner capability replaces SessionId-as-credential for catalog, attach, upload, bind, artifacts, and content ([R1](10-technology-decisions.md#decision-trusted-local-owner-capability-r1)); historical MVP still uses session IDs as demo bearers until that phase is verified.

Attach atomically acquires a fresh attachmentId lease, activating an in-memory runtime. When the durable catalog row is semantically `paused` (`pauseReason` other than `disconnected|recovered`), reject with recoverable `SessionPaused` until `POST /api/v2/sessions/{id}/reopen` clears pause and bumps `runtimeEpoch`. Transport `paused` rows (`disconnected|recovered`) attach without reopen and do not refresh `lastUserActivityAt`. If `MaxActiveSessions` would be exceeded, reject with recoverable `SessionCapacityExceeded` and `retryAfterMs` defaulting to 5000; the durable session remains and may be retried. An exact repeated Attach eventId on the same connection returns the original acknowledgement/ready snapshot and lease; it never creates a second owner. A new Attach on an already attached connection is rejected until it disconnects. A still-attached session rejects a second connection with SessionInUse; no silent takeover. Disconnect invalidates the old lease, supersedes output, closes STT, clears `pendingMode` (do not carry `PendingMode=Voice` into reconnect), and pauses initiative. If the server hasn't detected disconnect yet, reconnect retries SessionInUse with bounded backoff rather than stealing the session. Configure SignalR keepalive 10 seconds/client timeout 30 seconds. Grace retention is 60 seconds after disconnect is observed.

Attach loads/reconciles the durable snapshot if necessary and returns ready with current history/status/**mode**/pendingMode; it does not replay event/audio buffers. lastServerSequence helps diagnose a gap only. Before attach the browser discards all playback buffers and marks old active responses interrupted. Once ready arrives it replaces its history projection by entryId and may fetch older pages. A recently modified interrupted entry must be refreshed even if its entry sequence predates the cursor. User unsent text remains in the composer; retry uncertain accepted text only with original eventId. An ended session cannot be resumed or attached; create a new one explicitly. The browser may still GET the session view and `/messages` to render a read-only history. `session.mode.set` follows [controller mode rules](05-interaction-controller.md#mode-transitions). Re-entering voice after reconnect requires a new Voice click (preflight + `session.mode.set`); ready `pendingMode` is null after disconnect/crash recovery even if a voice request was queued.

ProtocolVersion !=1 yields ProtocolVersionMismatch before attachment or audio acceptance, reports supportedVersions=[1] in the safe error extension, then closes the connection. No automatic version downgrade. Clean End/DELETE first disables input and supersedes output, then persists status Ended, sends state changed, releases lease. If persistence fails, remain Ending with retryable SessionPersistenceUnavailable; do not falsely report successful durable end.

```mermaid
sequenceDiagram
    participant B as Browser
    participant H as Hub / SessionManager
    participant S as Session Runtime
    participant D as SQLite
    B--xH: Connection lost
    B->>B: Stop playback, discard PCM, show Reconnecting
    H->>S: Detach old lease
    S->>S: Supersede R1, cancel providers, clear pendingMode, pause initiative
    S->>D: Save interrupted history + paused snapshot
    B->>H: Reconnect then Attach(sessionId, cursor)
    H->>S: Acquire new lease, restore if evicted
    S->>D: Load checkpoint if needed
    S-->>B: session.ready(new attachment, snapshot, new streamId)
    B->>B: Replace history; no old audio replay
```

## Error policy

Categories: Provider, Session, Protocol, Audio, Validation, Connection, Transport. User-safe code/message/fatal are required. Authentication, RateLimited, Timeout, Cancelled, InvalidRequest, InvalidResponse, Unavailable, UnsupportedCapability and Unknown are provider codes normalized by the adapter. Session codes include SessionBusy, SessionInUse, SessionPaused, SessionPersistenceUnavailable, SessionCapacityExceeded, VoiceUnavailable and StaleCommand. `session.attach` on a durably paused session returns recoverable `SessionPaused` until `POST .../reopen` succeeds. Transport codes include recoverable `AudioDiscontinuity` (sequence/offset/gap; recognition restarts on a new streamId) and recoverable `MaxUtterance` (forced end after `Voice.MaxUtteranceSeconds`, default 120; recognition restarts on a new streamId). An unexpected execution failure may include optional `diagnosticId` on the `error` event and on a command ack `error`. Expected validation, not-found, conflict, protocol rejection, cancellation, and shutdown omit it. `diagnosticId` is not an extension. Expected user cancellation emits interruption, not an alarming error toast. A midstream provider failure keeps the partial entry marked failed, stops TTS/playback, and permits a new user turn; never auto-replay the request. STT/TTS failure disables voice input/output for that session until explicit reconnect or a mode reset to text; the same session's text mode remains usable. Session storage corruption/ownership failure is fatal; temporary provider/connection errors are recoverable. No automatic Real→Synthetic fallback that would mislead the user.

## Post-MVP planned until verified

Phases A–H are observed on the runtime (including Docker `sandbox.run`). Phase I WorkItems are not-applicable until the future trigger in [Technology Decisions](10-technology-decisions.md#post-mvp-planned-until-verified). Full R1–R6 text is in that same section. Phase D initiative/deactivation and Phase F workspace execution view are observed above.

**Lease versus Attachment.** Wire `attachmentId` is the hub **connection lease**. User-uploaded files are HTTP `Attachment` records with `AttachmentId`. Never send attachment binaries or base64 on SignalR.

**Trusted-local owner capability.** Catalog, lifecycle, attach, upload, bind, artifact, and content routes require `X-AgentCore-Owner-Capability` (hub attach includes the same token). `SessionId` is not a credential. Native issue/use is loopback `POST /api/v1/local/owner-capability`; Compose published-port NAT additionally trusts this container's default gateway when `Hosting:TrustPublishedPortGateway` is true. Browser restore from `localStorage`; hashed grant survives API restart. Fail closed without leaking other sessions.

**Additive lifecycle (keep v1 DELETE).** Versioned routes:

| Method | Meaning |
| --- | --- |
| GET /api/v2/sessions | Catalog, `UpdatedAt` descending, stable cursor pagination; includes labeled Ended rows. Each item includes additive `agentName` and `agentRole` from the session's pinned persona, or the definition identity when the session has no pinned persona. `agentId` remains the definition id |
| POST /api/v2/sessions | Create with exactly one of `agentInstanceId` (managed `Compatibility=false` instance; pins active definition version and persona) or legacy `agentId`/`agentVersion`; optional catalog-level `model` (`key`, `reasoningEffort`); omit resolves the system default and persists that concrete selection. v1 create remains `agentId`-only |
| POST /api/v2/sessions/{id}/rename \| archive \| unarchive | Persist catalog mutation in the runtime revision stream |
| POST /api/v2/sessions/{id}/reopen | When `paused`, clears pause, bumps `runtimeEpoch`, and refreshes `LastUserActivityAt`; reconciles a detached in-memory runtime when present. No-op on `created` (epoch unchanged). Rejected with `SessionInUse` when status is `attached` or a hub lease is active. Not the same as `session.attach` |
| POST /api/v2/sessions/{id}/deactivate | Runtime deactivation: cancel live output, persist Paused, increment RuntimeEpoch; not archive and not v1 end; idempotent |
| POST /api/v2/sessions/{id}/lifecycle | First-party/user `LifecycleTransition`. `target` Active/Paused/Completed/Expired/Cancelled/Ended; optional `reason`. Caller authority is always `User`; a request `source` cannot self-promote to Host/System/Agent/Legacy. Persist-before-ACK; protocol-v1 `status` stays `paused`, `created`/`attached` after resume, or `ended`. Idempotent same-outcome repeats |
| POST /api/v2/host/sessions | Trusted-host create. Same owner capability as catalog today (local single-owner); **not** a deployment security boundary when the browser is untrusted relative to an examination host—future platforms need separate host credentials/scopes. Separate contract from public `POST /api/v2/sessions`. Body may include `purpose` (`kind` ongoing/goal, `description`, optional ISO-8601 `deadlineAt` with explicit `Z` or `±HH:mm`), `completionPolicy` (`agentCompletion` disabled/advisory/allowed, `userCompletionAllowed`, `userCancellationAllowed`), `maxDurationSeconds` (positive integer, at most 30 days), and catalog-level `model` (`key`, `reasoningEffort`) stored as selection source Host. Provide `deadlineAt` or `maxDurationSeconds`, not both; max duration resolves once to absolute `deadlineAt`. Response is a host view that includes purpose/policy/lifecycle provenance. Do not send a raw .NET TimeSpan |
| GET /api/v2/host/sessions/{id} | Trusted-host projection of purpose, completion policy, and lifecycle provenance. Ordinary `SessionView`, catalog, `session.ready`, and history stay private of those fields |
| POST /api/v2/host/sessions/{id}/lifecycle | Host-authoritative `LifecycleTransition`. Server sets source `Host` regardless of request `source`. Host completion bypasses user/agent policy bits |
| GET /api/v2/models | Safe catalog: system `defaultKey` plus descriptors (key, display name, capabilities, supported/default reasoning effort). No secrets, provider alias, BaseUrl, or credentials |
| POST /api/v2/sessions/{id}/speech-locale | Set or clear session speech override (`locale` BCP-47-like or null). Does not rewrite agent conversation `language`. Effective locale is public on GET and `session.ready` |
| GET /api/v2/profile | Owner capability; returns local profile revision and allowlisted typed values with provenance (`source`) and per-field `updatedAt` |
| PATCH /api/v2/profile | Owner capability; optimistic `expectedRevision`; allowlisted keys only; null/whitespace removes a field; server stamps `UserSet` (browser cannot set source) |
| POST /api/v2/sessions/{id}/model | Catalog-level session model mutation (`key` Default or a catalog key, optional `reasoningEffort`). Server resolves and persists concrete `SessionModelSelection`. Rejects `SessionBusy` while generating. Terminal sessions are read-only |
| GET /api/v2/sessions/{id}/knowledge/{identity} | Approved knowledge retrieval with citation metadata; 403 if the pinned role does not allow `knowledge.retrieve` or the identity |
| GET /api/v2/sessions/{id}/workspace | Execution-view listing (`prefix` query, default `/`); owner capability |
| GET /api/v2/sessions/{id}/workspace/content?path= | Read authorized logical path; legacy `/agent`, `/attachments`, `/workspace` and managed `/home`; new mode uses `/home` or `/working` plus read-only overlays; host paths never returned |
| PUT /api/v2/sessions/{id}/workspace/content?path= | Legacy writes use `/workspace/working|artifacts|state`; new mode writes `/working` or direct `/home` with durable replacement CAS; 250 MiB scope quotas; read-only overlays/secrets/escaping paths/symlinks remain forbidden |
| POST /api/v2/sessions/{id}/attachments/{attachmentId}/materialize | Explicit working copy plus Artifact metadata; originals unchanged; SHA-256 preserved; deterministic `name-2` collisions |
| GET /api/v2/sessions/{id}/artifacts | List session-owned artifacts; owner capability |
| GET /api/v2/sessions/{id}/artifacts/{artifactId} | Canonical `displayName`, `contentType`, `byteSize` plus existing provenance; owner capability; cross-session 404 |
| GET /api/v2/sessions/{id}/artifacts/{artifactId}/content | Exact stored bytes with stored content type and attachment Content-Disposition using canonical display name; owner capability; cross-session 404 |
| DELETE /api/v2/sessions | Bulk durable delete for demo/catalog reset. Optional `includeArchived=true` matches catalog listing; tears down live runtimes first; returns `{ deletedCount }` |
| DELETE /api/v2/sessions/{id} | Server-owned durable deletion of session-owned data (no client revision); tears down live runtime first |
| DELETE /api/v1/sessions/{id} | Unchanged terminal-end |

Ended rows: reopen/rename/archive/unarchive fail closed or no-op without resurrecting a runtime. GET session and GET `/messages` remain available for a read-only UI. GET attachment and Artifact list/metadata/content remain available for that view; upload, stage, abort, and materialize stay rejected. Versioned durable delete remains available.

**Rich envelope (observed).** Parent `ResponseId` carries `reply.text`, optional `reply.speech`, Markdown, attachment/artifact reference blocks, independent display and speech receipts. Unknown blocks fallback. Artifact refs in C use fixtures (`fixture-artifact-1`). Reconnect history uses the received display prefix while streaming or interrupted, the full durable display text after Completed/Failed, and display-delivered blocks only.

Provider-invalid rich-block metadata (`invalidBlocks`) may receive the same single bounded text-only repair as `missingDisplayText`, before any visible final output and with no pending tools. Repair retains completed tool results, offers no tools, proposes no Memory and preserves the actual reason if repair fails. Invalid Memory/authority structures still fail closed. Provider schemas and compatibility guidance permit artifact references only to IDs returned by successful Artifact creation tools for this Session; a workspace filename/path is not an artifact ID. Materialize it with `artifacts.create_from_workspace` or mention it in ordinary text. Session authorization still replaces unavailable/foreign references with `[Unavailable artifact]`. After authorization, Core normalizes real Artifact GUID references to lowercase hyphenated (`D`) form before response-block publication and persistence, matching the metadata API even when the model supplied uppercase or another accepted GUID representation. The explicit non-GUID fixture reference is unchanged.

**Uploads (observed store).** HTTP multipart/streaming only under `/api/v2/sessions/{id}/attachments` (POST pending, GET collection, GET metadata, GET `/content`, DELETE pending, POST `/stage` for the next speech turn). Unread storage of types outside the supported processor set follows the pinned role `environment.attachments.allowUnreadUnsupportedTypes` (shipped fixtures reject-at-upload). Client headers cannot widen that policy. `user.text` may include `attachmentIds` (UUIDs, max 10); empty text is allowed only with at least one bindable id. Bind runs in the runtime persist callback after the user entry is durable. Processors then extract off the mailbox; `session.state.changed` may emit `outputState=processingAttachments` while that work runs. Typed tools may emit `outputState=runningTools` while bounded tool steps run. Caps in the [resource table](10-technology-decisions.md#planned-resource-limits). OCR and Office readers remain out of scope.

## P9.7 owner HTTP surface

All governance/advanced owner routes retain the local trusted-owner filter under `/api/v2/admin/agent-instances/{instanceId}/harness`. Primary Chat authoring uses contextual fixed semantic tools through existing SignalR/runtime exact approval messages; no new realtime wire envelope is introduced.

| Method / suffix | Request | Effect |
| --- | --- | --- |
| GET root | none | Policy, recent internal candidate/diff/evidence inspection. |
| PUT `/policy` | expectedRevision, mode, scopes, sources, eligibleTools, frozen | Grant/revoke/freeze. Normal UI sends empty source/eligible arrays; server derives authorized configured eligibility. |
| POST `/prepare` | expectedRevision, purpose | Advanced owner candidate fork; no model loop. |
| POST `/verify` | preparationId | Advanced current candidate Core checks. |
| POST `/cancel` | expectedRevision | Discard unfinished candidate/pending approvals; Published returns 409. |
| POST `/approvals/{approvalId}` | expectedRevision, actionHash, approve | Legacy advanced exact candidate decision. |
| POST `/publish-adopt` | expectedRevision, draftRevision | Advanced owner lifecycle publication/adoption. |

`POST /continue` was removed and returns 404. Normal tools are `harness.inspect`, `harness.knowledge.upsert/remove`, `harness.skill.upsert/remove`, `harness.instructions.update`, `harness.tool.select/configure`. Mutations carry expectedVersion/policyRevision and a semantic payload. Non-Skill mutations require expected/observed assessment. Skill creation requires `skill.name`, `skill.description`, and `skill.procedure`; an update requires an existing `skill.id` or unique `skill.name` and only changed fields, preserving omitted name, description, procedure and metadata when the ID is supplied. `skill.id` is optional: an omitted ID updates a unique existing Skill with the same normalized name or creates a Core-generated ID. Ambiguous matching names require an explicit ID. Optional activationKeywords, requiredCapabilities and knowledgeIds default to empty on creation; knowledgeIds refer to identities returned by inspect. Legacy resourcePaths is accepted separately. Skill IDs permit lowercase letters, digits, dots, underscores and hyphens after the initial letter. Exact runtime approval binds tool plus canonical arguments; authorization is rechecked after the wait. A changed save returns `saved=true`, `changed=true`, activeVersion, appliesTo=future conversations, currentSessionUnchanged, verification, limitation and skillId for Skill upsert. An identical Skill/knowledge/instruction/tool-state write returns `saved=true`, `changed=false` with unchanged activeVersion. Blocking verification returns `saved=false`, `error=verification_failed`, up to eight bounded findings with check/field/code/message, `activeVersionUnchanged=true` and `retryable=true`. Other errors use safe error/message and diagnosticId when available.

Typed owner review uses string modes/statuses and evidence actor, draft revision, check, status, expected/observed/limitation. Verified, PartiallyVerified, CannotVerify, RequiresExternalEvidence and Failed remain distinct. Published evidence retains tested publishedDraftRevision. Invalid requests use 400; stale revisions/grants use 409; unexpected failures use server diagnostics. No private credential or host-path projection enters model context.

## P9.8-P9.9 owner HTTP surface

Experience review items expose `sourceCreatedAt` and nullable `checkpointAt`. `sourceAt` remains a compatibility alias for source creation. Session checkpoint time records when Core captured the stable cursor; WorkItem checkpoint time is the persisted terminal update. Legacy records omit checkpoint time rather than inventing it.

All routes below are under `/api/v2/admin/agent-instances/{instanceId}` and require the existing owner capability. Core derives the local profile and validates active managed instance ownership. No model/client origin, arbitrary profile or authority field is accepted as authorization. Successful responses are JSON; validation/conflict/not-found/forbidden use existing safe ProblemDetails.

| Method / suffix | Request / result |
| --- | --- |
| GET `/experience` | enabled, settingsRevision, contextBudgetCharacters, bounded structured items with source/cursor/date/Definition/model/work/status/visibility/revision/eligibility and safe failure metadata |
| PUT `/experience/configuration` | `{expectedRevision, enabled}` → review |
| POST `/experience/checkpoints` | `{sessionId}` → review; Core chooses the stable cursor, same checkpoint deduplicates |
| PUT `/experience/{experienceId}` | `{expectedRevision, visibility}` (`Eligible`, `Suppressed`, `Deleted`) → review |
| POST `/experience/reset` | empty body → review; tombstones existing checkpoints without deleting source work |
| GET `/thoughts` | minIntervalSeconds (15) and owned registrations: revision, enabled/status, intervalSeconds, thinkingPrompt, optional model/effort, next/last run, outcome/work status and effective model |
| POST `/thoughts` | `{expectedRevision:0, enabled, intervalSeconds, thinkingPrompt, modelKey?, reasoningEffort?}` → registration |
| PUT `/thoughts/{registrationId}` | same shape with current expectedRevision → registration |
| POST `/thoughts/{registrationId}/delete` | `{expectedRevision}` → `{deleted:true}` |
| POST `/thoughts/{registrationId}/run` | `{expectedRevision}` → `{occurrenceId}`; conflict while a prior activation is nonterminal |
| GET `/work-items` | normal WorkItem page for this trusted owner; optional `limit` (default 100), `before`, and `attentionOnly` match session work reads |
| GET `/work-items/{workItemId}` | safe WorkItem DTO for this instance and trusted owner; 401 without capability, 404 for unknown/foreign work; supports exact run navigation outside the current list page |
| GET `/work-items/{workItemId}/result` | normal result DTO; thought text is the safe summary |
| POST `/work-items/{workItemId}/cancel` | existing `{expectedRevision}` contract |
| POST `/work-items/{workItemId}/approvals/{approvalId}/approve` or `/reject` | existing `{expectedRevision, expectedApprovalRevision, actionHash}` contract |

`WorkItemResponse` adds optional `sourceId`, `registrationId`, `modelKey`, `thoughtOutcome`, and `intent`. `intent` is only the owner-authored Schedule task or Thought thinking prompt pinned in that execution’s occurrence evidence; later registration edits do not change it. Missing/invalid historical intent and other run types return null. Raw occurrence evidence is not returned. `origin` is Core-projected `Thought activation` or `Retrospection`; work lifecycle status names and approval semantics remain unchanged. Thought outcomes are `NoAction`, `ActionCompleted`, `AttentionRequested`; approval pending/retry/failure/cancel remain normal work states. There is no new hub event or public chat endpoint. NoAction/ordinary success creates no proactive alert; attention reuses the existing owner delivery contract.

## Admin Scheduled Work HTTP contract

The existing owner-capability filter protects `/api/v2/admin/agent-instances/{instanceId}/schedules`. Active managed instance ownership is checked on every operation. These are the same registrations visible to owned Chat schedule queries; thoughts and application-event subscriptions are excluded from this schedule management surface.

| Method | Suffix | Request / result |
| --- | --- | --- |
| GET | empty | `{ items: AdminScheduleResponse[] }` |
| POST | empty | `AdminScheduleRequest`; new registration (expectedRevision 0) |
| PUT | `/{registrationId}` | `AdminScheduleRequest`; revisioned edit |
| POST | `/{registrationId}/cancel` | `{ expectedRevision }`; `{ cancelled: true }` |
| POST | `/{registrationId}/run` | `{ expectedRevision }`; `{ occurrenceId }` |

`AdminScheduleRequest` contains `expectedRevision`, `enabled`, `intent`, `schedule`, optional `modelKey` and `reasoningEffort`. `schedule.kind` is `oneShot`, `daily`, `weekly` or `fixedInterval`; timing fields are `timeZone` (default UTC), `atUtc`, `interval` (days/weeks/seconds), `localTime` (HH:mm), `weekdays` (0 Sunday through 6 Saturday), `anchorAtUtc`, `endAtUtc`, `startDate`/`endDate` (yyyy-MM-dd), and `maxOccurrences`. Irrelevant fields are ignored. Omitted/null timing is rejected. Existing timezone/DST/bounds validation applies. Shared Definition timing policy enforces permitted kinds, one-shot horizon, minimum cadence and finite recurrence. Admin bypasses only AllowUserScheduling; Definition MaxActiveRegistrations is checked atomically on create/enable, with the same non-event-registration count used by Chat. Existing unchanged schedules can be disabled even when TriggerPolicy has been removed; enabling or changing timing remains forbidden.

Response includes `registrationId`, `revision`, `intent`, `enabled`, lifecycle `status`, the structured `schedule`, `authorizationOrigin`, nullable `sourceSessionId`/`sourceEventId`, `createdAt`, nullable `nextRunAt`, model override/effort/effective model, and nullable latest `lastWorkItemId`/`executionStatus`. Lifecycle names use Domain casing on Admin responses; Chat schedule status remains lower camel case including `disabled`. Registration provenance preserves numeric compatibility: CurrentUserTurn=0, AdminThought=1, AdminOwner=2, ApplicationEvent=3. AuthorizationOrigin and source Session describe original creation: a Chat-authored registration retains CurrentUserTurn and its source Session through Admin edits, whose current owner authorization is audited separately. List responses additionally include `policy` with allowOneShot/allowDaily/allowWeekly/allowFixedInterval, allowIndefiniteRecurrence, oneShotHorizonDays, minRecurrenceDays, minFixedIntervalSeconds and maxActiveRegistrations. Source kind and event source identify application-event subscriptions separately from who authorized them.

Validation returns 400, policy/owner refusal 403, missing owned resource 404, stale revision or busy/duplicate Run now 409 through existing safe ProblemDetails. Run now is admission acceptance, not model completion. Ordinary Background Work endpoints provide eventual execution/approval/results. No new hub method or event is added.

### Model-facing Continuity contracts

`continuity.search` accepts required `query` (up to 200 characters) and optional `limit` (1–10). `continuity.get` accepts `kind` (`Memory`, `Experience`, `Session`), UUID `id`, optional nonnegative `afterEntrySequence` and `limit` (1–20). Unknown arguments and owner overrides are rejected. Core obtains instance/profile from execution admission and uses the pinned Definition. Both tools are read-only and offered only to eligible managed, tool-capable executions with Continuity context. Responses include an explicit untrusted-history label and bounded result JSON. Result provenance records instance/profile, source kind/id, Definition/version when available, cursor, scope and observation time. Detail includes content, hasMore and nullable nextAfter. Sensitivity, deletion and suppression are rechecked during inspection. The legacy experience.recent executor remains compatible; automatic recall and offered unified inspection use Continuity.

## P9.10 identity maintenance contracts

Maintenance routes require the trusted-local owner capability and active managed instance ownership. Historical Memory detail follows the existing owner/scope inspection rules, including archived instance inspection. No SignalR envelope change is introduced.

| Method/path relative to `/api/v2/admin/agent-instances/{id}` | Contract |
| --- | --- |
| `GET /maintenance` | `{ agentInstanceId, allowAgentConsolidation, revision }`; absent row means false/revision 0 |
| `PUT /maintenance` | `{ expectedRevision, allowAgentConsolidation }`; returns saved state; stale revision 409; same audit/transaction as existing owner configuration |
| `GET /learned-memory/{memoryId}?scope=IdentityUser` | Owned exact historical detail; Session scope also requires `sessionId`; Active/Superseded/Deleted status, content-free tombstone |

Learned-memory responses add `status`, `provenance.derivedFromMemoryIds` and `provenance.maintenanceOrigin`. Experience responses add `derivedFromExperienceIds` and `maintenanceOrigin`; sourceKind includes Consolidation and visibility includes Superseded. Existing enum numeric values are preserved. Deleted Experience is still omitted from normal review.

| Semantic tool | Required arguments |
| --- | --- |
| `memory.consolidate` | `sourceMemoryIds` (2–8 unique UUIDs), exact `kind`, `subject` (1–128 characters), `content` (1–2000 characters) |
| `experience.consolidate` | `sourceExperienceIds` (2–8 unique UUIDs), `goal`, `attempts`, `decisions`, `outcomes`, `corrections`, `unresolved`, `difficulties`, `lessons`; existing Experience content bounds |
| `memory.forget` | one `memoryId` UUID |

Additional properties are rejected. Arguments cannot supply owner, scope, origin, approval or settings. Successful consolidation returns `status: consolidated`, result ID and exact parent IDs; successful forgetting returns `status: forgotten`, memoryId, scope and precise retained-source explanation. Exact tombstone retry returns `already_forgotten`. Normal tool-error and exact-hash approval contracts remain. Historical Admin detail is inspection, not an eligibility bypass for model-facing Continuity.

### P9.10 maintenance provenance and approval projection

Owner-protected learned-memory provenance adds optional `maintenanceAgentInstanceId`, `maintenanceSessionId` and `maintenanceWorkItemId` strings. Older items return null; these identify initiation independently of semantic owner and promotion lineage. Existing live approval details and durable `approvalPreview` now include Core-resolved Memory scope/kind/source subjects and User-wide effects. No model-supplied scope or approval-authority field is added. The durable approval preview is bounded to 12,000 characters.

### Admin automatic continuity review cadence

Trusted-local owner routes for an active managed instance, relative to `/api/v2/admin/agent-instances/{id}`:

| Method/path | Contract |
| --- | --- |
| `GET /continuity-maintenance` | `{ configuredIntervalSeconds: number|null, effectiveIntervalSeconds, minimumIntervalSeconds, maximumIntervalSeconds, defaultIntervalSeconds, usesDefault, configuredIntervalAllowed, revision, lastMaintenanceAtUtc: string|null }` |
| `PUT /continuity-maintenance` | `{ expectedRevision, intervalSeconds: integer|null }`; null restores inheritance; returns saved/effective state |

Both PUT fields are required; only an explicit `intervalSeconds: null` restores inheritance. Absent rows report null configured interval, inherited default and revision 0. Invalid or malformed intervals return 400 without mutation; stale revisions return 409. Startup operator bounds are authoritative. If a formerly valid saved interval is outside new bounds, `configuredIntervalAllowed=false` and the effective interval explicitly uses the default while retaining the configured value. The timestamp records an eligible evaluation claim, not Retrospection completion. Updates preserve it and atomically append `ExperienceChanged` owner audit metadata with operation `configureContinuityCadence`. No model tool, Thought registration, SignalR contract or consolidation permission changes.

## Agent Instance workspace HTTP

All routes use the existing trusted-local owner capability filter. Managed ownership is required; compatibility home returns unavailable (ValidationError). No host path or public download link is returned.

| Method | Route | Contract |
| --- | --- | --- |
| GET | `/api/v2/agent-instances/{instanceId}/workspace` | Optional prefix, afterPath, limit (1–256); items, usage, counts, nextPath and Core quota |
| GET | `/api/v2/agent-instances/{instanceId}/workspace/{itemId}/content` | Exact bytes; safe attachment filename, revision ETag, private no-store, nosniff |
| DELETE | `/api/v2/agent-instances/{instanceId}/workspace/{itemId}?expectedRevision=…` | Matching current revision required; 204 on delete; 409 stale/archived |
| POST | `/api/v2/sessions/{sessionId}/workspace/retain` | source, destination, optional expectedRevision/expectedSha256; durable item metadata |
| POST | `/api/v2/sessions/{sessionId}/workspace/checkout` | source, optional destination/expectedRevision/expectedSha256; source metadata and exact scratch hash/size |

Session execution-view GET list/content also accepts `/home` for eligible managed Sessions. Mutations reject ended/archived Sessions; archived instance inspection remains available. Item metadata is stable id, owner id, logical path, MIME type, byte size, lowercase SHA-256, revision, created/updated timestamps and source Session provenance. ItemId is not an ArtifactId, and home content is not authorized through Artifact endpoints. SignalR contracts are unchanged.

### Workspace filesystem contracts

Agent workspace item metadata adds `directory` (boolean; old stored records default false). Owner workspace pages add `treeSha256`, computed over the complete owner tree regardless of prefix/cursor. Directory content reads return validation failure; empty-directory owner DELETE retains the existing expectedRevision contract, and non-empty directories return Conflict. Session workspace node metadata continues to use `directory`/`writable`.

Definitions explicitly opt into structural filesystem authority by allowlisting at least one of `workspace.mkdir`, `workspace.copy`, `workspace.delete` or `workspace.batch`. The predicate uses capabilities, not version numbers. Without that opt-in, `workspace.move` retains its scratch-file-only description/schema, execution semantics and `{source,destination}` result. Directory moves and all `/home` moves are rejected; general-assistant v12/v13 do not inherit v14 authority. Existing allowlist and execution-time role checks still apply to every tool.

Native model tools expose `workspace.mkdir(path)`, `workspace.copy(source,destination)`, `workspace.move(source,destination)`, `workspace.delete(path,recursive=false)` and `workspace.batch(operations)`. All support optional `expectedTreeSha256`, required for `/home` and rejected for scratch. Batch entries have `op` in mkdir/copy/move/delete plus the corresponding fields; unknown/duplicate fields, wrong types and non-structural operations are rejected. The whole ordered request stays in one logical scope. The 16-operation/16 KiB bound applies before any mutation. No structural HTTP endpoint or new realtime event is introduced; the normal tool and approval protocol carries these actions.

Structural tool results use camelCase: `completed`, `operationCount`, `completedCount`, `failedIndex`, `results`, optional `errorCode`/`message`, `mutationsMayHaveOccurred` and `treeSha256` (null for scratch), plus normalized `operations` in both scopes. Each outcome includes `index`, `operation`, `kind`, `status`, `filesAffected`, `directoriesAffected`, `bytesAffected`; incomplete outcomes describe planned counts. Execution failure preserves earlier steps and reports failed/notExecuted statuses. Safe errors expose no host paths. Exact-action approvals bind the complete arguments including recursive intent and the home tree token.

Scratch delete/batch approval binds exact paths and recursive intent, but not the contents below a scratch path. Scratch has no tree token; authorized edits while approval is pending can change the contents affected. A bounded scratch approval fingerprint is explicitly deferred. Durable `/home` mutations retain whole-tree compare-and-swap. Every batch keeps its static Destructive classification, including batches without delete.
