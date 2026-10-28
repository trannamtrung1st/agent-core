# Formal Workflows

Mode: Operate. Direction: the precise designer/monitor composition authorized in the 2026-10-28 implementation proposal. Code-led Ant Design v6 extension, with React Flow confined to diagram interaction. Design review: checked against current Access routing, TenantEvaluations collection, SessionSandboxFrame and the existing theme; no alternative identity or component system is needed. The real Synthetic browser flows and bounded visual confirmation are recorded in the delivery ledger; deployment/provider/device limits remain explicit.

## Primary flows

- Catalog: shared AdminShell/header/collection toolbar → Definitions or Executions tab → named row with status, version and next action. Explicit empty/denied/error feedback and readable authorized counts.
- Designer: wrapping name/revision/status/actions in normal document flow → palette/search at left, canvas center, selected-node properties at right. Validate exposes linked node/edge diagnostics. Publish confirms immutable version/participants. Published view is read-only; Draft returns to the current editable draft, and eligible Run pins the selected version.
- Keyboard/mobile: ordered step/branch outline and native property form are fully equivalent editing controls; add/connect/remove never requires dragging. Branch labels and required evidence appear as text in both representations.
- Monitor: status/current required step → proven applicable-step ratio → annotated read-only outline/diagram → chronological evidence timeline → current eligible action and source links. Human process sign-off and IAM operation Review are distinct.
- Chat: compact Workflows action → SessionSandboxFrame discovery/input/start → execution link/progress. Conversation and composer retain their existing layout.

## Reuse and spacing ownership

Reuse AdminShell, AdminCollectionToolbar, AccessLoading/AccessError, useIdentityQuery, useTenantCommand, useAccessDraftGuard, confirmAction and SessionSandboxFrame. Native AntD Form, Select, Drawer, Alert, Button, Typography, Modal, Table, Tabs and theme tokens own controls. Parent Flex/Grid owns 8/12/16 spacing; existing shell owns page insets. No negative margins, second wrappers or sample React Flow theme.

## Responsive acceptance

| Width | Expected composition |
| --- | --- |
| 320 / 390 | Outline primary; properties follow in a full-width stack; wrapping header/actions, no page overflow. Optional canvas preview. |
| 768 | Outline/canvas tabs; selected properties follow in document order. Collection scroll remains local. |
| 1024 | Palette collapsible, canvas/outline center, properties follow the structure region. |
| 1440 | Three-region designer; inspector evidence remains readable with bounded line width. |

At 200% zoom preserve native inputs/actions and document order; reduce to outline when necessary. Reduced-motion removes decorative edge animation. Status always has text/icon, never color alone. Dialog close restores opener focus; validation jump focuses the named node/outline entry. Async command feedback uses existing accessible feedback patterns.

## Node properties

| Kind | Form / readable facts |
| --- | --- |
| Start | Typed inputs, allowed origins, participant/coordinator bindings. |
| AgentTask | Explicit eligible target, bounded objective, context and budget; required evidence. |
| Verify | Exact evidence method and immutable Suite/Goal/source binding; inconclusive outcome route. |
| HumanDecision | Eligible reviewer, approval/rejection edges, decision deadline; process-only disclosure. |
| Branch | Typed prior Boolean fact, exhaustive true/false outcomes. |
| EndSuccess | Required global/selected-branch proof and optional independent Goal completion. |
| EndFailure | Explicit named failure route and retained source references. |

## State and interaction matrix

Empty/loading/denied/read-only use existing feedback. Dirty/stale/lost-save preserve local draft and offer compare/reload/readback. Retired version refuses new starts. Unbound Space, missing cooperation/model/revoked rights name the current remedy without exposing forbidden details. Waiting for agent, verification, human sign-off and operation approval remain distinct. Inconclusive verification stays waiting/blocked. Unknown effect says inspect before retry. Failed/cancelled/expired/completed show monotonic outcome and retained authorized evidence. No raw UUIDs in ordinary rows, no completion based on nodes visited or Task success alone.

Before delivery, exercise author → validate → publish → start → worker → verification → process decision → branch → success with a negative route and SQLite restart. Capture the five widths plus keyboard, zoom, reduced motion, draft recovery and source permissions. Final bounded polish permits one batched inspection/fix and one confirmation; evidence must name actual sources and limits.

## Local confirmation — 2026-10-29

Actual author/publish/start, Task/Evaluation/sign-off/branch completion, rejection, SQLite restart, Automation and Chat entry, stale/unknown recovery, read-only/revoked scope and nested focus were exercised. Five widths had no horizontal page overflow. Native keyboard and reduced motion were checked; 720×450 CSS pixels at device scale 2 was a 200% layout equivalent, not native browser zoom or screen-reader qualification. One visual batch removed overlapping sticky actions and separated branch outcome lines; one confirmation checked the final layouts. Later changes repaired functional receipt/source-access behavior without creating a second visual world. Evidence and external limits: [delivery ledger](../../docs/reports/formal-workflows-delivery-2026-10-28.md).
