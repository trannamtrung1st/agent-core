# Instance resource preview polish verification

Date: 2026-10-10. Bounded frontend follow-up; no milestone acceptance claim.

The shared Definition/Instance import preview now uses an AntD table with one set of Path, Kind, Size and Status headings, editable rows and named icon removal actions. Below 768px each row stacks its controls. The Instance add drawer allows 880px, capped to the viewport; edit and inspection retain 640px. AntD table cells own row padding, token-based Flex gaps own field spacing, and the drawer retains its 16px inset and persistent footer.

New fields are neutral. Path errors appear after blur; kind errors after blur or a committed choice. Touched fields revalidate while changing. Local file constraints are immediate neutral status text, independent of path error styling, and invalid selections still disable upload. Server upload failures remain explicit errors. [Frontend Implementation](../13-frontend-implementation-spec.md#scoped-instance-settings-and-resources-ui) owns the behavior.

## Runtime evidence

Playwright MCP used a disposable Synthetic/InMemory API at `127.0.0.1:5097` and Vite at `127.0.0.1:5187`. Health reported Synthetic. No existing host or data was reset.

- Selected 15 Instance files including two larger than 8 MiB. Observed 15 rows, one desktop header row, readable MiB sizes and zero invalid fields initially. Upload stayed disabled.
- Inspected Instance and shared Definition previews at 1440, 768 and 390 pixels. The Instance drawer measured 880, 768 and 390px respectively. Both consumers had matching client/scroll widths, desktop column alignment and stacked mobile rows. Screenshots are retained in gitignored `local/verification/instance-resource-preview/{instance,definition}-{1440,768,390}.png`.
- On mobile, selected a text file, focused and blurred Kind, and observed only Kind become invalid. Changing the path to a knowledge folder inferred Knowledge, enabled Add and stored the file in the Instance catalog.
- Bound a file through the shared Definition import preview and observed its Knowledge path and exact byte count in the resource table.
- CLI scenarios exercised partial upload failure/retry without duplicating committed files, nested folder paths, exact PDF bytes, duplicate-path validation after blur, rename with retained content, optional replacement and scoped configuration recovery.

Resource API requests completed successfully during MCP interaction. A later concurrent edit of `AdminApp.tsx` briefly produced Vite JSX transform errors outside this change; final reload and another Instance upload passed after that edit settled, with no console errors. The final build also passed. Existing development changes were preserved.

## Checks

| Command (web directory unless stated) | Result |
| --- | --- |
| `pnpm exec vitest run src/features/admin/resourceImportPanel.test.tsx src/features/admin/resourcePreview.test.ts` | 26 passed |
| `pnpm exec tsc --noEmit` | Passed |
| `pnpm exec playwright test --config .resource-preview-followup.playwright.config.ts` | 4 passed |
| `pnpm build` | Passed; existing SignalR annotation and bundle-size warnings |
| `git diff --check` (repository root) | Passed |

The temporary E2E config extended the standard runner with no managed web servers, the disposable Vite base URL and only the Instance import/scoped-configuration specs. It was removed after verification. Full frontend/backend suites, Compose and hosted CI were not rerun for this presentation change.

## Row alignment follow-up

The shared table cells now use vertical middle alignment, and the mobile Kind/Size Flex row uses center alignment. Playwright MCP checked both consumers at 1440/768/390px on a new disposable Synthetic host. Desktop/tablet Instance controls and text had vertical centers within 0.7px, including a wrapped oversized-file status; mobile Kind and Size had identical centers. Definition cells used middle alignment at every width with no local overflow. Removing the oversized Instance file enabled a successful upload, and the Definition bind also succeeded. `pnpm build` and `git diff --check` passed; no new unit tests or broad regression rerun was needed for these two alignment properties.
