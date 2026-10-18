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

## Exact Artifact metadata verification extension — 2026-10-19

This ordinary code-led extension converts the existing tenant `artifacts.verify` metadata read under Allowed policy or exact durable Review. It reuses `TenantApprovalDetail` in Organization Approvals and Chat. The publication contract and historical evidence above retain their original scope; metadata verification adds no file-body inspector, preview, download, visual world, asset or system change.

**THESIS:** Inspect one exact saved metadata request, deliberately decide it, then inspect the separate consent receipt.

**OWN-WORLD:** Preserve the incumbent dark Ant Design v6 palette, system typography, native viewport-capped large Drawer (736px), 8/12/16px spacing and shared responsive descriptions. Wide labels remain top-aligned at 12rem; below 768px they stack above wrapping values. Native confirmation retains its own responsive composition and 32px button baseline; scoped Drawer buttons retain 40px mobile height.

**STORY:** Verify file metadata and Agent → Pending/Review → organization/requester/expiry → Exact saved read → explicit metadata disclosure and read boundary → consequential confirmation → separate Operation result. Saved approval status does not establish that metadata inspection has been authorized. The completed receipt says Metadata inspection authorized; denied execution says Metadata not authorized.

**FIRST VIEWPORT:** Saved request identity, status and current context lead. File name, File type and Stored bytes start the one-sided Exact saved read. Data disclosed and Read boundary follow in the same shared descriptions. IDs, exact action hash and file checksum stay in collapsed Technical references. Refresh details and pending decisions remain in the native wrapping footer; narrow screens scroll the body independently.

**FORM:** No file body, workspace path, attachment provenance or download is offered. Confirmation names one exact metadata inspection and excludes creation, contents, download access and later-read authority. Escape or Keep reviewing returns to Approve without a decision POST; deliberate confirmation sends one POST and successful submission restores Refresh details. Chat Review details opens the same inspector. Current exact-file read loss closes the Chat inspector and removes its entry point; canonical detail denial clears private facts and uses shared approval-unavailable recovery guidance.

**FINISH:** The bounded extension ends with a fresh finish-review verdict and this source-grounded surface brief. The incumbent product/design context, sidecar and publication evidence remain preserved.

### Metadata verification evidence and limits

The [metadata UX contract](../../docs/19-multi-tenant-iam-ux.md#exact-artifact-metadata-verification) and [inspector specification](../../docs/13-frontend-implementation-spec.md#exact-artifact-metadata-verification-inspector) own behavior. The [focused metadata verification report](../../docs/reports/tenant-artifact-metadata-verification-2026-10-19.md) owns executed checks. This documenter compared `TenantApprovals.tsx`, `TenantConversationApprovals.tsx`, `identity.ts`, `useAdminDetailLayout`, `confirmAction`, `antdTheme.ts` and existing access Drawer CSS with the incumbent product/design context. This documentation pass ran no source changes, application, tests, build, browser journey, context launcher or detector. No current detector report was supplied; this extension makes no new scan claim.

The supplied fresh finish reviewer returned **SHIP**, covered persistence, fidelity, ceiling, material fixes and keep, and validated all 16 final captures in [artifact-verification](../review/artifact-verification/):

- `pending-1440.png`, `pending-1024.png`, `pending-768.png`, `pending-390.png`, `pending-320.png`, `pending-read-320.png`, `technical-320.png`.
- `confirm-1440.png`, `confirm-320.png`, `decision-1440.png`, `approved-1440.png`, `approved-320.png`.
- `chat-320.png`, `chat-read-lost-320.png`, `read-lost-1440.png`, `read-lost-320.png`.

This documenter opened `pending-1440.png`, `pending-320.png`, `technical-320.png`, `confirm-320.png`, `approved-320.png` and `read-lost-1440.png`. They show aligned wide facts, stacked narrow metadata, complete wrapping of the stored filename and technical values, native wrapping actions, explicit consent boundaries, distinct receipt copy and read-loss guidance without private filename. The filename reflects the actual stored sanitizer's 120-character bound; its visible ending is not evidence of visual truncation. The fixture has 56 stored bytes and its body never renders in this inspector. Earlier invalid native-opacity/resize captures were replaced. The desktop read-loss capture retains a disclosed transitional 708px Drawer width rather than the settled 736px; its visible content is complete, the mobile replacement is settled and this frame does not establish a new width rule. Static images alone establish neither focus, measured overflow nor authorization.

The focused runtime evidence records a separately scoped human deciding an actual model-created request, zero decision POSTs on both dismissal paths, one POST on confirmation, and the actual successor plus local metadata receipt without a grant. Exact Artifact read revocation closed Chat inspection and removed Review details; canonical detail returned 403 `approval_unavailable`, the unfiltered inbox total became zero, shared guidance appeared and private filename count was zero. Five-width checks measured no horizontal overflow. Backend checks passed 49 Infrastructure and 18 API cases plus one actual SQLite application-host recreation; 30 frontend cases and the production build passed. Final copy and recapture browser fixtures passed six cases each with zero outbound provider requests. Expected denied 403, old-cookie 401 and aborted obsolete reads remain distinct from unexpected application failures.

No CI, Real host/provider, deployment, installed-data migration, full proposal or milestone acceptance is claimed. No new generic Artifact read/list tool becomes a requirement. Existing context/sidecar fingerprint drift and historical summaries remain unchanged; no incidental rebase, global prohibition or presentation defect is canonized. The native confirmation baseline and transitional desktop capture are bounded observations, not new design tokens. Remaining proposal families and final authority/schema consolidation stay open.
