---
version: 1
slug: "web-src-features-access-tenantsandboxes-tsx"
primary_target: "web/src/features/access/TenantSandboxes.tsx"
related_targets: ["web/src/features/admin/SandboxProfileFields.tsx", "web/src/features/admin/SandboxProfilesSection.tsx", "web/src/features/admin/SandboxProfileDetails.tsx", "web/src/features/sandbox/SandboxEvidence.tsx", "web/src/features/access/AccessApp.tsx"]
---

# Organization Sandbox catalog

Mode: Operate. Ordinary extension of the incumbent dark Ant Design v6 application. Preserve `AdminShell`, the shared collection toolbar, native Table/Drawer/Form/Descriptions and existing confirmations, feedback and draft guards. No new visual world, palette, CSS, token, font, raster asset or UI kit.

The organization shell owns title/context/navigation and page inset. The collection owns its 16px vertical stack, search/pagination and wrapping Reload/Add action group. Prioritize full readable profile name, revision, enabled intent and named availability. Exact `profileId` inspection at `/t/{tenantId}/integrations/sandboxes?profileId=…` holds image/source details, immutable history and saved evidence in a viewport-capped 736px native access Drawer. The Drawer owns one 24px body inset and local scrolling; footer actions wrap.

Reuse `SandboxProfileFields` and the inherited responsive field grid for tenant Add/Edit and the existing platform catalog. Shared `SandboxProfileDetails` retains responsive labels, full wrapped immutable image and Profile initialization; evidence stays bounded escaped text. Each consumer keeps its own identity, credential transport, source bounds and commands. Saving, image-only readiness, full verification and agent execution authority remain separate facts.

The [interaction contract](../../docs/19-multi-tenant-iam-ux.md#organization-sandbox-catalog-interaction-contract) and [Frontend](../../docs/13-frontend-implementation-spec.md#organization-sandbox-profile-catalog) own behavior. Keep recovery on the affected surface, deliberate drafts intact until explicit save/discard and acknowledged saves retired before read recovery. Native dirty Cancel/Escape and focus recovery retain the existing owners; closing the editor restores Edit profile.

[Focused verification](../../docs/reports/tenant-sandbox-catalog-ui-verification-2026-10-18.md) owns actual Synthetic create/edit/disable, stale recovery/history, malformed acknowledged PUT recovery without replay and keyboard draft/focus evidence. Verification/access and shared-platform presentation use controlled adapters, separately from backend execution evidence. The fresh finish reviewer inspected 29 captures in `/private/tmp/agent-core-catalog-ui-JnGswL` and returned SHIP for the inspected scope. No document overflow was measured at 1440/1024/768/390/320px.

Dedicated visual acceptance excludes loading, empty/search-empty, transient, stale, malformed acknowledgement, history and confirmation. Shared platform lower fields and mobile CPU/memory remain visually unverified. This context sync performs no runtime checks and preserves the historical sidecar without regeneration. Instance Sandbox configuration/verification, Session controls UI, live SDK qualification and full proposal/milestone acceptance remain separate.
