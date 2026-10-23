# Tenant application progress messages — 2026-10-24

This module restores existing `app.message.send` behavior through canonical tenant authority. It closes the application-message parity gap recorded in the [150-tool checkpoint](tenant-model-tool-authority-verification-2026-10-24.md), without adding a product feature, execution framework, permission, approval default, schema or wire record. It does not accept the complete IAM proposal.

## Implementation

The existing application-message policy now recognizes only actual human UserMessage/ApprovalContinuation conversation output. The shared tenant tool gate checks the real Run plus the existing live-claim fence, user-turn/intermediate-work flags and configuration-continuation exclusion. Both normal tool projection and Runtime's post-brain offering path use the same conversation eligibility. Automation, detached work, background outcomes and completion reports retain their existing output contracts.

The existing mailbox retains sole ownership of admission. It requires the configured Run authority and repeats current executor/requester, lease and retained-source checks before deduplication or history mutation. Direct generic execution still returns `runtime_dispatch_required`; forged routing fields cannot select another destination. No provider effect is dispatched by the adapter.

Existing text validation, effect/text dedupe, twelve-message count, 2000-character per-message and 8000-character aggregate budget remain. Same provider call-ID reuse with changed arguments is refused by the existing tool loop; it is not a second mailbox effect. Notices have zero heard prefix and no speech. They stay visible in history but out of future prompts, compaction and completion evaluation. New user turns receive a fresh message budget.

## Executed journeys

The integration model reads actual human-uploaded Home bytes under separately granted executor IAM. It attempts a premature notice in the same batch, then emits real progress notices in the next generation and finishes normally. Valid notices persist once; identical text with a fresh call ID deduplicates; routing, empty and oversized payloads fail. The budget journey admits twelve notices, refuses the thirteenth and omits the exhausted tool on the next generation. A later human turn starts with twelve available slots and receives no prior notice text as model context.

Other journeys hold the actual model after its source read, then revoke its executor assignment, disable Home access, invalidate an exact bound Composer reference or cancel the actual Run. Notices are withheld. Cancellation and human steering after two notices preserve those entries, discard a deliberately late noncooperative call and retain the correct terminal state. Steering completes a fresh independent human Run. Reused provider call IDs and a simulated final provider failure preserve the already admitted notices without publishing changed text. Final reply timestamps remain ordered after progress. SQLite normal/budget journeys stop and reopen the real factory and read the persisted history after a new login.

All setup uses canonical tenant HTTP/services and disposable test roots. No user database was reset. The current browser UI already renders applicationMessage entries through its existing ChatMessage owner; this backend module changes no frontend source, design token or UI library.

## Fixture reconciliation and scope

The prior application-message Runtime suite had **11 failing cases** that expected private tool effects from unowned Runtime fixtures. Four local tests retain payload/effect-key rules, declarative offer gating, direct-executor refusal and prompt/compaction/completion exclusion. Live functionality moved into the tenant integration suite rather than restoring unowned authority. Obsolete Runtime/model scaffolding was removed.

The first new journey fixture incorrectly repeated a provider call ID within a batch and therefore failed at protocol validation. Reuse now has its own refusal journey; normal text dedupe uses a distinct valid call ID. The first grouped run also exposed a stale wire field manifest (the existing interruption fields were omitted), a steering assertion selecting the replacement Run's model request and an overstrict mutable-file expectation. Those fixtures were corrected together.

Ordinary Workspace observations intentionally retain their existing current-read semantics after a mutable file replacement; this module does not invalidate all prior task context or break read/edit/reply workflows. Exact source continuity is exercised using the already supported bound Composer reference. Current read revocation and Home Access withdrawal are tested independently. No new source-hash protocol or compatibility authority path was added.

Human approval-continuation eligibility is covered by the origin rules; this scoped report does not claim every approval/source combination, live external effect, native transport, responsive UI or final proposal-wide acceptance. Existing family reports own those checks. CI and live-provider calls were not requested or run.

## Focused verification

**91 distinct cases passed.** Repeated API fixture corrections are not counted twice.

| Check | Result |
| --- | ---: |
| Application-message rules, budget, offers, origin eligibility, prompt exclusion and independent-task contracts | 49 passed |
| Memory/SQLite current conversation gate, generic-adapter refusal and 150-tool stale-context matrix | 18 passed |
| Memory/SQLite actual progress journeys plus HTTP/history/MessagePack mapping | 24 passed |
| Serial solution build, no restore | 0 warnings / 0 errors |
| Whitespace and affected-document local targets | Passed |

The API group comprises 20 live tenant cases and 4 mapping/wire fixtures; it completed in about 1m47s. The infrastructure group completed in about 58s. The earlier unowned-fixture audit was 5 pass/11 fail before reconciliation. The first new API fixture run was not a clean product reproduction because its duplicate provider ID failed protocol validation. The first grouped API run was 19 pass/5 fixture failures; the full corrected API group passed 24/24. Application/infrastructure groups were not repeated after API-only fixture edits.

Commands and local logs:

- `dotnet build AgentCore.sln --no-restore --nologo -m:1 -nodeReuse:false -p:UseSharedCompilation=false`; `/private/tmp/tenant-progress-final-build.log`.
- Application: `dotnet test tests/AgentCore.Application.Tests/AgentCore.Application.Tests.csproj --no-build --no-restore --filter 'FullyQualifiedName~ApplicationMessage|FullyQualifiedName~TenantIndependentTaskContractTests'`; `/private/tmp/tenant-progress-application.log`.
- Infrastructure: `dotnet test tests/AgentCore.Infrastructure.Tests/AgentCore.Infrastructure.Tests.csproj --no-build --no-restore --filter 'FullyQualifiedName~Tenant_progress_policy_requires_actual_current_conversation|FullyQualifiedName~Registered_model_tools_require_current_claim'`; `/private/tmp/tenant-progress-infra.log`.
- API: `dotnet test tests/AgentCore.Api.Tests/AgentCore.Api.Tests.csproj --no-build --no-restore --filter 'FullyQualifiedName~Tenant_application_messages_use_owned_live_conversation|FullyQualifiedName~ApplicationMessageHistoryTests|FullyQualifiedName~ApplicationMessageWireTests'`; `/private/tmp/tenant-progress-api-final.log`.

This completes the scoped application-message restoration. The active original goal still requires permission/store/effect closure, safe historical Harness/schema reconciliation and final security/isolation/UI acceptance. No push, CI dispatch, live provider call or database reset was performed.
