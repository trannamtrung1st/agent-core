# Design-system source sync — 2026-10-15

This documentation-only update inspects the working tree based on `0c2c0f27`. [DESIGN.md](../../.agents/context/DESIGN.md) owns the lightweight Ant Design v6 presentation guide; [Frontend Implementation](../13-frontend-implementation-spec.md) owns behavior. The [preview metadata](../../.impeccable/design.json), [Admin surface brief](../../.impeccable/surfaces/web-src-features-admin-adminapp-tsx.md) and [Admin composition guide](../../web/src/features/admin/README.md) are synchronized to those owners.

## Current presentation

- Native dark Ant Design v6 and the existing 8/12/16 spacing roles remain the baseline; the strong feedback border maps to native `colorBorder`.
- Forms retain the shared 48rem bound, 72ch supporting copy, responsive field grid and single spacing owner. Sandbox creation uses the existing bounded 800px Modal, shared saved-profile fields and protected draft/focus behavior.
- Shared feedback retains the 40rem maximum, full wrapping, opaque elevated surface, 1px outline and 16px default bottom safe-area gap. AppShell raises overlapping messages only as needed, leaving 8px above visible actions. Covered page controls do not reserve extra space.
- Newer uncommitted Sandbox Image help and Workload protections describe ordinary-image readiness and image-default-user semantics through the existing helper/Descriptions layout. The guide records current source without claiming the whole working tree is a release.

## Evidence boundary

[Feedback visibility verification](feedback-visibility-verification-2026-10-15.md) records the prior five-viewport checks, nested dialogs, growing composer, 12 focused tests and frontend build. [Ordinary-image verification](ordinary-sandbox-images-verification-2026-10-15.md) records the separate in-progress image work and its observed browser/provider checks. Those reports retain their own source and limitations; this sync did not repeat their runs or establish new acceptance.

The sidecar's working-tree source hashes identify this documentation inspection. The committed presentation baseline and inspected working-tree changes are explicitly distinguished.

## Open source-copy mismatch

[SandboxProfilesSection.tsx](../../web/src/features/admin/SandboxProfilesSection.tsx) still describes initialization as running as the non-root workload user. Its current Image helper and Workload protections instead describe the image default user, matching the [ordinary-image frontend contract](../13-frontend-implementation-spec.md#ordinary-image-verification-presentation). The script helper needs a focused source-copy correction; this documentation task leaves application code unchanged.

## Documentation checks

Passed: the guide's eight-section structure, YAML/JSON, token/component references, Markdown links/anchors, source-target existence, narrative parity, 27 working-tree source hashes and whitespace checks. Application tests and a new runtime run were not needed for this docs-only update. CI was not requested.
