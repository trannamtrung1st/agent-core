# Canonical Automation integration qualification — 2026-10-23

This closes the private Skill event-origin qualification gap and replaces the four obsolete global-router configuration fixtures with actual canonical tenant workflows. It follows [generic management retirement](generic-management-retirement-verification-2026-10-23.md). The original IAM goal remains active in the [delivery ledger](multitenant-iam-delivery-2026-10-16.md).

## Actual workflows

The existing independent private Skill matrix now covers Scheduled, manual, Core and webhook origins with both InMemory and SQLite. Direct reads activate the exact pinned private procedure without an unnecessary approval. Explicit `skills.read` Review withholds the procedure and leaves the load count at zero until an exact witnessed decision. Approve activates it once; reject and missing raw Skill-read permission expose no procedure and activate nothing. Read consent creates no operation grant and no configuration or management authority. Core/webhook admissions have no fabricated human initiator/Profile, and event input supplies no delegation.

Core cases use an actual authorized persona change to produce `instance.config_changed`. Webhook cases use the protected source creation and bearer ingress. Both register through canonical Automation authoring and use the existing event processor, receipt/delivery/occurrence owner and worker. The test correlates the exact event, child trigger, occurrence and admitted Run, checks actual origin and the adopted Instance revision, and excludes Chat management/background-start capabilities. Four SQLite cases dispose the original host with consent pending, recreate it over the same private roots, verify the exact action/read selection and original event lineage, then approve or reject through a new authenticated login.

The replacement configuration cases create and publish an owned Definition, create a Session with explicit high model effort, save a local output-token override and Resource, publish/adopt version 2, and admit due scheduled work through `TenantAutomationService`. Fresh/background and existing-Session targets run to completion with the new publication/settings/resource pins. Existing Session provenance, speech-locale override and explicit model selection remain unchanged; its occurrence retains high effort. Existing-Session work has no Agent Session-create grant. No `TriggerOccurrenceRouter` registration or global authority path is restored.

## Repairs

The new event matrix passed, while two older private-Skill SQLite recovery fixtures reproduced `data_reset_required` after their deliberate downgrade-refusal test. Safe later Down steps had committed before the retained private-source guard refused, leaving the disposable database on a partially older schema. The fixtures now roll forward with the existing migrator and compare the entire applied migration sequence before reopening. Production schema admission and downgrade guards remain strict; no database reset or weakened check is introduced.

The four old `ScopedInstanceConfigurationTests.Fresh_occurrence_resolves_adopted_configuration_and_retains_existing_session_effort` cases requested an already-unregistered global router. Their exact method is retired after replacement with current tenant publication/settings/resource/admission/worker checks. The remaining shared fixtures and configuration tests are preserved. Other historical global fixtures and internal authority retirement remain separate work.

## Executed checks

| Check | Result |
| --- | --- |
| Serial solution build, `--no-restore --nologo -m:1 -nodeReuse:false -p:UseSharedCompilation=false` | Final build passes, zero warnings/errors, 11.49 s. |
| First integrated independent Skill selection | 4 InMemory direct-origin cases pass, including actual Core and webhook admission/activation. |
| Complete independent private Skill matrix | 32 pass: original Scheduled/manual qualification plus 20 Core/webhook, missing-read and actual SQLite host-recreation cases. |
| Repaired SQLite recovery, canonical occurrence replacement and retained configuration | 12 pass: four current tenant occurrence cases, two corrected private-read SQLite reopen cases and six preserved configuration cases. |

**44 distinct API cases pass.** The four initial direct cases overlap the complete matrix and are counted once.

Backend commands use `dotnet test tests/AgentCore.Api.Tests/AgentCore.Api.Tests.csproj --no-build --no-restore --nologo` with these exact filters:

```text
First: FullyQualifiedName~Independent_Automation_loads_private_Skills&DisplayName~sqlite: False&DisplayName~core&DisplayName~direct
Matrix: FullyQualifiedName~Independent_Automation_loads_private_Skills|FullyQualifiedName~Private_Skill_sources_and_exact_pending_read_consent
Recheck: FullyQualifiedName~Fresh_tenant_occurrence_pins|FullyQualifiedName~Private_Skill_sources_and_exact_pending_read_consent|FullyQualifiedName~ScopedInstanceConfigurationTests
```

The first filter matched all four InMemory direct origins because the fully qualified display names contain AgentCore; its observed count is four. The matrix initially had 32 passing event/independent cases and the two schema-fixture failures disclosed above. Only the failed/replaced/retained-configuration selection was rechecked after that fixture-only correction; the successful independent matrix was unchanged.

Logs: `/private/tmp/private-skill-events-{build,build-r2,build-r3,build-r4,first,matrix}-20261023.log` and `/private/tmp/canonical-automation-integration-recheck-20261023.log` (brace notation describes separate files). Initial compilation corrected the test namespace, nullable anonymous source shape and event-activity projection together. This is fixture and acceptance work; production contracts, permissions, approval defaults, Domain storage and runtime code are unchanged.

## Remaining scope

This backend qualification does not claim a new UI/browser or hosted-provider acceptance pass. All state was disposable Synthetic; no user database, Real host, CI, push or deployment was used. The original proposal's final security/isolation/functional/UI matrix, remaining generic/global authority and fixtures, historical Harness hash/consent/schema cutover and safe dependency/migration consolidation remain open. Earlier four-router-fixture failures are superseded only for the replaced configuration workflow; other obsolete global-router fixtures still require reconciliation.
