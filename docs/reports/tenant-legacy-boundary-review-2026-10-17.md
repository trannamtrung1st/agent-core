# Tenant legacy boundary review — 2026-10-17

Scope: review and fix the retained Instance boundaries after `2a4f48dd`. No UI, schema, provider or deployment changes. Existing user data and Real hosts remain untouched; all execution uses isolated test data.

## Findings and changes

1. Both credential stores returned tenant-owned binding rows through unscoped lists. SQLite raw bind/unbind could address an owned Instance; bulk binding cleanup in both stores lacked the ownership guard. Lists now filter actual ownership, including archived identities; raw mutations reject owned targets inside the Instance lock or SQLite transaction. Public binding counts follow the filtered list. Hidden rows continue to protect shared Credentials against deletion.
2. InMemory deletion-receipt recovery treated an owned Instance hidden by `FindAsync` as deleted. The filesystem adapter made the same mistake in both modes. Recovery and direct purge now consult `IAgentInstanceStore.HasBackingInstanceAsync`, which includes every retained backing row and returns no configuration. SQLite receipt recovery already used real backing rows. Existing cleanup of genuinely deleted legacy Instances still works.

The tests construct valid owned graphs with existing children to verify exclusion independently of creation order; they do not introduce a migration or live-data adoption workflow. Secret protection remains unchanged. No tenant credential access or `credentials.use` authority is inferred from an Instance binding.

## Runtime verification

Before the fixes, the binding workflow failed on both stores because its global list contained the hidden row. The workspace workflow reproduced erased exact bytes during InMemory recovery, then independently reproduced direct physical purge of owned content on both stores. After the fixes, the six focused cases passed: two binding workflows and four recovery cases, including owned and unowned roots. The final binding scenario additionally archives the owned Instance and checks that exclusion and mutation denial remain effective.

Final related regressions: **103 passed, zero failed/skipped**. The six focused reproduction cases are included in that total rather than counted again.

All commands used `dotnet test <project> --filter '<filter>' --no-restore --nologo --disable-build-servers -p:UseSharedCompilation=false -m:1 -v minimal`:

| Project under `tests/` | `FullyQualifiedName~` filter terms joined by `\|` | Result |
| --- | --- | --- |
| `AgentCore.Infrastructure.Tests/AgentCore.Infrastructure.Tests.csproj` | `IamIdentityIntegrationTests`, `SystemCredentialTests`, `BrowserCredentialSinkTests`, `AgentWorkspaceDeletionTests`, `AdminLifecycleDeletionTests`, `AdminLifecycleConcurrencyTests`, `AgentWorkspaceStoreTests`, `CanonicalWorkspaceFilesystemTests` | 86 passed |
| `AgentCore.Application.Tests/AgentCore.Application.Tests.csproj` | `AdminReadServiceTests`, `TriggerDurablePolicyTests` | 14 passed |
| `AgentCore.Api.Tests/AgentCore.Api.Tests.csproj` | `CredentialApiTests`, `TenantAgentAccessTests` | 3 passed |

The HTTP cases exercise Synthetic credential create/bind/rotate/disable/unbind/delete with no value readback, anonymous/invalid-input denial, and the canonical owned Instance lifecycle/scoped-read journey. Infrastructure cases additionally verify real protected-store behavior, failed deletion rollback and successful post-commit retry/sweep with temporary SQLite and real temporary workspace bytes.

Documentation hygiene passed: **6 Markdown files, 366 local links/fragments, 5 complete JSON examples**, balanced fences and `git diff --check`. VSTest initially failed to bind its local communication socket inside the sandbox; the same focused command ran with approved local execution. An initial test-only reference to `AgentInstance.Identity` was corrected to the actual model before execution. No hosted CI or full milestone suite was requested or run. No frontend changed, so no browser or visual regression was required for this checkpoint.

## Remaining goal obligations

Canonical tenant credential ownership/CRUD, typed binding/use authority, Account/session/tenant-bound OAuth, tenant Sandbox/decommission, other owning resource/effect families, complete Instance/access/preview UI, Session/Run/Hub/tool/background tenancy, trusted executor/requester/root lineage, durable approvals, obsolete authority removal and final schema/reset consolidation remain required. This review checkpoint does not qualify full tenant isolation or milestone acceptance.
