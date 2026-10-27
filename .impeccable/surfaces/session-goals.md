# Session Goals

Primary target: `web/src/features/chat/SessionGoalsDrawer.tsx`. Related: ChatApp, ChatHeader, AgentRunDetails, BackgroundWorkDrawer, tenantSessionGoals service.
Mode: Operate. Design gate: reviewed against the explicit Initiative 8 proposal and incumbent Chat composition before Goal UI implementation, 2026-10-27. The supplied proposal settles audience, behavior and direction; implementation proceeds under the user's instruction to complete it.

## Direction contract

**THESIS:** An explicit outcome and its proof remain beside the conversation. Goals use one operational drawer; the conversation remains the reading surface.
**OWN-WORLD:** Existing Ant Design v6 dark tokens, system typography, 8/12/16 rhythm and Chat operational drawer. No global token changes, decoration or new UI kit.
**STORY:** Create or inspect a proposed plan, confirm criteria, choose focus, deliberately work, inspect factual evidence, then verify. Reported work remains distinct from verified outcomes.
**FIRST VIEWPORT:** Header Goals action; drawer heading and goal selector, outcome and status, verified-required count, then ordered checkpoint disclosures. A wrapping action row names the next deliberate action. Other goals stay inside the drawer.
**FORM:** Extend incumbent Chat operational drawer, code-led; brief-pinned structure (no concept lottery). Drawer body owns outer padding; Flex owns sibling gaps; AntD controls own internal padding.
**FINISH:** unreviewed and undocumented is unfinished; this ordinary extension ends with the finish review, verdict and surface evidence. Preserve the incumbent DESIGN.md and design sidecar because no system change is intended; every shipping raster retains its provenance.

## Flows and states

| State | Primary action | Proof / recovery |
| --- | --- | --- |
| No goal | Create goal | Explain optional explicit outcomes |
| Proposed | Start goal | Review objective, criteria, verification and continuation (off) |
| Active | Work on checkpoint | Exact goal/plan/checkpoint-bound Run; ordinary composer stays unbound |
| Reported / blocked | Inspect / verify | Progress is factual reporting, never success |
| Verification pending | Open source | Failed/Inconclusive remains unverified; no effect replay |
| All required verified | Final acceptance if required | Goal completion leaves Session active |
| Material amendment | Review old/new, accept/reject | Draft preserved after conflict, verified facts retained |
| Multiple goals | Focus selection | Future admission only; running Run keeps its witness |
| Wrong Space / approval / budget | Named recovery | Switch context, inspect approval, or continue manually with current rights |
| Paused / cancelled / ended | Resume where eligible / inspect | Session lifecycle never implies Goal success |
| Denied / expired / logout | Clear private content | Late callbacks invalidated, protected reads repeated |
| Network / stale revision | Reload authoritative state | Preserve draft; request-key readback before retry |

## Content and accessibility

Test short and 200-character titles, bounded long objectives, 1/5/40 checkpoints, optional dependencies, mixed human/Evaluation/receipt sources and multiple goals. Show criterion and verification method before any acceptance. Native Drawer focus containment, Escape and return to the Goals trigger; native Form labels, live error/status announcements and accessible action names. No status communicated by color alone. On 320/390/768 widths the drawer fills available width, body scrolls and actions wrap; verify 1024/1440, zoom and reduced motion too. Long source IDs wrap or use inspected links; no page-level horizontal overflow.

## Reuse and review

ChatHeader owns discovery and existing action spacing. Drawer/Flex/Form/Collapse/Alert/Empty/Typography/Button/Select use AntD directly. Reuse AgentRun inspector and Evaluation source detail under independent read authorization; SessionRail and composer/voice lifecycle remain stable. Before code, review confirms the hierarchy meets the proposal's flows, bounded content and honest state language. Baseline source and recent native-evaluation report establish incumbent visual evidence; live baseline and completed workflow checks are recorded in delivery evidence. Final polish is a separate bounded pass after integration, with representative widths/states inspected together.

## Shipped documentation pass — 2026-10-27

The implementation extends the incumbent Ant Design v6 dark Operate system. `SessionGoalsDrawer` reuses `SessionSandboxFrame` for the native viewport-capped 640px/full-width Drawer, scroll/focus/close ownership and section stack. `GoalPlanEditor` uses native vertical Form, Collapse, Select, Input and confirmation/feedback controls. Resolved `paddingXS`/`paddingSM`/`padding` own 8/12/16 sibling gaps; native controls retain their insets. The Goal heading uses native `Typography.Title` level 3, body and secondary copy retain native AntD roles, and status always includes text. Header discovery stays in ChatApp's existing action composition; source Run details reuse `AgentRunDetails`. No palette, typeface, global token, component-preview or visual-identity change is introduced. The canonical [frontend contract](../../docs/13-frontend-implementation-spec.md#session-goals-in-chat-and-activity) owns behavior.

Documentation comparison checked `SessionGoalsDrawer.tsx`, `GoalPlanEditor.tsx`, related ChatApp/AgentRunDetails integration, `SessionSandboxFrame.tsx`, `app.css`, `antdTheme.ts`, incumbent `.agents/context/PRODUCT.md` and `DESIGN.md`, and the direction contract. Existing desktop and plan-mobile captures were visually sampled; all eight supplied capture dimensions were checked: `desktop`, `laptop1024`, `tablet768`, `mobile`, `width320`, `plan-desktop`, `plan-mobile` and `layout-zoom200` under `.impeccable/review/`. The supplied finish-review handoff records disposition **ship** after the private-inspector callback race was fixed with scope-epoch retirement, its delayed-response browser regression passed and the reviewer found no visual regression across those eight captures. The supplied implementation handoff separately records the actual 40-checkpoint browser flow and focused native checks. This documentation pass did not rerun the application, browser, tests or detector, and does not broaden that evidence into other states or milestone acceptance.

The shipped surface matches the incumbent system and intended local hierarchy; no material design-documentation mismatch was found. Preserve DESIGN.md and `.impeccable/design.json`. Preexisting launcher drift (stale design sidecar and two orphan briefs) remains reported, outside this extension's write scope, and is neither repaired nor promoted into a design rule.
