# Frozen migration metadata consolidation — 2026-10-22

This module follows `b831023b` and addresses the assembly string-heap limit encountered during the remaining IAM implementation. It consolidates **38** historical generated target-model constraint literals into **26** byte-identical embedded SQL resources. Migration history is retained. Harness/Skill authoring, private Skill loading, remedy execution, approval defaults, broader dependency/schema cleanup and proposal-wide acceptance remain open.

## Preserved behavior

Each affected generated `HasCheckConstraint` expression now reads its frozen constraint by SHA-256 from `Persistence/Migrations/Sql/Metadata`. Identical original SQL shares one resource; different historical values retain different resources. `FrozenMigrationMetadata` reads the assembly resource and verifies its exact UTF-8 digest before returning SQL, failing closed on missing or changed metadata. Target models do not consult today's mutable constraint builders.

The change replaces **2,711,198 UTF-8 bytes of literal occurrences** with **1,726,622 bytes of shared resource content**. The built Infrastructure assembly has **13,665,068 bytes** in its CLR user-string heap, leaving **3,112,148 bytes** below the 24-bit offset limit. A focused regression reserves at least 2 MiB of headroom. This removes the immediate compiler constraint on further implementation.

Byte comparisons against the pre-change revision verified that every affected Designer differs only in its exact SQL expression, including preservation of its original BOM choice. All **108** incumbent migration execution and SQL files retain their original SHA-256. No migration ID, Up/Down execution statement, current model/snapshot, table, installed history row, permission or product behavior is changed. No migration is added or squashed, and no installed data is converted.

## Executed evidence

**46 distinct focused cases passed** across the grouped run and focused repair recheck. The serial solution build and final affected-project build passed with zero warnings/errors.

| Check | Expected and observed result |
| --- | --- |
| Original historical target models | **38 passed**: reconstruct each actual EF migration target model and compare its constraint SQL to an independently captured pre-extraction SHA-256. All embedded resource names and exact original bytes resolve successfully. |
| CLR string-heap budget | **1 passed**: inspect the actual built Infrastructure PE metadata and retain at least 2 MiB below the 24-bit user-string offset limit. |
| Historical/current SQLite roundtrip | **1 passed**: migrate a disposable database to `20261019023004_TenantWorkspaceMutationReview`, upgrade fully, downgrade to the historical version and upgrade fully again. Current state retains all **117** exact trigger definitions and index definitions; final `foreign_key_check` returns no violations and there are no pending model changes. |
| Adjacent management migrations | **4 passed**: `TenantManagementInspectionMigrationTests`, `TenantManagementReadMigrationTests`, `TenantManagementMutationMigrationTests`. Their guard/index/FK roundtrips and the prior byte-exact Experience metadata extraction remain intact. |
| Actual Synthetic SQLite application recovery | **2 passed**: pending compound inspection and pending exact management mutation survive real host recreation. Exact child-source consent completes through the incumbent Runtime; the mutation completes once without replay, retaining current Run pins and factual receipts. |
| Source preservation | All 38 generated files differ only in the intended expression; all 108 existing execution/SQL file digests match the pre-change revision. |
| Documentation and Git | Changed local links and fences checked; staged whitespace check passed. |

Commands used:

```text
dotnet build AgentCore.sln --no-restore --nologo -m:1 -nodeReuse:false -p:UseSharedCompilation=false
dotnet build tests/AgentCore.Infrastructure.Tests/AgentCore.Infrastructure.Tests.csproj --no-restore --nologo -m:1 -nodeReuse:false -p:UseSharedCompilation=false
dotnet test tests/AgentCore.Infrastructure.Tests/AgentCore.Infrastructure.Tests.csproj --no-build --no-restore --nologo --filter 'FullyQualifiedName~FrozenMigrationMetadataTests|FullyQualifiedName~TenantManagementInspectionMigrationTests|FullyQualifiedName~TenantManagementReadMigrationTests|FullyQualifiedName~TenantManagementMutationMigrationTests'
dotnet test tests/AgentCore.Api.Tests/AgentCore.Api.Tests.csproj --no-build --no-restore --nologo --filter 'FullyQualifiedName~Canonical_management_inspection_pending_compound_consent_survives_SQLite_host_recreation|FullyQualifiedName~Management_mutation_pending'
```

The initial Infrastructure group passed 43 and failed the new roundtrip's raw index-string comparison. Diagnostic recheck identified seven existing historical indexes recreated with different identifier quoting/spacing. No trigger difference was present. The fixture now compares index tokens, retaining identifier values, literals, operators and filters, while comparing trigger SQL exactly. The focused final roundtrip recheck passed. Counts include each successful identity once; they do not claim the original grouped run was entirely green. No migration execution SQL was edited to accommodate the fixture.

This is focused local consolidation qualification. Backend runtime verification used the existing integrated Synthetic recovery fixtures. No product frontend changed, so browser/build suites were not repeated. CI, live-provider calls, installed user-database migration and complete proposal/milestone acceptance were not run. Broader remaining work stays recorded in the [delivery ledger](multitenant-iam-delivery-2026-10-16.md).
