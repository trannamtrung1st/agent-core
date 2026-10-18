---
version: 1
slug: "web-src-features-access-tenantapprovals-tsx"
primary_target: "web/src/features/access/TenantApprovals.tsx"
related_targets: ["web/src/features/chat/TenantConversationApprovals.tsx", "web/src/features/admin/useAdminDetailLayout.ts", "web/src/app/confirmAction.ts"]
---

# Exact Artifact publication review

Mode: Operate. Ordinary code-led extension of the incumbent dark Ant Design v6 system. Organization Approvals and Chat share `TenantApprovalDetail`; native controls, system typography and existing spacing own presentation. No new visual world, palette, token, CSS, route, UI kit, asset or decision comp.

## Direction contract — 2026-10-18–19

**THESIS:** Review one exact saved file, deliberately approve publication or authorize it once, then inspect the separate recorded result.

**OWN-WORLD:** Preserve the existing dark Ant Design v6 surfaces, system font, native large Drawer and direct Ant Design controls. The shared 736px Drawer is capped to the viewport. `useAdminDetailLayout` aligns wide labels and stacks them on narrow screens; existing compact/default/section spacing remains 8/12/16px.

**STORY:** Saved request and agent → Review or One-time authorization → organization/requester/expiry → Exact saved file → deliberate confirmation → separate Operation result. Approval grants no role or file read access. Approved and File published are distinct facts; denied execution reports File not published.

**FIRST VIEWPORT:** The shared Approval details Drawer leads with request identity, status and consent kind, followed by the current context and Exact saved file section. Filename, file type and UTF-8 bytes precede labeled Exact file content. Keep Refresh details and pending decision actions in the native wrapping footer. Chat retains its compact Conversation approvals status and Review details entry point.

**FORM:** Complete untrusted file content stays selectable in a read-only `Input.TextArea`, bounded to 6–12 visible rows with local scrolling; literal HTML, Markdown, Unicode and long lines remain plain text. The Drawer scrolls independently. Approve and Authorize once use the shared consequential confirmation, naming the exact saved publication, expiry and no-role/no-read-grant boundary. Dismissal restores the initiating decision button; successful submission returns to Refresh details, and closing the Chat inspector restores Review details. Refresh reads saved facts without replaying a decision.

**FINISH:** unreviewed and undocumented is unfinished; this build ends with the finish review, the verdict, DESIGN.md, and every shipping raster carrying its provenance

## Evidence and limits

The [UX contract](../../docs/19-multi-tenant-iam-ux.md#exact-artifact-publication-review) and [Frontend](../../docs/13-frontend-implementation-spec.md#exact-artifact-publication-inspector) own behavior. The [focused verification report](../../docs/reports/tenant-artifact-review-verification-2026-10-18.md) owns executed checks and backend authority evidence. This documenter compared the shared source, `useAdminDetailLayout`, `antdTheme.ts`, existing access Drawer CSS and incumbent [design system](../../.agents/context/DESIGN.md) and [product context](../../.agents/context/PRODUCT.md). This pass performed no application execution, test/build run or detector rerun.

The fresh finish reviewer returned **SHIP**, covered all five contract sections and found no material fixes after validating all 14 captures under `.impeccable/review/artifact-publication/`:

- `review-1440.png`, `review-1024.png`, `review-768.png`, `review-390.png`, `review-320.png`, `review-320-scrolled.png`.
- `confirm-review-320.png`, `result-review-320.png`, `chat-pending-1440.png`, `chat-result-320.png`.
- `grant-1440.png`, `grant-320.png`, `confirm-grant-320.png`, `result-grant-320.png`.

This documenter sampled `review-1440.png`, `review-320-scrolled.png`, `confirm-grant-320.png` and `chat-result-320.png`. They show incumbent dark surfaces, native typography, aligned wide descriptions, stacked mobile metadata, independently scrolling literal content, wrapping mobile actions and separate Approved/File published status. The mobile confirmation retains the exact-publication and independent-read boundaries. Static images do not establish focus, measured overflow or backend authorization.

The runtime report records actual Playwright MCP inbox Review, shared Chat inspection and exceptional authorization. Dismissed confirmation restores Approve or Authorize once; Escape restores Chat Review details. Actual confirmation produces successor publication and File published, with exact authenticated download and unchanged role/read authority checked by the interactive fixtures. The final two interactive fixtures passed. A 3,539-byte literal script/Markdown/Unicode fixture with 35 lines and a 600-character unbroken tail retained complete content and no measured horizontal document/body/TextArea overflow at 1440/1024/768/390/320px. The native wide Drawer measured 736px, mobile width was viewport-capped and the parent section gap was 16px. Final journeys reported zero console errors and relevant decision/detail/status HTTP 200 responses.

Focused checks reported 54 Application, 45 Infrastructure, 8 API and 25 frontend cases passing, plus the production build. The single design detector returned `[]`; this pass did not repeat it. The SHIP verdict covers captured consent/result presentation. Loading, denied, stale, expired and uncertain states have no dedicated screenshots. No CI, full suite, Real provider, live DB migration, deployment, milestone or full-proposal acceptance is claimed. Rich delivery/history, Browser/Workspace/Office adapters, owned-blob cleanup, autonomous sources and final authority/schema consolidation remain open.

## System comparison and existing drift

This extension matches the incumbent system and requires no system-file rewrite. Preserve `.agents/context/PRODUCT.md`, `.agents/context/DESIGN.md` and `.impeccable/design.json` exactly, including normative tokens, previews and historical provenance. The current AppShell and theme hashes still match the sidecar's historical inspection. The recorded hashes for DESIGN, PRODUCT, `app.css`, `AccessApp.tsx` and `accessRoute.ts` differ from the current tree. Existing broader pending-scope summaries remain historical context; this brief and the focused report provide evidence for this bounded publication-consent path.

No pre-existing drift is repaired, fingerprint rebased or presentation defect canonized. This surface-only handoff does not claim full design-sidecar synchronization or close the remaining IAM proposal.
