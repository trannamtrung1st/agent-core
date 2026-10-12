# Sensitive approval dialog refinement

Verified 2026-10-12 against `2adcb2e4` plus the current working tree. Scope: ordinary sensitive-action details in the existing ApprovalModal. This is local Synthetic verification, not hosted or Real email acceptance.

## Change and ownership

Ordinary approvals now use direct Ant Design small Descriptions rather than a label/value stack for every field. One shared 6rem secondary label column aligns recipients, subject and body within the existing 520px dialog. Values retain complete escaped text and line breaks. Supplied empty Cc/Bcc fields show a muted dash with the accessible name Not provided, removing blank sections without hiding recipient information.

ApprovalModal owns the ordinary/semantic presentation choice. Ant Design owns row density, typography, controls and dialog insets; app.css owns the compact label width and shrinking content. The existing focusable 40vh review region, dynamic viewport cap, stable footer, 40px narrow buttons and approval identity scroll reset are reused. Semantic harness/configuration/workspace/memory proposals keep their existing 720px stacked layout and Change inset. Titles, decision callbacks and exact approval payloads are unchanged.

Runtime captures: [desktop](assets/sensitive-approval-polish/email-desktop.png), [390px mobile](assets/sensitive-approval-polish/email-mobile.png). These are Chromium captures of presentation-only Synthetic email fixtures using an example.test address; no email was sent.

## Runtime checks

Used a disposable InMemory Synthetic host on 5086 and Vite on 5186, with storage under `/tmp/agent-core-approval-polish`. Health confirmed Synthetic. The existing Real app and its data were not used.

- Playwright MCP created an approval-demo Instance, selected it through Identity, submitted the scripted sensitive request, opened the real approval dialog, approved the Synthetic-only demo tool and observed Sensitive action completed after approval.
- Presentation-only email projections were injected through the existing development Zustand module. Short and long values were checked at 1440×900, 768×900, 390×844, 320×568 and 844×390. All label edges aligned, supplied empty fields remained visible, complete body text including literal HTML-like text survived, and footer controls stayed within the viewport without horizontal overflow.
- A long email body scrolled by keyboard inside Approval details at 390×568. Replacing the approval identity reset the region to its opening line. These fixtures were cleared without invoking an email decision callback.
- Existing approval-flow scenarios passed for Approve, Reject, keyboard approval and refresh/continuation. The existing semantic approval-layout scenario passed short viewports and replacement scroll reset, covering the unchanged consumer of the shared review shell.

## Checks and limits

| Check | Result |
| --- | --- |
| `pnpm run test --run src/features/chat/ApprovalModal.test.tsx` | 6 passed, including the new ordinary-email exact text/empty-field regression. |
| Existing `approval-flow.spec.ts` and `approval-layout.spec.ts` against the isolated host | 5 passed. |
| `pnpm run build` | Passed; existing SignalR annotation and bundle-size warnings remain. |
| Design hook | No deterministic findings on edited approval source/tests. |
| Scoped documentation links and `git diff --check` | Passed. |

MCP reported no console errors after the settled interactions. A startup models request returned 401 when a stale owner token from the previous disposable host was replaced; subsequent requests recovered. No failing request was observed during the exercised approval flow.

Physical touch/microphone behavior and Real email delivery were not tested. The existing design-sidecar freshness warning predates this refinement; no unrelated preview regeneration was performed. The design guide and approval surface brief were updated only for this compact details pattern.
