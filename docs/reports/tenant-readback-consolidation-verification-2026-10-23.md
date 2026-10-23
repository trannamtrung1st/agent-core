# Tenant approval readback consolidation — 2026-10-23

Status: this dependency/documentation consolidation slice is locally qualified. The original IAM goal remains active.

## Consolidated owner

`TenantRunContinuationService` previously injected twelve local read services already held by `TenantApprovedOperationService`. It repeated their post-consent dispatch and exception handling. The existing approved-operation owner now performs that readback; continuation consumes its bounded observation or unavailable reason and retains Run, transcript, budget and delivery responsibility. Its constructor decreases from 25 dependencies to 13. No dependency bag, service locator, registration registry, new framework or parallel state owner is introduced.

The twelve branches are management, Automation, Experience source, historical continuity, learned Memory, workspace/Resource, Attachment, completion, Artifact metadata, connected account, Credential metadata and Sandbox. Each still calls its incumbent exact approved-read method. Noncompleted receipts produce no private readback. Access/conflict denial preserves the existing reason; connected-account application errors retain their existing unavailable code. The account Run refresh stays after readback because inspection can durably rotate OAuth/source state. Sandbox retains its original output allowance; the other branches retain their existing 512-byte continuation headroom.

Retained account/resource read checks run through the same existing readers in their original order around external account-result validation. Browser/HTTP body owners, reserved external dispatch, exact source/decision/grant/receipt checks, family continuation projections and checkpoint accounting are unchanged. Test factories now pass readers once to the approved-operation owner rather than wiring them into both services. Production uses ordinary existing singleton DI registrations.

No Domain payload, contract, permission, policy, role, route, migration or frontend source changes. Inert Harness preparation and its hash-bound serialized history remain unchanged; their coordinated retirement is still open.

## Documentation consolidation

The backend specification's early persona-only suspension/continuation paragraphs described other families and worker/native/autonomous integration as still pending. They now describe the current common lifecycle once, distinguish management eligibility from unrelated consent, identify the actual readback owner, and retain dated historical report links. Current family qualification and remaining acceptance stay in the delivery ledger. The explicit risk-class floors preserve requester-based autonomous behavior and mandatory Sensitive/tenant controls.

## Local evidence

**498 distinct focused cases pass; none are skipped.**

- Both serial solution builds pass with zero warnings/errors; the final build took 20.68 seconds.
- API: 207 distinct actual HTTP/model and application-host recovery cases pass. The initial group passed 199 and failed eight stale Resource witness-count assertions. The corrected 16-case child-read group passes, rechecking eight already-passing Skill cases and qualifying the eight Resource cases.
- Infrastructure: 291 distinct cases pass. The initial group passed 286 and failed one downgrade fixture. The corrected fixture and four new learned-Memory read cases pass in the final five-case group.

The child-read fixture now counts only the explicitly requested tool, requires all additional witnesses to be independently authorized Skill-context sources, revalidates every retained source, and preserves receipt/grant/nonactivation assertions. The passing Resource rejection cases retain no requested Resource witness or effect receipt. The downgrade fixture now restores the current schema after the retained Attachment guard refuses, verifies model parity, and then checks that the saved request is unchanged. Its mixed-payload/source/grant rejection checks remain intact. These are grouped fixture corrections; the production consolidation was unchanged during the initial and final test selections.

All test invocations use `dotnet test <project> --no-build --no-restore --nologo --filter <selection>`. API selection covers the methods in `TenantSessionAccessTests.ManagementReads`, `ManagementReadRecovery`, `ManagementChildReads`, `AttachmentReads`, `AttachmentReadRecovery`, `CompletionRead`, `WorkspaceReadReview`, `TenantCredentialAccessTests.AccountReadModel`, `TenantAgentAccessTests.Automation.Read` and `Continuity`.

The store selection covers the methods in the changed metadata/account-action, Continuity/Experience/Memory/Sandbox-read, Artifact-verification, Resource namespace/read, workspace read/search, Attachment Review and completion-read fixture files, plus `TenantCredentialIntegrationTests.AccountReads` and `TenantDefinitionIntegrationTests.Continuity.Combined` / `Continuity.Boundaries`. It checks actual owning stores and source bytes, exact qualified decisions, original successor correlation, readback scope and bounded output, policy/source revocation, stable refusal, atomic receipts, retained proof and reopen/guarded migration behavior. Existing executable scenarios are reused. Four additional cases exercise the retained learned-Memory read variant through actual owned approval/rejection, SQLite reopen and repeated continuation; they address a missing execution path rather than mirroring the private switch.

The final correction selectors were:

```text
Build: dotnet build AgentCore.sln --no-restore --nologo -m:1 -nodeReuse:false -p:UseSharedCompilation=false
API: FullyQualifiedName~Canonical_management_child_read_Review_withholds_private_content_and_only_authorizes_the_exact_saved_read
Infrastructure: FullyQualifiedName~Tenant_attachment_Review_SQLite_shape_rejects_mixed_source_grants_and_unsafe_downgrade|FullyQualifiedName~Tenant_memory_exact_read_consent_preserves_owned_observation
```

Logs and complete selected paths/filters are in `/private/tmp/tenant-readback-consolidation-{build,final-build,api,api-final,store,store-final}.log` and `/private/tmp/tenant-readback-consolidation-{api,store}-selection.txt` (brace notation names separate files). Synthetic fixtures use disposable roots. No user database or Real host is changed, and no live provider call, CI run, push or deployment is requested or performed. No new rendered frontend evidence is claimed for this internal backend consolidation.

## Remaining goal

This is a concrete dependency and documentation consolidation slice, not proposal-wide acceptance. Broader approval preparation/decision dependencies, safe inert Harness/schema retirement, Composer reference-scope reconciliation, final store/native authority inventory and the complete security/isolation/functional/rendered UI acceptance matrix remain open.
