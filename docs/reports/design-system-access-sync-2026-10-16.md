# Current design-system documentation sync — 2026-10-16

Baseline: `cca9050d`. The owner requested a documentation sync of the current design system after the two access UI polish passes. This refresh changes documentation/tooling metadata only. No application code, visual identity, permissions or milestone status changed.

## Sources and ownership

- [DESIGN.md](../../.agents/context/DESIGN.md) remains the existing visual reference, using the eight canonical sections in the [DESIGN.md format](https://raw.githubusercontent.com/google-labs-code/design.md/main/docs/spec.md). No duplicate root reference was created. The existing Ant Design operate direction and descriptive language were retained.
- [antdTheme.ts](../../web/src/app/antdTheme.ts) selects `darkAlgorithm` with primary seed `#1677ff`. The installed Ant Design `theme.getDesignToken()` resolves the primary to `#1668dc`; [AppShell](../../web/src/app/AppShell.tsx) owns the root CSS bridge. The recorded colors, font stack, radii, body type, control height and 4/8/12/16 spacing were compared to those resolved tokens.
- [app.css](../../web/src/app.css), [AdminShell](../../web/src/features/admin/AdminShell.tsx), [collection toolbar](../../web/src/features/admin/AdminCollectionToolbar.tsx) and the actual [access screens](../../web/src/features/access/AccessApp.tsx) own presentation. The access foundation shares shell/header/navigation/heading/confirmation/form-feedback compositions; it introduces no second UI kit or replacement theme.
- [Frontend Implementation](../13-frontend-implementation-spec.md) and [IAM UX](../19-multi-tenant-iam-ux.md) retain behavior ownership. PRODUCT.md already identifies the implemented access foundation and pending cutover; no product facts needed changing. README and the implementation plan retain the same Ant Design v6 baseline and incomplete multi-tenant status.

## Synchronized guidance

Removed the stale October 15 synchronization claim and recorded the current source/evidence boundary. Overview now includes Chat, Admin and account/organization access. The shared shell can show Admin, Account or Access as its secondary area marker.

Layout and Components now record the actual repeated access patterns: 26rem shrinking account-entry column, 48rem bounded account/access forms, shared organization list/switch dialog, wrapping summary/action tables, and native large 736px member/role drawers capped at the viewport. Ant Design owns elevated drawer surfaces and native control states; parent stacks own 16px section gaps, permission choices own 12px gaps, and the role footer owns 12px block / 16px inline padding with 8px action gaps. Existing mobile rules apply to outer controls without doubling affix-input heights.

The access surface brief and design reference now include no-result permission search with selection retention and search-focus return, one dirty confirmation through nested Escape, opener-focus recovery, and persistent invitation manual-copy guidance for absent/denied Clipboard. Existing read-only, last-Owner and stale-revision states remain distinct. These descriptions reflect implemented behavior from the owning frontend spec; they do not grant new authority.

The schema-2 [sidecar](../../.impeccable/design.json) was regenerated with exact Overview/characteristics/rules/Do/Don't text from the same Markdown, valid component references, current source fingerprints and this report as documentation evidence. Existing illustrative previews and dated runtime evidence were retained. Preview snippets explain visual composition; the real React/Ant Design components remain authoritative for behavior.

## Verification and limits

Documentation checks compare the YAML palette/spacing/radii/type against the installed resolved Ant Design theme, validate the eight section names/order, supported component properties and token references, parse JSON, compare sidecar narrative verbatim, verify source hashes, check affected Markdown links/fences and run `git diff --check`.

No application host or runtime test was started for this docs-only request. [Access UI verification](access-ui-polish-2026-10-16.md) retains the executed 320–1440px Synthetic interactions, focused tests, production build and two-browser owner/Reader journey through the second polish pass. The [earlier design sync](design-system-sync-2026-10-16.md) retains its State/preset/shared-preview evidence. Neither is renewed as full-product acceptance by this refresh. Tenant resource/Hub isolation, tenant-aware Chat, Agent Access and durable approval work remain pending; real-device clipboard, non-Chromium, provider/audio and production acceptance remain outside this documentation sync.

Result: **passed** — 17 resolved color roles, spacing/radii/font/body/control comparisons, eight canonical sections, 28 component definitions, exact sidecar narrative, 60 source fingerprints, JSON/YAML parsing, affected Markdown links/fences and whitespace. Application tests were not needed or run for this documentation-only change.
