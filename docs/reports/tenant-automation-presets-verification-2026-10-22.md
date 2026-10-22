# Tenant Automation preset authoring verification — 2026-10-22

This focused module completes canonical tenant recipe authoring and related recovery over the existing Automation owner. It does not claim full proposal or milestone acceptance. The three recipes are editable disabled drafts, and origin metadata grants no permission. The main-branch commit is authorized by the user; no push is included. Event-triggered Experience coverage/orchestration, canonical Harness execution, remaining legacy/internal/schema retirement and exhaustive proposal audit remain open.

## Implemented behavior

- Existing tenant editor reads expose three versioned recipes with prerequisites derived from the selected owned Agent's current Definition, isolation/maintenance settings and unattended model. No legacy Profile service or separate preset route/store is used.
- Create accepts an exact known preset ID/version pair or neither. Both stores independently validate creation; ordinary immutable provenance prevents relabeling. Omitted/null update metadata preserves origin. SQLite reopen retains the actual customized instructions and origin.
- UI Start from fills a disabled draft. Weekly defaults are Monday 09:00 with 52 occurrences; review uses Built-in `run.completed`, UserTurn/non-NoAction filtering and 900-second coalescing. Dirty replacement requires confirmation, retired when its owner unmounts. Saved details show immutable Started from history while ordinary fields remain editable.
- Disabled structural event drafts can save despite catalog outage. An unchanged unavailable saved model can be retained while disabled; activation or a different selection requires current model validation. Active event configurations require at least one enabled subscription.
- Consolidation recipe instructions use Instance-owned Experience and exact consent, without adopting private human Profile, Memory or Experience. Review instructions require exact owned source Run references and factual uncovered-source reporting. Harness remains ordinary recipe guidance; canonical Harness execution is not accepted by this module.
- Unconfirmed/mismatched save acknowledgements retain the draft, block command replay and make no saved-success claim. Explicit read recovery checks actual retained state. Existing known-committed view-refresh errors preserve their separate factual saved wording.

No migration, scheduler, semantic outbox, mutable conversation owner or realtime protocol event was added. Normal admission, source reads, actual customized requirements and exact protected-tool consent remain authoritative.

## Executed use cases

Both InMemory and temporary SQLite API fixtures created all three disabled recipes without a Run, customized their actual fields, retained immutable origin, refused forged/partial/unknown origin without partial writes, and rechecked creation through the stores directly. Current prerequisites followed the selected Agent rather than another Agent with the same Definition. A customized recipe could not run while disabled or without independent executor permissions; after deliberate permitted customization/activation and actual grants it completed with its saved instructions. Disappearing model and all-disabled event activation boundaries were exercised. Existing admission Review, current-human scope, editor filtering/action hints and cold-host occurrence recovery remained passing.

The native Chromium flow ran independently at 1440×1000 and 390×844. It created a disabled consolidation draft, customized/saved it, checked retained origin, intercepted one actual saved update response to spoof its returned origin, and observed an unconfirmed outcome with disabled Save and no success claim. A new read recovered the actual saved name/revision and original origin. It then deliberately enabled a customized daily task, ran it under independent executor permissions, and inspected its completed Run with a factual Needs attention outcome. This proves customized task execution, not semantic consolidation or Harness execution. Finally a controlled Built-in catalog 503 retained the known review source, showed the corrected disabled-save guidance, allowed Save, and read back a disabled review recipe. No page errors, failed requests, legacy-owner API calls or document horizontal overflow occurred in the final runs.

Playwright MCP was attempted against the running Synthetic host and returned `Transport closed`; the practical fallback was the repository Playwright CLI driving native .NET/Vite. Temporary configuration used disposable owned ports 5096/5186, `reuseExistingServer:false`, explicit Synthetic health verification and exact loopback origin. Existing user ports 5080/5173 were left alone. No hosted providers, user data resets or real credentials were used. Owned runners stopped their hosts.

## Focused checks

| Check | Result |
| --- | --- |
| Solution build, serial no-restore | Passed, 0 warnings/errors, 49.79 s |
| Final API test-project build after added boundary cases | Passed, 0 warnings/errors, 9.65 s |
| API preset + affected editor/admission/current-authority/reopen group | 99 passed, 3 m 38 s; 18 new preset cases and 81 existing cases |
| Application existing authoring and durable scheduling policy | 17 passed, 663 ms |
| UI editor, shared command recovery, identity service and event Activity | 30 passed across 4 files, 147.54 s |
| Native final browser review-fix run | 2 passed, 35.4 s; both viewport workflows include the corrected source-outage state |
| Final web TypeScript/Vite build | Passed, 7.07 s; existing chunk-size advisory |
| Mechanical UI detector | One product-target run: `[]`; copy-edit hook also reported no deterministic issue |

Total: **148 focused passing cases** across API, application, UI and native browser checks. The 30-case UI group ran before the final warning-copy correction; that correction was compiled and exercised through both final browser journeys. Existing jsdom ResizeObserver/NaN height diagnostics were present in UI tests; the real browser checks passed. No full local CI suite or GitHub Actions was requested or run.

Commands (repository root unless stated):

```sh
dotnet build AgentCore.sln --no-restore --nologo -m:1 -nodeReuse:false -p:UseSharedCompilation=false
dotnet build tests/AgentCore.Api.Tests/AgentCore.Api.Tests.csproj --no-restore --nologo -m:1 -nodeReuse:false -p:UseSharedCompilation=false
dotnet test tests/AgentCore.Api.Tests/AgentCore.Api.Tests.csproj --no-build --no-restore --nologo --filter 'FullyQualifiedName~Tenant_automation_preset|FullyQualifiedName~Tenant_automation_editor_filters_before_paging_and_uses_independent_exact_action_hints|FullyQualifiedName~Tenant_automations_use_current_human_scope_owned_rows_fresh_occurrence_roots_and_actual_worker_outcomes|FullyQualifiedName~Automation_admission_consent_reserves_only_the_exact_occurrence_before_any_work_exists|FullyQualifiedName~Tenant_owned_Automation_occurrence_reopens_in_a_new_SQLite_host_without_a_profile_or_duplicate_Run' --logger 'trx;LogFileName=automation-presets-qualified.trx'
dotnet test tests/AgentCore.Application.Tests/AgentCore.Application.Tests.csproj --no-build --no-restore --nologo --filter 'FullyQualifiedName~AdminAutomationServiceTests|FullyQualifiedName~TriggerDurablePolicyTests'
```

From `web/`:

```sh
NODE_OPTIONS=--no-experimental-webstorage pnpm exec vitest run src/features/access/TenantAgentAutomations.test.tsx src/features/access/useTenantCommand.test.tsx src/services/identity.test.ts src/features/access/TenantAutomationEventActivity.test.tsx
pnpm exec playwright test --config /private/tmp/tenant-automation-presets-native/playwright.config.mjs
pnpm run build
```

Local logs: `/private/tmp/tenant-automation-presets-qualified-api.log`, `-qualified-application.log`, `-qualified-ui.log`, `-solution-build.log`, `-final-test-build.log`, `-web-reviewed-build.log`; browser output is `/private/tmp/tenant-automation-presets-native/browser-review-fix.log`. Raw request traces, cookies, user data and temporary databases are not committed.

## Failures addressed and design review scope

An initial API fixture expected forbidden before the actual weekly-policy validation; the assertion was corrected to the observed validation failure without widening product policy. Initial render inspection found legacy recipe wording about private Memory/unavailable inspection and an uncertain-save error that could claim saved; canonical tenant recipe instructions and scoped unconfirmed acknowledgement wording were corrected together. One browser capture attempt held the prior Ant Design success toast open because the pointer hovered over it; the fixture moved the pointer away and waited for settlement before the final evidence.

A fresh Impeccable full review opened all eight initial required final-state captures and requested one material correction: source-unavailable copy said restore access before saving although disabled saves are supported. That copy was corrected in one batch. All eight viewports/states were recaptured, plus the actual outage state at both widths. The same reviewer opened all ten captures and returned **ship** on the verdict pass, scoring the one copy fix resolved. This disposition covers the scored fix; it is not a new whole-surface approval. The fresh documenter compared the extension against the existing design system, updated only the Automation surface brief, and verified six sampled system/context hashes stayed unchanged. Historical sidecar source-hash drift was reported without repair. No visual-world/token/CSS/asset change was introduced. Exhaustive keyboard/focus, hover, measured contrast and all recipe/error combinations were not qualified.

Selected retained screenshots:

- [Desktop recipe draft](assets/tenant-automation-presets/desktop-preset-draft.png)
- [Desktop unconfirmed acknowledgement](assets/tenant-automation-presets/desktop-preset-uncertain.png)
- [Narrow reloaded immutable origin](assets/tenant-automation-presets/mobile-preset-saved-origin.png)
- [Actual customized Run result](assets/tenant-automation-presets/desktop-preset-result.png)
- [Desktop disabled source-outage draft](assets/tenant-automation-presets/desktop-preset-source-unavailable.png)
- [Narrow disabled source-outage draft](assets/tenant-automation-presets/mobile-preset-source-unavailable.png)

Canonical owners: [architecture](../03-system-architecture.md#tenant-automation-recipe-ownership), [ports](../04-backend-interfaces.md#tenant-automation-editor-projection), [backend](../12-backend-implementation-spec.md#canonical-tenant-automation-preset-authoring), [UI](../13-frontend-implementation-spec.md#core-event-and-preset-editor), [HTTP](../14-api-and-realtime-protocol.md#tenant-automation-editor-http), [persistence](../15-persistence-and-configuration.md#tenant-automation-preset-provenance), [UX](../19-multi-tenant-iam-ux.md#tenant-automation-recipe-drafts-and-acknowledgement-recovery).
