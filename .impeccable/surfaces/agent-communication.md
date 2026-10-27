# Agent communication

Primary target: `web/src/features/admin/InstanceCommunicationSection.tsx`.
Related: InstanceActivitySection, Instance settings, task detail, tenant authorization scope.

Job: inspect an Agent's assigned work and private two-Agent correspondence, with honest delivery/wake facts. Humans oversee direct threads without a composer; task guidance is visibly attributed Human guidance. Exactly two Agent participants; no group navigation or shared transcript.

Hierarchy: existing Agent Activity → Inbox/Communication → authorized tasks/questions/threads → ordered message history and delivery receipts. Cooperation settings stay in the existing configuration owner: Accept delegated tasks, Accept direct messages and Allow direct-message wake, all off by default. Explain independent permissions and bounded operating cost. Direct-thread identity never comes from a human-selected speaker. Read/unread is not model execution or acknowledgement.

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
| Conflict / transport loss | Retain draft and saved request key; read current facts. |
| Capacity / policy refusal | Safe precise reason; no alternate Agent/Space substitution. |

## Verification disposition

Design contract recorded before UI code. Rendered review, detector, finish-review and behavior evidence are pending; no polished or accepted claim.
