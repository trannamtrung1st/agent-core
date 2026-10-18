# Tenant model Sandbox command verification — 2026-10-18

## Scope

The existing tenant authorization tool now offers a disjoint exact Sandbox command proposal for eligible admitted profile IDs, independently of Settings self-management. Core derives all identity, current setup, source, generation and security witnesses. The existing Sensitive nondelegatable Review, Runtime suspension/successor, shared-capacity external preparation and installed SDK remain the effect owners. Direct legacy tenant Sandbox dispatch stays closed.

The exact pending model call must match the saved operation before SDK work. After settlement, the existing continuation reads only that owned operation's execution under current Runtime/requester/executor, Session, profile and policy scope, with post-I/O Run/security checks. Stored evidence needs neither SDK contact nor provider readiness. Read denial withholds streams without rewriting the known effect. Bounded UTF-8 result prefixes fit the actual existing checkpoint plus terminal reserve. The factual result is data, never an authorization token, arbitrary shell success guarantee or a reason to replay uncertain work.

No Core schema, table, migration, alternate executor or queue is added. Existing typed Review and command reservation/factual settlement remain authoritative. Model Stop/reset, Sandbox inspection/discovery and tenant Sandbox UI remain separate work.

## Exercised behavior

- Both stores: an exact model-shaped command reaches actual authorization preparation, saves a durable wait, receives human Review and creates an approved successor. The installed SDK sends the exact command once to an authenticated controlled peer. Continuation appends its exact stdout, exit/process and cleanup facts to the pending tool call; another observation neither sends SDK work nor appends again.
- Both stores: a changed checkpoint command rejects before provider contact or reservation. A near-capacity checkpoint receives bounded Unicode output without broken UTF-8 or loss of operation outcome. Revoke the executor's exact Sandbox grant after factual completion: output becomes unavailable while the immutable completed effect and canonical receipt remain retained. Cross-Session and forged operation targets return no output.
- Hosted API/Runtime: a recording Synthetic model receives the restricted schema, requests exact Review without Settings self-management, releases its claim, then resumes through production Run coordination and the shared SDK worker after a canonical human decision. Approve yields factual output in the next actual model request. A fresh generation creates once, completes controlled mount/readiness protocol, runs reviewed Profile initialization once and sends the main command once. An acknowledged background launch reports Running/combined logs and `sandbox_command_started` independently of process completion. The sole Runtime slot becomes reservable during every approval wait. Reject/cancel sends no SDK work. An uncertain provider reply withholds the provider body, permits only the existing response envelope for explanation and records `side-effect-indeterminate` without replay.
- Contract checks: exact literal argv and saved action matching; unknown/mixed/caller-authority fields, duplicate members, unsafe cwd, null/fractional/string/out-of-range timeout, malformed argv and non-Boolean flags reject. Unverified/unadmitted profiles and missing capability/context cannot create a Sandbox offering. Persona, preference and Credential branches remain independently filtered.

For existing-generation cases, the hosted fixture seeds an owned Ready lease through the real reservation/factual stores before the model proposal. The fresh-generation case starts without a lease and uses an explicitly allowed local image ID. Reusable verification/readiness use controlled prerequisite evidence. It disables the unrelated periodic Sandbox cleanup service to isolate foreground execution; the production tenant Run coordinator, mailbox, approval services, continuation, command executor and SDK remain active. The loopback peer controls process/readiness replies. This establishes SDK protocol and application/store behavior, not a real shell/container, live mount isolation or Docker qualification.

## Verification

The exact authorization contract checks passed **9/9** (including existing persona/preference/Credential contract compatibility). The focused both-store SDK/output boundary checks passed **8/8** in 19 seconds. The hosted Sandbox/persona/preference/SQLite-resume regression run passed **27/27** in 1m34s.

The affected backend regression run passed **72/72** in 1m58s, including SDK failure/cleanup, selected-profile fencing and existing persona/preference/Credential continuation. The final hosted Sandbox journey run passed **6/6** in 21 seconds, adding fresh provisioning and acknowledged background launch to the approve/reject/cancel/uncertain cases.

```sh
dotnet test tests/AgentCore.Infrastructure.Tests/AgentCore.Infrastructure.Tests.csproj --no-restore --nologo --filter 'FullyQualifiedName~Tenant_Sandbox_approved|FullyQualifiedName~Tenant_Sandbox_SDK_|FullyQualifiedName~Tenant_Sandbox_profile_switch|FullyQualifiedName~Tenant_Sandbox_model_|FullyQualifiedName~Approved_continuation_|FullyQualifiedName~Cancelled_or_expired_continuation_|FullyQualifiedName~Credential_successor_' -v minimal
dotnet test tests/AgentCore.Api.Tests/AgentCore.Api.Tests.csproj --no-restore --nologo --filter 'FullyQualifiedName~Tenant_Sandbox_|FullyQualifiedName~Model_prepares_durable_persona|FullyQualifiedName~Model_preference_authorization|FullyQualifiedName~Model_approval_and_exact_checkpoint' -v minimal
dotnet test tests/AgentCore.Api.Tests/AgentCore.Api.Tests.csproj --no-restore --nologo --filter 'FullyQualifiedName~Tenant_Sandbox_model_request_waits' -v minimal
```

The 27-case API run preceded the two added hosted cases; its four original Sandbox cases are also included in the final six-case run. These counts overlap and are not a distinct-test sum. The final generic tool-policy/checkpoint regression run passed **64/64** in 679 ms:

```sh
dotnet test tests/AgentCore.Application.Tests/AgentCore.Application.Tests.csproj --no-restore --nologo --filter 'FullyQualifiedName~TenantApprovalToolContractTests|FullyQualifiedName~TenantSandboxAuthorizationContractTests|FullyQualifiedName~ToolRegistryPolicyTests|FullyQualifiedName~CapabilityProjectionTests|FullyQualifiedName~AgentRunToolCallCheckpointTests' -v minimal
```

Changed-document checks passed for **1,086** local Markdown link targets, balanced fences and the complete model-request JSON example. `git diff --check` passed.

Initial compilation and fixture failures exposed missing imports, a manual helper that attempted to claim the completed source instead of its durable successor, and an invalid fixture profile ID; these were corrected. The first fresh-generation fixture used a remote digest and was rejected during registry manifest authorization before SDK create/command; it was corrected to use an explicitly permitted local image ID. Final passing fresh provisioning performs no registry probe. Hosted uncertainty assertions were aligned with the existing response-only envelope and indeterminate terminal semantics, preserving that behavior.

## Remaining scope

No hosted inference/speech provider call, live Docker check, frontend browser acceptance, user-data reset/migration or hosted CI was performed. CI and the full local CI suite were not requested. Model Stop/reset and Sandbox read/discovery tools, tenant Sandbox UI, other resource/effect families, Governance/platform, voice/rich Chat, autonomous origins, final authority/schema consolidation and the earlier SQLite preference reliability follow-up remain open. The full proposal stays active; this is no full proposal or milestone acceptance claim.
