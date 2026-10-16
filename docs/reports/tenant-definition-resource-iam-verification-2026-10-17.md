# Definition resource binding IAM verification — 2026-10-17

This continues `847f899f` toward the full tenant/IAM/approval proposal. Definition draft/publication file bindings now have actual owner/context-qualified scopes, independent typed resource permissions and matching resource UI states. The full goal remains active; this is not full tenant runtime, durable approval, deployment or milestone acceptance.

## Implemented boundary

Type `resource` accepts `{definitionId:D}:draft:{draftId:D}:{resourceId:D}` and `{definitionId:D}:publication:{positiveVersion}:{resourceId:D}`. GUIDs and version spelling are canonical. The same resource ID in another draft/publication is a distinct target. Both stores verify the actual owned root, exact context and binding; paths, hashes and caller labels never establish ownership. Existing assignment columns/child rows are reused without a migration or registry. Instance child scopes keep their existing format.

Parent Definition read remains required. Existing-file edits need resource read/manage and removal needs resource read/delete, at the exact binding or inherited tenant scope. Definition configuration update alone permits neither. A settings-read-only actor can manage a separately granted file. New bindings and resource copies created by publish/fork/template require tenant-wide destination resource read/manage when files are present. Offline evaluation requires read on every captured file. Aggregate root deletion retains its explicit Definition delete/read and complete revision-witness contract; it returns no protected child content.

The authorizer owns one shared filtered-resource projection for Instance and Definition views. Draft state returns filtered resources and `resourceAccess` with inherited `allRead`, create/evaluate/publish eligibility and visible item scopes/manage/delete choices. Publication lists include filtered items and `allRead`; mutation resource responses filter hidden siblings too. Direct grants do not imply a complete manifest. Typed permissions precede stale security/draft witnesses and byte preparation; an authorized stale write conflicts, while an actor who lost file permission receives denial. Incoming batch membership is captured before awaits.

Downloads resolve actual owned bytes, verify hash/length/media, then recheck exact current permission and parent. A changed draft revision rejects old prepared bytes with `resource_changed`; revocation prevents bytes returning. New bytes cannot be borrowed through a supplied hash, and metadata-only edits require an existing binding in the same draft. Existing path/media/secret/count/byte limits and publication/evaluation gates remain intact.

Removal, draft consumption/deletion and aggregate Definition deletion retire corresponding grants in the same security/draft/audit transaction. Raw writes retaining retired grants reject before mutation. Fork/publication addresses receive no copied grants. Immutable content remains retained for other bindings/history. Grant/delete races have one security-CAS winner; SQLite reopen retains retirement.

## UI and observed journeys

The existing AdminShell, owner heading/configuration frame, direct Ant Design tables/drawers/alerts and 8/12/16 spacing roles are reused. No palette, font, token subsystem, generic control wrapper or second UI kit was added. Per-item metadata controls Edit/Remove; tenant destination authority controls Upload. Settings read-only copy is separate from file eligibility. Limited lists use a compact native info alert and access-aware empty state. Evaluation and publication explain missing source/destination access. Template/publication fork metadata disables unavailable file-copy choices without granting authority.

| Journey | Expected and observed result |
| --- | --- |
| Definition editor without file authority | Resource state contains no hidden row/path sentinel; download/upload/removal/publication deny. Definition update does not substitute for typed file authority |
| Exact file grant with parent-only Reader | Only the granted file appears, metadata edit/download succeed without Definition update, hidden sibling and new-file creation deny; mutation responses contain no hidden rows |
| Evaluation source authority | An actor who reads only part of the manifest cannot run the evaluator over all captured files; read/manage of one file does not confer tenant-wide creation |
| Publish/fork/delete | Consumed draft grants retire; an explicit publication grant reads immutable bytes but grants no fork-draft access. Explicit fork-file delete retires only its new scope and preserves publication bytes; aggregate/draft deletion removes the reviewed target grants |
| Raw writes and isolation | Retained-grant remove/publish/delete reject without partial state/audit; foreign roots/contexts and noncanonical versions deny. Concurrent grant/delete produces one coherent resource/assignment graph |
| Private I/O fence | Gate a download, then revoke its exact grant or replace bytes: no old bytes return. The authorized owner reads the current exact bytes afterward on both stores |
| Browser edit/download/revocation | Reader of settings edits the granted file, downloads exact bytes, retains unsaved input after a denied write and reloads to hide revoked rows/actions. Unauthorized template option is disabled. Console/unexpected HTTP/request failures are checked |
| Existing browser recovery | Owner authoring/upload/evaluation/publication/lifecycle still works; scoped read-only, stale-save and delayed tenant-switch reads retain their established behavior |

The scoped resource journey checks document overflow and reachable controls at 1440, 1024, 768, 390 and 320 pixels. Desktop and narrow captures were inspected together, repeated permission copy was reduced, and settled controls were confirmed once. Evidence is task-local under `.impeccable/review/definition-resource-iam-{desktop,mobile}.png`; the owning shell/parent Flex retains spacing and the narrow table wraps actions and paths.

## Focused verification

The representative owner HTTP journey ran before broad regressions. The new exact-binding HTTP and eight both-store cases then ran before the affected families. Commands use `dotnet test <project> --filter '<selection>' --no-restore --nologo --disable-build-servers -p:UseSharedCompilation=false -m:1 -v minimal`. Prefix every selection term with `FullyQualifiedName~` and join with `|`.

| Project / selection | Result |
| --- | --- |
| Infrastructure: `TenantDefinitionIntegrationTests`, `IamIdentityIntegrationTests`, `AgentInstanceStoreContractTests`, `AdminInstanceDefinitionVersionHistoryTests`, `AdminInstancePersonaHistoryTests`, `AdminInstanceLifecycleHistoryTests`, `AgentDefinitionResourceAdminStoreContractTests`, `InstanceSkillsStartupTests`, `DefinitionResourceInfrastructureDiTests` | 183 passed, zero skipped; includes eight new cases |
| API: `TenantDefinitionAccessTests`, `TenantAgentAccessTests`, `IdentityAndTenantAccessTests`, `IdentitySqliteRestartTests`, `AgentInstanceSkillTests`, `SelfManagementAuthorityTests`, `SelfManagementOperationTests` | 41 passed, zero skipped after the destination-permission fixture update |
| Final API: `Definition_resource_scopes_filter_content`, `Shipped_template_http_fork`, `Owned_resource_upload_evaluate_publish_and_read` | 3 passed, overlapping cases, after permission-first ordering and final evaluator/fork-delete assertions |
| Final Infrastructure: `Definition_resource_grants`, `Definition_resource_scope_grant`, `Definition_resource_download_rechecks` | 8 passed, overlapping cases, after permission-first ordering |
| Frontend: `pnpm --dir web exec vitest run src/features/access/accessRoute.test.ts src/features/access/useTenantCommand.test.tsx src/features/admin/definitionCandidate.test.ts src/features/admin/definitionDraftPublishGate.test.ts` | 35 passed across four files |
| Frontend: `pnpm --dir web run build` | Passed; existing SignalR annotation and bundle-size warnings remain |

The projects are `tests/AgentCore.Infrastructure.Tests/AgentCore.Infrastructure.Tests.csproj` and `tests/AgentCore.Api.Tests/AgentCore.Api.Tests.csproj`. The configured Playwright MCP was discovered and attempted, but `browser_navigate` failed with **Transport closed**. The fallback used task-owned native Synthetic API/Vite at 127.0.0.1:7298/7398, explicit test bootstrap and isolated temporary workspace/artifact/attachment/resource/key roots. `/health` confirmed Synthetic before browser execution. No unknown/Real host was reused.

Browser command: `TENANT_UI_EXTERNAL=1 TENANT_UI_URL=http://127.0.0.1:7398 pnpm --dir web exec playwright test --config playwright.tenant-definitions.config.ts`. The final confirmation passed all three journeys against the permission-first API, including explicit console/unexpected HTTP/request checks. This is 262 distinct focused checks across storage/IAM, API, frontend unit and browser selections; overlapping reruns are excluded. MCP verification is not claimed.

The initial API compile lacked the scope helper namespace import; that was corrected. The first broader API run passed 40 and failed the old template-author expectation that Definition-create alone could copy files. Its revised fixture first proves denial, then explicitly grants resource read/manage and verifies successful copy without granting agent creation or Definition read. The final browser network assertion exposed stale security revision being checked before lost file permission; live typed permission now runs first, matching the Instance API. Authorized stale-write conflict and denied unauthorized stale-write assertions pass. These were addressed without production bootstrap/authority exemptions.

## Remaining scope

Other tenant-owned resource/effect families, tenant Sandbox/decommission, complete Instance/editor access/preview panels, Session/Run/Hub/tool/background tenancy, trusted executor/requester/root lineage, durable approval execution, obsolete global authority removal and final schema/reset consolidation remain required. The [delivery ledger](multitenant-iam-delivery-2026-10-16.md) retains those obligations. File-authoring evidence does not qualify protected tenant execution or the complete designed UI.

Existing user data, Real hosts and credentials were preserved. Only task-owned disposable Synthetic hosts were restarted for browser checks; no deployment, live reset, hosted provider call or CI was performed.

Documentation hygiene passed for 12 changed Markdown files, 692 local links/fragments, 20 complete JSON examples, fences and whitespace. Both task-owned verification hosts were stopped and their isolated temporary root was removed; task-local browser evidence remains available.
