# Tenant Session Sandbox control-read verification — 2026-10-18

Status: focused backend prerequisite over `dd7266047e23995cb399f16083b7b7ab24aa9619`. The full multi-tenant IAM proposal remains active. This supplies a canonical coherent control projection for the tenant Chat Sandbox adapter; it does not enable or accept that UI.

## Implemented boundary

`GET /api/v2/tenants/{tenantId}/sessions/{sessionId}/sandbox/controls` extends `TenantSandboxRuntimeReadService`, returning the existing stored observation, current Instance revision, independent reset/Stop hints and permission-filtered current profile choices. Every choice carries only ID/revision/name/image/source availability and a separate selection hint. Hidden/default IDs and counts are omitted. No administrative Instance read, global source, owner capability or client role inference is needed.

The existing original-human read fence is shared by observation and controls. Current effective policy bounds choices; independent Session read/manage, agent Sandbox read/manage and referenced profile read retain their meanings. The preflight reuses existing pure reset/Stop/selection rules and current reusable source inspection. It never requests verification, SDK I/O, environment creation or a lifecycle mutation. Source eligibility and effective setup selectability are distinct: a lower current source ceiling can leave a reusable profile eligible while prohibiting the saved Instance setup. Cleanup remains independently available.

After source I/O, original login/security, current Instance and Session owner/revision, generation/lease and latest execution identity/revision are checked again. Any mismatch rejects the full captured projection. Stored observation keeps `liveAuthorityConfirmed=false`; hints grant no command or initialization authority. Existing POST transactions still reauthorize exact witnesses and workers separately confirm cleanup. No schema, migration, new effect owner or provider DTO crosses a boundary.

[Backend interfaces](../04-backend-interfaces.md#tenant-session-sandbox-control-preflight) and [Protocol](../14-api-and-realtime-protocol.md#tenant-session-sandbox-control-projection) own the implementation contract. The [Session interaction contract](../19-multi-tenant-iam-ux.md#session-sandbox-controls-interaction-contract) records the future Drawer behavior before UI implementation. [Frontend](../13-frontend-implementation-spec.md#tenant-session-sandbox-control-adapter-prerequisite) explicitly retains the pending integration.

## Verification

| Focused check | Result |
| --- | --- |
| New control-read InMemory/SQLite cases | 14 passed |
| Existing original-human stored-read fence cases | 6 passed |
| Canonical empty/populated HTTP and exact control commands | 4 passed |

Commands:

```sh
dotnet test tests/AgentCore.Infrastructure.Tests/AgentCore.Infrastructure.Tests.csproj --no-restore --nologo --filter 'FullyQualifiedName~Tenant_sandbox_controls_read|FullyQualifiedName~Tenant_sandbox_runtime_read_' -v minimal
dotnet test tests/AgentCore.Api.Tests/AgentCore.Api.Tests.csproj --no-restore --nologo --filter 'FullyQualifiedName~Tenant_Sandbox_HTTP_returns_scoped_stored_observations|FullyQualifiedName~Tenant_Sandbox_command_approval_and_human_controls_HTTP' -v minimal
```

The final 24 focused cases passed. Both projects compiled as part of the test runs. Initial focused control cases passed12/12, then the added reduced-ceiling/disabled-source cases and six existing stored-read regressions completed the final20-case store selection. Final HTTP passed4/4 after populated scenarios consumed returned witnesses.

The first sandboxed .NET invocation failed before compilation with MSBuild named-pipe `SocketException (13): Permission denied`. Normal cancellation did not exit; the identified test parent was explicitly stopped and its handle confirmed terminal before retrying with permitted local test-host/named-pipe access. This setup failure was not counted as test evidence. No source/test edit occurred while a build/test process was live.

## Exercised outcomes

- Empty environment: readable allowed choices/default remain available but all mutation hints are false; the GET does not provision anything.
- Ready owned generation: reset is available, same exact selection is disabled and a verified alternate is selectable. The returned profile/Instance/Session/security/generation/lease witnesses are accepted by the existing profile-switch command; retirement remains pending and no initialization is dispatched.
- Limited reader: an inaccessible default is omitted, only independently readable choices appear, and unreadable stored-profile scope rejects the entire observation. Session management alone cannot reset; adding exact agent Sandbox management enables the hint without `agents.update`. Revoking an alternate removes it.
- Exact active receipt: returned Stop witnesses are accepted once by the existing service. Subsequent reads remove Stop, retain request evidence and show unconfirmed cleanup; no second interrupt starts from the GET.
- Logout followed by replacement login, concurrent security change and concurrent lease change during image inspection reject captured controls in both stores. A fresh authorized read recovers explicitly.
- Current source ceiling below a saved CPU override: source remains independently verified but selection is disabled without failing the read; reset stays available. A later disabled source is labeled Disabled and cannot be selected. The read adds no verifier call.
- Actual HTTP: no-store headers and empty projection shape are checked; anonymous/legacy/foreign scope is rejected. Existing reset, Stop and switch scenarios now read their exact witnesses through the new endpoint before submitting. Stale revisions, extra/forged fields, missing witnesses and unauthorized writes remain rejected; accepted requests retain separate unconfirmed retirement/termination facts.

These are real application/store/WebApplicationFactory integrations with deterministic verification fixtures. They are not live Docker or successful provider execution proof. The control projection performs source authority/verification reads only; no live registry contact or secret payload is exposed.

## Remaining work

The tenant Chat Session Sandbox Drawer, profile/reset/process interactions and responsive/browser verification remain next. Existing organization and Instance Sandbox UI evidence retains its own scope. Other resource/effect families, Governance/platform, voice/rich Chat, live input arbitration, autonomous origins, final obsolete-authority/schema consolidation and the earlier SQLite preference reliability follow-up remain open. No full proposal or milestone acceptance is claimed.

No UI source changed, so no browser/visual acceptance or frontend build was performed for this backend-only prerequisite. No hosted/full local CI, live provider/Docker qualification, deployment, user-data reset/migration or Real-host restart was performed. All test processes are terminal. Changed Markdown link/fragment/fence checks and whitespace validation passed.
