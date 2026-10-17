# Execution authorization audit evidence

Date: 2026-10-17. Baseline: `1246f635e6e22b9102291133649af73eeb5b2755`. This follows the [approval-loop checkpoint](tenant-approval-loop-verification-2026-10-17.md). The [full delivery goal](multitenant-iam-delivery-2026-10-16.md) remains active; no milestone acceptance, deployment or installed-data migration/reset is claimed.

## Reproduced and implemented

An actual admitted tenant Run inspected its own agent resource through `TenantOperationAuthorizer`. Both adapters returned RequiresApproval but saved no authorization decision. The two-case reproduction failed before the change. It now records the current executor/requester, Session/Run/activation/root/parents, observed Run/security revisions and claim generation, normalized resource, catalog permission, decision/reason and purpose.

The existing AuthorizationAudit owner gains a nullable bounded JSON execution context through `20261017071356_TenantExecutionAuthorizationAudit`. The append adapter validates actual same-tenant saved lineage and current witnesses. A stale/missing acknowledgement stops the supported protected use case; it cannot return usable authority and continue an effect. Appending evidence changes neither Run nor security revisions. Duplicate identity cannot overwrite evidence, and replay after a security revision change cannot reuse the old acknowledgement.

Ordinary checks distinguish Inspection, ApprovalPreparation, ApprovalDecision, EffectEligibility and ToolBoundary. RequiresApproval eligibility does not mean human consent or a completed effect. Existing persona/preference effect transactions attach EffectResult evidence with exact operation/request/grant references atomically with their immutable result. Historical replay preserves the original outcome audit. Same-root refusal and quota denial are recorded without another request, request audit or durable wait. Candidate human qualification and internal Runtime reasoning do not append a row for every intermediate calculation.

The authenticated audit API retains its existing entry fields and adds explicit nullable DTOs with string purpose/origin/approval kind. Unknown permission text and malformed resource payloads are not copied into audit content. No action arguments, bearer credentials or role snapshots are stored or returned in execution evidence. A blocked tenant credential tool remains blocked and now retains safe boundary-denial evidence; credential use is not enabled by this checkpoint.

## Exercised behavior

- Actual admission → permission inspection → role grant/revoke → human-only denial: persisted RequiresApproval/Allowed/Denied reflect the current executor and survive a new SQLite adapter. Inspections change no authority or Run state.
- Fake tenant, executor, requester, lineage, revision and generation: both append adapters refuse them with no new row or state change. Same audit retry is immutable; later security state rejects reuse.
- Missing audit acknowledgement → real direct preference tool: no setting, receipt, effect audit or revision changes. Retrying with the working audit adapter succeeds once and commits the exact effect evidence.
- Unknown permission/resource input containing a test secret: bounded safe decision metadata contains none of that input. Empty inventory, unavailable executor and unsupported tenant credential-tool cases retain their appropriate safe evidence.
- Temporary SQLite upgrade retains legacy null-context rows; malformed/oversized JSON is rejected without replacing the saved event. Strict canonical-schema refusal and IAM/identity recovery regressions pass.
- Hosted Synthetic authorization → human decision → recorded successor → exact local effect: canonical HTTP audit reads show the preparation Run and committed successor's parent, operation, request and grant references for Review/ExceptionalGrant, rejection/cancellation, freeze and resource deferral scenarios.

## UI and browser evidence

The Audit page now separates permission checks, review requirements, denials, applied/not-applied effects and legacy recorded changes. It reuses the existing Admin collection, result tags, responsive description layout and bounded access drawer. One record is selected at a time; mobile details include the result/reason summary. Refresh and shared loading/empty/error recovery remain scoped to the current organization.

Playwright MCP used a dedicated InMemory Synthetic API at `127.0.0.1:55270` and Vite at `127.0.0.1:55271`, with explicit test bootstrap credentials and temporary roots. The documented loopback-only HTTP authentication exception was enabled for this fixture. No existing process or user database was reused. Both verification servers were stopped and the verification tab was closed after the journey. The offered read-only Synthetic script `synthetic authorization audit: <permission> <agent-guid>` ran through the real tenant worker. It supplied only permission/resource arguments; Core retained executor and tenant authority.

Observed browser sequence: sign in → open the organization → create an owned agent and Session through canonical HTTP → submit independent Synthetic inspection messages → observe RequiresApproval and human-only Denied → assign own-agent read permission → submit another message and observe Allowed → open Audit through navigation → inspect the saved Run/root/requester details. The browser checked 320, 768 and 1440px with document scroll width equal to viewport width. The mobile drawer settled at 320px inside the viewport; keyboard disclosure and Escape dismissal worked. Resizing an open record keeps one active detail ID; a retained hidden inline copy was removed from the mobile render owner. A deliberate audit-read 503 showed the shared error, then Reload restored real server data. Final successful interactions had no unexpected page errors or failed network requests; the injected 503 produced its expected browser console entry.

Session-local screenshots: `/private/tmp/tenant-audit-browser-20261017/audit-final-1440.png` and `audit-final-320.png`. Hardware/audio and native browser zoom were not requalified; no live tenant Chat/voice UI is claimed. The strict build retains the repository's existing large-bundle advisory.

## Focused checks

All 496 focused backend tests and 21 frontend tests passed. Commands use `dotnet test tests/<Project>/<Project>.csproj --no-restore --nologo --filter '<filter>'`. The first integrated reproduction was exercised before expanding coverage.

| Project | Filter | Result |
| --- | --- | --- |
| AgentCore.Domain.Tests | `FullyQualifiedName~ActivationAdmissionTests\|FullyQualifiedName~AgentRunContractTests\|FullyQualifiedName~ExecutionBudgetPolicyTests` | 53 passed |
| AgentCore.Application.Tests | `FullyQualifiedName~AgentRunDurabilityTests\|FullyQualifiedName~TenantApprovalToolContractTests\|FullyQualifiedName~CapabilityProjectionTests\|FullyQualifiedName~ToolRegistryPolicyTests` | 101 passed |
| AgentCore.Infrastructure.Tests | `FullyQualifiedName~TenantPreferenceOperationTests\|FullyQualifiedName~TenantOperationAuthorizationTests\|FullyQualifiedName~TenantRunAdmissionTests` | 252 passed |
| AgentCore.Infrastructure.Tests | `FullyQualifiedName~IamIdentityIntegrationTests\|FullyQualifiedName~CanonicalSchemaRefusalTests\|FullyQualifiedName~IdentityConsolidationContractTests\|FullyQualifiedName~SyntheticAuthorizationAuditTests\|FullyQualifiedName~InMemoryAndScriptedTests` | 51 passed |
| AgentCore.Api.Tests | `FullyQualifiedName~TenantSessionAccessTests` | 32 passed |
| AgentCore.Api.Tests | `FullyQualifiedName~IdentityAndTenantAccessTests\|FullyQualifiedName~IdentitySqliteRestartTests` | 7 passed |
| web | `pnpm run test --run src/features/access/TenantAudit.test.tsx src/features/access/useTenantCommand.test.tsx src/services/identity.test.ts src/features/access/accessRoute.test.ts` | 21 passed |
| web | `pnpm run build` | Passed |

The 27 new audit integration cases are included in the 252 total. The added Synthetic fixture is included in the 51 total. The four hosted direct-preference cases were rechecked after the final preflight-denial audit change and are counted once within the 32 API cases. The UI cases cover legacy records, exact effect versus inspection labels, one disclosure, mobile drawer summary/close, refresh, recovery and clearing another organization's details. Generated migrations were not applied to installed data; no CI or live-provider calls were requested or run. Final whitespace checks and 590 local links/fragments across nine changed Markdown documents passed.

## Remaining full-goal work

General human HTTP/read and remaining ingress/dispatch authorization audit coverage, proactive revocation, typed credential/external effect approval/execution, notification delivery, complete tenant Chat/Hub/output/audio, typed audit filters and remaining IAM/approval UI, and final obsolete-authority/schema removal remain required. This checkpoint does not replace those requirements with the implemented Run/local-effect subset.
