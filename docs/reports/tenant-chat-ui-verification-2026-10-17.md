# Tenant text Chat UI — 2026-10-17

Successor to `52985c27`. Canonical tenant text Chat now uses the existing React/Zustand conversation UI, HTTP identity client and SignalR/MessagePack transport at `/t/{tenantId}` and `/t/{tenantId}/c/{sessionId}`. This completes a bounded text UI checkpoint, not the full multitenant proposal or milestone acceptance.

## Delivered behavior

`TenantChatEntry` validates current membership before mounting Chat. The document binds one immutable tenant ID; scoped HTTP calls, candidate/action hints and transport cannot silently switch organization. Entry, exit and organization changes replace the document. Retirement aborts scoped requests, clears private projections and releases transport/media. Restoring a cached document reloads before reuse. No canonical Chat call issues an owner capability or falls back to owner services.

Public agent search/paging and the safe model catalog supply the existing new-Chat picker. Session creation uses fresh security and selected Instance revision witnesses. Explicit human model choices save User origin; existing tenant Sessions display the saved model read-only. The protected conversation view requires actual human/session, exact Session-read and agent-Chat authority independently of metadata administration. Metadata includes public pinned identity and exact management hints; Run reads include exact cancellation hints. Hints never authorize a later write. Archive filtering precedes count/page in both stores and defaults to the prior API behavior when omitted.

The shared rail/transcript/composer supports plain text, local queued messages, rendered text receipts, exact Run Stop, saved-history reload, rename/archive/restore/delete and quiescent End. End remains disabled during a live response; ended rows retain their existing delete-only menu. Tenant mode omits unavailable voice, rich-content/upload, automation, background-work, sandbox and legacy approval controls. Stop uses a fresh canonical Run witness and the attachment's trusted response mapping. Receipt refusals after an interrupted-row commit do not imply that the human lost Chat access. Actual recipient authorization failures clear protected content and retain the person's own pending text. Reconnect keeps unsent drafts, reconciles durable admissions and never replays media.

Serialized command preparation precedes sequence allocation and is fenced to its original connection/attachment. A rejected stale attachment/sequence/security witness reattaches without silently replaying that action. A late rejected send cannot restore old text into another conversation. Synthetic durable-stream probes now hold their visible prefix in native structured output too; their cancellation-aware behavior confers no authority and uses no provider service.

## Executed journeys

Playwright MCP used task-owned native Synthetic/InMemory API and Vite hosts on ports 61275/61276, with all data/cache roots under `/private/tmp/agent-core-tenant-chat-ui-20261017-rr_mbsqp`. Sign-in, scoped discovery, visible Send and actual Synthetic reply succeeded. Saved history reported assistant received=21 and heard=0. Full reload preserved one user and one assistant row. End produced read-only completed history. The visible organization chooser opened Beta with no Alpha transcript, draft or rail, and Beta sent its own message. Console inspection reported zero errors/warnings before shutdown. Both task-owned hosts were stopped afterward; installed data and unknown processes were untouched.

The repeatable CLI runner starts fresh non-reused Synthetic hosts and disposable roots, with browser/sandbox integrations disabled and no provider calls. Fixture API setup creates organizations, agents and grants; it is not evidence of agent creation through UI. Conversation actions use visible product controls.

| Journey | Expected and observed |
| --- | --- |
| Text, receipt and reload | Actual reply, persisted received=21/heard=0, no duplicate text after reload |
| Rail and End | Rename, archive current Chat, show archived, restore, reopen and confirm End; final Session is completed/read-only |
| Organization switch and logout | Empty next-scope draft/history; separate reply; logout and direct old deep link reveal no prior transcript |
| Real WebSocket reconnect | Passthrough server socket is closed; a new connection attaches, preserves draft and admits the next text once |
| Queue and Stop | A local second message drains once after the held first reply; Stop saves the delivered Run as cancelled and a subsequent message succeeds |
| Chat-only member and revocation | Conversation works without administrative read; End/row-management controls are absent; revoked Chat rejects the next action, clears protected transcript and offers recovery |

## Focused checks

- `dotnet test tests/AgentCore.Infrastructure.Tests --no-restore --filter 'FullyQualifiedName~TenantSessionIntegrationTests|FullyQualifiedName~TenantSessionLifecycleTests|FullyQualifiedName~TenantRunAdmissionTests' --logger 'console;verbosity=minimal' --nologo`: **125 passed**. Includes both-store public-view/management separation, exact Run cancellation hints/revocation, archive filtering and explicit model origin.
- `dotnet test tests/AgentCore.Api.Tests --no-restore --filter 'FullyQualifiedName~Tenant_chat_inventory_HTTP|FullyQualifiedName~Tenant_live_MessagePack_JavaScript|FullyQualifiedName~Tenant_text_receipts_are|FullyQualifiedName~Canonical_Session_HTTP' --logger 'console;verbosity=minimal' --nologo`: **19 passed**. Public view/model HTTP safety, actual cookie-scoped creation/text, real JavaScript wire/reconnect/receipt and legacy-exclusion checks.
- In `web`, focused Vitest across routes, Chat components, tenant service and realtime queue/race/protocol/approval/navigation: **215 passed**. After final transport guards, the affected service selection plus two new race cases: **101 passed** (overlapping regression selection; not 316 distinct tests). `realtime.tenant.test.ts` verifies attachment changes during witness preparation and late rejection after conversation switching.
- `cd web && pnpm run test:e2e:tenant-chat`: **5 passed** on final source in 47.3s; root `/private/var/folders/8f/r5cy7q69413g8xpd_z9v2yt40000gn/T/agent-core-tenant-chat-e2e-kYDVNM`. Final log `/private/tmp/tenant-chat-e2e-current.log`.
- `cd web && pnpm run build`: TypeScript and production build **passed**; existing large-chunk/dependency annotation warnings remain.
- `git diff --check`: passed. Changed-document local-link and Markdown-fence checks passed.

The local pnpm runtime resolved Node 26, whose experimental native storage shadowed jsdom storage in the race suite. `NODE_OPTIONS=--no-experimental-webstorage pnpm exec vitest run …` supplied jsdom storage; no product workaround or weakened assertions were added. Initial E2E assertions used wrong receipt field names and attempted the existing unavailable archive action on an ended row; corrected to the actual protocol and active-row workflow. Repeated fixture authentication exceeded the unchanged 10/minute production limiter; one private setup identity now supplies fixture contexts while browser sign-ins retain independent sessions. The observed post-Stop failure led to the receipt/recipient distinction and passed the targeted and final journeys. A temporary optional-code TypeScript error was fixed before the final passing build.

## Design and documentation

The existing Ant Design v6 dark shell, typography, icons, additive insets and 8/12/16px spacing owners remain. One scoped mobile width rule lets a short current organization name remain readable while bounding long names. Captures at 1440×900, 1200×647, 390×844 and 320×740 showed no page overflow and reachable controls/composer. Review evidence is local under `.impeccable/review/{desktop,user-1200,mobile,small-320}.png`.

A fresh Impeccable finish reviewer validated all four captures and requested only the mobile organization-label fix. The verdict pass scored that fix resolved with disposition **ship**, at the scope of that fix. Detector findings were `[]`; the CSS hook also reported no deterministic issues. A fresh documenter compared source and all captures with the incumbent design, preserved DESIGN.md/design.json and recorded `/private/tmp/tenant-chat-design-documentation.md`. Pre-existing Audit narrative drift in the sidecar remains reported and untouched. No shipping raster assets were added. Canonical behavior/protocol/storage/testing/status owners and the Chat direction contract are synchronized.

## Remaining scope

Tenant voice/rich resources and receipts, live mailbox input arbitration, original Chat approval links/notifications, remaining resource/effect families, autonomous admissions and final obsolete-authority/schema removal remain open. The earlier intermittent SQLite direct-preference provider-unknown follow-up remains unresolved; these selected checks do not establish its cause or repair. This is local Synthetic evidence, not real-provider, device/audio, performance, full SQLite browser, full proposal or milestone acceptance. CI was not requested or run.
