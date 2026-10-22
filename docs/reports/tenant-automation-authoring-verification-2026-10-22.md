# Tenant Automation authoring verification — 2026-10-22

Scoped local qualification of the shared authoring and durable transition module. The [delivery ledger](multitenant-iam-delivery-2026-10-16.md) owns remaining integration and proposal acceptance.

`TenantAutomationAuthoring` now constructs the canonical tenant Schedule/Event configuration for the human service. It preserves child IDs/revisions, requirements, explicit destinations, creation provenance and occurrence progress. The service retains its original-human IAM, source, destination, model and concurrency gates. Both incumbent stores independently validate the authoring transition against the actual saved parent; this is not a second authority or execution owner.

Ordinary edits of an active unchanged Schedule retain its selected next occurrence. Previously recurring edits recalculated the next future occurrence and could skip due work; an elapsed one-shot edit was rejected. The preserved due one-shot case now passes current capability policy while remaining due. New, retimed or re-enabled one-shots still require future timing. Timing changes and re-enabling recurring schedules deliberately calculate future work. Durable writes reject forged next-occurrence selection, accepted occurrence counts, suspended authoring states and cancellation disguised as update. Exact cancellation retains configuration and history.

## Executed checks

| Check | Result |
| --- | --- |
| Solution build with no restore, one MSBuild node, node reuse/shared compilation disabled | Pass; zero warnings/errors |
| API integrated due edit → retained state → SQLite reopen where applicable → current executor rights → admission → actual worker completion | 8 passed; one-shot/fixed/daily/weekly × InMemory/SQLite |
| API durable rejection, existing preset/provenance and current-human/occurrence admission matrix | 56 passed |
| Application shared authoring and existing time-resolution checks | 27 passed |

**91 distinct focused cases passed.** The integrated cases observed exactly one completed owned Run using the revised instructions, retained selected timing and child identity, and no duplicate immediate admission. No provider request was made. Rejected store writes left the parent and tenant revision unchanged. Unit boundaries cover elapsed create/retime/re-enable, current policy revocation, new timing, disable/re-enable, event child revision/source replacement, unknown children, cross-owner and terminal edits.

Commands:

```sh
dotnet build AgentCore.sln --no-restore --nologo -m:1 -nodeReuse:false -p:UseSharedCompilation=false
dotnet test tests/AgentCore.Api.Tests/AgentCore.Api.Tests.csproj --no-build --no-restore --nologo --filter 'FullyQualifiedName~Tenant_automation_authoring_preserves_due_occurrence'
dotnet test tests/AgentCore.Api.Tests/AgentCore.Api.Tests.csproj --no-build --no-restore --nologo --filter 'FullyQualifiedName~Tenant_automation_authoring_transaction|FullyQualifiedName~Tenant_automation_preset|FullyQualifiedName~Tenant_automations_use_current_human_scope'
dotnet test tests/AgentCore.Application.Tests/AgentCore.Application.Tests.csproj --no-build --no-restore --nologo --filter 'FullyQualifiedName~TenantAutomationAuthoringTests|FullyQualifiedName~TriggerScheduleTimeResolutionTests'
```

This module changes backend construction and validation, with no schema or frontend change. The integrated fixtures use disposable Synthetic state. Browser verification and optional CI were not run for this module. Canonical model mutation preparation/consent/effect integration is still required; sharing the construction rules does not qualify that path or establish proposal-wide acceptance.
