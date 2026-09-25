# P7D — Managed instance, persona, and managed chat

**Status:** gate candidate (W04 slice; cumulative product approved through review-0024 at `cad4dab`; slice gate approval pending)

**Baseline:** `e25cd4650699ae4ec1e7ddff8a06abcfbde53567` (post-P7C W03 gate)

**Gate HEAD:** `faac25ca29a6d35fafa89dc989a3e57ec8d44154` (W04 slice gate documentation on product `cad4dab`)

**Review ranges (incremental):** storage/API foundation `0e1621b`; lifecycle/persona admin APIs `6b615ce`; pinned persona revision `acf28f2`; Admin managed controls `2f55fe9`; managed new-chat inventory `90ced7c`; archive admission tests `07ae0ce`; P7D Playwright + `session.ready` pinned persona `cad4dab`

## Scope delivered (observed)

- `AgentInstance` aggregate `Revision`, `PersonaRevision`, and `Archived` lifecycle with migrations `20260925101355_P7ManagedAgentInstance` and `20260925101918_P7ManagedAgentInstanceRevisionToken`; compatibility rows unchanged.
- `IAgentInstanceService` managed create, revision-protected version reassociation, persona update, archive/unarchive; `SessionManager.CreateForInstanceAsync` rejects archived instances; session snapshots pin `PinnedPersona` and `PinnedPersonaRevision`.
- Owner Admin APIs and UI: instance inventory, effective config (instance/persona revisions), Form|JSON persona editor, version apply, archive; dirty-persona confirm on destructive actions.
- User new-chat: `GET /api/v2/agent-instances` (active managed only); identity picker groups managed vs legacy; inventory loading gates send/create; v2 create by `agentInstanceId`.
- Trigger/schedule admission denies non-active managed instances at create and due execution; occurrence routing regression uses revision-protected version reassociation.
- `session.ready` agent descriptor projects pinned persona name/role for managed sessions (historical identity in reopened chats).

## W04 verification (slice gate)

| Check | Command | Result |
| --- | --- | --- |
| Managed instance application | `dotnet test tests/AgentCore.Application.Tests --filter FullyQualifiedName~AgentInstanceTests` | Pass (9) |
| Trigger archive + routing | `dotnet test tests/AgentCore.Application.Tests --filter "FullyQualifiedName~TriggerDurablePolicyTests\|FullyQualifiedName~TriggerOccurrenceRoutingTests"` | Pass (23) |
| Session lifecycle API | `dotnet test tests/AgentCore.Api.Tests --filter FullyQualifiedName~HealthAndSessionLifecycleTests` | Pass (11) |
| Admin API (managed path) | `dotnet test tests/AgentCore.Api.Tests --filter FullyQualifiedName~AdminApiTests` | Pass (33) |
| Admin UI unit | `cd web && pnpm exec vitest run src/features/admin` | Pass (27) |
| P7D browser journey | `cd web && CI=1 pnpm exec playwright test e2e/z-admin-managed-instance-journey.spec.ts --project=synthetic` | Pass (1) |
| W04 combined browser gate | `cd web && CI=1 pnpm exec playwright test e2e/admin-shell.spec.ts e2e/z-admin-resource-journey.spec.ts e2e/z-admin-managed-instance-journey.spec.ts --project=synthetic` | Pass (4) |
| Strict frontend build | `cd web && pnpm run build` | Pass (gate) |

## Acceptance mapping (P7D)

Criteria follow the frozen P7D contract (`p7d-managed-instance-identity.md` §5).

| ID | Requirement (summary) | Evidence |
| --- | --- | --- |
| AC-P7D-01 | Create managed `Compatibility=false` instance from exact published version | `POST /api/v2/admin/agent-instances`; `AdminApiTests`; `z-admin-managed-instance-journey.spec.ts` |
| AC-P7D-02 | Initial Persona from definition Identity | `AgentInstanceService.CreateAsync` seeds persona; `AgentInstanceTests` managed create |
| AC-P7D-03 | Managed new-chat by instance id pins current Persona and exact ActiveVersion | `SessionManager.CreateForInstanceAsync`; v2 `agentInstanceId` create; journey managed chat steps |
| AC-P7D-04 | Legacy compatibility new-chat path still works | `SessionManager.CreateAsync` + compatibility instance; `AgentInstanceTests.Two_instances_keep_history_when_one_definition_moves_forward` |
| AC-P7D-05 | Managed instance never auto-upgrades when a newer definition exists | Managed rows hold explicit `ActiveVersion`; no auto-follow on publication; `Managed_session_pins_persona_revision_across_later_edits` |
| AC-P7D-06 | Explicit version change preserves InstanceId, Persona, memory/trigger owner | `UpdateActiveVersionAsync` / `ReassociateActiveVersionAsync`; `TriggerOccurrenceRoutingTests` revision-protected reassociation |
| AC-P7D-07 | Persona Form and JSON round-trip same valid data | Admin Form\|JSON tabs shared validator; journey JSON save; `InstanceManagedControls` Vitest |
| AC-P7D-08 | Stale persona revision conflicts | `UpdatePersonaAsync` expected revision + `ExpectedPersonaRevision`; `AdminApiTests` / application conflict tests |
| AC-P7D-09 | Old session keeps pinned persona after later edit | `PinnedPersonaRevision` on snapshot; `PublicHistory.FromSnapshot` on `session.ready`; journey reopen header assertions |
| AC-P7D-10 | New managed session sees new persona | Second managed chat in journey after persona save |
| AC-P7D-11 | New instance does not inherit another instance's learned memory/triggers | Each managed `CreateAsync` allocates a distinct `InstanceId`; P4/P5 owner keys remain session/instance-scoped (no copy-on-create). **Note:** W04 gate does not add a dedicated two-managed-instance memory/trigger isolation regression beyond existing owner scoping. |
| AC-P7D-12 | Archived instance rejects new managed session | `SessionManager` archived guard; `Archived_managed_instance_rejects_new_session`; journey archive → create denial |
| AC-P7D-13 | Archived instance rejects new triggered/headless execution | `TriggerDurablePolicyTests.Archived_managed_instance_denies_schedule_create_and_due_admission` |
| AC-P7D-14 | Archiving does not delete completed history/memory/trigger history | Archive updates lifecycle only (`SetLifecycleAsync`); journey asserts session histories remain visible after archive; no purge hooks on archive |
| AC-P7D-15 | Archiving does not silently cancel accepted/running P6 work | Lifecycle mutation does not call WorkItem cancel/suspend (P6 freeze semantics). **Gap:** no W04 automated test proving in-flight WorkItem survives instance archive. |

## Canonical documentation (updated with this slice)

- Interfaces: [04-backend-interfaces.md](../04-backend-interfaces.md#p7d-managed-instances-observed)
- Backend: [12-backend-implementation-spec.md](../12-backend-implementation-spec.md#p7d-managed-instances-observed)
- Persistence: [15-persistence-and-configuration.md](../15-persistence-and-configuration.md#p7d-managed-instance-store-observed)
- Protocol: [14-api-and-realtime-protocol.md](../14-api-and-realtime-protocol.md) — agent-instances list, Admin persona/lifecycle/version PATCH, session view pins, `session.ready` persona projection
- Frontend: [13-frontend-implementation-spec.md](../13-frontend-implementation-spec.md#admin-area-p7d-observed)
- Testing: [16-testing-strategy.md](../16-testing-strategy.md) — P7D observed matrix
- Roadmap: [08-development-roadmap.md](../08-development-roadmap.md) — W04 gate candidate
- Implementation plan: [18-implementation-plan.md](../18-implementation-plan.md) — W04 gate
- Observed status: [TODO.md](../../TODO.md) — P7D section (gate candidate, not slice-approved)

## Deferred to later P7 slices (not W04 gaps)

- Scoped memory/automation Admin (P7E).
- Validation/eval publish gate (P7F).
- Admin history rollback catalog (P7G).

## Hosted CI

Pending: no workflow URL on exact gate candidate SHA until pushed to `origin`.
