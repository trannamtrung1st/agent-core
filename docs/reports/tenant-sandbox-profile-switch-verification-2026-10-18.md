# Tenant Session Sandbox profile-switch verification — 2026-10-18

## Scope and ownership

Human Session profile selection now extends the existing human control facade, owning lease/security/audit transactions and SDK retirement worker. It uses existing selected/transition profile fields plus a compact current Instance/source/host witness in lease JSON. No Core table, migration, second lifecycle queue or executor is introduced. Selection performs metadata eligibility checks only; it does not fetch source secrets, create a provider environment or run initialization/commands.

Independent current Session read/manage, agent Sandbox read/manage and old/target profile read apply before stale witnesses and at commit. The target must be currently allowed, enabled and fully verified against the current host and source epochs. Active execution, uncertain cleanup or failed initialization without explicit reset rejects selection. Reset clears pending targets and retains canonical bytes and initialization evidence.

The old generation's profile and provider ownership remain intact until exact original-host retirement. Pending/foreign cleanup preserves the target and blocks execution. After confirmed retirement, a fresh exact Review must name the pinned target before a one-time new generation reservation consumes selection. Current Instance/latest-profile drift, protected source epochs, host changes and old-profile fallback reject. Current Run admission and command authority remain independent ceilings.

## Exercised journeys

- InMemory and disposable SQLite: select B while idle A exists, observe pending B without SDK contact, retire A after logout (and SQLite reopen), retain canonical working bytes, relogin, reject fallback A, then review and reserve B once.
- Through the actual installed SDK and an authenticated controlled loopback peer: create the selected B image, complete mount/readiness protocol, initialize B once, execute the exact reviewed command once, settle its result, and prove duplicate observation sends no further SDK work. This is protocol/state/filesystem evidence; controlled readiness/process replies do not qualify a real container.
- Foreign A provider metadata: preserve reconciliation/retirement intent without killing foreign state or creating B. Repair exact ownership and confirm old retirement before fresh Review.
- Independent scoped manager: Session read, Session manage, agent Sandbox read/manage and target-profile read must all be present. Raw store preparation cannot substitute setup or host pins. Revoke target read after selection: old authorized facts remain readable while pending target IDs/details are redacted. Explicit reset can clear it with the old target's authority.
- Raw stores: original login logout, replacement login, password change and wrong Principal reject without lease/security/audit changes. Factual CAS cannot remove the selection witness. Failed initialization cannot be bypassed by switching; explicit reset retains evidence and consent while allowing fresh selected initialization Review.
- Prepare and approve B, then change only Instance conversation language or append a new B profile revision: fresh Review, saved approved reservation and raw generation reservation all reject the obsolete selection. Explicit reset clears pins for deliberate recovery. A changed provider-host reservation also rejects.
- Synthetic HTTP: exact strict body including explicit nullable execution witness, unknown-member rejection, stale target revision, anonymous/legacy/foreign/CSRF denial, no-store, canonical Location, pending acceptance, safe selected-target observation and stale retry. Existing reset/Stop and Instance/profile verification routes remain covered.

## Verification results

The final focused profile-switch run passed **20/20** checks in 47 seconds:

```sh
dotnet test tests/AgentCore.Infrastructure.Tests/AgentCore.Infrastructure.Tests.csproj --no-restore --nologo --filter 'FullyQualifiedName~Tenant_Sandbox_profile_switch' -v minimal
```

The first SDK fixture build exposed an incorrect test-only import; correcting it allowed the four representative SDK/store cases to pass. Boundary extensions then passed 16/16. Final review added current Instance/latest-profile drift fences and their regressions, producing the final 20/20 result above. No product test failure remained in that focused run.

The affected Sandbox regression suite passed **363/363** checks in 7m29s, excluding opt-in Docker integration:

```sh
dotnet test tests/AgentCore.Infrastructure.Tests/AgentCore.Infrastructure.Tests.csproj --no-restore --nologo --filter 'FullyQualifiedName~Sandbox&FullyQualifiedName!~OpenSandboxIntegrationTests' -v minimal
```

The final-source focused Synthetic HTTP recheck passed **9/9** checks in 10 seconds:

```sh
dotnet test tests/AgentCore.Api.Tests/AgentCore.Api.Tests.csproj --no-restore --nologo --filter 'FullyQualifiedName~Tenant_Sandbox_|FullyQualifiedName~Owned_instance_sandbox_HTTP|FullyQualifiedName~Tenant_verification_HTTP|FullyQualifiedName~Owned_instance_sandbox_verification_HTTP' -v minimal
```

Changed-document local-link checks passed for **1,079** targets and balanced code fences. `git diff --check` also passed.

## Limits and remaining proposal work

No hosted provider calls, live Docker qualification, user-data migration/reset, tenant Sandbox browser UI or model Stop/reset/command offering/output delivery was exercised here. Hosted CI and the full local CI suite were not requested or run. Other proposal resource/effect families, Governance/platform, voice/rich Chat, autonomous origins, final authority/schema consolidation and the prior SQLite preference reliability follow-up remain open. This report advances the backend human profile-switch boundary only; the full proposal remains active with no milestone acceptance claim.
