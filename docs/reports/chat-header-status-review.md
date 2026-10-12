# Chat header status review and design synchronization

Reviewed 2026-10-12 against `00b432db` plus the current header working tree. Scope: the status relocation in ChatHeader/ChatApp, shared shell spacing, responsive controls, and the corresponding design documentation. This is local Synthetic verification, not milestone or hosted acceptance.

## Result

No unresolved header defects were found in the exercised scope. Profile and status stay beside the role under the name/time. Action coordinates remain identical through Ready, generation and Reconnecting at 1440/1200/1199/768/767/390/320px. Long identity, role and connection-error projections remain contained, with status ellipsis and no settled page overflow. Product code was not changed during this follow-up review.

The stale design guide and preview described content-dependent wrapping and status beside the controls. They now reflect the existing implementation. The static preview also uses the observed 24px small Speech locale Select and keeps its label on one line.

## Ownership and consistency

| Relationship | Verified rule and owner |
| --- | --- |
| Theme and portal roles | Ant Design darkAlgorithm; AppShell publishes resolved colors and 8/12/16px spacing roles. No new tokens or UI dependencies. |
| Header shell | app.css owns 8px block / 16px inline insets, with 12px inline below 768px. Chat and Admin retain the shared compact block inset; Admin comparison was source-level in this review. |
| Identity/actions layout | Existing ChatHeader composition; app.css owns shrinking identity plus intrinsic actions columns at 1200px+, and a deliberate actions row below 1200px. Major gap is 12px. |
| Identity metadata | Name/time lead. Role, profile and status use 12px secondary type and 8px sibling gaps. ChatApp supplies the same existing state projection. |
| Long text | Role uses natural width capped at 40%; remaining metadata shrinks. Ant Design Typography provides role/status ellipsis and full-text tooltips. |
| Actions | Existing SpeechLocalePicker and direct Ant Design icon controls. Icons remain 32px with 8px gaps, hover fill and the shared 2px inset keyboard outline. |

The [frontend specification](../13-frontend-implementation-spec.md#primary-screens), [design guide](../../.agents/context/DESIGN.md#chat-header), [schema-v2 preview](../../.impeccable/design.json) and [Chat surface brief](../../.impeccable/surfaces/web-src-features-chat-chatapp-tsx.md#header-status-synchronization-2026-10-12) describe the same layout. Unrelated design tokens, components and behavior remain untouched.

## Runtime evidence

Playwright MCP used a separate InMemory Synthetic backend at 5086 and Vite at 5186, with temporary storage under `/tmp/agent-core-header-review`. Health reported Synthetic and build `00b432db`. The existing Real app was not used for provider calls or test fixtures.

- Created a disposable Instance, selected it through Identity, sent Hello and observed the completed `Hello from synthetic.` response.
- At each of seven widths, sent the held-response fixture, measured action rectangles during generation, disconnected the actual attachment through the existing test seam, verified Reconnecting, reconnected, and used Stop. Control rectangles remained identical. Settled pages had no horizontal overflow; tooltip exit and responsive transitions were allowed to settle before overflow assertions.
- Injected and restored long name/role/error projections through the development Zustand module at 1440/768/390/320px. This checked rendered containment, not a real server failure. Actions remained steady and error text ellipsized inside the metadata row.
- Opened Automations with the keyboard and closed it with Escape. Tab focused End with the shared `rgb(60, 137, 232)` 2px solid outline and -2px inset. Opened End confirmation and cancelled it.
- Observed Paused after detached inactivity, resumed through the visible Resume button, and verified the composer/Ready state. Selected French speech locale, reloaded to verify persistence, then restored Agent default. New chat retained visible profile/status, including the initial profile loading placeholder.
- Rendered the updated standalone header snippet at all seven widths. Profile/status stayed in identity, action coordinates stayed fixed as status length changed, and the small Select measured 24px.

Runtime captures: [desktop](assets/chat-header-status-review/header-desktop.png), [mobile](assets/chat-header-status-review/header-mobile.png). Static documentation capture: [320px preview](assets/chat-header-status-review/preview-mobile.png). These are browser captures of runtime/source-derived presentation; preview labels are illustrative.

## Checks

| Check | Result |
| --- | --- |
| `pnpm run test --run src/features/chat/ChatApp.test.tsx src/features/chat/SpeechLocalePicker.test.tsx src/app/AppShell.test.tsx` | 31 passed. Includes pending Voice, interrupted/ended history, reconnect and narrow header consumers. |
| Existing text-conversation Playwright scenarios: session path refresh; text/pending Voice/disconnect; pause/Resume | 3 passed using the temporary config against the isolated host. |
| `pnpm run build` | Passed. Existing SignalR annotation and large-bundle warnings remain. |
| Impeccable layout detector on ChatHeader, ChatApp and app.css | No deterministic findings. |
| JSON, scoped Markdown links, documentation/source consistency and `git diff --check` | Passed. |

The existing React act/localStorage warnings did not fail unit tests. Opening Automations emitted the existing Ant Design List deprecation warning. One startup models request returned 401 while an owner token from the previous disposable host was replaced; subsequent identity/model loading and exercised requests recovered. No failed request was observed in the settled header interaction flows.

## Limits

Automatic approval review rejected permanently ending the disposable Synthetic conversation. That terminal transition was not rerun or bypassed; ended/read-only rendering remains covered by the passing ChatApp unit tests. No physical touchscreen, microphone/speaker or Real-provider behavior is established by this review. Header icon hits preserve the existing documented 32px baseline. No full-app accessibility certification, performance benchmark or hosted CI acceptance is claimed.
