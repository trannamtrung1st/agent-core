# Agent self-management and configuration assistance verification

Date: 2026-10-11. Reviewed baseline: `4c05de7f8add60504f4eeba933925c36146492c9`. Implementation is an uncommitted successor on the current checkout; source identity is recorded below. **Acceptance remains open.** Synthetic/local checks establish the implemented paths; automatic safe fresh-Run continuation and the required real tool-capable model UAT are outstanding. No prior frozen milestone is reopened and P10/P11 remain unopened.

## Architecture, owners and migration

One effective grant resolves `AgentDefinition.SelfManagement` plus nullable fields in the existing `AgentInstance.HarnessManagement` record. Legacy non-null policies without `Overrides` retain their precise old scopes; no Settings/Skills/Resources/Connections authority is inferred. Fresh defaults are Off. Freeze is an Instance-local lock retained across version adoption. Enum readers accept old numeric JSON; new writes use names. General Assistant v23 is a new immutable seed; historical versions are untouched. No parallel configuration store, generic Admin proxy, scheduler or Skill engine was added. No user database was reset.

Admin and Chat share Settings, Skill, Resource, Harness and Credential application owners. Chat adds current/admitted management scope, existing capability/host authorization, attached owner-turn provenance and action-bound approval. Instance revision fences policy, settings, item writes and credential binding changes; binding audit and owner revision commit atomically in both stores. Run configuration pins settings/catalog/resources and credential binding identities/revisions. New state resolves only on a new Run; live freeze/revocation denies management effects immediately.

Core-authored procedures use the existing pinned Skill catalog: `builtin:configuration-assistance.v1`, `builtin:harness-authoring.v1`, read-only and OnDemand. Definition defaults and nullable enabled overrides determine stored choice; Off/Frozen suppresses projection. Loading know-how never grants authority. Initial unrelated requests omit management procedures/schemas; generic capability discovery loads relevant administrative interfaces lazily.

## Schemas, risk and surfaces

`GET/PUT /api/v2/admin/agent-instances/{id}/self-management` returns/updates source, effective/default policy, sparse overrides and revisions. Identity & version → Settings owns the one policy panel; the old Automation Harness deep link migrates there. Definition Settings edits operator defaults. Built-ins reuse the Skill table/drawer. Shared Harness promotion/evidence remains reviewed separately.

Agent tools are `configuration.inspect/propose`, `management.inspect/policy.update/freeze`, `settings.list/inspect/patch`, `resources.list/inspect/upsert/set_enabled/delete/copy`, `credentials.binding.propose/remove`, and existing administrative `skills.*`. Settings patch example:

```json
{"expectedInstanceRevision":3,"section":"conversationPolicy","set":{"responseLength":"concise"},"clear":[]}
```

Inspection options bind owner, Instance/policy revisions, section and semantic operation; arbitrary/stale options fail. Recursive strict schemas reject duplicate/unknown nested fields, types, bounds and invalid arrays. Owning services retain field constraints, dependencies, content/hash/media/path/secret and capability validation.

| Actual change | Managed | Assisted / hard floor |
| --- | --- | --- |
| Granted safe read | Read | Read; no protected values |
| Response length/language/one-question preference | Auto only explicit ConversationPreferences delegation | Exact approval |
| New bounded text resource | Auto only explicit TextResources delegation and provenance | Exact approval |
| Skill administration | Auto only explicit SkillChanges delegation, requirements/dependencies/revisions | Exact approval |
| Memory, capability, trigger, model/cost, credential, management grant | Exact approval | Exact approval |
| Provider secrets, OAuth/MFA, host policy, Definition-denied authority | Owner setup destination | No agent setter |
| Freeze | One exact decision, immediate revocation | Only Admin unfreezes |

Preview uses complete bounded before/source/after, target, revisions, next-Run timing and consequences. Credential preview/audit uses safe metadata/IDs only. Reject/stale actions do not commit. Configuration approval remains distinct from approval of the original sensitive task.

## Executed evidence

Full frontend run: `pnpm --dir web exec vitest run --maxWorkers=2 --testTimeout=60000`: **112 files / 909 tests passed**. Reduced workers avoid machine contention observed during concurrent broad runs. Afterwards, the final Definition defaults/self-management panel tests passed **6/6** and `pnpm --dir web build` passed. Existing Vite large-chunk and SignalR annotation warnings remain.

Focused API run: `dotnet test tests/AgentCore.Api.Tests --filter 'FullyQualifiedName~SelfManagement|FullyQualifiedName~AgentDefinitionDraftDiff'`: **13 passed / 1 opt-in live test skipped**. A final follow-up after correcting Built-in Core provenance ran SelfManagement + AgentInstanceSkill integration tests: **17 passed / 1 opt-in live skip**; Skills UI **7/7** and final build passed. It covers both stores, freeze/adoption/reopen, same settings/resource owners, stale mutation/option denial, lazy prompt/tool absence, strict schemas, credential exact approval/redaction, next-Run binding pins and audit. Final full .NET run used `dotnet test AgentCore.sln --no-restore`: Domain 195 passed, Application 1,668 passed / 8 opt-in skips, API 470 passed / 3 opt-in skips, OrderEvents 4 passed; Infrastructure 1,172 passed / 9 opt-in skips. Total: **3,509 passed / 20 opt-in skips**, no failures. The later focused blocker regression additionally passed 3/3, exercising both actual Instance selection restriction and Definition denial without granting either tool.

Browser command used an isolated two-host copy of the standard Playwright configuration (same project/use settings, list reporter), avoiding unrelated STT/browser provider hosts:

```sh
CI=1 PLAYWRIGHT_API_PORT=5097 PLAYWRIGHT_WEB_PORT=5187 PLAYWRIGHT_FIXTURE_PORT=5101 PLAYWRIGHT_SQLITE_PATH=/private/tmp/agent-self-management-e2e-final2/synthetic.db PLAYWRIGHT_FAITHFUL_MANUAL=1 pnpm --dir web exec playwright test --config playwright.self-management.local.config.ts self-management.spec.ts instance-skills.spec.ts scoped-instance-configuration.spec.ts resource-management.spec.ts approval-flow.spec.ts p97-harness-management.spec.ts --project=synthetic --project=p97-harness
```

**22/22 passed**. The temporary runner file is removed after verification; its complete customization is `defineConfig({...standard, webServer: standard.webServer.slice(0,2), reporter:'list'})`. Recreate it beside the standard config to reproduce these isolated ports, or use the standard config with all required host ports available.

Observed journey: fresh inherited Off → Admin Managed Settings/Skills/Resources with only ConversationPreferences auto grant → brief preference saved without approval → sensitive memory change rejected, then fresh exact request approved → same Session new Run readback → one approved Freeze → ordinary Hello succeeds → management inspection unavailable → explicit Admin unfreeze retains choices. Existing Skill create/load/customize/upgrade, resource ownership/hash/delete conflict, settings refresh/conflict, Harness knowledge/instructions/tool approval and stale approval recovery also passed.

Playwright MCP independently exercised the running disposable Synthetic app, the exact approval modal and Admin confirmations, mode keyboard focus/Escape, and document overflow at 1440×900, 768×900 and 390×844. Screenshots were inspected: [desktop](evidence/self-management/desktop.png), [tablet](evidence/self-management/tablet.png), [mobile](evidence/self-management/mobile.png). Host restart retained Managed policy and Instance revision 6. Built-in inspection had Core v1 provenance, procedure content and no customize/delete actions ([inspection](evidence/self-management/builtin-inspection.png)). Canonical docs and Impeccable context/design/surface briefs were synchronized; incumbent Ant Design v6 tokens, shared panels, collection drawers and confirmations were retained. Component accessible labels and focus were exercised; no separate screen-reader application audit is claimed.

## A01–A20 disposition

| ID | Evidence / status |
| --- | --- |
| A01 | API/service InMemory + SQLite, UI Off→Managed, Assisted component/API paths passed |
| A02 | Sparse per-field resolver, reset, explicit false, actual Definition adoption and SQLite reopen freeze passed |
| A03 | Captured initial prompt/tool absence, Off/Frozen discovery silence and live execution revocation passed; exhaustive resumed-Run captured-request matrix remains open |
| A04 | Unrelated captured request omits management procedures/schemas passed |
| A05 | Synthetic explicit discovery passed; real blocker + Built-in load/continuation UAT remains open |
| A06 | Shared Settings service/readback/source/revision and Chat/UI passed |
| A07 | Existing Skill browser/integration regressions, explicit additional authority and independent skills.load passed |
| A08 | Shared resource parity/pins/reopen, existing content/path/media/dependency tests and browser ownership/conflicts passed |
| A09 | Managed preference auto-save; Assisted/sensitive exact approvals and policy matrices passed |
| A10 | Settings sensitive floor parameterization plus credential and management exact approval passed |
| A11 | Safe existing-credential metadata, exact bind, no secret preview/output/audit, new Run pin and no repeated write passed; full conversational missing-secret journey remains open |
| A12 | Implemented truthful Definition/Instance/host blockers and owner destinations; comprehensive browser/email/model blocker journey remains open |
| A13 | Existing durable approval/recovery suite plus new stale CAS and consumed-action mutation checks; complete new-tool expire/cancel/restart matrix remains open |
| A14 | Immutable configuration/resource/binding pins and same-Session new Run readback; live freeze/revocation tests passed |
| A15 | Deliberate fresh owner-turn fallback is implemented; automatic fresh safe continuation with pending-task identity, delayed approval and uncertain-effect replay test remains open |
| A16 | Full Synthetic Chat/Admin journey, same-owner service, SQLite reopen and adoption freeze passed |
| A17 | Attached/detached origin and owner scoping enforced; existing untrusted-source/foreign-owner regressions retained; broader new-tool injection matrix remains open |
| A18 | Existing selected shared promotion/other-Instance isolation regression retained; no automatic shared publication path introduced |
| A19 | Preserved stale drafts/errors, approval/rejection/readback, 3 widths and keyboard checks passed; external screen-reader review unrun |
| A20 | Canonical/design owners synchronized; exact final behavior commit SHA/hosted acceptance pending |

## Outstanding acceptance and owner-only exceptions

The implemented safe continuation fallback asks the owner for a deliberate new turn after a commit. It does not schedule an automatic new Run, infer a pending-task identity or replay original input. This is a deliberate safety limit permitted by the failure invariant, but the mandatory automatic A15 journey is not accepted. Host-only configuration, secrets/OAuth/MFA, shared publication/adoption and unsupported typed operations remain explicit owner interactions.

The prepared `SelfManagementLiveTests` is default-skipped. Automatic approval review rejected enabling OpenRouter UAT because it would send test prompts/configuration metadata to `https://openrouter.ai/api/v1/` using the protected development key without specific authorization for that payload/destination. An authorization question is pending. No live request was sent. Approved bounded UAT would send disposable Instance prompts, synthetic messages, tool schemas, safe metadata and tool results; no key values or repository source. Do not retry until authorization arrives.

Acceptance is not complete; no full proposal closure, hosted CI result, production migration run or external action quality is claimed.
