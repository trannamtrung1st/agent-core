# Tenant Instance Sandbox selection verification — 2026-10-18

This successor to `43514408f415aaad05bc2a7b077da850d8d26386` advances Phase 4 configuration over the canonical tenant catalog and disposable verification. Instance setup reads and sparse selection/script/limit/reset writes now use their existing Application owners and actual original-human Instance/security/audit transaction. It does not accept effective Instance setup verification, Session Sandbox execution/mounts/cleanup, the Sandbox UI, a milestone or the full proposal. Other resource/effect families, Governance/platform, voice/rich Chat, live input arbitration, autonomous origins and final obsolete-authority/schema consolidation remain open. The prior SQLite preference reliability follow-up remains active in the [delivery ledger](multitenant-iam-delivery-2026-10-16.md).

## Implemented boundary

Canonical `GET /api/v2/tenants/{tenantId}/agents/{instanceId}/sandbox` returns safe exact Instance witnesses/lifecycle, current readable Definition-authorized profile metadata/availability and the shared settings section. It runs through the original-human read facade, repeating current Account/session, agent read and security/Instance witnesses after profile/source I/O. Agent Sandbox read and exact profile read remain independent. Definition IDs never fall back to a global profile catalog. Partial readers receive no hidden profile metadata, selected defaults, ceilings or script information. After narrowing to a readable effective set, they can inspect saved setup with inaccessible inherited default/constraint IDs redacted and `completeCatalog: false`.

Existing canonical `PATCH .../settings/sandboxPolicy` now uses the same strict sparse set/clear owner as other settings. Current agent read/update and Sandbox read/manage are separate from exact profile read; Sandbox target authority is checked before stale witnesses. Allowed choices only narrow the Definition set. Every effective allowed profile caps shared explicit limits, including a smaller non-default profile. Explicit/changed default, newly allowed profiles and reset to a different default require current full compatibility and Ready private host. Removing unavailable choices or narrowing limits remains possible. Profile/source/evidence preparation is followed by the existing Instance/security CAS and original-session commit check; concurrent canonical changes cannot save a captured selection.

No new migration, backing resource, grant or execution engine is introduced. Only existing settings overrides change, with the existing tenant security revision and audit. Save/reset never dispatches verification, provisioning or initialization. Reusable profile compatibility and effective Instance setup verification remain distinct. The tenant tool guard still denies unconverted effect kinds; this configuration checkpoint does not enable runtime Sandbox execution. The frontend editor retains its unavailable section until effective setup verification/runtime and its supported UX are integrated.

## Focused local checks

| Check | Result |
| --- | --- |
| Infrastructure selection/settings/editor/adoption/CAS regression | **26 passed**, 27.7291s: 8 new selection cases and 18 existing shared-owner cases |
| Canonical setup and shared settings/editor/atomic-human HTTP regression | **4 passed**, 4.3887s: 1 new complete journey and 3 existing regressions |
| Production API build | Passed in 1.17s, zero warnings/errors |
| Documentation | 1338 local links across 14 changed/new Markdown files; zero missing targets/anchors and balanced fences; `git diff --check` passed |

There are **30 distinct passing cases**. Earlier two/six/eight-case Infrastructure and one-case HTTP runs are subsets, not additional distinct cases. All test processes completed and WAF fixtures were disposed. The production build and documentation checks passed.

Commands run from the repository root with `--no-restore --nologo`, tests using normal console verbosity:

```sh
dotnet test tests/AgentCore.Infrastructure.Tests/AgentCore.Infrastructure.Tests.csproj --filter 'FullyQualifiedName~Tenant_instance_sandbox_selection_|FullyQualifiedName~Tenant_instance_sparse_settings_|FullyQualifiedName~Tenant_settings_mutation_|FullyQualifiedName~Tenant_instance_editor_|FullyQualifiedName~Tenant_instance_configuration_|FullyQualifiedName~Tenant_adoption_|FullyQualifiedName~Tenant_persona_cas_'
dotnet test tests/AgentCore.Api.Tests/AgentCore.Api.Tests.csproj --filter 'FullyQualifiedName~Owned_instance_sandbox_HTTP|FullyQualifiedName~Owned_instance_sparse_settings_|FullyQualifiedName~Editor_witnesses_|FullyQualifiedName~Agent_settings_write_rejects'
dotnet build src/AgentCore.Api/AgentCore.Api.csproj --no-restore --nologo
```

Observed workflows:

- Both actual stores: create two owned reusable profiles, record explicit full verification through a scripted adapter, author/publish their allowed Definition policy and create an Instance. Read coherent inherited setup, narrow/select the second profile, save smaller CPU and Instance initialization text, inspect accurate source/default fields, reopen SQLite and retain the sparse values. Reset restores inherited values. A ceiling above the smaller non-default profile fails without settings/security/audit mutation. Changing default to the second profile and then disabling the first makes reset back to the disabled inherited default fail; narrowing away from it still succeeds.
- Give a minimal member exact agent read/update/Sandbox read/manage and only one profile read. The setup returns that readable profile and no hidden selected metadata/default IDs. Narrowing to the readable profile succeeds, and the saved section is inspectable with inherited hidden choices redacted. This grants no `sandbox.execute`. Revoking the agent assignment denies read/write before stale target revision reporting and leaves security unchanged.
- Reject explicit unverified selection. After reusable verification, simulated private-server outage blocks selection and reports ServerUnavailable, while narrowing CPU remains possible. A Definition containing a same-Account foreign tenant profile cannot expose or bind it; explicit narrowing to the owned readable profile recovers. Global-shaped IDs fail without lookup fallback or saved mutation.
- Retire the original session during profile availability I/O on read: no captured setup is released. Retire it during write preparation: no Instance/security/audit change. Change the reusable profile after captured eligibility: security CAS rejects the old write, preserving the Instance. Explicit new-session/current-source verification then permits one selection commit.
- Actual Synthetic HTTP: create a profile and owned Definition/Instance, inspect NeedsVerification, refuse selection without a receipt/security write, explicitly verify using a controlled Synthetic adapter, observe Eligible, save selection/CPU/script, reject stale replay and reset sparse values. The adapter call count stays one across save/reset, so these actions do not reverify or run a script. No-store is preserved; anonymous, legacy owner-only and same-Account foreign tenant reads deny. Existing generic settings, editor witnesses and original-human atomic commit regressions pass.

## Review and limits

The first fixture compilation used an invalid memory-store method; that unrelated assertion was removed before the first executed flow. Review tightened default reset eligibility, all-profile ceilings and partial catalog projection. A narrowed readable setup no longer remains needlessly unavailable because an unused inherited choice is hidden, while its hidden default/constraint IDs remain redacted. Existing global-shaped profile rejection is preserved as a safe unavailable error.

Tests use actual Application services and InMemory/SQLite/HTTP boundaries. Reusable successful verification and host readiness are controlled Synthetic fixtures; no live container or script compatibility is claimed. The actual disabled-host/SDK failure and cleanup paths have [preceding verification evidence](tenant-sandbox-verification-2026-10-18.md). No frontend source/presentation changed, so no browser/Impeccable acceptance is claimed. No live provider/registry credentials, Docker server, hosted CI, full local CI suite, production deployment, user-data reset or Real-host restart occurred. Effective Instance verification, runtime scopes/leases/mounts/process/reset/cleanup, Sandbox UI and all other remaining proposal obligations stay active in the [boundary inventory](multitenant-iam-boundary-inventory-2026-10-16.md#instance-sandbox-configuration-successor--2026-10-18).
