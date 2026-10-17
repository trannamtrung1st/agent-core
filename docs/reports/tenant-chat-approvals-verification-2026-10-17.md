# Original tenant Chat approvals verification — 2026-10-17

This successor to the [tenant text Chat checkpoint](tenant-chat-ui-verification-2026-10-17.md) adds original-conversation approval status, shared review entry/return and authorized inbox attention. Baseline `b6a59dac`; current branch main. These focused checks advance the proposal's Phase 5/6/7 integration, without claiming full proposal or milestone acceptance. The full goal remains active.

## Delivered behavior and boundaries

- The exact Session approval feed derives the actual human/session and independently rechecks Session-read and agent-Chat around I/O. Both stores filter tenant/Session before count/page. Minimal generic operation, request/Run, dates, consent status and closed effect outcome do not disclose prepared fields, target resources, saved names, action hashes or grants. Full inspection still requires existing approval-read and underlying action/resource access.
- The Runtime emits `DurableApprovalPendingOutput` only after the actual saved wait. Recipient-aware MessagePack delivery validates the exact pending request/Run and publishes `agent.approval.pending` with only requestId/runId. The original response envelope remains; the live generation retires without fabricated completion, interruption or received/heard credit.
- Browser event and HTTP recovery release busy state only for matching Session/response/Run. Independent messages remain usable while a request waits. An authoritative tenant-ready null active response stays null despite a retained Streaming history row. Attaching/reloading does not automatically Resume, decide or replay the action.
- Original Chat and the inbox use one exact-action inspector. Chat keeps its route, closes without deciding and restores the invoking link's focus. The inbox links to the original conversation, whose access is checked independently. Chat-only readers see minimal status and an explanation of separate action access, without Review details or protected preview fields.
- Consent and effect result are separate: Approved without a receipt says the result is unrecorded; completed mutation says Applied, completed connection check says Check completed, definite denial says Not applied and indeterminate says Outcome uncertain. These labels reuse existing saved results; no new execution authority or operation family is introduced.
- Scoped polling serializes reads, aborts on scope retirement, discards former-scope results, pauses hidden timers and clears on pagehide. Service failures retain safe last-known rows with stale/refresh guidance; authority loss clears protected Chat. The header badge reads only the current authorized pending inbox total, with no repeated popup or external notification promise.
- New Chat refreshes public agent candidates after an effect may have advanced the Instance revision. The shared header action group wraps within narrow viewports; existing Ant Design v6 controls, typography, tokens, shell and inspector remain.

No durable schema/migration, second approval engine, provider DTO boundary, conversation owner or UI kit is added. Existing durable decisions, exact continuations, operation receipts and current IAM still govern effects.

## Executed local verification

All hosts and stores were fresh disposable Synthetic fixtures; no installed user data or Real provider credentials were used.

| Check | Result / evidence |
| --- | --- |
| `dotnet build src/AgentCore.Api --no-restore --nologo` | Passed, 0 warnings/errors; `/private/tmp/tenant-chat-approvals-build-current.log` |
| `dotnet test tests/AgentCore.Infrastructure.Tests --no-restore --filter 'FullyQualifiedName~TenantOperationAuthorizationTests' --logger 'console;verbosity=minimal' --nologo` | **125 passed**, including two new both-store conversation-status cases; `/private/tmp/tenant-chat-approvals-infrastructure-final.log` |
| `dotnet test tests/AgentCore.Api.Tests --no-restore --filter 'FullyQualifiedName~Original_conversation_receives_saved_wait_and_recovers_decision_after_independent_work' --logger 'console;verbosity=minimal' --nologo` | **4 passed**, approve/reject × InMemory/SQLite actual loopback MessagePack hosts; `/private/tmp/tenant-chat-approvals-wire-current.log` |
| Focused Vitest command below, from `web` | **202 passed / 8 files**; `/private/tmp/tenant-chat-approvals-web-final.log` |
| `cd web && pnpm run test:e2e:tenant-chat` | **7 passed**; final log `/private/tmp/tenant-chat-approvals-e2e-final.log`, disposable root suffix `agent-core-tenant-chat-e2e-vDYko1` |
| `cd web && pnpm run build` | TypeScript/production build passed; existing bundle/dependency annotation warnings remain; `/private/tmp/tenant-chat-approvals-web-build.log` |
| Whitespace and changed-document local links/fences | Passed after documentation synchronization |

```sh
NODE_OPTIONS=--no-experimental-webstorage pnpm exec vitest run \
  src/features/access/useApprovalUpdates.test.tsx \
  src/state/sessionStore.test.ts \
  src/services/sessionRouteNavigation.test.ts \
  src/services/realtime.tenant.test.ts \
  src/services/realtime.race.test.ts \
  src/features/chat/Conversation.test.tsx \
  src/features/access/TenantApprovals.test.tsx \
  src/features/chat/ChatApp.test.tsx
```

Both-store integration checks minimal Chat-only status versus denied protected inspection, qualified owner inspection, other-Session exclusion and page/count, rejection, foreign tenant and lost Chat. Native cases create a real model-originated wait, assert the two-field notification and safe feed, reattach with no active response, send independent work, decide and observe the original saved response continuation/result.

Browser journeys exercise the existing text/lifecycle/organization/reconnect/queue/Stop paths plus an actual Chat-only wait and permission loss. Added approve/reject journeys send the Synthetic approval marker, reload while waiting, send independent text, open the shared exact inspector without route change, decide from a distinct authenticated fixture context for approval or through visible confirmation for rejection, close with focus return, recover saved status/result, reload without duplication and create New Chat after the effect. The approval journey injects one status-read 503 and observes retained rows followed by successful refresh.

Playwright MCP also exercised the running fresh native Synthetic app at loopback ports 61275/61276 with roots under `/private/tmp/agent-core-chat-approvals-20261017-vr4cc5k_`: sign in, create a tenant conversation, submit an actual approval request, send an independent message, inspect saved language en→vi, authorize once through confirmation, close back to Chat and observe distinct Approved/Applied state. A second actual wait supplied responsive review captures. Synthetic canned continuation text is fixture output, not natural-language model-quality evidence.

Verification found and fixed reload's inappropriate automatic Resume and New Chat's stale cached Instance revision after an effect; final journeys pass both. An initial preview assertion matched `vi` inside “review” and was corrected to check the exact protected value. A missing test import was fixed before the passing native run. A transient HMR `date is not defined` error occurred during a formatter rename; all call sites now use the exported formatter and the final builds/tests/captures pass. Node 26's experimental native storage required the existing Vitest `NODE_OPTIONS` workaround for jsdom; no product storage workaround or weakened assertions were added. The MCP console history therefore is not a claim of zero development-time errors.

## Design review and documentation

The interaction contract in [Accounts, organizations, access and approvals UX](../19-multi-tenant-iam-ux.md#original-chat-approval-status-and-review) preceded UI implementation. Two bounded visual rounds checked Chat and inspector at 1440×900, 1200×647, 1024×768, 768×1024, 390×844 and 320×740. An initially clipped narrow header led to a shared wrapping fix; confirmation contained every action and retained a reachable composer. The 320px header intentionally uses an additional row. Inspector footers wrap with content remaining scrollable.

The one detector run returned `[]` (`/private/tmp/tenant-chat-approvals-detector.json`). The fresh Impeccable finish reviewer first requested recapture of two unsettled mobile inspector images. After animation-settled replacement, it performed a full review of all **12** valid captures and returned **disposition: ship**, with no material fixes. It reviewed supplied source and runtime evidence but ran no browser/tests itself. Captures remain local under `.impeccable/review/chat-approvals/`; no raster product assets were added. The minor retained empty assistant metadata before status was observed and does not imply completion.

The fresh finish documenter independently checked source, theme/spacing owners, all capture dimensions and representative desktop/mobile pixels against `.agents/context/DESIGN.md`, PRODUCT.md and the sidecar. Its scoped record is `/private/tmp/tenant-chat-approvals-design-documentation.md`; no system change is needed. This ordinary extension preserves the incumbent system files. Previously reported Audit narrative drift in `.impeccable/design.json` and historical pending-scope wording in the design guide remain outside this change. Canonical architecture/ports/events/backend/frontend/protocol/persistence/testing/UX and status owners are synchronized.

## Remaining scope

Tenant voice/rich resources and receipts, live mailbox input arbitration, remaining resource/effect families, autonomous admissions and final obsolete-authority/schema removal remain open. The earlier intermittent SQLite direct-preference provider-unknown reliability follow-up remains unresolved; these checks do not establish its cause or repair. This is local Synthetic evidence, not real-provider, live Google, device/audio, performance, full SQLite browser or full milestone acceptance. CI was not requested or run.
