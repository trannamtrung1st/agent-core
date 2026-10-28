# General Assistant review stabilization — 2026-10-28

This follow-up addresses the three review findings against `825d121fbc2a8c16c9d45820a0a26b790c04a035`. It preserves the existing Definition, IAM, Instance, configuration and execution owners. Workflow remains the next unstarted initiative. The original [delivery evidence](general-assistant-delivery-2026-10-28.md) retains its source-specific results; this report records only the subsequent checks.

## Corrections

1. **Discovery:** `TenantChatService` computes IAM-eligible IDs without reading each Instance. The existing tenant-agent store query applies active lifecycle, visibility, optional exact Instance and public name/role search before count/order/page. Management queries still include hidden Instances. SQLite performs count and page in two reads and preserves .NET ordinal case-insensitive Unicode matching through a connection-local deterministic label predicate. Private persona/settings fields never enter search. Existing original-human and security revision read fences remain.
2. **Provisioning recovery:** committed bootstrap/tenant creation returns success when subsequent managed setup fails. A failed setup remains observable as `setup_pending`, with owner-only page `assistantSetupPending` also covering tenants that have ordinary agents. Startup and explicit owner retry use the same bounded reconciler, with at most five CAS attempts. `POST /api/v2/tenants/{tenantId}/general-assistant/reconcile` verifies original-human tenant Owner authority before and after, returns `ready` or `setup_pending`, and accepts no arbitrary Definition/Instance/grant payload. Chat's existing setup area offers **Retry assistant setup**, refreshes candidates and focuses the recovered Identity picker. GET discovery never performs repair writes. There is no new worker, queue or polling subsystem. Cancellation remains cancellation.
3. **Historical templates:** new General Assistant template creation and Definition forks reject non-current versions even when explicitly requested. v35 exact resolution, retained Run pins and ordinary Instance version-adoption policy remain unchanged. Current HTTP/E2E creation fixtures now request v36. Tests specifically exercising pre-existing v35 pins use an explicit historical-release fixture rather than weakening production validation.

Setup logs now retain tenant, safe reason, exception type, numeric error code and trace ID. They omit raw exception messages/stacks, which may contain provider/SQL/credential data. No new permissions or executor privileges are installed by retry.

## Focused checks

Backend commands run at the repository root; frontend commands run in `web`. All .NET test commands use `--no-restore --nologo -m:1 -nr:false -p:UseSharedCompilation=false`. No hosted CI or full local CI suite was run.

| Executed command | Result | Local log |
| --- | --- | --- |
| `dotnet build src/AgentCore.Api --no-restore --nologo -m:1 -nr:false -p:UseSharedCompilation=false` | Passed, zero warnings/errors | `assistant-stabilization-build.log` |
| `dotnet test tests/AgentCore.Infrastructure.Tests` with `--filter 'FullyQualifiedName~Tenant_chat_large_catalog\|FullyQualifiedName~Tenant_chat_discovery'` | 10 passed | `assistant-stabilization-discovery.log` |
| `dotnet test tests/AgentCore.Infrastructure.Tests` with `--filter 'FullyQualifiedName~ManagedGeneralAssistantTests\|FullyQualifiedName~Tenant_chat_'` | 23 passed | `assistant-stabilization-infra.log` |
| `dotnet test tests/AgentCore.Api.Tests` with the filter below | 6 passed | `assistant-stabilization-api-qualified.log` |
| `pnpm exec vitest run src/features/chat/AgentPicker.test.tsx src/features/chat/ChatApp.test.tsx src/services/tenantChat.test.ts` | 45 passed | `assistant-stabilization-ui-qualified.log` |
| `pnpm run build` | Passed; existing chunk-size advisory | `assistant-stabilization-web-build-qualified.log` |
| `pnpm exec tsc --noEmit` | Passed | `assistant-stabilization-ts-final.log` |
| `CI=1 pnpm exec playwright test --config playwright.tenant-chat.config.ts e2e/managed-general-assistant.spec.ts` | 2 passed | `assistant-stabilization-e2e-final.log` |

Exact API selection:

```text
FullyQualifiedName~Managed_setup_failure|FullyQualifiedName~Managed_template_cutover|FullyQualifiedName~Managed_default_chat_only|FullyQualifiedName~Version_catalog_and_preview|FullyQualifiedName~Shipped_template_http_fork
```

The large-catalog regression seeds 1,024 extra owned Instances. On both stores it verifies Unicode name/role search, hidden exclusion, final-page totals, private-field exclusion and exact cached selections. Its SQLite command interceptor observes exactly two reads per call, ten reads for five selector calls; the store probe observes zero per-Instance discovery lookups. Existing scoped discovery/revocation/logout/archive read-fence tests also pass. These counts establish removal of the sequential read regression, not a production latency SLA.

Both-store HTTP failure injection verifies successful bootstrap and live/system plus application tenant creation while provisioning throws; pending feedback; read-only repeated discovery; safe diagnostics without injected sensitive exception text; explicit pending then recovered retry; pending feedback alongside an ordinary agent; one managed identity/provision audit; and independent bootstrap recovery without restarting. The Chat-only member is denied retry. Create/fork v35 requests return 403 without advancing security revision, current v36 requests succeed, v35 exact reads remain available, and ordinary version preview/adoption still works.

The repeatable browser scenarios exercise pending-state retry through the real API, refresh/focus and a completed ordinary Synthetic conversation, plus the prior managed hide/reset/retained-conversation journey. The pending read in the CLI recovery test is a browser fixture; actual provisioning failure/recovery is independently verified through HTTP injection and the native SQLite browser journey below. The first CLI attempt encountered an ambiguous test locator because the focused picker also exposes an accessibility announcement. The locator was narrowed to the visible native Select title; both scenarios passed on rerun. No product behavior was weakened.


### Additional existing native fixture probe

Because current creation fixtures now request v36, two existing native Office cases were also probed:

```sh
dotnet test tests/AgentCore.Api.Tests --no-build --no-restore --nologo --filter 'FullyQualifiedName~Tenant_Office_mutations_use_current_authority_exact_consent_and_native_packages&DisplayName~docx&DisplayName~create&DisplayName~allowed'
```

Both stores fail before tool execution: message admission returns `permission_denied`, so the old fixture cannot read `runId`. The fixture's executor role lists Agent/Session/workspace permissions but omits `spaces.read`, required by `TenantSpaceContextResolver.CurrentAsync`. To classify this, a temporary `git archive 825d121f` source export was restored and the same two cases executed with only an added response-diagnostic assertion in its test. Both baseline cases return the identical `permission_denied`. This is a pre-existing Office fixture qualification gap, not a failure introduced by the three stabilization changes. No product permission check was weakened and no broader Office repair was undertaken. These two cases are **failed**, not counted among the passing stabilization checks; Office tool execution remains unverified by this probe.

Evidence: `assistant-stabilization-native-fixture.log`, `assistant-stabilization-native-fixture-diagnostic.log`, `assistant-stabilization-baseline-restore.log` and `assistant-stabilization-baseline-native.log`. The baseline test command additionally used the same .NET build flags and `--no-restore`; its restore used `dotnet restore tests/AgentCore.Api.Tests --ignore-failed-sources -p:NuGetAudit=false` in the temporary export.

## Native browser failure and recovery

Playwright MCP used the task-owned disposable SQLite Synthetic API at `127.0.0.1:5098` and Vite at `127.0.0.1:5189`. No user database, provider account or installed deployment was modified.

- Added a temporary SQLite trigger rejecting managed identity insertion in `/private/tmp/agent-core-assistant-mcp/store.db`.
- The actual organization-create route still returned 201 for tenant `01a18101-c2b1-78b8-98d5-ba899ca74002`. Its Chat showed **Default assistant setup pending** and the owner retry action, with Send disabled.
- Retrying while the storage failure remained returned truthful pending feedback; no default or conversation was fabricated. The native 320px and 1440px views were inspected together. At 320px, document width stayed 320px and the control/links/error copy wrapped within the existing picker.
- Removed the temporary trigger while the same host remained running, then clicked **Retry assistant setup** again. The picker selected/focused General Assistant; readback showed exactly one managed v36 Instance, `01a18103-4779-717e-8650-49ea4d3aff83`, and one management row.
- Sent `Hello after the storage failure was fixed. Verify normal chat.` through the visible composer. Saved conversation `c309f938-f84d-4991-bdcc-b92ad21ef614` completed with `Hello from synthetic.` through the ordinary Session/Run path.
- Browser console inspection found zero errors/warnings for the journey. Network evidence records successful tenant creation, pending/ready retry and subsequent conversation calls. The expected server-side trigger failures remain in the isolated host log; the trigger was removed.

Evidence is retained locally under ignored `local/verification/general-assistant-stabilization-2026-10-28/`: selected command logs, `setup-pending-320.png`, `setup-pending-1440.png`, `recovered-conversation.yml`, `console.txt` and `network.txt`.

## Review boundary and status

The three findings are addressed for this focused local scope. The separate pre-existing Office fixture gap above remains explicitly unqualified. Original managed reset/adoption/persistence checks remain green. The owning ports, frontend, protocol, operations and cutover documentation describe the revised behavior. Markdown relative-file/fence checks and all 33 complete JSON examples passed; `git diff --check` is clean. No new storage schema, execution mechanism, permission bypass or broad refactor was introduced. Existing Impeccable sidecar/orphaned-brief drift remains outside this stabilization.

Live providers, production-scale/multi-process load, physical voice, manual screen-reader/browser-zoom qualification and deployment remain unverified, as in the original ledger. The version-only fixture updates were not used as a reason to run every unrelated historical suite. Stop General Assistant refinement after these corrections; Workflow remains unstarted.
