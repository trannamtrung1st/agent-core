# Cross-feature feedback stabilization — October 16, 2026

The requested fixes are implemented on the checkout starting at `fd6ea780`, which includes the reviewed `5de17558` design sync. The owner selected **focused local checks, commit and push**. This pass does not dispatch CI, restart the Real host or renew release/milestone acceptance.

## Findings closed

- **Built-in Skill fixture:** General Assistant v32 is included among the skill-bearing publications. The existing assertions still require exactly `browser.record.lookup` for each listed version and no Skills on other applicable built-ins.
- **Registry credential race fixture:** verification receives dedicated canonical Core/daemon mount roots. A counted loopback manifest request proves credential replacement was reached exactly once; credential revision advances, provisioning stays at zero and secret sentinels remain absent from the failure message. No production authorization check is relaxed.
- **Email persistence fixture:** initialize the current canonical SQLite baseline, insert a General credential using the schema default, reopen through normal startup and assert unchanged ciphertext, protection version, credential/secret revisions and absent Email Account profile. Retired migration compatibility is no longer asserted.
- **Memory/Experience fixture:** initialize the current baseline, persist explicit empty memory lineage and ordinary Experience, reopen, and verify retained content, empty lineage/maintenance origins, eligible Experience and default-off autonomous consolidation. No historical migration is restored.
- **Workspace default:** the Application resolves omitted, empty and `/` list prefixes from the effective policy: `/working` when HomeAccess is Disabled, otherwise `/home`. The HTTP endpoint passes the optional prefix through. Explicit forbidden Home paths are still rejected. The canonical protocol table is synchronized.
- **Agent guidance:** schedule rejection feedback requests a complete structured proposal, preserves clear intent, and asks only for missing/unsupported timing. Draft evidence grants no authorization. Sandbox recovery describes immediate complete/partial canonical writes and uncertain termination, replacing the retired post-execution synchronization model. The matching sandbox stop documentation is corrected.

## Executed checks

The four reported stale fixtures were first run unchanged: **4 failed**, reproducing the omitted v32 expectation, the registry probe not reaching rotation, the missing historical migration, and the required lineage column.

The final focused local checks pass, **261 tests total**:

| Project | FullyQualifiedName filter terms joined by `\|` | Passed |
| --- | --- | ---: |
| Infrastructure | SkillDefinitionRoundTripTests, RegistryAuthorizationProbeTests, EmailAccountIntegrationTests, IdentityConsolidationContractTests | 76 |
| Infrastructure | CanonicalWorkspaceFilesystemTests, WorkspaceStructureTests, AgentWorkspaceDeletionTests | 27 |
| Application | TriggerScheduleSemanticsTests, ScheduleDraftAdmissionTests, SandboxFailureSummaryTests | 18 |
| Application | ModelTriggerCommandAuthorizerTests, WorkspaceStructureToolContractTests, SessionWorkspaceLifecycleTests, WorkspaceArtifactToolTests, TriggerScheduleRuntimeTests | 62 |
| API | WorkspaceApiTests, AutomationSemanticAuthorizationTests, AutomationAuthorizationJourneyTests, HealthAndSessionLifecycleTests | 65 |
| API | StateIsolationWorkspaceTests, StateIsolationConfigurationTests | 13 |

Commands use `dotnet test tests/AgentCore.<Project>.Tests/AgentCore.<Project>.Tests.csproj --no-restore --nologo --filter 'FullyQualifiedName~<term>|FullyQualifiedName~<term>...'`. The second groups reuse the successful build with `--no-build`; the final State policy group rebuilds the strengthened Stateless regression. These are focused selections, not the full CI suite.

The new HTTP theory writes retained bytes and checks ReadWrite, ReadOnly and Disabled Home. Disabled uses a newly created **Stateless** Session after applying the reviewed preset. Omitted/empty/root list requests return the correct file; read-only Home lists report non-writable nodes; explicit Home listing/file paths remain Forbidden when disabled; `/working` bytes remain readable. The existing policy regression also verifies old-Run read/mutation fencing after policy tightening, preserved owner bytes and stale revision rejection.

The selected API Automation regressions exercise independent exact-action semantics, explicit approval/rejection recovery, persisted outcomes/reopen and Session lifecycle. Application checks cover complete corrected proposals and untrusted draft evidence, independent verifier decisions, workspace structure/artifacts and lifecycle. SQLite checks use temporary databases; registry checks use local loopback fixtures. No hosted provider or registry credentials are required. Opt-in live tests within the selected classes remain opt-in; their default return is not live acceptance evidence.

Changed Markdown references/fences and `git diff --check` pass. No frontend behavior changes were made; application integration tests exercise the affected use cases.

## Remaining qualification boundaries

Hosted Linux AMD64 OpenSandbox acceptance, a complete latest-commit Synthetic workflow and live daily/weekly semantic-authorization UAT remain **unrun** under the selected scope. The intended Real host and user data are unchanged. This report does not claim those release qualifications are complete.

Large-tree scanning remains sampled enforcement. The current command watcher samples every 250ms and checks mounted authority/usage before and after commands; background supervision also samples live leases. This pass preserves those safety checks and does not claim a hard quota or concurrent multi-tenant capacity. Scan/polling budget measurements and tuning remain necessary before such a deployment, as the review recommends. No filesystem architecture or approval framework is replaced.
