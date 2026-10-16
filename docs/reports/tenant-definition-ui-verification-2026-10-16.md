# Tenant Definition UI verification — 2026-10-16

This extends checkpoint `4e60f9d9` with canonical owned Definition authoring. The original multi-tenant IAM/approval proposal remains active. This is a focused local checkpoint, not full migration, tenant isolation or milestone acceptance. Tests used disposable Synthetic hosts/data; existing user data and Real processes were preserved.

## Changes and review corrections

Canonical tenant routes provide an authorized Definition inventory, blank/exact shipped-template creation, detail, shared draft configuration, resources, offline evaluation and publication lifecycle. Safe summary, current-self resource access and scoped authoring metadata GETs support the editor without global owner APIs. The backend checks actual ownership and current read/create authority; capability configuration never grants IAM permissions.

Existing AdminShell, owner headings, typed draft navigation, candidate form, Skills, state/isolation, self-management, execution limits, knowledge binding, configuration panels, confirmations and Ant Design feedback are reused. Knowledge binding is shared with the existing editor; its default-path copy now describes a default rather than a legacy fallback. Parent layouts retain existing token spacing and one inset owner. No new UI kit, palette, font or token was introduced.

Integrated review found and corrected missing native App feedback context after successful creation, draft-guard interference with successful navigation, and read loading that blocked organization switching. Commands now exclude duplicates, abort retired owners and ignore their late completions. If a write is acknowledged but local completion fails, persistent Reload page guidance remains and the action cannot be replayed; clearing an ordinary request error cannot erase this recovery state. Resource/scenario failures remain visible inside their open drawers. The test fixture explicitly isolates Definition content and uses a separate tenant browser artifact directory.

## Observed workflows

| Journey | Expected and observed result |
| --- | --- |
| Blank Definition → edit → section navigation → Save | Local description survives moving to Instructions without discard; explicit Save persists both fields and disables Save until another edit |
| Upload → required scenario → publish | Actual `reference.txt` bytes bind to the owned draft; Publish remains disabled before current Passed evidence; offline evaluation enables publication; confirmed publish consumes the draft |
| Publication → download → fork → deletion | Read-only inspection shows the resource; protected browser download has the expected filename and exact uploaded bytes; fork creates an editable draft; confirmed draft then logical-definition deletion remove only the selected fixture |
| Resource-only Reader → revocation | Inventory contains its granted Definition only; no create/save/publish/delete actions; current read revocation followed by reload hides the private owner and shows denial |
| Stale save → access refresh → explicit retry | One injected 409 preserves local edits; Reload access and keep edits reads current authority; a subsequent explicit Save persists the value across page reload |
| Organization switch during a delayed read | A real old-owner response is held while switching through visible organization controls; its late completion cannot populate the new organization's empty inventory |
| Shared access regression | Actual sign-in failure/recovery, role editing, invitation/Reader access, dirty drawers/focus, mobile containment, denial and Account lifecycle remain passing |

The primary Definition journey also asserts zero page errors and zero requests to `/api/v2/admin/`. This is evidence for that exercised journey, not a claim that the old global routes have been removed.

## Checks and commands

| Check | Result |
| --- | --- |
| API filter `TenantDefinitionAccessTests|TenantAgentAccessTests|IdentityAndTenantAccessTests|IdentitySqliteRestartTests` | 14 passed, zero skipped; includes scoped self-access/metadata, creator-only metadata, foreign denial and real cookie/CSRF/SQLite restart composition |
| Vitest: access routes/commands, candidate editor, isolation policy, Skill drawer | Initial five-file run: 29 passed. Final route/command rerun: 6 passed, including the added owner deep-link case and persistent acknowledged-write recovery assertion |
| Legacy `AdminApp.test.tsx -t 'knowledge|resource'` | 6 passed; 38 unrelated cases deliberately filtered out |
| Definition Playwright CLI | 2 passed, including exact-byte download, security and recovery; repeated on new fixture-owned ports with the final isolated artifact configuration |
| Access foundation Playwright CLI | 1 passed on fresh disposable ports 5302/5402 |
| `pnpm --dir web build` | Passed TypeScript and Vite production build; existing SignalR annotation and large-chunk warnings remain |
| Documentation hygiene | Local links/fragments, fences, complete changed-file JSON examples and `git diff --check` checked before commit |

The frontend runs cover 36 distinct passing cases across the selected files and final route update; overlapping route/command reruns are not additional unique cases.

API command:

```bash
dotnet test tests/AgentCore.Api.Tests/AgentCore.Api.Tests.csproj --filter 'FullyQualifiedName~TenantDefinitionAccessTests|FullyQualifiedName~TenantAgentAccessTests|FullyQualifiedName~IdentityAndTenantAccessTests|FullyQualifiedName~IdentitySqliteRestartTests' --no-restore --nologo --disable-build-servers -p:UseSharedCompilation=false -m:1 -v minimal
```

Frontend commands:

```bash
pnpm --dir web exec vitest run src/features/access/accessRoute.test.ts src/features/access/useTenantCommand.test.tsx src/features/admin/definitionCandidateEditor.test.tsx src/features/admin/DefinitionStateIsolationPolicy.test.tsx src/features/admin/SkillDrawer.test.tsx
pnpm --dir web exec vitest run src/features/admin/AdminApp.test.tsx -t 'knowledge|resource'
pnpm --dir web exec vitest run src/features/access/accessRoute.test.ts src/features/access/useTenantCommand.test.tsx
env ACCESS_API_PORT=5303 ACCESS_WEB_PORT=5403 pnpm --dir web exec playwright test --config playwright.tenant-definitions.config.ts --reporter=line
env ACCESS_API_PORT=5302 ACCESS_WEB_PORT=5402 pnpm --dir web exec playwright test --config playwright.access.config.ts --reporter=line
pnpm --dir web build
```

Playwright MCP navigation was attempted but failed with `Transport closed`; the actual browser evidence above uses Chromium through the Playwright CLI fallback. An initial parallel access run failed during trace cleanup because both configurations shared the default artifact directory. Tenant artifacts now use ignored `local/test-results/tenant-definitions`; the access scenario passed on its isolated rerun. Earlier UI/test failures were corrected and their affected journeys rerun. No CI was dispatched or awaited and no full CI suite was run locally.

## Bounded visual and system review

Desktop 1440×1000 and mobile 390×844 Test & Publish captures include a long name, saved state, required Passed result and reachable actions. Initial title spacing was corrected by reusing AdminOwnerHeading; quiet captures remove success-toast occlusion. The mobile page has no document horizontal overflow. An independent Impeccable finish reviewer returned **ship** for these captured surfaces only, with no material fixes remaining. This does not establish uncaptured UI acceptance.

Local evidence is retained at `.impeccable/review/tenant-definition-desktop.png` and `.impeccable/review/tenant-definition-mobile.png` (ignored). The independent documenter confirmed incumbent system reuse and requested no design-system change. Existing DESIGN.md and `.impeccable/design.json` are preserved. It noted inherited drift: DESIGN.md's same-row supporting-text guidance differs from the pre-existing separate metadata row in AdminOwnerHeading, also present at the starting checkpoint. That unrelated rule/component disagreement was reported rather than canonized by this extension. Product context and the existing surface brief now point to the measured feature scope.

## Remaining boundaries

The tenant Sandbox catalog, rich resource previews, resource assignment panels and complete validation/diff workflow are pending. Template selection, deprecation and Instance-creation UI controls have source/API coverage but no new full browser assertion in this slice. Shared fields have focused regression evidence, not a complete responsive journey for every editor section. MCP itself remains unavailable; no real-device/audio or hosted-provider behavior was verified here.

Tenant-aware runtime definition/resource resolution, remaining Instance settings/resources/decommission, Sessions/Runs/Hub/tool/background effects, trusted executor/requester lineage, durable approvals, obsolete-authority removal, canonical schema/reset consolidation and complete designed browser acceptance remain required by the [full delivery ledger](multitenant-iam-delivery-2026-10-16.md). No live reset, deployment or user-host restart was performed.
