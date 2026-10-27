# Task delegation

Primary target: `web/src/features/chat/TaskCenterDrawer.tsx`.
Related: ChatHeader, ChatApp, SessionGoalsDrawer, BackgroundWorkDrawer, AgentRunDetails, Activity, approvals.

Job: a conversation operator assigns a bounded objective to an explicitly selected eligible worker and concrete target Space, then reads honest progress and original outcomes without leaving Chat. The worker owns its own Session/Run; same-Agent assignments still use distinct Sessions.

Hierarchy: Chat header → Tasks → created/assigned authorized list → inline selected Task detail. Detail: status/assignee/source → objective/Space/deadline → actual result/attention → Messages/Activity → authorized Cancel/Reply/Acknowledge and independent Run/Artifact links. Delegate is an inline form with explicit target, saved Space, optional earlier-sibling dependencies and inbox-only default. Do not stack another detail Drawer. Back restores focus to the opener row. Acknowledge labels handling only.

Mode: Operate. Visual authority: Ant Design v6, `.agents/context/DESIGN.md`, `antdTheme.ts` and existing Chat/Activity drawers. No global theme change. Canonical behavior: `docs/proposals/initiative-9-task-delegation-communication.md`, adopted 2026-10-27 before UI implementation.

## Shared layout and access contract

Reuse `SessionSandboxFrame` (640 px capped operational drawer, full width on narrow screens, focus restoration/Escape), `DrawerListFooter`, `AgentRunDetails`, current approval and Goal deep links. Drawer Flex owns 16 px sections; control rows use 8 px token gaps, wrap and native AntD inner padding. System type, semantic status text, escaped wrapping evidence, no avatars or decorative status colors. Do not expose worker transcript/files through a Task permission.

Widths 320/390/768/1024/1440; 200% zoom, keyboard, reduced motion, long names/objectives and 2,000-character messages must remain usable with one scroll/focus owner. Loading uses skeletons; empty teaches the next eligible action; denied refresh clears private projection. On tenant/session/instance/logout/403/404/410 changes retire scope epoch before any delayed HTTP reply can apply. Lost command replies use request-key readback; never silently resubmit an effect. Server rights inform controls and are rechecked on submit.

| State | Visible treatment / action |
| --- | --- |
| Loading / refreshing | Scoped skeleton; no old private text on denied reload. |
| Empty | Explain default-off cooperation; offer authorized creation/settings link. |
| PendingDependencies / Queued | Exact factual status; no invented worker Run before readiness. |
| Running / Waiting | Actual Run-derived status; bounded messages; cancel is a request. |
| Blocked / Failed / Cancelled | Saved reason; deliberate retry creates a new Task. |
| NeedsAttention | Uncertain effect warning, inspect facts; no automatic retry. |
| Succeeded | Original saved result; explicitly not Goal verification. |
| Denied / unavailable / expired | Clear content, actionable safe feedback, no hidden names/counts. |
| Required Review | Separate saved approval receipt and independently readable inspector link; no placeholder Task/worker. Admission and completion remain separate. |
| Conflict / transport loss | Retain draft and saved request key; read current facts. |
| Capacity / policy refusal | Safe precise reason; no alternate Agent/Space substitution. |

## Verification disposition

Design contract recorded before UI code. Rendered review, detector, finish-review and behavior evidence are pending; no polished or accepted claim.
