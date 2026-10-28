# Built-in General Assistant delivery — 2026-10-28

The accepted proposal is implemented through the existing Definition, tenant IAM, Instance configuration, private Space, Chat and Run owners. Global immutable `general-assistant` v36 is the product default. Each active tenant receives a distinct protected managed Instance; ordinary customer clones retain ordinary lifecycle and manual version adoption. Workflow remains the next unstarted initiative.

## Source and checkpoints

The initial source audit and design brief used clean `main` at `25db1f9755b56ebd745efcc7090ea1d4db6435ac`. The delivery commit contains this report and the implementation. The verified changed/new code manifest covers 111 files beneath `agents`, `src`, `tests` and `web`; its SHA-256 is `fa0022e488d0d757bb8db54a3aa396f90f30ba9c6c76f939babd5a6f83226a88`. Compute it by sorting repository-relative paths, then hashing each UTF-8 path, NUL, complete file bytes and NUL. Documentation is excluded to avoid a self-referential hash.

| Phase | Delivered checkpoint |
| --- | --- |
| 0 — Source/design | [Pre-code brief](general-assistant-design-2026-10-28.md); existing Ant Design v6 Operate surfaces, ownership, starter grants, reset scope and release admission decided before UI implementation. |
| 1 — Storage/provisioning | Three additive fields, filtered tenant uniqueness, both stores, trusted transaction, bootstrap, both organization application entry points, live system-organization endpoint and paged startup reconciliation. |
| 2 — Definition/adoption | v36 removes demo fixtures, retains six always-projected tools, inherits model setup, defaults Voice off and selects no Sandbox profile. Mandatory instructions plus subordinate tenant addendum; shared compatibility planning; protected lifecycle/version writes; preserved admitted Run pins. |
| 3 — Visibility/reset | Authorization/visibility before search/count/paging; stable managed-first selection; universal visibility; one managed reset transaction, exact witnesses, current human authorization and audit-based operation readback. |
| 4 — Product integration | Existing Chat picker, management table/settings/lifecycle/version surfaces, owner-only setup links, safe errors, draft/busy guards and focus restoration. |
| 5 — Qualification/docs | Focused tests and native Synthetic browser interaction, actual tool outputs, responsive polish, synchronized owning docs and [cutover runbook](../general-assistant-cutover.md). Results and limits follow. |

No parallel runtime, special Chat transport, per-tenant Definition copy, additional table, second UI kit, Group Chat or Workflow implementation was introduced. v35 remains immutable for real historical/ordinary pins; new General Assistant template catalogs propose v36.

## Executed local checks

Backend commands run from the repository root with `--no-restore --nologo -m:1 -nr:false -p:UseSharedCompilation=false`. Frontend commands run from `web`. Local runner isolation uses `CI=1`; no hosted workflow was dispatched or awaited. Logs are retained locally under ignored `local/verification/general-assistant-2026-10-28/` with the names below.

| Command / focused selection | Result | Evidence log |
| --- | --- | --- |
| `dotnet test tests/AgentCore.Domain.Tests` with `--filter FullyQualifiedName~AgentDefinitionValidatorTests` | 17 passed | `general-assistant-domain-release.log` |
| `dotnet test tests/AgentCore.Application.Tests` with `--filter FullyQualifiedName~InstanceSettingsResolverTests` | 8 passed | `general-assistant-application.log` |
| `dotnet test tests/AgentCore.Infrastructure.Tests` with managed assistant, Definition store and decommission selections | 47 passed | `general-assistant-infra-release.log` |
| Final managed-assistant and public discovery regression | 21 passed | `general-assistant-infra-qualified.log` |
| `dotnet test tests/AgentCore.Api.Tests` with managed defaults, ordinary versions/lifecycle, editor, Definition and Session HTTP selections | 9 passed | `general-assistant-api-release.log` |
| `pnpm exec vitest run src/features/access/TenantAgentPresentation.test.tsx src/features/access/TenantAgentLifecycle.test.tsx src/features/chat/AgentPicker.test.tsx src/features/chat/ChatApp.test.tsx src/features/chat/chatIdentity.test.ts src/services/tenantChat.test.ts src/features/access/TenantAgentConfiguration.test.tsx src/features/access/TenantAgentVersion.test.tsx` | 72 passed across 8 files | `general-assistant-vitest-final.log` |
| `pnpm run build` | Passed; existing large-chunk advisory remains | `general-assistant-web-build.log` |
| `pnpm exec tsc --noEmit` on final frontend source | Passed | `general-assistant-ts-release.log` |
| `CI=1 pnpm exec playwright test --config playwright.tenant-chat.config.ts e2e/managed-general-assistant.spec.ts` | 1 passed | `general-assistant-e2e2.log` |
| `CI=1 pnpm exec playwright test --config playwright.tenant-chat.config.ts e2e/tenant-chat.spec.ts` | 5 passed | `general-assistant-e2e-chat.log` |
| `CI=1 pnpm exec playwright test --config playwright.tenant-instance-editor.config.ts` | 2 passed | `general-assistant-e2e-editor.log` |
| `CI=1 pnpm exec playwright test --config playwright.tenant-agent-access.config.ts` | 2 passed | `general-assistant-e2e-access.log` |

Exact final backend filters:

```text
Infrastructure release: FullyQualifiedName~ManagedGeneralAssistantTests|FullyQualifiedName~AgentDefinitionStoreTests|FullyQualifiedName~ChatInventory|FullyQualifiedName~TenantAgentDecommissionTests
Infrastructure final: FullyQualifiedName~ManagedGeneralAssistantTests|FullyQualifiedName~Tenant_chat_discovery
API: FullyQualifiedName~Managed_default|FullyQualifiedName~Version_catalog_and_preview|FullyQualifiedName~Creation_scoped_inventory|FullyQualifiedName~Editor_witnesses|FullyQualifiedName~Author_publish_and_instantiate|FullyQualifiedName~Canonical_Session_HTTP
```

`dotnet ef migrations has-pending-model-changes --project src/AgentCore.Infrastructure --no-build --context AgentCoreDbContext` passed: no model changes since the last migration. EF tools 10.0.5 reported their existing version advisory against runtime 10.0.12; model inspection completed successfully. Evidence: `general-assistant-ef-model.log`.

`git diff --check`, all modified Markdown relative-file checks, 20 added-link/fragment checks and 35 complete JSON examples passed. These focused commands are the selected local acceptance scope; the full solution/CI/browser matrices were not run.

## Observed user and tool journeys

Playwright MCP exercised a separate native SQLite Synthetic host at API `127.0.0.1:5098` and Vite `127.0.0.1:5189`, with disposable roots in `/private/tmp/agent-core-assistant-mcp`. It used the real login, tenant route, ordinary composer and MessagePack/SignalR conversation path:

1. A fresh CLI bootstrap created tenant `01a18086-f8fb-7979-8cb7-f4a6d11b8858`, managed Instance `01a18086-f9b2-77ca-b4f6-6d14279da3f8` and private Space `9bc6b2c1-a97d-447e-9319-8df59b871c54`.
2. Owner sign-in proposed General Assistant. Sending a normal message created saved conversation `04af3b82-3f62-4515-81eb-ae4e938f33c3` and completed with `Hello from synthetic.`
3. Settings hid the managed Instance, then reset it through the current-read confirmation. Authoritative readback retained the same identity/Space and `showInChat=false`; new Chat had zero candidates and the correct no-visible-agents guidance. The saved conversation remained readable and usable.
4. A real private workspace fixture was published with the ordinary authorized `artifacts.create_from_workspace` flow. Run `01a1808c-d4a7-7f24-8c11-5682e0a3c5a6` completed; operation/Artifact `01a1808c-da09-746b-b26b-1de89970c4d7` was allowed/applied. The artifact readback returned `store-review.md`, `text/markdown`, 131 bytes, SHA-256 `1105496e31485516d06bae2f7388c8609b38342ac8a9bc9922c15d9141745381`.
5. Session Files → Artifacts displayed the file and opened its native preview. The rendered heading and body matched the actual bytes. The fixture model's text reports the factual operation JSON; the existing Session Files UI exposes the resulting Artifact.
6. Stopped only this disposable API process and reopened the same SQLite database with the final build. Readback retained managed Instance/revision 3, v36 and hidden visibility; the saved conversation retained both prior exchanges. Sending another message through the composer completed with `Hello from synthetic.`. The recovered page had zero console errors/warnings; network evidence records successful API calls. Expected connection-refused/reconnect errors occurred while the deliberately stopped API was offline.

API runtime integration separately executed `web.search` → `web.fetch` → native private `workspace.read` under the default's starter rights, on both stores. It asserted completed v36 Runs, exact tool evidence, two deterministic public-web provider calls, the actual private file content and zero outbound provider HTTP attempts. This proves native authorization/tool routing with a deterministic public-web port; it does not certify live internet search or real model reasoning.

Repeatable Playwright tests additionally exercised ordinary Chat persistence/rename/archive, actual WebSocket reconnect with no duplicate admitted text, Chat-only revocation, independent approve/reject recovery, editor form/JSON validation and stale drafts, scoped-reader revocation, ordinary Agent Access/lifecycle and the new managed hide/reset/retained-conversation journey.

## Acceptance trace

| Proposal | Executed evidence and observed result |
| --- | --- |
| A01 | Fresh native SQLite CLI bootstrap, actual owner login and completed ordinary browser conversation above. |
| A02 | InMemory API bootstrap plus live system organization creation; both-store user research Runs need no provider credentials. |
| A03 | Four racing provisioning calls, repeated reconciliation, one provision audit, same Instance/principal/private Space; SQLite reopen and filtered uniqueness violation test. |
| A04 | Separate live organization receives another Instance; wrong-tenant reset is denied; exact private-Space grants refuse another Instance's private files. Normal Session ownership is retained. |
| A05 | Ordinary v36 clone has no managed marker and can archive; ordinary decommission regression retains the established cleanup/retirement contracts. |
| A06 | Chat-only member without `agents.read` sees the eligible default; revoking the grant removes search matches/counts immediately. |
| A07 | Both-store empty model catalog reports `no_model_configured`, returns no fabricated route and refuses Session creation; incompatible fixed-model upgrade preserves the override and recovers when the catalog returns. Optional speech aliases are absent while Voice is off; v36 validates without speech setup. |
| A08 | Actual ordinary Chat, deterministic public search/fetch, native private-file read and Artifact publication/preview. |
| A09 | Starter principal denied agent updates, credential use, Browser, Sandbox, delegation, approval and governance rights; other private Space and wrong tenant denied. Existing approve/reject browser journeys pass. |
| A10 | Managed hidden search/count is zero before paging, management still returns the Instance; public discovery tests cover scoped filtering and human/security read fences. Ordinary visibility uses the same command/store/UI. |
| A11 | Hidden saved conversation keeps its history and admits another authorized turn. Store integration verifies a prior Run retains v35 while the next Run uses v36. Visibility never participates in ordinary Run admission. |
| A12 | Last-visible empty browser state, stale cached-selection preflight refusing Session POST, Chat-only revocation and public no-leak filters. No automatic unhide or identity substitution. |
| A13 | Managed lifecycle/manual adoption refused; managed decommission shows ownership protection. Ordinary lifecycle, versions and decommission regressions run separately. |
| A14 | Saved persona/additional instructions and retained local Skill/resource; canonical instructions remain the prefix. Application tests preserve ordinary-clone replacement behavior; existing editor regressions preserve constrained settings semantics. |
| A15 | Both-store v35→v36 system adoption follows inherited persona, preserves hidden visibility and explicit customized persona; ordinary manual version tests remain separate. |
| A16 | Fixed-model incompatibility leaves Instance revision/version/override unchanged, exposes the field-specific upgrade error, blocks fresh admission and succeeds on corrected setup. |
| A17 | Admitted v35 Run retains its pin. Current-resource reads remain possible while release adoption is pending; fresh admission is blocked. After adoption the next turn in the same hidden conversation pins v36. |
| A18 | Hidden reset restores current persona/settings/provenance, retains private Space/assignments, physical file, local Skill/resource and saved conversation/history. The transaction does not touch memory/credential/history stores. |
| A19 | Duplicate reset reads one existing operation audit; stale reset refused, wrong tenant/Chat-only actor denied; browser current-read/cancel/unknown guards are covered. |
| A20 | Managed browser journey at 320/390/768/1024/1440 widths, visibility focus restoration, native confirmation cancel/commit, correct system labels and lifecycle explanation. Desktop/mobile rendered polish inspected directly. |
| A21 | SQLite reopen preserves managed identity, private Space, hidden state and persona provenance; additive populated migration down/up preserves ordinary identity and retirement guards. Failed compatible adoption can retry safely; startup logs tenant-qualified safe reason codes and retries at restart. |
| A22 | Architecture/ports/frontend/protocol/persistence/operations/IAM/design system, README, TODO and implementation plan synchronized; runbook distinguishes installed rollout from local tests. |

## Self-review and final polish

Review traced every tenant creation route, trusted marker mutation, ordinary lifecycle/version boundary, effective-instruction consumer, reset receipt and fresh Run admission path. The live system-organization route was additional to the proposal's older source inventory and is covered. A final correction moved release readiness from shared live-resource resolution to fresh admission, preserving existing admitted Runs. Reset also refuses a host rollback's obsolete target release.

Migration generation initially exposed inherited migration attributes causing EF's ambiguous-attribute failure. The affected historical classes now compose their frozen predecessor target models rather than inheriting attributed migrations; historical SQL remains unchanged. New designer and snapshot metadata were generated by EF. Native SQLite downgrade statements avoid rebuilding tables referenced by retained retirement triggers. The populated additive roundtrip and model parity tests verify the result. Stale ordinary-version and historical migration test fixtures were corrected to their actual supported cutovers.

The batched Impeccable polish checked the actual 320px and 1440px Settings render, native labels, copy, wrapping, confirmation/focus behavior and shared Ant Design tokens. At 320px, document width remained 320px; at desktop, the incumbent 768px detail panel retained 16px padding and 12px gaps. No new CSS/theme was needed. Browser interaction also caught and fixed the missing `system_managed_instance` error-code parser case. Evidence captures: `general-assistant-settings-320.png`, `general-assistant-settings-1440.png`, `artifact-preview.yml`, `reopened-hidden-conversation.yml`, `reopened-console.txt` and `reopened-network.txt` in the local verification directory.

The Impeccable context loader reported existing sidecar drift and two orphaned briefs. They remain outside this feature's scope and can be repaired in a separate context-maintenance task; no automatic context repair was performed.

## Qualification boundaries

This is focused local implementation acceptance, not deployment or a renewed claim for historical milestones. Hosted CI and the full local CI matrix were not requested or run. Live model/search/Email/Drive/Browser/Sandbox services, production multi-process load, physical microphone/speaker behavior, manual screen-reader testing and browser zoom remain external qualification. Voice is off by default and optional infrastructure does not become silently configured. The build's existing chunk-size advisory and test-only Ant Design deprecation messages are recorded in their logs.

Follow the cutover runbook for stopped writers, matching database/files/key backups and deployment. No installed user data was erased. Stop feature refinement after the focused gates pass; Workflow remains unstarted.


## Review stabilization successor

The [focused stabilization](general-assistant-stabilization-2026-10-28.md) removes per-Instance discovery reads, adds owner-triggered in-process setup recovery with committed tenant success and safe diagnostics, and restricts new General Assistant template creation/forking to the current release. Its tests/evidence are scoped to the subsequent source; the original results above remain historical.
