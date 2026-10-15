# Admin form feedback verification — 2026-10-15

This record preserves local verification performed earlier in the Sandbox/validation implementation session. The subsequent design-system documentation sync did not rerun the application. Inspected working-tree source hashes are recorded in [.impeccable/design.json](../../.impeccable/design.json); base commit `58bd05b3` alone does not include the uncommitted changes. Those hashes identify the documentation inspection, not an immutable snapshot of each earlier browser/test run. This is focused local evidence, not milestone or hosted acceptance.

[Frontend Implementation](../13-frontend-implementation-spec.md) owns behavior. [DESIGN.md](../../.agents/context/DESIGN.md#admin-forms-creation-dialogs-and-validation) and the [Admin composition guide](../../web/src/features/admin/README.md) describe the shared presentation and source owners.

## Exercised behavior

Playwright MCP exercised the affected use cases against disposable local Synthetic hosts (API 5088, Vite 5188). Those hosts were stopped after verification.

| Use case | Observed result |
| --- | --- |
| Sandbox create, blank Image, 1440×900 | Immediate warning; inline error and `aria-invalid`; focus moved to Image, visible within y=276…330. |
| Invalid lifetime inside collapsed Advanced | Advanced reopened, warning named the lifetime error and focus moved to Maximum lifetime. |
| Sandbox image rejected by the server | Actual API 400 produced Save failed feedback while retaining the create draft. Correcting to a local image ID saved with API 200, closed the Modal and selected the saved profile. |
| Nested Registry credential, blank required fields, 390×844 | Warning named Display name; focus moved to that field. The toast stayed within x=36…354 of the 390px viewport. |
| Instance Sandbox server validation | A 5,500-character multibyte script received API 400, displayed Save failed through the shared configuration owner and retained all draft characters. A corrected script saved with API 200. |
| Sandbox creation lifecycle | Save, dirty Cancel → Keep editing/Discard changes, save-error recovery, automatic profile-ID generation and nested credential Escape/focus were exercised. |
| Global/Instance layout and storage | Form/control containment checked at 1440/1024/768/767/390/320 and reduced 720×500. The shared stack/grid used 16px gaps without additive Form.Item margins; Modal footer remained reachable. Instance 128.5 MiB saved as 134,742,016 integer bytes and Discard restored 128.5 MiB. |

Console/network inspection found the expected validation 400 responses; no unexpected runtime exceptions remained in the exercised paths.

## Focused local checks

Focused Vitest suites passed for Sandbox profiles, Email accounts, Add credential, Credentials, Instance settings/configuration coordination, shared form/error feedback, Skill drawer/Instance Skills, Browser privacy and Instance Automations. Two timing-sensitive failures in the initial parallel run passed on isolated rerun. The privacy assertion was scoped to the persistent local alert because the new toast intentionally repeats the failure summary.

The frontend production build, Impeccable detector and whitespace checks passed after the implementation. CI was not requested or run. The later documentation sync checks Markdown links/anchors, structured metadata, source hashes and guide/preview agreement separately.

## Limits

Synthetic mode establishes local UI recovery, not private-registry connectivity or real provider acceptance. Native browser zoom, physical touch and manual screen-reader behavior were not established by these checks. A server failure can identify the action without identifying a particular field; automatic field reveal/focus is established for client validation. Transient feedback supplements persistent field/local details and does not replace them.
