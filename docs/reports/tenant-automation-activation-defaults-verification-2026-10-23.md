# Canonical Automation activation defaults — 2026-10-23

The model tool already promises disabled new Event Automations, but canonical normalization activated non-preset creations whenever `enabled` was omitted. The parser now resolves the source first: new Event parents and presets default Disabled, ordinary Schedules retain their Active default, and existing-resource updates preserve current activation. Explicit activation controls still require actual intent, current IAM and applicable consent. Child subscription flags remain independent of parent activation. No saved record, permission classification, policy floor, schema, transport shape or execution owner changes.

This also qualifies actual autonomous authoring under the [unchanged approval floors](tenant-approval-floor-verification-2026-10-22.md). A genuinely admitted independent task with current executor authority does not invent a denied human requester. Explicit tenant Always policy and independent source permissions remain effective. The [full proposal](multitenant-iam-delivery-2026-10-16.md) remains in progress.

## Observed integrated behavior

The new Runtime matrix uses canonical HTTP setup and real conversational submissions or due Schedule occurrence admission, the incumbent Runtime/worker, semantic verifier, exact approval APIs and owning stores. Each of nine modes runs against both InMemory and disposable SQLite:

- Human requests omit activation, explicitly enable or explicitly disable; saved parent state matches the requested/default behavior.
- Actual independent tasks omit activation or explicitly enable; both finish with their original task intent and no fabricated initiator or unnecessary approval.
- Human and independent omitted-activation requests under tenant Always policy pause before creation, then commit only after exact ordinary Review. A rejected independent explicit-enable request completes with NoAction and creates nothing.
- An independent task with Automation authoring rights but no `core-events.read` cannot create the watch. It reaches neither semantic verification nor an approval workaround and finishes with NoAction.
- Disabled watches retain enabled child subscriptions. A real completed-Run Core event exists; running the actual tenant event processor admits no Run from the disabled new parent.

The fixture models report blocked outcomes truthfully. Positive cases grant the separate Core-event source read explicitly; the negative case withholds it. No product permission or classifier gate is relaxed for these tests.

## Focused checks

| Check | Observed result |
| --- | --- |
| Serial solution build | Passed, zero warnings/errors, 12.11 s. |
| Application activation, approval floors, authoring contract and pure timing | **133 passed**, no failures/skips. Includes 21 source/explicit-choice creation cases and four existing-status/control cases. |
| Actual Event authoring/default/consent/source-denial matrix | **18 passed**, no failures/skips, 1 m 5 s. |
| Existing canonical Automation read/authoring/consent/replay/delete/manual-invocation and clarification | **81 passed**, no failures/skips, 4 m 35 s. Includes actual SQLite pending-consent host recreation and independent task authoring. |

**232 distinct focused cases pass.** The earlier 103-case application selection overlaps the final 133-case group and is counted only once.

Commands use the repository root:

```text
dotnet build AgentCore.sln --no-restore --nologo -m:1 -nodeReuse:false -p:UseSharedCompilation=false
dotnet test tests/AgentCore.Application.Tests/AgentCore.Application.Tests.csproj --no-build --no-restore --nologo --filter 'FullyQualifiedName~AutomationActivationDefaultTests|FullyQualifiedName~PermissionApprovalFloorTests|FullyQualifiedName~TenantAutomationMutationContractTests|FullyQualifiedName~TriggerScheduleTimeResolutionTests|FullyQualifiedName~TriggerScheduleSemanticsTests|FullyQualifiedName~TriggerScheduleCalculatorTests'
dotnet test tests/AgentCore.Api.Tests/AgentCore.Api.Tests.csproj --no-build --no-restore --nologo --filter 'FullyQualifiedName~Tenant_model_Event_creation_defaults_disabled'
dotnet test tests/AgentCore.Api.Tests/AgentCore.Api.Tests.csproj --no-build --no-restore --nologo --filter 'FullyQualifiedName~Tenant_model_Automation_|FullyQualifiedName~Tenant_model_manual_Automation_|FullyQualifiedName~Tenant_independent_Automation_authors|FullyQualifiedName~Tenant_independent_Automation_metadata_read|FullyQualifiedName~Tenant_model_existing_Automation_deletion|FullyQualifiedName~Tenant_Automation_direct_replay|FullyQualifiedName~Canonical_timezone_clarification|FullyQualifiedName~Canonical_disabled_creation|FullyQualifiedName~Canonical_missing_timezone'
```

Final logs: `/private/tmp/automation-activation-defaults-build-r4-20261023.log`, `/private/tmp/automation-activation-defaults-contracts-final-20261023.log`, `/private/tmp/automation-activation-defaults-runtime-r3-20261023.log` and `/private/tmp/automation-activation-defaults-regressions-20261023.log`.

The first Runtime attempt correctly denied all 16 then-selected cases because the positive fixture lacked independent source-read authority. Adding that permission and a separate denial case exposed six test assertion failures: the final no-dispatch assertion incorrectly passed human Runs to a helper that accepts only Automation origins. Guarding the actual source kind fixed the assertion; the final 18-case run supersedes those attempts. The first build also corrected a test timezone argument and namespace import. These were fixture/setup repairs, distinct from the reproduced product activation default.

This backend module introduces no UI change and claims no new browser or proposal-wide acceptance. Broader service/schema retirement, historical fixture reconciliation, final authority inventory and complete security/isolation/function/UI acceptance remain required. Verification uses offline scripted adapters and disposable stores. No CI, hosted provider, user-data reset, Real restart, push or deployment was performed.
