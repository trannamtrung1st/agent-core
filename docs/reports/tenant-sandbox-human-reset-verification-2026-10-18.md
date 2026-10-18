# Tenant Sandbox human reset — 2026-10-18

## Delivered boundary

The explicit human tenant reset path now records exact retirement consent through the existing lease/security/audit transaction. It requires the original live Account/login/Principal, independent Session read/manage, agent Sandbox read/manage and exact source-profile read. No current source eligibility, private Credential use, model approval or agent execution permission is borrowed for cleanup.

The strict request binds expected security and Session revisions, exact generation and lease revision, and the explicit nullable active-or-latest execution identity. Both stores repeat original-human and scoped-permission checks at commit. Rejected/stale requests cannot modify lease, tenant security or audit. Factual recovery cannot invent or clear reset consent or change selected/switch target identity.

Acceptance is separate from confirmed retirement. An active generation becomes Terminating, its saved selection/switch target clears, and the existing bounded worker performs exact known-handle or negative-generation-fence retirement. Unknown, foreign, pending or changed-host proof keeps reconciliation pending. The worker continues after the initiating login ends, without SDK create, initialization or command replay. Canonical home/working files, initialization evidence, Artifacts, Attachments and an already saved original command/launch outcome remain retained. In-flight process facts become Canceled with possible file effects still explicit.

Already confirmed Terminated failed initialization may record explicit reset while preserving its failure evidence. A replacement still requires fresh exact Review/reservation; consent to reset cannot restart an old action. The protocol is documented in [the canonical API owner](../14-api-and-realtime-protocol.md#canonical-tenant-sandbox-reset).

## Runtime evidence

The representative reset fixtures use actual Definition/Instance/Session/Run/Review services, both InMemory and disposable SQLite, real canonical directories, the installed SDK and authenticated loopback Kestrel. Provider status/deletion/proof responses are controlled.

- Exact known background and unknown-provisioning generations retire after logout and store reopen, with zero create/command replay and retained working/home bytes.
- Foreign actual handle metadata, foreign/pending negative proof and changed host mapping block retirement. Repairing the original scope permits a later worker pass; a further pass does no provider work.
- The saved successful background-launch effect stays unchanged while the process receipt becomes Canceled. An uncertain setup effect stays Indeterminate.
- Raw transactional writes reject logout, replacement login, password rotation and mismatched Principal, with no partial lease/security/audit write; a fresh actual login can recover.
- Independent exact Session and agent manage grants, profile read, revoked authority before stale witnesses and disabled-source cleanup are exercised. A repeated stale reset does not append another reset audit.
- Failed initialization evidence and generation/host identity survive reset; factual attempts to erase consent or replace selection reject. A fresh reviewed replacement reservation has a different generation and a single winning reservation.

Focused owned Sandbox regression:

```sh
dotnet test tests/AgentCore.Infrastructure.Tests/AgentCore.Infrastructure.Tests.csproj --no-restore --nologo --filter 'FullyQualifiedName~TenantDefinitionIntegrationTests&FullyQualifiedName~Sandbox|FullyQualifiedName~TenantSandboxRuntimeMigrationTests' -v minimal
```

**182 passed, 5m35s.** This includes the final 22 reset scenarios, fresh reviewed replacement and disabled-source cleanup, current policy, owned runtime/Review/provisioning/recovery, profile/Instance configuration/verification and actual runtime migration/reopen/constraint checks.

Focused Synthetic HTTP regression:

```sh
dotnet test tests/AgentCore.Api.Tests/AgentCore.Api.Tests.csproj --no-restore --nologo --filter 'FullyQualifiedName~Tenant_Sandbox_|FullyQualifiedName~Owned_instance_sandbox_HTTP|FullyQualifiedName~Tenant_verification_HTTP|FullyQualifiedName~Owned_instance_sandbox_verification_HTTP' -v minimal
```

**7 passed, 7s.** The actual approved-command HTTP setup exercises reset with no command dispatch: wrong execution returns 409; omitted nullable witness and unknown command member return 400; anonymous/legacy calls return 401; foreign tenant returns 403. The exact request returns 202 with canonical Location/no-store, one security/audit update and pending stored environment state. Its stale retry returns 409. Reusable/Instance verification and scoped configuration/runtime observation regressions remain green.

Changed Markdown links/fences and `git diff --check` passed. No frontend source changed, so browser and UI acceptance were not claimed.

## Limits and remaining proposal work

These checks establish actual Core/store/SDK protocol behavior against controlled peers. They do not qualify real Docker binds, scripts/Git, daemon outages or deployed tenant isolation. No live/user data was reset, existing server restarted, installed database migrated, provider credential spent or hosted CI requested. No Core schema migration or additional lifecycle engine was introduced.

This successor closes explicit human reset only at the exercised boundary. Process Stop, profile switching, model-requested reset/command offering/output delivery, tenant Sandbox UI, other resource/effect families, Governance/platform, voice/rich Chat, live input arbitration, autonomous origins and final obsolete-authority/schema consolidation remain open. The earlier SQLite preference reliability follow-up remains active. The full proposal remains active; no full proposal or milestone acceptance is claimed.
