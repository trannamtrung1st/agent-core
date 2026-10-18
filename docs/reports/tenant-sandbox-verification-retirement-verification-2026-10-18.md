# Tenant Sandbox verification retirement — 2026-10-18

## Delivered boundary

Disposable tenant reusable-profile and Instance verification now seal their exact existing attempt through the trusted provider before deleting files or promoting compatibility. The pinned vendor provisions in a background thread: an empty list after a lost create reply cannot prove that a future container will not appear. The same negative-fence primitive already used for tenant runtime generations now covers the real tenant/attempt scope, without fabricated Session/executor identities, another Core engine or a Core migration.

Normal RW/RO/Disabled Home probes may create sequential environments under one attempt. Known handles require exact metadata before mount work and deletion. Final success, failure, cancellation or technical cleanup seals the whole attempt. Its durable provider tombstone precedes waiting for active provisioning and rejects queued/late creation. Exact bounded cleanup rechecks both listed and actual container metadata and uses the existing provider deletion service. Partial batches remain pending.

Core checks `verificationFencing` before tenant mount preparation/create and accepts only bounded versioned exact tenant/attempt retirement proof from the original configured host scope. Unknown, malformed, oversized, foreign, pending or changed-host proof cannot remove files. Technical cleanup checks its exact revision/claim/expiry/Instance/host both before and after provider I/O. It can retire after the original login ends and cannot repeat scripts or purge sibling attempts.

A current-authority watcher cancels stalled SDK work after original-human/source/Instance authority changes. Captured output cannot promote compatibility under retired authority. Caller cancellation still settles factual cleanup independently before propagating. Initialization additionally requires an error-free complete SDK acknowledgement.

## Executed checks

Integrated scenarios run through actual owning services, InMemory and disposable SQLite, the installed SDK and authenticated loopback Kestrel:

- Lost-create/cleanup checks: reusable and Instance exact ownership, original-session retirement, forged cleanup refusal and sibling-directory preservation.
- Sequential-probe/proof checks: all three Home probes and initialization once; pending final retirement prevented promotion and retained files; malformed, oversized, foreign, wrong-version, pending and expired-claim cleanup replies retained files.
- Old-server/stalled-create checks: unsupported verification capability caused no mount/create; original-human revocation and caller cancellation aborted a stalled SDK create, retired the exact attempt once and did not replay creation.

Final focused Sandbox regression:

```sh
dotnet test tests/AgentCore.Infrastructure.Tests/AgentCore.Infrastructure.Tests.csproj --no-restore --nologo --filter 'FullyQualifiedName~Sandbox&FullyQualifiedName!~OpenSandboxIntegrationTests' -v minimal
```

**291 passed, 4m54s.** This includes the final foreign returned-handle refusal and host-mapping-change-during-cleanup cases, all reusable/Instance verification scenarios above, owned generation execution/provisioning/recovery, current policy/Review/storage and shared legacy cleanup regressions.

Focused Synthetic HTTP regression:

```sh
dotnet test tests/AgentCore.Api.Tests/AgentCore.Api.Tests.csproj --no-restore --nologo --filter 'FullyQualifiedName~Tenant_verification_HTTP|FullyQualifiedName~Owned_instance_sandbox_verification_HTTP|FullyQualifiedName~Tenant_Sandbox_|FullyQualifiedName~Owned_instance_sandbox_HTTP' -v minimal
```

**7 passed, 7s.** Reusable and Instance verification enforce scoped manage authority, mask revoked/foreign history and internal proof, keep profile/sibling evidence separate and preserve setup, exact Review and stored runtime observation behavior.

Provider checks:

```sh
python3 -m unittest scripts.tests.test_sandbox_retirement scripts.tests.test_sandbox_compatibility scripts.tests.test_opensandbox_config -q
```

**29 passed.** Real SQLite reopen and deterministic provisioning-thread barriers cover sequential probes, sealing before/while creation, scope collisions and independent runtime/verification fence families. Controlled container metadata checks cover foreign actual labels and bounded partial cleanup. Existing generation, mount compatibility and configuration regressions remain green.

The [pinned-image proof command](../../scripts/tests/README.sandbox-retirement.md) passed with the installed vendor FastAPI, Pydantic, authentication middleware and actual `DockerSandboxService` hook. Both generation and verification routes rejected unauthenticated and invalid payloads, returned exact proofs, deleted exact controlled containers and rejected late provisioning and scope replacement. The container had disabled networking, a read-only root, disposable tmpfs, no Docker socket, host ports or user data.

## Qualification limits and remaining proposal work

These checks execute actual Core/store/SDK protocol and actual pinned-image routes/hooks against controlled peers. Readiness commands and bidirectional filesystem probe responses are controlled; they do not prove a live Docker bind, shell/Git behavior, real daemon outage or deployed multi-tenant isolation. No existing server was restarted, user database migrated/reset, provider credential used or hosted CI requested.

This successor closes the verification lost-create/orphan-fence gap described in the earlier [recovery report](tenant-sandbox-recovery-verification-2026-10-18.md) only at the exercised boundary. Explicit tenant process Stop/reset/profile switching, model offering/output delivery, tenant Sandbox UI, other resource/effect families, Governance/platform, voice/rich Chat, live input arbitration, autonomous origins and final obsolete-authority/schema consolidation remain open. The prior SQLite preference reliability follow-up remains active. The full proposal remains active; no milestone or full proposal acceptance is claimed.
