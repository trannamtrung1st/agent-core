# Core Chat and Activity client authority cutover — 2026-10-21

Status: complete module; original proposal goal remains active. No full milestone or proposal acceptance is claimed.

## Implemented boundary

- Core Chat HTTP is a canonical tenant facade. Removed owner issuance, local-storage reads/writes, refresh/retry, header injection, global private endpoints and owner error types. An obsolete stored token has no authority.
- Realtime requires exact tenant document context and authenticated `AttachTenant`. Removed old Attach, live approval decision and Hub End branches. Text, Stop and Steer use fresh current witnesses inside serialized dispatch, retaining attachment/epoch/sequence fences, queue identity and no microphone replay. End uses canonical HTTP completion.
- Upload/abort use only serialized canonical tenant writes, current witnesses and cookie/CSRF identity. No owner XHR, global list/content or voice staging fallback remains. First-file intake continues to fence Send/Enter/form submission.
- `ChatApp` requires tenant context. Removed unreachable owner rich editor/choice, email binding, locale, Schedule and live approval components. Native tenant approvals, files, Sandbox, Model display, Voice and shared Ant Design presentation remain.
- Shared Activity/Run history requires an explicit tenant source and independent read/cancel actions. No generic SDK/default authority remains. Tenant links replace old Admin URL builders; `/c` parsing and unscoped URL construction are removed. Lost Continue and cancellation replies retain deliberate readback recovery.
- A focused race exposed a late stale Stop acknowledgement reconnecting a newer live response. The shared invocation owner now ignores that obsolete stale cancellation outcome. Real authorization refusals still retire inaccessible content; the server boundary is unchanged.

## Verification

| Check | Result and observed outcome |
| --- | --- |
| Focused frontend | 302 distinct cases pass across the final 21-file run and the corrected 27-case ChatApp run. The combined run passed 301/302; its sole remaining case lacked a current Run authority fixture, then all 27 ChatApp cases passed. No failing behavior is excluded. |
| Build | `pnpm --dir web run build` passes TypeScript and Vite. Existing large-chunk warning only; final independent `tsc --noEmit` passes. No C# source changed; browser hosts use the previously built canonical Synthetic API with `--no-build --no-restore`. |
| Native Chat | All 11 distinct tenant Chat scenarios pass: send/display receipt/reload/rename/archive/completion; organization switch/logout; actual WebSocket reconnect with retained draft and no duplicate admission; queue/Stop; access loss; durable approve/reject; explicit Steer and independent cancellation permission; entry/retired URLs at desktop/narrow widths. The final lifecycle repeat seeds an obsolete local token and observes no owner header or private unscoped request. |
| Native Voice | 2 cases pass at 1440/390px with fake devices: capture remains live through output, display/heard credit remain separate, reconnect requires a fresh Voice click. |
| Native files | 2 cases pass at 1440/390px: first-file intake/upload/send, original-to-Working and Artifact preview, exact-byte downloads, controlled read refusal and deliberate recovery. |
| Native Activity | 1 case passes at 390px: create/enable/bound a saved Automation, admit/complete its Run, inspect its Run and original Background Session in Activity, Continue in chat, verify the exact original Session ID, send a follow-up and observe a new completed assistant history entry. No page error or obsolete private request. |
| Static boundary | No production source occurrence remains of owner issuance/header/token helpers, old Activity default or retired Admin path-builder exports. No token compatibility host/facade or test skips were added. Standalone staged diff check and local documentation-link checks pass. |

Focused command: `NODE_OPTIONS=--no-experimental-webstorage pnpm --dir web run test --run` with `src/services/{realtime.tenant,realtime.race,realtime.queue,realtime.overflow,realtime.clientTranscriptBlocked,realtime.protocol,attachments.tenant,sessionHistory,catalog,tenantActivity}.test.ts`, `src/features/chat/{BackgroundWorkDrawer,TenantBackgroundRecovery,Composer,ChatApp,AgentPicker,SessionRail,Conversation}.test.tsx`, `src/features/admin/{InstanceActivitySection.test.tsx,adminErrors.test.ts}` and `src/app/{sessionRoute,appRoute}.test.ts`. The correction command selects `src/features/chat/ChatApp.test.tsx` only.

Native commands use `TENANT_CHAT_API_PORT`/`TENANT_CHAT_WEB_PORT` and `pnpm --dir web exec playwright test --config /private/tmp/core-client-cutover/{chat,voice,files,activity}.config.mts`. They import the committed tenant configurations, override isolated ports/output, and add `--no-build` to API startup. Chat runs are split to respect the real login limiter; the added Activity journey is selected with `--grep 'tenant Activity inspects'`. Repeatable committed scenarios are in `web/e2e/tenant-chat.spec.ts`, `tenant-voice.spec.ts`, `tenant-file-reads.spec.ts` and `tenant-automations.spec.ts`.

Logs and retained failures/traces: `/private/tmp/core-client-cutover/`, including `native-activity-qualified.log`, `final-focused.log`, `final-chatapp.log`, `final-build.log`, `final-tsc.log`, `native-final-token.log`, `native-chat.log`, `native-permission.log`, `native-voice.log`, `native-files.log` and the Activity result log. These are local disposable evidence, not durable application state.

Playwright MCP navigation was attempted and returned `Transport closed`; all completed browser evidence here uses Playwright CLI with isolated native Synthetic API/Vite hosts on 5096/5186 or 5097/5187. The user's 5080/5173 hosts were not reused or altered. No provider credentials or external provider traffic were needed.

Intermediate failures were retained and corrected: race fixtures had to await tenant witness dispatch before resolving acknowledgements; Activity fixtures had to supply exact source/current facts and scoped read storage; management/Stop fixtures had to declare independent authority. One grouped browser scenario encountered the production sign-in limiter after repeated fixture sign-ins; it passed on a fresh disposable host. The limiter was not weakened. New Activity fixture corrections used the current enable label and parsed the URL pathname separately from its return-navigation query; the final native journey passed. The late Stop product race was fixed as described above. No test is skipped to obtain passing results.

Native checks reuse the incumbent Ant Design Chat, composer, rail, drawer and Activity surfaces. Desktop 1440px and narrow 390px Voice/file/entry flows are covered; Activity continuation uses 390px. No CSS/theme/design sidecar changes or new visual-design review are claimed. `.impeccable/design.json` remains SHA256 `8bdc3659a25b706454ac8cc0394323c5e0e60ea1ade616756b180efbaecef49b`.

## Explicit remaining work

This cutover removes unreachable owner controls; it does not prove equivalent canonical rich Skill/reference authoring, mutable conversation locale/model, draft-resource/Drive previews, coordinated State/Runtime/Harness/Experience/maintenance parity, internal legacy SessionHost/approval/callback/worker authority or OwnerCapability store/schema/DTO removal. Remaining platform guardrails and the exhaustive original proposal route/store/effect/security/design audit remain required. Preserve this scope when the goal continues. Physical audio, live vendors, production/multi-instance deployment, optional CI and full milestone acceptance are unverified.

## Removed runtime exports

The core cutover removes these 37 named exports, in addition to deleting the unreachable component/source modules. Shared data types remain for canonical consumers and the later schema audit.

| Owner | Removed exports |
| --- | --- |
| `services/api.ts` | `OWNER_STORAGE_KEY`, `OwnerCapabilityError`, `clearOwnerCapability`, `ensureOwnerCapability`, `ownerFetch`, `ownerHeaders`, `listAgents`, `setSessionModel`, `setSpeechLocale`, `transitionLifecycle`, `deleteAllSessions`, `listSessionAutomations`, `cancelSessionAutomation`, `listBackgroundSessions`, `getBackgroundSession`, `continueInChat`, `listAgentRuns`, `getInstanceSession`, `listInstanceSessions`, `listInstanceAgentRuns`, `getInstanceAgentRun`, `cancelAgentRun`, `decideAgentRunApproval` |
| `services/attachments.ts` | `listAttachments`, `stageAttachments`, `fetchAttachmentBlob` |
| `services/realtime.ts` | `applySpeechLocale`, `respondToApproval` |
| `services/catalog.ts` | `deleteAllCatalogItems` |
| `app/appRoute.ts` | `adminHomePath`, `adminDefinitionPath`, `adminDraftPath`, `adminInstancePath` |
| `features/admin/adminNavigation.ts` | `instancePath`, `definitionPath`, `draftPath`, `globalPath` |

## Retired owner-contract test declarations

The following 63 declarations exercised removed owner-only controls/transports or old owner action semantics. Parameterized declarations can represent multiple former runtime cases. Useful race, queue, rendering, focus, polling, diagnostics and canonical recovery coverage was ported and retained. Rich input and locale entries below remain explicit parity gaps, not accepted canonical functionality.

### `web/src/features/chat/ApprovalModal.maintenance.test.tsx`

Retired owner-only transport or unreachable owner-only control. Canonical parity is tracked separately.

- lets the owner review complete consolidation text and every parent before deciding

### `web/src/features/chat/ApprovalModal.test.tsx`

Retired owner-only transport or unreachable owner-only control. Canonical parity is tracked separately.

- puts the complete task and schedule ahead of technical metadata
- names the exact %s decision
- keeps unknown proposed settings visible and preserves changed instructions
- shows resolved weekly limits and destination without inferring defaults
- shows every Event trigger and its filter, dispatch and disabled state
- presents the proposed Event source rather than the previous source on update
- shows clearing an Event filter as No filter and does not duplicate it
- exposes malformed details without inventing a readable proposal
- keeps rejection available when the exact action is missing
- resets technical disclosure for a different pending action
- shows an unconfirmed decision inside the dialog and keeps rejection retryable
- reviews %s as an exact Instance configuration change
- keeps the complete plain-text proposal in a keyboard-accessible review region
- supports an empty-details sensitive approval and removes a cleared request
- preserves complete email text and identifies empty recipient fields
- starts a replacement approval at its opening line after a previous proposal was scrolled
- shows trusted inert targets and action-specific confirmation for %s
- keeps old incomplete Drive requests rejectable and requires fresh trusted targets
- keeps exact IDs with filenames in the narrow two-column table
- preserves all twenty distinct targets at the supported metadata bounds
- requires fresh details for %s and keeps rejection available

### `web/src/features/chat/AutomationDrawer.test.tsx`

Retired owner-only transport or unreachable owner-only control. Canonical parity is tracked separately.

- loads a bounded next page, keeps existing rows on failure, and retries
- retains the opened page count when a conversation refresh arrives
- shows an empty list
- shows intent, schedule, timezone, and status
- shows a load error
- confirms cancel and keeps the updated row

### `web/src/features/chat/EmailAccountPicker.test.tsx`

Retired owner-only transport or unreachable owner-only control. Canonical parity is tracked separately.

- requires an explicit account choice and records owner consent with the current revision
- refreshes the same request revision before confirming a still-eligible account
- removes a revoked account from the current request and requires a new explicit choice
- cancels without granting any account and retains a failed selection for retry
- discards a late decision receipt after disconnecting the same session

### `web/src/features/chat/SpeechLocalePicker.test.ts`

Retired owner-only transport or unreachable owner-only control. Canonical parity is tracked separately.

- uses the pending override before a session exists
- uses the persisted override only when the source is sessionOverride

### `web/src/features/chat/composerChoices.test.ts`

Retired owner-only transport or unreachable owner-only control. Canonical parity is tracked separately.

- combines resource sources while retaining their individual cursors
- preserves successful results and retries only the failed source at its original cursor
- pauses a source whose cursor does not advance
- loads slash Skills without querying reference sources

### `web/src/services/attachments.test.ts`

Retired owner-only transport or unreachable owner-only control. Canonical parity is tracked separately.

- lists with the owner capability header

### `web/src/services/api.catalog.test.ts`

Retired owner-only transport or unreachable owner-only control. Canonical parity is tracked separately.

- sends the restored capability header
- reissues after 401 then fail-closes
- uses bulk durable delete on the catalog collection
- uses server-owned durable delete

### `web/src/services/schedules.api.test.ts`

Retired owner-only transport or unreachable owner-only control. Canonical parity is tracked separately.

- loads and cancels a session schedule

### `web/src/services/work.api.test.ts`

Retired owner-only transport or unreachable owner-only control. Canonical parity is tracked separately.

- uses Session identity, scoped run routes and exact revision approval

### `web/src/services/realtime.approval.test.ts`

Retired owner-only transport or unreachable owner-only control. Canonical parity is tracked separately.

- does not send a queued decision to a different Session
- keeps a failed transport decision reviewable and retries the exact rejection
- does not clear a replacement approval on a delayed ACK (accepted=%s)
- ignores a failed decision after reattachment
- retains a refused decision and exposes the ACK error for retry

### `web/src/services/catalog.test.ts`

Retired owner-only action contract.

- calls bulk delete and refreshes the catalog

### `web/src/features/chat/SessionRail.test.tsx`

Retired owner-only action contract.

- confirms delete all and returns to new chat when the active session is removed
- does not leave an archived active chat when delete all keeps archived rows

### `web/src/features/chat/BackgroundWorkDrawer.test.tsx`

Retired owner-only action contract.

- makes expired approvals readable and disables both decisions
- requires a fresh review if the run changes while approval confirmation is open

### `web/src/services/realtime.race.test.ts`

Retired owner-only action contract.

- refreshes owner capability and retries attach once after Unauthorized

### `web/src/services/realtime.queue.test.ts`

Owner rich-input admission was removed. Canonical Skill/reference authoring and wire parity remain explicitly unqualified; shared pure message-parts and draft-identity tests are retained.

- sends a Skill-only turn with its canonical structured identity
- preserves a newer reference revision when history confirms a lost ACK
- replays uncertain typed input unchanged while preserving a newer typed draft
- keeps structured chips in queued snapshots and restores them after a rejected steer

### `web/src/features/chat/SpeechLocalePicker.test.tsx`

Retired owner-only locale HTTP mutation control. Canonical mutable conversation-locale parity remains open.

- shows a visible field label in the header row layout
- keeps the stacked label on the new-chat intro
