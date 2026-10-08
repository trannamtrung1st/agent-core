# HTTP API and Realtime Protocol

Physical Infrastructure paths use one `Persistence:WorkspaceRoot`: `agent-<instanceN>/home/blobs/<opaqueBlobIdN>` for immutable home bytes and `agent-<instanceN>/sessions/session-<sessionN>/working/` for scratch. The model sees `/home` and `/working`, never these host paths. Artifacts, attachments and definition resources retain separate roots. The sibling `.provisioned` marker prevents repeat template seeding; no scratch artifacts/state or intermediate workspace directory is created.

## Capability authoring and loading contracts

`GET /api/v2/admin/tools` returns exact selectable `toolNames`, `maxToolAllowlistEntries:null`, and a `capabilities` catalog of name/category/summary/tags/discoverable/defaultProjectionClass/configured. The `capabilities` catalog contains only Definition-authorizable descriptors. Context-owned Credential, Continuity, Experience, identity maintenance, Harness, bootstrap and occurrence-completion tools cannot become Definition grants; attachment reads remain authorizable despite contextual projection. `toolNames` excludes context-owned grants. No secret configuration values are returned.

New candidate environment accepts `capabilities.mode` Selected or All, exact `resolvedCapabilities`, optional fingerprint, and `projection.alwaysCapabilities`. Server draft saves and publications resolve All against the trusted Definition-authorizable registry and write the fingerprint. Selected input is normalized to the same authorizable set; context-owned names are omitted from authorization and the fingerprint. Context-owned tools still require their execution-time instance/model/trigger/policy checks. Published runtime authority always uses the stored explicit names. `toolAllowlist` remains the compatibility input; new-mode grants cannot use both representations. Findings identify authorization/projection fields and duplicate, unregistered, unauthorized-projection or context-only errors. Missing optional configuration may be authorized in new mode. Candidate persistence rejects documents over 1 MiB with `candidate/document_too_large`.

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

All Session workspace GET list/content and PUT content accept public `/home` and `/working`. Relative HTTP paths resolve from `/home` independently of runtime cwd. PUT uses a raw byte body and Content-Type, with optional expectedRevision/expectedSha256 to guard durable replacement. Ended/archived mutation is rejected. Removed transfer routes return 404. Artifacts remain immutable Session-scoped deliverables independent of home ItemIds.

The normal model tool/approval protocol carries `workspace.cwd` and direct home write/patch. Approval details resolve public paths against trusted cwd while action hashes bind the exact original call. Individual cross-root copy accepts source/destination and optional durable destination CAS; whole-tree tokens apply to same-scope home restructuring. Cross-root move and mixed-root batch fail closed. No public cwd-setting or structural HTTP endpoint is added.


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
| GET /api/v2/admin/instances | Owner capability; trusted local caller | 200 instance inventory (id, definition, version, lifecycle, persona name, timestamps) | 401 capability; 403 non-local |
| GET /api/v2/admin/instances/{instanceId}/effective-config | Owner capability; trusted local caller | 200 allowlisted effective configuration: resolved catalog model, offered tools, harness refs, knowledge backing path (`resourcePath` when set, otherwise `knowledge/{identity}`), workspace template id, policies, durable-work eligibility, and the stored unattended catalog key and reasoning effort (no secrets). The projection is read-only | 401 capability; 403 non-local; 404 unknown instance or missing exact definition version |
| GET /api/v2/admin/tools | Owner capability; trusted local caller | 200 registered Definition-grant tool names for draft allowlist editing; context-owned Continuity, Experience, Harness and execution capabilities are excluded | 401 capability; 403 non-local |
| GET /api/v2/admin/events | Owner capability; trusted local caller; optional query `targetType`, `targetId`, `limit` | 200 append-only admin events (safe summary metadata only) | 401 capability; 403 non-local |
| POST /api/v2/admin/agent-instances | Owner capability; trusted local caller; `{ definitionId, version, persona? }` exact durable publication. Omit or null `persona` stores the definition identity (`personaSource` Default). A present persona is validated and stored as Custom on the inserted instance, with that source and fingerprint on the single `ManagedInstanceCreated` summary | 201 Agent Instance | 400 validation; 401 capability; 403 non-local; 404 |
| PATCH /api/v2/admin/agent-instances/{instanceId}/persona | Owner capability; trusted local caller; JSON `{ expectedRevision, expectedPersonaRevision, persona }` typed identity (Form/JSON share schema) | 200 instance summary | 400 validation; 401 capability; 403 non-local; 404; 409 stale revision |
| PATCH /api/v2/admin/agent-instances/{instanceId}/lifecycle | Owner capability; trusted local caller; `{ expectedRevision, lifecycle }` (`Active` / `Archived`) | 200 instance summary | 400 validation; 401 capability; 403 non-local; 404; 409 stale revision |
| DELETE /api/v2/admin/agent-instances/{instanceId} | Owner capability; trusted local caller; `{ expectedRevision }`. Managed archived instances only. Commits removal of the instance row and home metadata with `InstanceDeleted`, then purges home bytes. A post-commit cleanup failure can return 500 while the instance remains deleted; startup/five-minute recovery retries inaccessible leftover bytes from the committed receipt. Does not delete Sessions, learned memory, Automations, Occurrences, Activations, AgentRuns or approval/effect evidence | 204 | 400 instance is not archived; 401 capability; 403 non-local; 404; 409 stale revision or the instance is still referenced |
| PATCH /api/v2/admin/agent-instances/{instanceId}/active-version | Owner capability; trusted local caller; `{ expectedRevision, version }` exact durable publication version | 200 instance summary | 400 validation; 401 capability; 403 non-local; 404; 409 stale revision |
| GET /api/v2/admin/agent-instances/{instanceId}/learned-memory | Owner capability; trusted local caller; query `scope` (`Session` \| `IdentityUser` \| `User`); `sessionId` required for `Session` | 200 bounded active items with safe provenance; `IdentityUser`/`User` return 200 empty list when effective retrieval is disabled | 400 invalid scope or missing/invalid `sessionId` for `Session`; 401 capability; 403 non-local, Session scope when pinned session memory disabled, or session not owned by trusted profile; 404 instance or session |
| DELETE /api/v2/admin/agent-instances/{instanceId}/learned-memory/{memoryId} | Owner capability; trusted local caller; query `scope`, `sessionId` when `scope=Session`, `confirm=true` | 204 | 400 validation (`scope`, missing `sessionId` for `Session`, `confirm` not true); 401 capability; 403 non-local, effective policy denial on mutation, or session profile mismatch; 404 instance, session, or memory item |
| POST /api/v2/admin/agent-instances/{instanceId}/learned-memory/reset | Owner capability; trusted local caller; `{ scope, sessionId?, confirm: true }` (`sessionId` required when `scope=Session`) | 200 `{ scope, itemsRemoved }` | 400 validation (`scope`, `confirm`, `sessionId`); 401 capability; 403 non-local, effective policy denial, or session profile mismatch; 404 instance or session |
| GET /api/v2/admin/credentials | Owner capability; trusted local caller | Safe inventory with binding counts; no protected payload/value/version | 401; 403 |
| GET /api/v2/admin/credentials/{credentialId} | Owner capability; trusted local caller | Safe resource | 401; 403; 404 |
| POST /api/v2/admin/credentials | Owner; `{displayName,kind,metadata,allowedOrigins,protectedValue}` | 201 safe Active resource; predefined Kind | 400; 401; 403 |
| PATCH /api/v2/admin/credentials/{credentialId} | Owner; `{expectedRevision,displayName,status,metadata,allowedOrigins}` | Safe update; Kind immutable | 400; 401; 403; 404; 409 |
| PUT /api/v2/admin/credentials/{credentialId}/value | Owner; `{expectedRevision,protectedValue}` | Safe resource; ID/grants retained | 400; 401; 403; 404; 409; 503 protection unavailable |
| DELETE /api/v2/admin/credentials/{credentialId}?expectedRevision=N | Owner; positive revision | 204; bound delete conflicts | 400; 401; 403; 404; 409 |
| GET /api/v2/admin/agent-instances/{instanceId}/credential-bindings | Owner | Safe grants; archived inspectable | 401; 403; 404 |
| POST /api/v2/admin/agent-instances/{instanceId}/credential-bindings | Owner; `{credentialId,reference,expectedInstanceRevision}` | 201 normalized alias; unique owner/alias and owner/credential | 400; 401; 403 archived; 404; 409 |
| DELETE /api/v2/admin/agent-instances/{instanceId}/credential-bindings/{bindingId}?expectedRevision=N&expectedInstanceRevision=N | Owner | 204; grant scoped to instance | 400; 401; 403 archived; 404; 409 |
| POST /api/v2/admin/agent-instances/{instanceId}/browser-profile/reset | Owner; `{expectedInstanceRevision,confirm:true}` | 204; deletes only owner's profile, preserving grants | 400; 401; 403 archived; 404; 409; 503 |
| GET /api/v2/admin/event-sources | Owner capability; trusted local caller | 200 `{ items }` of source id, display name, kind, public source key, status, and revision. No token and no token hash | 401 capability; 403 non-local |
| POST /api/v2/admin/event-sources | Owner capability; trusted local caller; `{ displayName }`. Creates an active Webhook source. The raw token is in this response only | 200 `{ sourceId, sourceKey, token, status }` | 400 validation; 401 capability; 403 non-local |
| POST /api/v2/admin/event-sources/{sourceId}/rotate | Owner capability; trusted local caller. Replaces the hash immediately and returns the new raw token once. The public source key stays | 200 `{ sourceId, sourceKey, token, status }` | 401 capability; 403 non-local; 404 source |
| POST /api/v2/admin/event-sources/{sourceId}/revoke | Owner capability; trusted local caller. Clears the hash and sets `Revoked`. Historical events stay | 200 source summary | 401 capability; 403 non-local; 404 source |
| POST /api/v1/hooks/{sourceKey} | Bearer token for that Event Source. JSON body at most 8 KiB: `eventId`, `type` (`order.placed`), optional `occurredAt`, and `data.orderReference`, with optional paired rootAgentRunId/triggerDepth (maximum four). Does not run the agent, browser, or router. The body does not list matching Automations | 202 `{ eventId }` after the External Event commits; 200 `{ eventId }` when that source event was already admitted, including when no agent is configured | 400 malformed, unknown, unsupported, or oversized payload; 401 missing, wrong, or revoked bearer |
| POST /api/v2/admin/agent-instances/{instanceId}/unattended-model | Owner capability; trusted local caller; `{ expectedRevision, catalogKey, reasoningEffort }`. A blank catalog key clears the instance unattended default | 200 instance summary including `unattendedModelCatalogKey` and `unattendedReasoningEffort` | 400 unknown model or effort; 401 capability; 403 non-local; 404 instance; 409 stale revision |
| GET /api/v2/agent-instances | Owner capability | 200 active managed instances for User new-chat (`instanceId`, `definitionId`, `activeVersion`, persona `name`/`role`, exact-version `voiceAvailable`/`language`) | 401 capability; 403 non-local |
| GET /api/v2/admin/definition-drafts/{draftId}/resources | Owner capability; trusted local caller | 200 draft resource bindings (metadata only) | 401 capability; 403 non-local; 404 |
| POST /api/v2/admin/definition-drafts/{draftId}/resources/content | Owner capability; trusted local caller; raw body with `Content-Type` and bounded size | 201 stored content hash and byte length | 400 oversize/empty; 401 capability; 403 non-local |
| PUT /api/v2/admin/definition-drafts/{draftId}/resources | Owner capability; trusted local caller; JSON `{ expectedRevision, resourceId?, logicalPath, kind, mediaType, contentSha256, byteLength }` to bind or replace one draft resource | 200 draft resource row | 400 path/kind/secret; 401 capability; 403 non-local; 404; 409 stale revision |
| PUT /api/v2/admin/definition-drafts/{draftId}/resources/batch | Owner capability; trusted local caller; `{ expectedRevision, items[] }` of already-stored `{ logicalPath, kind, mediaType, contentSha256, byteLength }`. One transaction. Success returns the new revision and the complete draft resource list ordered by logical path, including rows that were already bound. An invalid item, collision, limit failure, or stale revision leaves rows and the revision unchanged | 200 `{ revision, items }` | 400 validation; 401 capability; 403 non-local; 404; 409 stale revision |
| DELETE /api/v2/admin/definition-drafts/{draftId}/resources/{resourceId}?expectedRevision={n} | Owner capability; trusted local caller; `expectedRevision` query parameter | 200 removed draft resource row | 401 capability; 403 non-local; 404; 409 stale revision |
| GET /api/v2/admin/definition-drafts/{draftId}/resources/{resourceId}/content | Owner capability; trusted local caller | 200 resource bytes | 401 capability; 403 non-local; 404 |
| GET /api/v2/admin/definitions/{definitionId}/publications/{version}/resources | Owner capability; trusted local caller | 200 immutable publication bindings | 401 capability; 403 non-local; 404 |
| GET /api/v2/sessions/{sessionId}/automations | Owner capability; optional `limit` (minimum 1, clamped to 100) and `before` (Automation ID) | 200 newest-first safe Automation page for the session's Agent Instance and trusted profile; omitted pagination preserves the full-list contract | 400 invalid query; 401 capability; 404 session or foreign/unknown cursor |
| POST /api/v2/sessions/{sessionId}/automations/{triggerId}/cancel | `{ "expectedRevision": n }` | 200 updated safe Automation | 400 invalid revision; 401 capability; 404 session, guessed id, or other instance; 409 stale revision |
| GET /api/v2/agent-instances/{instanceId}/background-sessions | Optional opaque `cursor`, `limit` (1–100, default 50), `includeArchived` | 200 `{items,nextCursor,hasMore}` for the instance and trusted profile, with Session origin/surfaces/latest run | 400 invalid cursor/limit; 401 capability; 404 owner/instance |
| GET /api/v2/sessions/{sessionId}/background | No body | 200 the owned Background Session, immutable origin, surfaces, latest run and canContinueInChat | 401 capability; 404 other owner, missing or non-background Session |
| POST /api/v2/sessions/{sessionId}/continue-in-chat | No body | 200 `{sessionId}` after idempotent ChatList visibility commit; the same Session and run survive | 401 capability; 404 owner/Session; 409 archived/terminal/ineligible |
| GET /api/v2/agent-instances/{instanceId}/agent-runs | Optional `before` (AgentRun UUID), `limit` (1–100, default 50) | 200 bounded newest-first `{items,nextCursor,hasMore}` | 400 invalid or foreign cursor; 401 capability; 404 instance |
| GET /api/v2/agent-instances/{instanceId}/agent-runs/{agentRunId} | No body | 200 owned safe run detail with immutable source links | 401 capability; 404 other owner or missing run |
| GET /api/v2/sessions/{sessionId}/agent-runs | Optional `before`, `limit` as above | 200 only runs belonging to this owned Session | 400 invalid/foreign cursor; 401 capability; 404 Session |
| GET /api/v2/sessions/{sessionId}/agent-runs/{agentRunId} | No body | 200 owned run belonging to this Session | 401 capability; 404 foreign Session/run |
| POST /api/v2/sessions/{sessionId}/agent-runs/{agentRunId}/cancel | `{expectedRevision}` | 200 updated same run after mailbox admission; confirmed effects remain | 400 invalid request; 401 capability; 404 owner/run; 409 stale or ineligible |
| POST /api/v2/sessions/{sessionId}/agent-runs/{agentRunId}/approvals/{approvalId}/approve or /reject | `{expectedRevision,expectedApprovalRevision,actionHash}` | 200 same run after exact approval decision admission | 401 capability; 404 owner/run; 409 stale revision, expiry or altered action |
| GET /api/v2/sessions/{sessionId}/artifacts/page | Optional `before` (artifact UUID), `limit` (1–100, default 20) | 200 bounded metadata-only `{items,nextCursor,hasMore}`; normal artifact content route downloads bytes | 400 foreign cursor/limit; 401 capability; 404 Session |

Operational pages return `{items,nextCursor,hasMore}` with a bounded metadata page. AgentRun and Artifact `before` UUIDs are exclusive and owner scoped; Background Sessions use an opaque owner-scoped cursor. Ordering is descending immutable creation time, then descending ID. Clients use the returned cursor and hasMore without fetching an extra UI row. Read acknowledgements are browser-local UI state and do not mutate Sessions or Runs.

AgentRun JSON uses status tokens `queued`, `running`, `needsApproval`, `retrying`, `completed`, `failed`, and `cancelled`. List and detail include `agentRunId`, `sessionId`, `activationId`, `activationKind`, revision, attempt count/bound, cancellation availability/request, bounded progress, retry and creation/update timestamps, pinned model catalog key, response ID and optional Automation/Experience/Occurrence source IDs. Pending `approval` contains its exact ID, revision, action hash, tool, bounded preview and expiry. An `outcome` contains kind, bounded summary, optional outcome entry ID and attention flag. Safe failure code/summary, diagnostic ID and known-effect summary may be present. These projections omit private evidence, checkpoint payload, prepared action JSON, raw tool output and provider bodies. Background Session summaries add immutable origin, surfaces, latest Run, Continue eligibility and a bounded artifact count with a truncation flag. No legacy work-result route or new SignalR message carries these records. Owned paused/ended Sessions remain inspectable; cancel and approval decisions are separately checked for current Run eligibility and admitted through the mailbox.

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

Roles `user|assistant|applicationMessage`; an `applicationMessage` stores visible text only, has no `speechText`, and uses heard length 0. It is not a prompt turn. Chat groups it before the assistant entry with the same `responseId`. Clients apply assistant text, speech, blocks, terminal metadata, and response-scoped failure to that assistant entry only. A failed entry's `failure` object may include allowlisted `failureReason`, `providerResponseChannel`, `protocolRepair` (`attempted`), and `protocolRepairOutcome` (`succeeded`, `failed`, or `cancelled`). Timeout reasons include `setupTimeout`, `streamIdle`, and `totalTimeout`; the existing diagnostic details/copy surface displays those safe tokens. Older rows omit them. An application message changes through `session.entry.upsert` or an authoritative snapshot. statuses `completed|interrupted|failed|streaming`; deliveryMode `text|voice` is the mode in which that entry was produced (prompt builder: text assistant entries use received prefix; voice assistant entries use heard prefix). A pending local user entry is reconciled by sourceEventId (the original user.text eventId or voice utteranceId); assistant entries are reconciled by responseId. Streaming and interrupted assistant text is limited to ReceivedTextEndExclusive so superseded generated tails stay hidden. A durably Completed or Failed assistant entry exposes its full stored terminal display text even when the browser disconnected before its final receipt; ReceivedTextEndExclusive remains the conservative delivery coordinate and does not become hearing evidence. Additive optional `speechText` is the persisted public spoken projection when it was stored on the envelope; omit or null when absent. Additive optional `memoryReceipts` is controller admission metadata for that assistant entry (`outcome`, `operation`, `subject`, optional `scopes` listing memory layers successfully established or confirmed by this admission (e.g. `session`, `identityUser`, `user`), `presentation` `indicator|explicit`, `label`). It is omitted when every receipt is silent. It is not part of `text` or `speechText`. `indicator` is a quiet confirmation such as Remembered or Forgotten. `explicit` is the visible failure of a user-explicit save or delete. Agent-inferred outcomes are not projected. The first-party UI shows it only when it meaningfully differs from display text: **Speech text** for text-delivered secondary speech, **Spoken** reserved for Voice delivery. Same-mode or omitted speech does not duplicate display. Public heardTextEndExclusive is clamped to the projected text length; the internal heard offset remains available for model context even if a last text receipt was lost. History is ordered by stable entry sequence; a streaming entry is updated in place, so reconnect fetches a fresh snapshot, not merely entries after its old sequence. Omit `after` and `before` for the newest page; `before` requests the immediately older page; `after` remains a forward cursor. `before`+`after` is rejected. Newest and backward pages take `limit+1` internally, return items ascending, and set `hasOlder` plus `nextBefore`. `after` is an entry sequence, not an event cursor. No persisted PCM or speculative partial user transcript appears here.

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

Envelope record describes common metadata; each command has a statically typed payload DTO following the table, not an arbitrary dictionary. Implement SignalR hub `Attach(AttachCommand)`, `SendText(UserTextCommand)`, `CancelResponse(CancelResponseCommand)`, `SetMode(SetModeCommand)`, `SpeechStarted(SpeechStartedCommand)`, `SpeechEnded(SpeechEndedCommand)`, `SpeechEvidence(SpeechEvidenceCommand)`, `SendAudio(InputAudioDto)`, `PlaybackStarted(PlaybackCommand)`, `PlaybackProgress(PlaybackCommand)`, `PlaybackCompleted(PlaybackCommand)`, `PlaybackStopped(PlaybackCommand)`, `ResponseReceived(ResponseReceiptCommand)`, `SetMuted(MuteCommand)`, `EndSession(EndCommand)`. All return `Task<CommandAck>` except SendAudio returns Task after local ingress admission. Browser uses ordered `connection.send("SendAudio", dto)` calls, not a round-trip `invoke` for every frame; audio rejection is reported through SessionEvent error. CommandAck is `{eventId,accepted,error}` with error nullable; a non-null error uses the category/code/message/fatal/retryAfterMs payload from the error table. Accepted user text means its user entry and pending-input intent are durable. Core batches accepted entries into one Activation/AgentRun when the turn becomes effective; acknowledgement does not mean the response is complete.

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

Client serializes control calls in its connection service; API accepts increasing sequence values, tolerates gaps caused by a rejected command, rejects backwards values except exact known eventId retry. Store dedupe outcomes for the attachment (maximum 1,024 controls; reject older unknown retries as StaleCommand). Repeated eventId with different payload is ProtocolError, including a changed `user.text.behavior`. A new attachment resets command sequencing. Text eventId is also stored as ConversationEntry sourceEventId; a retry after reconnect is deduplicated by that ID with fresh attachment/sequence metadata. Keep at most one unacknowledged text submission in the browser; audio and speech boundaries are never replayed. Non-text commands are not retried across attachment changes. `agent.response.cancel` is accepted when the expected `responseId` is still active or already terminal for this session (idempotent); it is rejected as StaleCommand when a newer response is live, and as ValidationError when the id is missing, malformed, or unknown. Accepted user.text is persisted before success ACK. A valid `agent.approval.respond` ACK means the decision was accepted and the approval waiter was signalled. It does not wait for durable execution resume or downstream tool/model completion; those continue inside the owning Runtime/mailbox. Stale/unknown decisions retain their existing rejection semantics. `behavior=queue` while a response is live does not supersede it. The shipped first-party web client uses a local pending-send FIFO instead of `behavior=queue` during live responses; row Steer uses `behavior=interrupt`, `agent.response.completed` dequeues the FIFO head with the default non-interrupt path, and explicit Stop (`userStop`) does not dequeue queued drafts.

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
| session.ready | mode, pendingMode: text\|voice\|null, status, lifecycleStatus, agent descriptor, streamId: UUID\|null, audioFormat, capabilities, lastEntrySequence, history: entry array (latest 50, public projection; on reattach the active Streaming row includes the already-published display prefix so its length equals the next `agent.text.delta.textStart`, while `receivedTextEndExclusive` remains the durable receipt boundary), hasOlderHistory: bool, historyBeforeSequence: long\|null (pagination cursor for `GET /messages?before=` when more than 50 entries exist; null when `hasOlderHistory` is false), activeResponseId and agentRunId: UUID\|null when accepted conversational execution survives reattach, outputState, pendingApproval: same bounded fields as `agent.approval.requested` plus `responseId` when a tool approval wait is still open (null otherwise) |
| transcript.partial | utteranceId, revision: integer, text |
| transcript.final | utteranceId, text, entryId: UUID, entrySequence: integer |
| transcript.discarded | utteranceId |
| agent.response.started | entryId: UUID, entrySequence: integer, trigger: userTurn\|longSilence\|environmentUpdate\|unfinishedInteraction\|scheduledOccurrence\|applicationEvent, agentRunId: UUID\|null (present for accepted user conversational execution) |
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

Phases A–H are observed on the runtime (including Docker `sandbox.run`). Historical Phase I WorkItems were not-applicable until the future trigger in [Technology Decisions](10-technology-decisions.md#post-mvp-planned-until-verified). Full R1–R6 text is in that same section. Phase D initiative/deactivation and Phase F workspace execution view are observed above.

**Lease versus Attachment.** Wire `attachmentId` is the hub **connection lease**. User-uploaded files are HTTP `Attachment` records with `AttachmentId`. Never send attachment binaries or base64 on SignalR.

**Trusted-local owner capability.** Catalog, lifecycle, attach, upload, bind, artifact, and content routes require `X-AgentCore-Owner-Capability` (hub attach includes the same token). `SessionId` is not a credential. Native issue/use is loopback `POST /api/v1/local/owner-capability`; Compose published-port NAT additionally trusts this container's default gateway when `Hosting:TrustPublishedPortGateway` is true. Browser restore from `localStorage`; hashed grant survives API restart. Fail closed without leaking other sessions.

**Additive lifecycle (keep v1 DELETE).** Versioned routes:

| Method | Meaning |
| --- | --- |
| GET /api/v2/sessions | Catalog, `UpdatedAt` descending, stable cursor pagination; includes labeled Ended rows. Each item includes additive `agentName` and `agentRole` from the session's pinned persona, or the definition identity when the session has no pinned persona. `agentId` remains the definition id |
| POST /api/v2/sessions | Requires non-empty `agentInstanceId` for an active existing instance; pins its active Definition version/persona. Optional mode, speechLocale and catalog-level model (`key`, `reasoningEffort`). Missing/empty owner returns 400; absent owner 404; archived owner 400. Definition-only creation and v1 POST creation are removed; unrelated v1 read/history/end routes remain |
| POST /api/v2/sessions/{id}/rename \| archive \| unarchive | Persist catalog mutation in the runtime revision stream |
| POST /api/v2/sessions/{id}/reopen | When `paused`, clears pause, bumps `runtimeEpoch`, and refreshes `LastUserActivityAt`; reconciles a detached in-memory runtime when present. No-op on `created` (epoch unchanged). Rejected with `SessionInUse` when status is `attached` or a hub lease is active. Not the same as `session.attach` |
| POST /api/v2/sessions/{id}/deactivate | Runtime deactivation: cancel live output, persist Paused, increment RuntimeEpoch; not archive and not v1 end; idempotent |
| POST /api/v2/sessions/{id}/lifecycle | First-party/user `LifecycleTransition`. `target` Active/Paused/Completed/Expired/Cancelled/Ended; optional `reason`. Caller authority is always `User`; a request `source` cannot self-promote to Host/System/Agent/Legacy. Persist-before-ACK; protocol-v1 `status` stays `paused`, `created`/`attached` after resume, or `ended`. Idempotent same-outcome repeats |
| POST /api/v2/host/sessions | Trusted-host create. Same owner capability as catalog today (local single-owner); **not** a deployment security boundary when the browser is untrusted relative to an examination host—future platforms need separate host credentials/scopes. Separate contract from public `POST /api/v2/sessions`; requires `agentInstanceId`. Body may include `purpose` (`kind` ongoing/goal, `description`, optional ISO-8601 `deadlineAt` with explicit `Z` or `±HH:mm`), `completionPolicy` (`agentCompletion` disabled/advisory/allowed, `userCompletionAllowed`, `userCancellationAllowed`), `maxDurationSeconds` (positive integer, at most 30 days), and catalog-level `model` (`key`, `reasoningEffort`) stored as selection source Host. Provide `deadlineAt` or `maxDurationSeconds`, not both; max duration resolves once to absolute `deadlineAt`. Response is a host view that includes purpose/policy/lifecycle provenance. Do not send a raw .NET TimeSpan |
| GET /api/v2/host/sessions/{id} | Trusted-host projection of purpose, completion policy, and lifecycle provenance. Ordinary `SessionView`, catalog, `session.ready`, and history stay private of those fields |
| POST /api/v2/host/sessions/{id}/lifecycle | Host-authoritative `LifecycleTransition`. Server sets source `Host` regardless of request `source`. Host completion bypasses user/agent policy bits |
| GET /api/v2/models | Safe catalog: system `defaultKey` plus descriptors (key, display name, capabilities, supported/default reasoning effort). No secrets, provider alias, BaseUrl, or credentials |
| POST /api/v2/sessions/{id}/speech-locale | Set or clear session speech override (`locale` BCP-47-like or null). Does not rewrite agent conversation `language`. Effective locale is public on GET and `session.ready` |
| GET /api/v2/profile | Owner capability; returns local profile revision and allowlisted typed values with provenance (`source`) and per-field `updatedAt` |
| PATCH /api/v2/profile | Owner capability; optimistic `expectedRevision`; allowlisted keys only; null/whitespace removes a field; server stamps `UserSet` (browser cannot set source) |
| POST /api/v2/sessions/{id}/model | Catalog-level session model mutation (`key` Default or a catalog key, optional `reasoningEffort`). Server resolves and persists concrete `SessionModelSelection`. Rejects `SessionBusy` while generating. Terminal sessions are read-only |
| GET /api/v2/sessions/{id}/knowledge/{identity} | Approved knowledge retrieval with citation metadata; 403 if the pinned role does not allow `knowledge.retrieve` or the identity |
| GET /api/v2/sessions/{id}/workspace | Execution-view listing (`prefix` query; omitted or `/` resolves `/home`); owner capability |
| GET /api/v2/sessions/{id}/workspace/content?path= | Read authorized `/home` or `/working` content and read-only `/agent` or `/attachments`; relative HTTP paths resolve from `/home`; host paths are never returned |
| PUT /api/v2/sessions/{id}/workspace/content?path= | Writes use `/working` or direct `/home` with durable replacement CAS; 250 MiB scope quotas; read-only overlays/secrets/escaping paths/symlinks remain forbidden |
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
| PUT `/policy` | expectedRevision, mode, scopes, sources, eligibleTools, frozen | Grant/revoke/freeze. Normal UI sends empty source/eligible arrays; server derives authorized configured eligibility. Advanced arrays are bounded to 24 sources and 64 eligible tool names. |
| POST `/prepare` | expectedRevision, purpose | Advanced owner candidate fork; no model loop. |
| POST `/verify` | preparationId | Advanced current candidate Core checks. |
| POST `/cancel` | expectedRevision | Discard unfinished candidate/pending approvals; Published returns 409. |
| POST `/approvals/{approvalId}` | expectedRevision, actionHash, approve | Legacy advanced exact candidate decision. |
| POST `/publish-adopt` | expectedRevision, draftRevision | Advanced owner lifecycle publication/adoption. |

`POST /continue` was removed and returns 404. Harness tools are `harness.inspect`, `harness.knowledge.upsert/remove`, `harness.instructions.update` and `harness.tool.select/configure`. Mutations carry expectedVersion/policyRevision and a semantic payload. Exact runtime approval binds tool and canonical arguments; authority is rechecked after waiting. Changed writes return saved/changed, activeVersion, future-conversation scope and verification/limitations. Identical state writes return changed=false. Blocking verification returns saved=false, verification_failed and bounded findings. Safe errors carry diagnostic references when available. Skills have no Harness alias or scope.

Typed owner review uses string modes/statuses and evidence actor, draft revision, check, status, expected/observed/limitation. Verified, PartiallyVerified, CannotVerify, RequiresExternalEvidence and Failed remain distinct. Published evidence retains tested publishedDraftRevision. Invalid requests use 400; stale revisions/grants use 409; unexpected failures use server diagnostics. No private credential or host-path projection enters model context.

## Unified Automation and Experience HTTP contracts

Owner capability and active managed ownership protect `/api/v2/admin/agent-instances/{instanceId}`. Existing safe ProblemDetails return 400 validation, 403 policy, 404 missing/foreign, and 409 stale revision/busy admission. No new hub method/event or public chat endpoint is added.

| Method / suffix | Contract |
| --- | --- |
| GET `/automations` | items plus current Schedule policy |
| POST `/automations` | AutomationRequest, expectedRevision 0 |
| PUT `/automations/{automationId}` | AutomationRequest with current expectedRevision |
| DELETE `/automations/{automationId}` | JSON `{expectedRevision}`; content-free deleted result |
| POST `/automations/{automationId}/run` | `{expectedRevision}`; occurrenceId, eventual normal Run |
| GET `/experience` | settings and bounded observations with source/cursor/date/model/work/visibility/revision |
| PUT `/experience/configuration` | `{expectedRevision, enabled}` |
| POST `/experience/checkpoints` | `{sessionId}`; Core selects stable cursor and deduplicates manual review |
| PUT `/experience/{experienceId}` | `{expectedRevision, visibility}` |
| POST `/experience/reset` | empty body; tombstones observations |
| GET `/agent-runs`, `/{agentRunId}` | bounded owner-scoped run inspection; outcome is part of the safe run projection |

AutomationRequest has expectedRevision, enabled, name (1–120), instructions (1–2000), trigger, modelKey?, reasoningEffort?. Trigger is exactly `{kind:"schedule", schedule:{...}}` or `{kind:"event", eventSourceId, eventType:"order.placed"}`. Mixed variants are rejected. Schedule kinds oneShot/daily/weekly/fixedInterval use existing IANA/DST, horizon, recurrence and finite bounds. Fields are timeZone, atUtc, interval, localTime, weekdays, anchorAtUtc, endAtUtc, startDate, endDate, maxOccurrences. Model selection must exist and support tools; no fallback model substitution occurs.

AutomationResponse exposes automationId, revision, name, instructions, enabled/status, structured trigger, immutable authorizationOrigin/sourceSessionId/sourceEventId/createdAt, nextRunAt, model selection/effective model, lastAgentRunId/executionStatus/outcome. Admin lifecycle uses Domain casing. AgentRunResponse exposes owned Session/Activation/run IDs, readable status, revision, attempt limits, timestamps, progress, retry, safe approval preview, bounded outcome, failure diagnostics, confirmed effect summary, pinned model and source links. It excludes raw checkpoints/private evidence. Session-first background responses carry immutable origin and surfaces separately from lifecycle.

Owned Chat reads GET `/api/v2/sessions/{sessionId}/automations` (limit/before) for all Automation variants; POST `/{automationId}/cancel` uses expectedRevision. The compact Session projection includes automationId, name, instructions, status, triggerKind, timeZone?, when, nextOccurrenceAt?, revision and suspensionReason?. Cancellation is terminal deletion from active authoring, while history remains inspectable.

Model-facing management tools are automation.create/list/inspect/update/disable/delete/run. Their catalog symbols are AutomationCreate/List/Inspect/Update/Disable/Delete/Run; creation/inspection tool results call the generic trigger concurrency version triggerRevision. Create accepts bounded name/instructions and either existing flat Schedule timing fields or eventSourceId/eventType. Update/disable/delete/run use exact current resource revision and current owned user-turn authorization. Historical text, Automation instructions and event payload cannot authorize another future behavior. A scheduled Automation executes its configured Instructions: Chat preserves action-oriented work for explicit recurring-action requests and uses reminder-style instructions for reminder requests. Creation does not pre-authorize future actions; each Run receives only currently authorized and eligible capabilities, with normal policy and exact-action approvals still required.

Every initial background Run calls work.complete with outcome NoAction|ActionCompleted|AttentionRequested, summary <=2000 and attentionRequired true exactly for AttentionRequested. A contradictory NoAction with an accepted mutation is rejected. NoAction stays quiet. ActionCompleted persists the child outcome without unsolicited parent delivery; only eligible immediate work explicitly requesting report-back may produce a completion report. Manual review is a generic run without an Automation source.

`experience.source` accepts sourceKind Session|AgentRun and UUID sourceId. It returns owned stable bounded evidence/cursor. `experience.record` accepts bounded structured goal/attempts/decisions/outcomes/corrections/unresolved/difficulties/lessons and optional explicit sourceKind/sourceId/throughCursor; explicit source selection requires throughCursor and rejects stale/foreign/ineligible sources. Both use trusted execution context and existing settings/policy. Recurring review is ordinary Automation configuration; `/thoughts`, `/schedules`, event-subscription behavior and `/continuity-maintenance` routes are removed.

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

Owner-protected learned-memory provenance adds optional `maintenanceAgentInstanceId`, `maintenanceSessionId` and `maintenanceAgentRunId` strings. Older items return null; these identify initiation independently of semantic owner and promotion lineage. Existing live approval details and durable `approvalPreview` now include Core-resolved Memory scope/kind/source subjects and User-wide effects. No model-supplied scope or approval-authority field is added. The durable approval preview is bounded to 12,000 characters.

### Recurring continuity review

Use the Automation contract for recurring instructions that inspect completed work and record useful observations. Enable Experience and consolidation independently under Continuity. No cadence configuration endpoint remains.

## Agent Instance workspace HTTP

All routes use the trusted-local owner capability filter. Every Agent Instance owns home; archived home remains inspectable and read-only. No host path or public download link is returned.

| Method | Route | Contract |
| --- | --- | --- |
| GET | `/api/v2/agent-instances/{instanceId}/workspace` | Optional prefix, afterPath, limit (1–256); items, usage, counts, nextPath and Core quota |
| GET | `/api/v2/agent-instances/{instanceId}/workspace/{itemId}/content` | Exact bytes; safe attachment filename, revision ETag, private no-store, nosniff |
| DELETE | `/api/v2/agent-instances/{instanceId}/workspace/{itemId}?expectedRevision=…` | Matching current revision required; 204 on delete; 409 stale/archived |

Session execution-view GET list/content also accepts `/home` for eligible managed Sessions. Mutations reject ended/archived Sessions; archived instance inspection remains available. Item metadata is stable id, owner id, logical path, MIME type, byte size, lowercase SHA-256, revision, created/updated timestamps and source Session provenance. ItemId is not an ArtifactId, and home content is not authorized through Artifact endpoints. SignalR contracts are unchanged.

### Workspace filesystem contracts

Agent workspace item metadata adds `directory` (boolean; old stored records default false). Owner workspace pages add `treeSha256`, computed over the complete owner tree regardless of prefix/cursor. Directory content reads return validation failure; empty-directory owner DELETE retains the existing expectedRevision contract, and non-empty directories return Conflict. Session workspace node metadata continues to use `directory`/`writable`.

Canonical `workspace.move` supports file and complete subtree rename within either home or scratch. Directory paths may contain `#`. Destination must not exist; descendant cycles and moves of cwd/ancestors are rejected. Home moves require the current complete tree token. Each tool still requires pinned authorization and execution-time eligibility.

Native model tools expose `workspace.mkdir(path)`, `workspace.copy(source,destination)`, `workspace.move(source,destination)`, `workspace.delete(path,recursive=false)` and `workspace.batch(operations)`. All support optional `expectedTreeSha256`, required for `/home` and rejected for scratch. Batch entries have `op` in mkdir/copy/move/delete plus the corresponding fields; unknown/duplicate fields, wrong types and non-structural operations are rejected. The whole ordered request stays in one logical scope. The 16-operation/16 KiB bound applies before any mutation. No structural HTTP endpoint or new realtime event is introduced; the normal tool and approval protocol carries these actions.

Structural tool results use camelCase: `completed`, `operationCount`, `completedCount`, `failedIndex`, `results`, optional `errorCode`/`message`, `mutationsMayHaveOccurred` and `treeSha256` (null for scratch), plus normalized `operations` in both scopes. Each outcome includes `index`, `operation`, `kind`, `status`, `filesAffected`, `directoriesAffected`, `bytesAffected`; incomplete outcomes describe planned counts. Execution failure preserves earlier steps and reports failed/notExecuted statuses. Safe errors expose no host paths. Exact-action approvals bind the complete arguments including recursive intent and the home tree token.

Scratch delete/batch approval binds exact paths and recursive intent, but not the contents below a scratch path. Scratch has no tree token; authorized edits while approval is pending can change the contents affected. A bounded scratch approval fingerprint is explicitly deferred. Durable `/home` mutations retain whole-tree compare-and-swap. Every batch keeps its static Destructive classification, including batches without delete.

### Harness inspection identity fields

`harness.inspect` returns `activeDefinitionVersion`, `authoringEligibleTools`, `activeDefinitionAuthorizedCapabilities`, `currentSessionPinnedDefinitionVersion` and `changesApplyToFutureSessions:true`. `policyRevision` remains the authoring CAS token; writes use `expectedVersion` equal to inspected `activeDefinitionVersion`. Instruction text is bounded and explicitly marks `instructionsTruncated`. Authoring eligibility never grants the current Session authority; live pins remain unchanged.

## Credential tool boundary

`credentials.list` accepts optional `{cursor,limit}` (default limit 20, range 1–100) and returns `{items:[{reference,displayName,kind,metadata}],hasMore,nextCursor}` for the current active owner's active grants. Items use ordinal alias order; cursor is the last returned alias, and nextCursor is null on the final page. Each call evaluates current grants, so concurrent mutations can change later pages. Complete records fit the execution's remaining UTF-8 output budget; the page shrinks when necessary. If even one record cannot fit, return a budget-fitted `output_limit` error rather than truncated metadata or an empty non-progressing page. It has no selector for another owner and no value endpoint. `browser.act` adds an exclusive operation branch `{operation:"fill_credential",ref:"el_…",credentialRef:"store-admin"}` with no extra properties, raw value, selector or owner field. Secure fill is direct UserTurn only. Other kinds/sinks and detached injection are denied. Tool results/errors, checkpoints, history and captures must exclude protected material. Old `/connection` endpoints are removed, not compatibility aliases.


## Agent Instance Skill HTTP and tool contract

All HTTP routes below are under `/api/v2/admin/agent-instances/{instanceId}/skills` and require the existing trusted-local owner capability. Keys are URL-encoded canonical `definition:<id>` or `instance:<skill-id>` strings.

| Method/path | Operation |
| --- | --- |
| GET collection | Definition/local metadata; no full procedure bodies |
| GET `/{key}` | Complete read-only/editable inspection |
| POST collection | Create local Skill, optional readable ID |
| PATCH `/{key}` | Update local content with `expectedRevision` |
| PUT `/{key}/enabled` | `{expectedRevision, enabled}` for either origin |
| DELETE `/{key}?expectedRevision=N` | Delete local Skill only |
| POST `/{key}/customize` | `{expectedRevision}`; atomic independent copy plus source disable; returns `{instanceSkill,definitionSkill:{key,enabled:false,revision}}` |

Create/update content is `{name, description, procedure, projection, enabled, requiredCapabilities}` with projection exactly `Always`/`OnDemand`; update additionally requires the current revision. Create additionally accepts optional `id`: empty or omitted generates from the name with an owner-local collision suffix. New IDs match `^[a-z][a-z0-9._-]{0,63}$`. Duplicate IDs within the same instance return 409; the same ID in a different instance is allowed. Update cannot change an existing ID (400). Existing UUID IDs remain valid and unchanged. Responses use camelCase and string origin/projection. Views include key, origin, description, revision, current Definition version or local source provenance, and `missingCapabilities`. Invalid/wrong-origin/resource-bound customization is 400; missing owner/key is 404; stale revision is 409; missing owner capability is 401. Archived owners are read-only. Admin mutations append safe operation/key history atomically; model writes retain Agent provenance without false Admin events.

Ordinary `skills.list/inspect/create/update/set_enabled/delete/customize` use the same service and CAS rules, strict additionalProperties=false tool schemas and trusted owner context. Writes are non-replayable; requirements never grant authority. `skills.load` retains `{ids:[canonicalKey,...]}` and loads only pinned OnDemand entries. Existing tool-result error envelopes and output limits apply; no new SignalR envelope is introduced.

### Definition draft Automation policy number errors

Draft create/update returns HTTP 400 for missing required, fractional, unreadable or out-of-range integer Automation policy limits. The optional minimum fixed interval retains its 60-second default when omitted. The existing validation problem includes `field` (for example `triggerPolicy.minRecurrenceDays`), `validationCode: "invalid_integer"` (or `"out_of_range"` for a whole number outside its bounds), and an actionable `detail`: “Minimum recurrence days must be a whole number from 1 to 365.” The same mapping applies to max active registrations, one-shot horizon days and minimum fixed interval seconds using their canonical bounds. Rejected draft updates do not change persisted candidate content or revision.

Background Session DTOs include `artifactCount` and `artifactCountHasMore`: count at most 50 owned Artifact metadata rows and render a plus suffix when more exist. `canContinueInChat` requires an active Agent Instance and an openable Session. Continue rejects an archived/missing instance with 409 before changing visibility. An Automation source can be opened at `/admin/instances/{instanceId}/automation/automations?automation={automationId}`; this is owner-scoped navigation, not context transfer.

## Quiet background outcome projection

`session.entry.removed` carries `{entryId}` under the existing response envelope. The browser removes only the matching assistant entry for that response; unrelated history remains. This accompanies a committed NoAction outcome and does not change Session identity or lifecycle. Run-backed response diagnostics and Ready projection use `agentRunId`; there is no old execution-ID alias.

The current source implements the new routes above; full backend/browser acceptance and all five hosted jobs pass on `8cec78c5d47a43e0236a5c38f2e312f4e36ce283` ([workflow](https://github.com/trannamtrung1st/agent-core/actions/runs/37756244306)). Old WorkItem routes are removed from registration and are not compatibility aliases. Historical closure reports retain the contracts they originally verified.
