# Tenant Instance Skills UI verification — 2026-10-18

The canonical tenant Instance editor now supports Skills inspection, local authoring/editing, enabled choices/reset, inherited Customize and local deletion through scoped original-human APIs. This advances Phase 7 for Skills; it does not accept a milestone or the full proposal. Resources UI/rich/bulk adapters, Sandbox and remaining resource/effect families, voice/rich delivery, autonomous origins and obsolete-authority/schema consolidation remain open. The earlier SQLite direct-preference reliability follow-up is unchanged.

## Ownership and behavior

`/t/{tenantId}/admin/agents/{instanceId}/skills` reuses the bounded configuration column, panels, Instance heading, native Ant Design search/pagination and shared `SkillDrawer`. Readable full names, origin/projection, required/missing capabilities, effective enabled status, inherited/Instance choices and copy provenance wrap. Configuration and know-how grant no IAM authority. Existing commands retain independent typed permissions and atomic security/Instance/child witnesses.

An exact `/skills/{key}/editor` read supplies current item facts/witnesses independently of collection search/paging. It shares catalog visibility/action hints inside the existing original-human/final Instance/security facade. Missing/unreadable items deny. An open draft refreshes its exact item while retaining edits; Discard draft adopts saved values. Stale saves block replay until explicit Reload and another Save. A confirmed acknowledgement closes the submitted editor before parent read recovery; malformed successful bodies cannot replay a command. Definitive detail read loss closes private content and refreshes the collection so the retired row also disappears. Retired scopes abort requests and ignore late results.

Read-only/archived views retain inspection while disabling changes. Built-in/inherited procedures remain immutable; stored enabled choices and effective management/capability suppression remain separate facts. Reset/Customize/Delete confirmations name exact Skill, agent and organization. Customize atomically creates independent content and disables its source. Dirty cancellation/navigation uses the shared guard. Native cancellation restores the surviving opener; submitted collection confirmations return focus to Reload.

The shared Drawer adds optional dirty notification and independent field/Save disabling; existing consumers retain default behavior. The shared heading accepts typed read-only explanation. The shared feedback-placement owner recognizes explicit `data-feedback-actions` surfaces, keeping native notices clear of collection controls. Skill success notices use one native key and retire on consequential confirmation. No CSS, palette, font, theme token, asset, UI kit, per-page offset or alternate feedback system was added.

## Executed local checks

| Check | Result |
| --- | --- |
| Vitest: `TenantAgentSkills`, `accessRoute`, `SkillDrawer`, `appFeedbackPlacement` | 21 passed: seven Skills, six routes, four shared Drawer and four shared feedback cases |
| TypeScript/Vite: `pnpm run build` | Passed; existing large-chunk advisory |
| `CI=1 TENANT_CHAT_API_PORT=61307 TENANT_CHAT_WEB_PORT=61308 pnpm run test:e2e:tenant-instance-skills` | Two passed together on a fresh isolated Synthetic host, 13.9 seconds |
| Infrastructure: exact detail/current write witnesses and original-human/parent/security races | Eight passed, InMemory/SQLite |
| API: exact readable/unreadable detail plus logout/relogin during protected detail I/O | Three passed |

Total distinct focused checks: 34 (23 frontend, eleven backend). Diagnostic/initial reruns are not additional cases. All final handles reached terminal success. Vitest ran from `web/` using `NODE_OPTIONS=--no-experimental-webstorage pnpm exec vitest run` with the four files named above. Final logs: `/private/tmp/tenant-skills-ui-vitest-clearance.log`, `/private/tmp/tenant-skills-ui-build-clearance.log` and `/private/tmp/tenant-skills-ui-e2e-clearance.log`.

Backend commands used `dotnet test` on the respective project with `--no-restore --nologo`. Infrastructure filter: `FullyQualifiedName~Tenant_child_editors_return_coherent|(FullyQualifiedName~Human_agent_reads_reject_captured_configuration&DisplayName~skill-detail)`. API filter: `FullyQualifiedName~Child_editor_HTTP_reads|(FullyQualifiedName~Agent_read_does_not_release_captured_data&DisplayName~builtin)`. Logs: `/private/tmp/tenant-skills-detail-infra.log` and `/private/tmp/tenant-skills-detail-api.log`. The preceding [child editor prerequisite](tenant-child-editor-reads-verification-2026-10-18.md) owns its separate 40-case evidence, not added here.

The owner browser case publishes thirteen inherited Skills and exercises 320px paging/search, inspection/Escape/focus, enabled choice/reset, Customize/cancel/focus, local creation, dirty cancellation/Keep editing, concurrent settings change, stale save, exact-item reload with retained draft, explicit save and deletion. It asserts persisted procedure/source choice, no document overflow, no uncaught page errors and no owner-capability/global-admin browser calls. The exact reader case uses independent parent/one-Skill read grants, disabled create/manage, hidden-row omission and inspection without IAM-inspector requests. Revocation during open inspection removes procedure/detail and cached collection controls.

The initial browser run and first correction attempt failed because a hover-paused success notice intercepted the Delete opener at 320px. Destroying a notice only after that opener ran was insufficient. Registering actual row action surfaces with the shared feedback-placement owner fixed the reproduced obstruction; the same two cases then passed. The failure was not bypassed with forced clicks or arbitrary waits. Component coverage also verifies malformed-ack/read recovery without replay, explicit draft discard, private detail retirement and late-scope abort.

## Playwright MCP observations

A task-owned InMemory Synthetic API on 61305 and Vite on 61306 used fresh isolated roots and a separate organization/owned source. Visible interactions created a local Skill, searched/opened it and edited its procedure. An independent public settings write made Save genuinely stale; the procedure remained in the draft. Exact Reload followed by explicit Save persisted it, confirmed by public readback. Inherited Customize created one local copy with provenance and disabled the original. Built-in inspection and Delete confirmation cancelled with focus restored to their openers.

Canonical archive/restore supplied truthful disabled controls. A separate exact reader signed in through the UI and inspected only its permitted inherited procedure. Revoking its exact grant through an independently authenticated task owner and clicking Reload latest Skill closed private detail, refreshed the collection and removed private identity/rows. Final console/network inspection showed the expected two 403s for detail and collection, plus normal StrictMode aborts; this is not a zero-error-console claim. Fixture setup initially exceeded a Skill-name limit and used the wrong draft-list envelope; those setup mistakes were corrected before product verification.

The existing canonical Definition draft also opened/cancelled its shared Skill details Drawer at 320px. No owner capability was used for that consumer check, no candidate was saved/published, and no execution Run or provider call was started.

## Bounded visual review

One batched round captured 18 PNGs under `.playwright-mcp/tenant-skills-ui-20261018/`. The implementation and fresh finish reviewers each opened every image:

- `skills-{1440,1024,768}-top.png` and `skills-{390,320}-{top,lower}.png` (seven).
- `skills-editor-{1440,320}.png`, `skills-confirm-{1440,320}.png`, `skills-builtin-inspect-320.png` and `skills-provenance-1440.png` (six).
- `shared-definition-drawer-320.png`, `skills-archived-320.png`, `skills-readonly-320-{top,lower}.png` and `skills-denied-320.png` (five).

Measured parent section gap is 16px; the shared 48rem bound yields 768/744/736px at 1440/1024/768. All five widths have no document horizontal overflow. Narrow top/lower captures show intentional internal main scrolling and wrapping actions. The fresh reviewer returned **ship**, with no material fixes and the incumbent Operate craft ceiling reached. All captures are valid for their named viewport/state. Drawer/footer images establish captured presentation, not acceptance of every uncaptured lower field or full procedure. Loading, transient-error, stale and malformed-ack presentations were not visually accepted; functional evidence stays separate. The narrow design-system merge preserves existing tokens, identity and historical provenance.

No hosted CI/full CI suite, full-proposal or milestone acceptance, Real provider, installed-data reset or deployment was requested or run. Local runner `CI=1` prevents server reuse. All browser data/hosts are task-owned disposable Synthetic fixtures.
