# Tenant Attachment copy design handoff — 2026-10-19

Documentation is complete for this bounded Operate extension. The supplied finish-review outcome is **SHIP**, after the correction retaining the original filename for a known 409 refusal. No approved visual-system change is present. Preserve [DESIGN.md](../../.agents/context/DESIGN.md), [design.json](../../.impeccable/design.json) and [PRODUCT.md](../../.agents/context/PRODUCT.md), including historical fingerprints and acceptance boundaries.

The [surface direction](../../.impeccable/surfaces/ures-chat-attachmentmaterializeaction-tsx-84193f51.md) extends the existing Chat file inspector. [Human Attachment copy](../19-multi-tenant-iam-ux.md#human-attachment-copy-to-working-files) and [Frontend](../13-frontend-implementation-spec.md#tenant-original-attachment-copy-and-session-file-collections) remain the behavior owners.

Compared source: `AttachmentMaterializeAction.tsx`, `AttachmentPreview.tsx`, `ChatFileCard.tsx`, `TenantSessionFilesDrawer.tsx` and `SessionArtifacts.tsx` under `web/src/features/chat`; shared `SessionSandboxFrame.tsx`, `web/src/app/antdTheme.ts`, `AppShell.tsx` and `web/src/app.css` supply the presentation evidence. The original file action uses the existing card slot; native confirmation explains both destinations; source/copy recovery precedes independently readable Working files and Artifacts tabs. The known-refusal branch retains source context while disabling another copy pending Reload. Unknown outcomes clear the access detail and require explicit collection/source recovery.

All twelve supplied final Synthetic-app captures in [the evidence directory](assets/tenant-attachment-materialization-ui-2026-10-19/) were inspected: `source-{1440,1024,768,390,320}.png`, `unknown-{1440,1024,768,390,320}.png`, [stale-320.png](assets/tenant-attachment-materialization-ui-2026-10-19/stale-320.png) and [success-1440.png](assets/tenant-attachment-materialization-ui-2026-10-19/success-1440.png). Long multilingual source names, outcome text and recovery controls remain contained in these views. The narrow known-refusal capture includes the native confirmation and retained `source.txt` context. Success reports the working path; uncertainty keeps creation disabled beside Reload. These are application evidence captures, not generated imagery or new product assets. This handoff takes no new captures and runs no application checks.

The incumbent system still fits:

- Palette: Ant Design dark neutrals, blue primary action, native labeled success/warning feedback; no new color roles.
- Type: existing system UI body/secondary text and strong section label; filenames wrap in the source section, while shared cards retain their existing full-name tooltip.
- Rhythm: native Drawer inset, 16px section stack, 12px source/confirmation stack and 8px wrapping action groups; no new spacing scale.
- Structure: existing 640px viewport-capped Drawer, full width below 768px, native Tabs/Modal and shared file collections; existing narrow controls retain their sizing owner.
- Rules: Role Surfaces, Compact Rhythm, One Face and Flat-By-Default remain applicable; no signature or local state becomes a new global prohibition.

Pre-existing metadata drift is preserved: `extensions.inspection` retains an older app-CSS fingerprint, while the current CSS matches the later recorded `3b86dd…` fingerprint; theme and AppShell still match their inspection hashes. Historical metadata is not a fresh extraction of this tree. No tokens, component previews, fonts, assets, kit or visual world need regeneration for this extension.

Dedicated visual acceptance is not established here for loading, partial working-only completion, source/destination denial, every confirmation variant, or lower working-table content beyond the narrow captured viewport. Runtime, exact bytes, focus/recovery and no-replay qualification belong to the implementation evidence. Measured contrast, screen-reader/manual device testing and broad design-system re-extraction remain outside this handoff; no milestone or full tenant-proposal acceptance is claimed. No defect or historical drift is canonized or repaired by this report.
