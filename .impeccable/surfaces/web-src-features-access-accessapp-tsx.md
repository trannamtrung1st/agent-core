---
version: 1
slug: "web-src-features-access-accessapp-tsx"
primary_target: "web/src/features/access/AccessApp.tsx"
related_targets: ["web/src/features/access/AccountForms.tsx","web/src/features/access/Organizations.tsx","web/src/features/access/Members.tsx","web/src/features/access/Roles.tsx","web/src/features/access/InvitationAcceptance.tsx","web/src/features/access/useAccessDraftGuard.tsx","web/src/services/identity.ts","web/src/features/admin/AdminShell.tsx","web/src/features/admin/AdminCollectionToolbar.tsx","web/src/app.css"]
---

# Account and organization access

Mode: Operate. Extend the incumbent dark Ant Design v6 system. The product contract is docs/19-multi-tenant-iam-ux.md; current implemented scope and remaining security cutover are in docs/reports/multitenant-iam-delivery-2026-10-16.md.

PURPOSE: Sign in once, choose an organization, understand current authority, invite people and manage roles without confusing human, agent, system and tenant privileges.

COMPOSITION: Account forms are a bounded single column with persistent inline errors. Authenticated screens use AdminShell with an explicit context model, the same header/navigation drawer/page heading, 8/12/16 spacing and direct Ant Design controls. Organization selection is a searchable, paged list reused inside the switch dialog. Members and roles use the shared collection toolbar and wrapping two-column tables. Keep one inset owner and no nested card scaffolding.

AUTHORITY: Current server permissions control navigation and editable controls. A global Principal reference alone is never a platform-admin flag. Reader can inspect builtins but has no mutation CTA. Organization selection is not a platform-admin destination. Existing unscoped Chat/resources are not exposed through these access pages; their security cutover remains pending.

FORMS: Role authoring uses grouped searchable permissions with contextual accessible names and People only labels. Builtins are read-only. A bounded native Drawer owns scrolling and reachable Save/Cancel; its wrapper is capped at the viewport. Match 40px mobile controls without adding height to inner affix inputs. Use the shared confirmation and form-feedback owners. One dirty guard owns nested Escape and deduplicates confirmation until the native close transition finishes.

STATES: Real login failures, expired sessions, no organizations, search emptiness, read-only access, revoked membership, last-Owner and stale revision errors have named recovery. Do not suggest Reload for a last-Owner denial. Do not add horizontal-table guidance to a non-scrolling list. Success follows a committed response. Invitation links are shown once, remain purpose-bound and are transmitted in POST bodies after stripping the URL fragment; no email delivery is implied.

SWITCHING: Unmount/abort previous scoped reads before rendering a new context. Full document switching closes previous browser connection/audio resources. Fetch current effective tenant permissions on each destination. Cookie/CSRF material stays in browser-managed cookies or transient service memory, never durable client storage.

VERIFICATION: The dated access UI report owns the bounded audit/correction/confirmation, real Synthetic browser interactions, widths 320/390/768/1024/1440 and exact checks. No durable Approval, agent access, tenant-aware Chat, full resource isolation or production acceptance is established by this foundation slice.
