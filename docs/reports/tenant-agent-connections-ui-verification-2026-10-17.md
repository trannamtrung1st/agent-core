# Tenant Instance Connections UI verification — 2026-10-17

Baseline: `4439301c`. This checkpoint connects the existing owned Credential binding API to the canonical tenant Instance editor. The complete proposal remains active; this is focused Phase 4/7 progress, not full proposal or milestone acceptance.

## Delivered behavior

- Instance inventory links to Access and Connections. Scoped Instance navigation, headings and breadcrumbs use exact canonical routes; `/t/{tenantId}/admin/agents/{instanceId}/connections` has no legacy owner-capability fallback. Access retains its independent IAM-inspection gate, so a configuration manager can use Connections without that authority.
- Safe binding pages show the authorized agent name/lifecycle and visible alias/Credential metadata, purpose and status. Partial visibility is explained without hidden names/counts. The existing authorizer computes exact parent-update and per-visible-row removal hints within the same final read fence. Hints grant no authority; mutation services still recheck independent parent/Credential permissions and exact revisions.
- Add binding uses a protected draft Drawer with readable Credential pages, explicitly loaded-page search, fresh selected-Credential metadata/read-bind affordances and the existing alias validation/normalization. Disabled/registry credentials cannot be newly selected for binding; disconnected OAuth may be associated without connecting it. Credential management/consent remains in Integrations. Save posts displayed security/Instance/Credential witnesses.
- Remove confirms organization, agent, alias and Credential, using exact displayed security/Instance/binding witnesses. It preserves the shared Credential and independent grants. Archived/read-only views retain safe inspection without editing controls. Binding grants no execution permission or capability and reveals no protected value.
- Stale/denied failures retain a draft without retrying the mutation. Explicit reload refreshes witnesses and selection authority without changing the alias; lost parent reads clear protected content. Shared command/query hooks fence retired scopes and acknowledged-write recovery. Cancel/discard/navigation use the existing draft guard; cancel restores focus to Add binding, and saved refresh can fall back to the page heading. Save has a stable accessible label through loading/error recovery.

## Executed user journeys

Playwright MCP used a task-owned native Synthetic API/Vite host on `61289/61290` with disposable root `/private/tmp/agent-core-connections-20261017-nkt6z7sd`. Through visible controls, the owner opened Connections, selected a long-named Credential, saved a mixed-case/spaced alias, and observed normalized `primary-operations`. A separate disconnected Synthetic Gmail draft raced a real security change: save returned the expected 409, preserved its selection/alias, and succeeded only after explicit Reload latest and another Save. Exact removal named all targets and removed only the association. Authenticated public reads confirmed the shared Credential remained and Core still denied the agent `credentials.use`. No provider connection/check was started.

Two repeatable Synthetic E2E journeys use separately allocated roots/ports (`61291/61292`):

1. At 320px, clean cancellation returns focus; dirty cancellation offers Keep editing. The user selects an eleventh Credential through actual paging, saves a normalized alias, and Core confirms no automatic use authority. A concurrent security change makes removal conflict; the binding remains until explicit reload/confirmation. The Credential survives removal. Archive makes Connections inspectable without Add/Remove. The page has no document overflow, page errors or legacy authority requests.
2. A member receives only exact agent read/update, exact Credential read/bind and separate read-only Credential access. Hidden unrelated metadata is absent. A readable Credential without bind authority disables Save and explains why. A stale draft retains its alias, reloads without submitting, then saves explicitly. The same manager is denied Access inspection, returns through scoped navigation to Connections, and loses private content after parent-read revocation/reload.

The first scoped-manager E2E attempt timed out after stale-save recovery because the loading icon changed Save's accessible name. The product action now has an explicit stable label; the isolated failing journey passed in 5.3s, then both final journeys passed together. Initial component-test failures were async Drawer/confirmation readiness checks; awaited visibility preserves the original assertions. No test outcome was weakened. Final MCP navigation has zero console errors/warnings; the deliberately induced stale save earlier produced the expected HTTP 409 console entry.

## Focused checks

| Check | Selection | Result |
| --- | --- | --- |
| Infrastructure | Binding lifecycle/authority/write-policy/FK cases plus binding-read freshness and human-session retirement cases | 21 passed, both stores represented |
| HTTP | `Binding_HTTP` | 5 passed, including current hints/archive and in-flight read fences |
| Frontend | `TenantAgentConnections`, `TenantAgentAccess`, `accessRoute`, `useTenantCommand`, `AdminInstanceNavigation` | 23 passed in 5 files |
| Real browser | `pnpm run test:e2e:tenant-agent-connections` | 2 passed in 19.5s |
| Frontend build | `pnpm --dir web run build` | Passed; existing bundler annotation/chunk-size notices only |

Total: **51 distinct passing focused cases**, plus the MCP journey. Backend commands use `dotnet test tests/<project> --no-restore --filter '<FullyQualifiedName~term|...>' --logger 'console;verbosity=minimal' --nologo`. Infrastructure filter terms: `Binding_inventory_never_returns`, `Human_binding_inventory`, `Owned_bindings_share`, `Binding_metadata_and_mutations`, `Prepared_binding_writes`, `Agent_binding_changes`, `Sqlite_binding_composite`. Frontend uses `NODE_OPTIONS=--no-experimental-webstorage pnpm exec vitest run` with the five named test files. E2E adds `TENANT_CHAT_API_PORT=61291 TENANT_CHAT_WEB_PORT=61292` and refuses existing-server reuse.

Final logs are `/private/tmp/tenant-agent-connections-infrastructure.log`, `tenant-agent-connections-http.log`, `tenant-agent-connections-web-tests-final.log`, `tenant-agent-connections-e2e-final.log` and `tenant-agent-connections-build-final.log` under the same temporary directory. Final E2E root suffix is `agent-core-tenant-chat-e2e-5SzeFF`. No full suite, CI, live provider, deployment, installed-data reset or unknown-process shutdown was run. SQLite behavior uses integrated temporary-store cases; the browser host uses InMemory, without a claim of full SQLite browser acceptance.

## Presentation and documentation

The interaction contract in [multi-tenant UX](../19-multi-tenant-iam-ux.md#instance-connections-interaction-contract) and the presentation-only surface brief were written before UI implementation. The existing direct Ant Design v6 dark system, AdminShell, collection layout, Form/Drawer/Select/Pagination, status tag, shared confirmation/draft/command/query owners and 8/12/16px spacing remain in use. No CSS, theme, token, generic wrapper or second UI kit was introduced.

Parent visual work was bounded: one batched inspection of 18 captures, one correction batch (shorter draft guidance, natural-width Reload latest and cleared stale command feedback on explicit reload), then the same confirmation matrix. Final local evidence is `.impeccable/review/agent-connections/final/`: overview and populated draft at 1440×900, 1200×647, 1024×768, 768×1024, 390×844 and 320×740; removal at 390/320; inventory and Access consumers at 1440/320. All 18 final files were opened and inspected. Long metadata wraps, selected names remain separately readable and drawer/footer/confirmation controls stay usable at 320px. Native input/Select text truncation retains their standard editing behavior.

The detector ran once on the four changed product UI sources and returned `[]` in `/private/tmp/tenant-agent-connections-detector.json`. The fresh finish reviewer inspected every final capture and returned **ship**, with no material fixes, in `/private/tmp/tenant-agent-connections-finish-review.md`. The fresh documenter preserved incumbent DESIGN/sidecar and recorded the handoff in `/private/tmp/tenant-agent-connections-design-documentation.md`; no new system rule was needed. Preexisting pending-status tooling context and sidecar provenance/fingerprint drift were reported without repair in this ordinary extension. Canonical docs own current implementation/behavior. All 11 changed Markdown files passed local link/fragment/fence checks, and `git diff --check` passed. Task-owned native API/Vite and E2E hosts were stopped; all four allocated ports were verified free.

## Remaining full-goal work

Remaining Instance configuration/persona/Skills/resources pages, other resource Access panels, Governance/platform administration, voice/rich resources and receipts, live input arbitration, remaining resource/effect families and autonomous ingress, final obsolete authority/schema consolidation and the earlier SQLite direct-preference reliability follow-up remain open. Existing historical checkpoint reports keep their original scope; this successor supersedes Instance-binding-UI-pending statements only for the delivered association editor.
