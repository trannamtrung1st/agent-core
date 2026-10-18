# Tenant Sandbox model inspection verification — 2026-10-18

## Scope and ownership

The existing `sandbox.profiles`, `sandbox.status` and `sandbox.process.status` names now have a canonical tenant tool path through `TenantToolAuthorization` and the existing `TenantSandboxRuntimeReadService`. Human and model reads share owned fact projection and exact scope construction. There is no global fallback, new table/migration, alternate lifecycle owner or SDK refresh path.

Models require exact saved/current read capability, actual active tenant/Session/Instance/Run/claim context, current original requester/executor, independent executor Session and agent Sandbox reads, referenced-profile read and current Allowed operation policy. Stronger read-review policy cannot be bypassed: review-required or denied decisions release no data. This read path does not prepare or consume typed read Reviews; that workflow remains open.

Discovery permission-filters immutable admitted profile snapshots against current allowed/readable sources before metadata I/O. Counts cover visible entries only; unread defaults and selection IDs are redacted. Later successful verification cannot promote a saved NotAdmitted pin. Stored status omits initialization evidence, command text and receipt streams; exact process reads return bounded stored output, exit and cleanup facts. Both carry `observationOnly:true` and `liveAuthorityConfirmed:false`. A stored Ready value grants no current execution permission. Explicit read capabilities remain useful after execution configuration is removed; discovery then returns no newly allowed profiles.

## Exercised journeys

- Both stores: deny discovery until independent exact Session and agent Sandbox read grants exist; then read only authorized saved profile metadata, environment summary and exact receipt output/exit. Owner initialization evidence and command text remain absent. Foreign receipts, extra authority arguments and stale claims reject. Lease/receipt facts and revisions remain unchanged, and global storage cannot see the tenant lease.
- Both stores: bound large Unicode output to the actual remaining tool budget without replacement characters or malformed JSON; stored reads remain available while host readiness is unavailable.
- Both stores: revoke the default profile and hide its name/ID/default/selection; independently revoke a pending target while old environment facts stay readable. Read operations preserve the actual pending selection and never retire/provision anything.
- Both stores: verify an initially unverified profile after Run admission and retain NotAdmitted. Revoke exact profile access during image-metadata I/O: reject the captured projection at the final security fence, then recover through a fresh filtered read.
- Both stores: adopt a new publication with execution configuration removed. Retained exact read capabilities still inspect old facts while discovery returns no choices. Removing current read capability, or adding it only after admission, denies rather than expanding the old Run. The retained lease remains unchanged.
- Strengthen current read policy with and without an eligible independent reviewer: withhold data and preserve the safe review/denial reason. New typed read-review preparation is not claimed.
- Hosted API/Runtime: a recording Synthetic model discovers profiles and reads stored status before requesting exact command Review; production coordination/continuation and the installed SDK execute once after human decision. Stop the controlled SDK peer, then let the model inspect its exact receipt. The output reaches the next actual model request without any additional SDK contact. Existing fresh provisioning, background, rejection/cancellation and uncertainty cases remain in the hosted fixture.
- Tool-contract checks: strict empty/receipt arguments, no caller authority or duplicate fields, exact capability/context/tools-support constraints, contextual tenant read offering independent from execution/readiness, and preserved legacy on-demand projection.

The hosted fixture keeps the production tenant Run coordinator, mailbox, approval, continuation, executor and SDK. It uses controlled verification/readiness and provider responses, and excludes unrelated periodic Sandbox cleanup. These facts prove application/store/controlled protocol behavior, not real container shell semantics or live Docker qualification.

## Verification

The first representative store read passed 2/2 checks. The first expanded scope/output/revocation matrix passed 10/10. After sharing scope construction and preserving stronger-policy denial reasons, the human-read/selection/inspection regression run passed 38/38 in 1m23s. The first hosted command-plus-inspection run passed 7/7 in 24 seconds.

The generic tool/policy/checkpoint run passed 67/67 in 629 ms. An initial legacy-projection assertion omitted the explicit capability load required by the existing legacy on-demand contract; the fixture was corrected while preserving that behavior.

The final model-read/recovery/human-read run passed **24/24** in 52 seconds, including all six execution-policy-removal/adoption cases. The final-source hosted/canonical Sandbox HTTP run passed **16/16** in 34 seconds. The final-source generic contract recheck passed **67/67** in 652 ms, including explicit read capabilities without execution configuration. The prior “IAM inspection only” exact-set assertion was updated to the five implemented IAM/Sandbox read tools; unsupported tools and mutations remain excluded.

```sh
dotnet test tests/AgentCore.Infrastructure.Tests/AgentCore.Infrastructure.Tests.csproj --no-restore --nologo --filter 'FullyQualifiedName~Tenant_Sandbox_model_inspection_|FullyQualifiedName~Tenant_Sandbox_model_profile_discovery_|FullyQualifiedName~Tenant_Sandbox_model_discovery_|FullyQualifiedName~Tenant_Sandbox_model_stored_inspection_|FullyQualifiedName~Tenant_sandbox_runtime_read' -v minimal
dotnet test tests/AgentCore.Api.Tests/AgentCore.Api.Tests.csproj --no-restore --nologo --filter 'FullyQualifiedName~Tenant_Sandbox_|FullyQualifiedName~Owned_instance_sandbox_HTTP|FullyQualifiedName~Tenant_verification_HTTP|FullyQualifiedName~Owned_instance_sandbox_verification_HTTP' -v minimal
dotnet test tests/AgentCore.Application.Tests/AgentCore.Application.Tests.csproj --no-restore --nologo --filter 'FullyQualifiedName~TenantSandboxInspectionContractTests|FullyQualifiedName~TenantApprovalToolContractTests|FullyQualifiedName~TenantSandboxAuthorizationContractTests|FullyQualifiedName~ToolRegistryPolicyTests|FullyQualifiedName~CapabilityProjectionTests|FullyQualifiedName~AgentRunToolCallCheckpointTests' -v minimal
```

Counts above overlap and are not a distinct-test sum. Changed-document checks passed for **1,093** local Markdown link targets and balanced fences; `git diff --check` passed.

## Limits and remaining proposal work

No live Docker, hosted inference/speech provider, frontend browser acceptance, user-data reset/migration or hosted CI was run. The full local CI suite was not requested. Read observations deliberately do not refresh provider facts; the existing worker owns later process and retirement updates. Typed Sandbox read-review, model Stop/reset, tenant Sandbox UI, other resource/effect families, Governance/platform, voice/rich Chat, autonomous origins, final authority/schema consolidation and the earlier SQLite preference reliability follow-up remain open. The full proposal stays active; no complete proposal or milestone acceptance is claimed.
