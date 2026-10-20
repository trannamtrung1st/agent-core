---
version: 1
slug: "web-src-features-access-accessapp-tsx"
primary_target: "web/src/features/access/AccessApp.tsx"
related_targets: ["web/src/features/access/TenantDefinitions.tsx","web/src/features/access/TenantDefinitionDetail.tsx","web/src/features/access/TenantDefinitionEditor.tsx","web/src/features/access/TenantDefinitionResources.tsx","web/src/features/access/TenantDefinitionEvaluations.tsx","web/src/features/access/TenantDefinitionCapabilities.tsx","web/src/features/access/TenantCredentials.tsx","web/src/features/access/TenantCredentialEditor.tsx","web/src/features/admin/CredentialConnectionTag.tsx","web/src/features/admin/EmailAccountDialog.tsx","web/src/features/access/AccountForms.tsx","web/src/features/access/Organizations.tsx","web/src/features/access/Members.tsx","web/src/features/access/Roles.tsx","web/src/features/access/InvitationAcceptance.tsx","web/src/features/access/useAccessDraftGuard.tsx","web/src/services/identity.ts","web/src/features/admin/AdminShell.tsx","web/src/features/admin/AdminCollectionToolbar.tsx","web/src/app.css"]
---

# Account and organization access

Mode: Operate. Extend the incumbent dark Ant Design v6 system. The product contract is docs/19-multi-tenant-iam-ux.md; current implemented scope and remaining security cutover are in docs/reports/multitenant-iam-delivery-2026-10-16.md.

PURPOSE: Sign in once, choose an organization, understand current authority, invite people and manage roles without confusing human, agent, system and tenant privileges.

COMPOSITION: Account forms are a bounded single column with persistent inline errors. Authenticated screens use AdminShell with an explicit context model, the same header/navigation drawer/page heading, 8/12/16 spacing and direct Ant Design controls. Organization selection is a searchable, paged list reused inside the switch dialog. Members and roles use the shared collection toolbar and wrapping two-column tables. Keep one inset owner and no nested card scaffolding.

AUTHORITY: Current server permissions control navigation and editable controls. A global Principal reference alone is never a platform-admin flag. Reader can inspect builtins but has no mutation CTA. Organization selection is not a platform-admin destination. Existing unscoped Chat/resources are not exposed through these access pages; their security cutover remains pending.

FORMS: Role authoring uses grouped searchable permissions with contextual accessible names and People only labels. Builtins are read-only. A bounded native Drawer owns scrolling and reachable Save/Cancel; its wrapper is capped at the viewport. Match 40px mobile controls without adding height to inner affix inputs. Use the shared confirmation and form-feedback owners. One dirty guard owns nested Escape and deduplicates confirmation until the native close transition finishes.

STATES: Real login failures, expired sessions, no organizations, search emptiness, read-only access, revoked membership, last-Owner and stale revision errors have named recovery. Do not suggest Reload for a last-Owner denial. Do not add horizontal-table guidance to a non-scrolling list. Success follows a committed response. Invitation links are shown once, remain purpose-bound and are transmitted in POST bodies after stripping the URL fragment; no email delivery is implied.

RECOVERY: An unmatched permission search shows native Empty with Clear search; clearing returns focus to the search field and preserves selected permissions. Invitation Clipboard failure, including an absent browser API, keeps the one-time link selectable and shows persistent inline manual-copy guidance. Successful retry clears the error. Member-specific role choices reset when closing or changing inspectors. Failed member reads keep their error and Reload inside the open drawer; successful assignments are not replayed or described as unsaved when only the refresh fails. Preserve the shared 16px parent stack, native controls and narrow viewport containment for these states.

SWITCHING: Unmount/abort previous scoped reads before rendering a new context. Full document switching closes previous browser connection/audio resources. Fetch current effective tenant permissions on each destination. Cookie/CSRF material stays in browser-managed cookies or transient service memory, never durable client storage.

VERIFICATION: The dated access UI report owns the bounded audit/correction/confirmation, real Synthetic browser interactions, widths 320/390/768/1024/1440 and exact checks. No durable Approval, agent access, tenant-aware Chat, full resource isolation or production acceptance is established by this foundation slice.


DEFINITION EXTENSION: The canonical owned Definition collection, owner/draft headings, shared configuration sections, resource/scenario drawers and sticky Save/Publish actions reuse incumbent compositions and spacing owners. Same-owner section navigation preserves the candidate; departing unsaved edits confirms discard. Read-only resource grants, stale saves, revoked access, retired requests and acknowledged-write recovery have explicit states. Sandbox catalog, rich previews, assignment panels and complete runtime integration remain pending. The dated tenant Definition UI report owns the two captured Test & Publish widths and actual browser evidence; no new palette, tokens or component recipes were introduced.

## Credential extension direction contract

THESIS: Make an organization's protected accounts understandable and manageable through the incumbent collection and exact-owner inspector. Connection, configuration, binding and use remain separate facts.

OWN-WORLD: Preserve the current dark Ant Design v6 palette, typography, native controls, 8/12/16 spacing and existing AdminShell. Reuse credential fields, connection tags, collection toolbar and responsive detail alignment.

STORY: Find readable credentials, inspect a saved account, edit safe configuration or replace a transient secret, then connect/disconnect Google explicitly. Show current scoped authority, retained drafts and recoverable failures without implying runtime use is delivered.

FIRST VIEWPORT: One searchable paged collection, Reload and permitted Add credential action. A bounded native inspector holds full metadata and connection controls; focused creation/edit dialogs protect unsaved input.

FORM: Existing organization access composition extension; code-led, no concept seed or new visual world. Canonical OAuth return opens the exact saved credential and displays a verified connection outcome.

FINISH: unreviewed and undocumented is unfinished; this build ends with the finish review, the verdict, DESIGN.md, and every shipping raster carrying its provenance

## Organization Sandbox catalog successor

The scoped Integrations Sandbox catalog is now represented by [its focused surface brief](web-src-features-access-tenantsandboxes-tsx.md). It reuses this shell and the existing native access Drawer/collection owners, with shared platform profile fields/details. [Focused evidence](../../docs/reports/tenant-sandbox-catalog-ui-verification-2026-10-18.md) supersedes catalog-UI-pending notes above only; Instance Sandbox configuration/verification, Session controls UI and the wider tenant cutover remain open. Preserve the incumbent system and historical sidecar fingerprints.

## Tenant webhook connections successor

Organization Integrations → Events now uses [the focused webhook surface brief](web-src-features-access-tenantevents-tsx.md) for scoped source management, transient tokens and bounded literal receipts beside independently read built-in metadata. It reuses this shell, collection/configuration sections, native access Drawer, confirmations and draft guard. [Focused qualification](../../docs/reports/tenant-webhook-verification-2026-10-21.md) covers this source/receipt module only; event subscriptions/fanout, the wider tenant cutover and full design/security acceptance remain open. Preserve the incumbent tokens and historical sidecar fingerprints.
