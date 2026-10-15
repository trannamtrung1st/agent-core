# Three-layer context cutover verification

Status: **local implementation complete; AC-01–AC-17 verified.** This owner-requested replacement does not reopen an application milestone. No hosted CI, live provider call, user database conversion, commit, push or deployment was performed.

## Delivered architecture and producer inventory

The initial exhaustive search found 428 matches across source, tests, shipped agents and canonical/historical docs. The final production audit finds **one `new ModelRequest`**, in `ContextAssembler`. Historical reports and EF migrations retain their original facts.

| Owner | Final path |
| --- | --- |
| `PromptContextBuilder` / `DefaultAgentBrain` | Immutable typed sections; selected Core procedures; Runtime configuration; bounded Evidence and native interaction roles |
| `SessionRuntime.PumpModelAsync` | Fresh projection per attempt; section-identity upserts; separate interaction transcript; manifest includes the selected semantic-output protocol |
| Initiative / completion evaluation | Common assembler and selected Core protocol; initiative retains complete authored instructions and their field source |
| `ConversationCompactor` | Common assembler, compaction Core protocol, bounded historical Evidence, no tools |
| Schedule command classifier | Core classification protocol; Runtime proposed action; Evidence referents/current request |
| Draft Synthetic behavior evaluator | Common assembler; Runtime candidate/fixture facts; Evidence resources/task |
| Semantic response adapter | `PrepareRequest` exposes the selected Application-owned Core output section before manifest recording; forwarding upserts the same identity |
| Chat Completions / Responses | Explicit developer-role serialization; no downgrade; original tool IDs, multipart content and continuation state |
| Published Definitions / drafts / Session Definition snapshots | Explicit one-way schema-2 conversion to `agentInstructions` |
| Instance settings | Sparse per-field conversion; inheritance and optimistic revisions preserved |
| Run configuration / activation | Frozen per-field sources, resource/credential/configuration pins; canonical hash recomputation during conversion |
| Automation / manual / immediate background admission | Bounded typed `TaskIntent` frozen from the admitted owner configuration/objective; raw trigger text cannot replace it |
| Evaluation / management history | Immutable historical evidence; old candidate fingerprints do not satisfy current publication gates; retired input cannot revive current configuration |
| Tool-loop checkpoint | Format **2**, Core contract **1**; interaction only, typed phase/finalization/cleanup/repair/activation state, response transcript identity, redacted manifest |
| Admin and shipped fixtures | Existing instruction editors renamed; owner-protected, read-only three-group provenance; all 18 shipped JSON definitions and Compose fixture use the current field |

Only Core factories can produce `system` content, from platform code/current built-in constants. Runtime projects to `developer`. Evidence preserves native user/assistant/tool roles and explicit current-versus-historical semantics. The optional future `RuntimeEnvironmentScope` stays absent; current `RoleEnvironment` remains Definition-owned configuration. No new Environment, permission engine, context editor or Session lifecycle was introduced.

Voice delivery and attachment/Office reading procedures are selected Core rules. Runtime mode/locale/capability sections carry facts; tool-free executions omit the reread procedure and tool name.

Core and Runtime fail rather than truncate above their separate hard budgets. Optional Evidence sections are deterministically omitted with coverage metadata; existing history/attachment/Office selection and current-turn protections remain in place. Byte-based token figures are estimates, not model-tokenizer measurements.

## Acceptance matrix

| ID | Result and observed evidence |
| --- | --- |
| AC-01 | Pass: assembler source/semantics/identity/immutability validation, required operating contract, deterministic fingerprint; exhaustive producer audit |
| AC-02 | Pass: Core/system, Runtime/developer, Evidence/native roles in assembler and both wire transports; authoritative transcript roles refused |
| AC-03 | Pass locally: explicit Chat Completions/Responses mapping and Synthetic execution; undeclared/unknown/mismatched routes refused before HTTP/admission. Real remote semantics remain opt-in |
| AC-04 | Pass: sparse Definition/Instance effective values and field sources; reset/clear restores Definition; initiative uses the same provenance |
| AC-05 | Pass: old Session/new Run API journey refreshes settings/resources while old Run stays pinned; waiting Run preserves configuration/content/budget through adoption and SQLite reopening |
| AC-06 | Pass: dynamic/explicit Skills, capability loads and management visibility; typed identity replacement; current Core built-ins never reconstructed from stored authored bytes |
| AC-07 | Pass: memory/profile/summary/Experience/Continuity/resources/attachments stay Evidence; trust/approval/owner/credential checks remain enforced |
| AC-08 | Pass: current batch once; delivered/heard prefixes; summary boundaries/compaction; attachment extraction/recall and Office coverage tests |
| AC-09 | Pass: Automation/background/native events/initiative/completion/compaction use common assembly; typed admitted task defeats forged trigger/historical replacement |
| AC-10 | Pass: Skill/capability/tool/browser/dialog/cleanup/finalization continuations reproject Core/Runtime ahead of interaction without duplicate identities |
| AC-11 | Pass: checkpoint pending calls/results/continuation, wait/resume, capture handling, completion and budget/effect fences; SQLite reopens and native browser cleanup journeys |
| AC-12 | Pass: retired/unknown checkpoint refusal; active preflight denial; completed historical checkpoint retained byte-for-byte as inert audit evidence |
| AC-13 | Pass: schema-2 seeds/domain/API/UI; old field rejected; explicit backup/conversion/rollback/hash/no-op/reopen tests and populated CLI proof |
| AC-14 | Pass: Admin Form/JSON/save/invalid-input recovery; Instance override/discard/reset/next-Run; read-only redacted provenance; Vitest and live Synthetic Playwright |
| AC-15 | Pass: no current legacy builder, old DTO alias, prefix-based context replacement, checkpoint suffix slicing, marker restoration or provider-role downgrade |
| AC-16 | Pass: focused approval, model eligibility, budget, browser credentials/dialog/cleanup, sandbox cleanup/policy, scheduling, workspace owner/scope/isolation and revocation regressions |
| AC-17 | Pass: canonical docs/API/product labels and maintenance steps synchronized; responsive Impeccable evidence recorded; no shared design primitives changed |

## Focused local checks

Environment: macOS arm64, .NET SDK 10.0.401/runtime 10.0.12, Node 22.18.0, pnpm 9.6.0. The sandbox initially denied the .NET test runner's local communication socket; successful runs used local-socket permission. No test contacted a hosted model.

Counts below are per successful invocation and overlap; do not add them into a distinct-test total.

| Check | Result |
| --- | --- |
| Main Application context/Skill/approval/budget/attachment/tool-loop regression set | **255 passed, 1 opt-in hosted test skipped** |
| Additional native browser/Responses reopen/finalization/scheduling/initiative/admission set | **170 passed, 3 opt-in hosted/process tests skipped** |
| Final assembler/initiative/source/model-eligibility/workflow boundary recheck | **30 passed** |
| Final Core rules / Runtime facts / Synthetic voice locale+playback / memory / attachment / Office / Skills | **127 passed** |
| Checkpoint budget and Office sparse coverage | **15 passed** |
| Workspace owner/scope/approval/lifecycle | **16 passed** |
| Infrastructure transport/semantic response/catalog/Run store/Definition store/migration set | **336 passed, 5 live probes skipped** |
| Final provider declaration/mismatched-route/semantic-manifest/migration recheck | **91 passed** |
| Fake-provider sandbox cleanup, SQLite workspace and Skill pin isolation | **50 passed** |
| API Admin/Automation/scoped configuration/management boundaries | **142 passed** |
| API sandbox policy/credential redaction/Run pins | **16 passed** |
| API Office / Experience / Continuity evidence and injection journeys | **23 passed** |
| Domain Definition schema and Run contracts | **48 passed** |
| Admin + provenance Vitest | **367 distinct tests across 50 files passed** across the initial pass and serial recheck described below |
| Web production build | Pass (`tsc --noEmit` + Vite); existing large-chunk/SignalR annotation warnings |
| Static/document checks | `git diff --check`, Compose fixture `bash -n`, all shipped JSON shapes, local links in canonical docs/report: pass |

The wider Vitest invocation initially passed 357 tests but timed out ten tests in four files under parallel load (including one missing-element assertion). Those exact four files passed **56/56** with `--maxWorkers=1`, including every failed test, without product/test changes to those files. The other 46 files account for 311 distinct passing tests. Existing AntD List deprecation and jsdom `NaN` height warnings remain; the live changed surfaces had no console errors after the verification host became ready.

### Exact commands

From the repository root; test commands used `--no-restore --nologo`. Build-enabled invocations additionally used `--disable-build-servers -m:1 -p:UseSharedCompilation=false`.

```bash
dotnet test tests/AgentCore.Application.Tests --no-restore --nologo --disable-build-servers -m:1 -p:UseSharedCompilation=false --filter 'FullyQualifiedName~ContextAssemblerTests|FullyQualifiedName~PromptContextBuilderTests|FullyQualifiedName~SummaryBoundaryPromptTests|FullyQualifiedName~MessagingSkillJourneyTests|FullyQualifiedName~AgentRunDurabilityTests|FullyQualifiedName~RunBudgetDiagnosticsTests|FullyQualifiedName~InstanceSettingsResolverTests|FullyQualifiedName~LongSessionCompactionTests|FullyQualifiedName~InitiativeEvaluatorTests|FullyQualifiedName~CompletionEvaluatorTests|FullyQualifiedName~PreferredNameProvenanceTests|FullyQualifiedName~SessionMemoryPromptTests|FullyQualifiedName~CompactionLifecycleTests|FullyQualifiedName~DynamicSkillActivationTests|FullyQualifiedName~SkillActivationTests|FullyQualifiedName~ToolApprovalTests|FullyQualifiedName~ToolRegistryPolicyTests|FullyQualifiedName~ApplicationMessageToolPolicyTests|FullyQualifiedName~ExecutionBudgetRuntimeTests|FullyQualifiedName~ToolExecutionBudgetTests|FullyQualifiedName~BrowserScreenshotRecoveryTests|FullyQualifiedName~BrowserCloseRecoveryRuntimeTests|FullyQualifiedName~BrowserDynamicRecoveryRuntimeTests|FullyQualifiedName~BrowserLogoutRecoveryTests|FullyQualifiedName~BrowserLifecycleTests|FullyQualifiedName~SandboxFailureSummaryTests|FullyQualifiedName~AttachmentProcessingRuntimeTests|FullyQualifiedName~AttachmentBindTests|FullyQualifiedName~QueuedAttachmentRuntimeTests|FullyQualifiedName~SessionAttachmentRecallTests|FullyQualifiedName~WorkspaceArtifactToolTests|FullyQualifiedName~ExecutionModelPolicyTests|FullyQualifiedName~SessionModelBinderTests'

dotnet test tests/AgentCore.Application.Tests --no-restore --nologo --disable-build-servers -m:1 -p:UseSharedCompilation=false --filter 'FullyQualifiedName~ResponsesCheckpointRecoveryTests|FullyQualifiedName~TerminalDisplayRepairTests|FullyQualifiedName~GenerationRetryTests|FullyQualifiedName~HistoricalImageSyntheticContinuationTests|FullyQualifiedName~CheckpointBudgetMeasurementTests|FullyQualifiedName~BrowserModelCompatibilityTests|FullyQualifiedName~ExecutionBudgetChromiumTests|FullyQualifiedName~CapabilityUsageGuidanceTests|FullyQualifiedName~EffectReceiptJourneyTests|FullyQualifiedName~TriggerOccurrenceRoutingTests|FullyQualifiedName~TriggerScheduleTimeResolutionTests|FullyQualifiedName~RoleEnvironmentTests|FullyQualifiedName~InitiativeEvaluatorTests|FullyQualifiedName~ContextAssemblerTests|FullyQualifiedName~ToolWorkflowRuntimeTests|FullyQualifiedName~SessionModelBinderTests'

dotnet test tests/AgentCore.Infrastructure.Tests --no-restore --nologo --disable-build-servers -m:1 -p:UseSharedCompilation=false --filter 'FullyQualifiedName~ThreeLayerProviderTests|FullyQualifiedName~ContextCutoverTests|FullyQualifiedName~OpenAICompatibleLanguageModelTests|FullyQualifiedName~ResponsesTransportTests|FullyQualifiedName~SemanticResponseLanguageModelTests|FullyQualifiedName~AgentRunAdmissionStoreTests|FullyQualifiedName~AgentDefinitionAdminStoreContractTests|FullyQualifiedName~ModelCatalogFactoryTests'

dotnet test tests/AgentCore.Api.Tests --no-restore --nologo --disable-build-servers -m:1 -p:UseSharedCompilation=false --filter 'FullyQualifiedName~AdminApiTests|FullyQualifiedName~AutomationJourneyTests|FullyQualifiedName~ScopedInstanceConfigurationTests|FullyQualifiedName~HarnessManagementBoundaryTests|FullyQualifiedName~HarnessManagementRecoveryTests'

dotnet test tests/AgentCore.Domain.Tests --no-restore --nologo --disable-build-servers -m:1 -p:UseSharedCompilation=false --filter 'FullyQualifiedName~AgentDefinitionValidatorTests|FullyQualifiedName~AgentRun'
```

Additional successful invocations used the same test flags with these project/filter pairs:

- Application, build enabled: `FullyQualifiedName~ContextAssemblerTests|FullyQualifiedName~InitiativeEvaluatorTests|FullyQualifiedName~SessionModelBinderTests|FullyQualifiedName~ToolWorkflowRuntimeTests`.
- Application, `--no-build`: `FullyQualifiedName~AgentRunToolCallCheckpointTests|FullyQualifiedName~OfficeSparseCoverageTests`.
- Application, `--no-build`: `FullyQualifiedName~SessionWorkspaceLifecycleTests|FullyQualifiedName~WorkspaceStructureToolContractTests`.
- Infrastructure, build enabled: `FullyQualifiedName~ModelCatalogFactoryTests|FullyQualifiedName~ThreeLayerProviderTests|FullyQualifiedName~SemanticResponseLanguageModelTests|FullyQualifiedName~ContextCutoverTests`.
- Infrastructure, `--no-build`: `FullyQualifiedName~OpenSandboxCleanupTests|FullyQualifiedName~WorkspaceStructureTests|FullyQualifiedName~SkillPinStoreTests`.
- API, build enabled: `FullyQualifiedName~SandboxApiTests`.
- API, build enabled: `FullyQualifiedName~OfficeJourneyTests|FullyQualifiedName~ExperienceJourneyTests|FullyQualifiedName~ContinuityBoundaryTests`.
- Application, build enabled: `FullyQualifiedName~PromptContextBuilderTests|FullyQualifiedName~ContextAssemblerTests|FullyQualifiedName~SpeechLocaleRuntimeTests|FullyQualifiedName~PreferredNameProvenanceTests|FullyQualifiedName~MemoryProposalAdmissionTests|FullyQualifiedName~CapabilityUsageGuidanceTests|FullyQualifiedName~SkillActivationTests|FullyQualifiedName~DynamicSkillActivationTests|FullyQualifiedName~InitiativeEvaluatorTests|FullyQualifiedName~AttachmentProcessingRuntimeTests|FullyQualifiedName~SessionAttachmentRecallTests|FullyQualifiedName~TriggerScheduleTimeResolutionTests`.

```bash
cd web
pnpm exec vitest run src/features/admin src/features/chat/ContextProvenance.test.tsx
pnpm exec vitest run src/features/admin/SandboxProfilesSection.test.tsx src/features/admin/InstanceAutomationsSection.test.tsx src/features/admin/InstanceContinuitySection.test.tsx src/features/admin/InstanceSkillsSection.test.tsx --maxWorkers=1
pnpm run build
```

Successful logs and screenshots are retained locally in `local/verification/three-layer-context-20261015/` (ignored verification artifacts).

## Representative request and durable continuation

Previously identity, authored instructions and remembered summary occupied `system`. The current request projects:

```text
system     OperatingContract       AgentCore / Core contract 1
system     selected Core clauses   AgentCore / current BuiltInSkill
system     semantic output rule    platform-selected, before all Evidence
 developer AgentIdentity           Instance persona revision 1
 developer AgentInstructions       Instance revision 2, or Definition general-assistant:31 after reset
 developer Policies/capabilities   frozen Run configuration and trusted execution facts
user       Summary/Memory/etc.     source-framed Evidence
user       current task            exactly once; actual current-turn parts retained
assistant  tool-call batch          original IDs and opaque continuation state
tool       receipts                matching call IDs, never authoritative roles
```

The manifest stores section IDs, source kinds/revisions, bounded sizes/omissions, per-field sources and a fingerprint. It contains no prompt bodies, profile/memory/user text, tool arguments/results, keys or images.

`MessagingSkillJourneyTests` exercises load → send → SQLite reopen/next-turn behavior, asserting unique typed identities and authoritative sections before the tool transcript. `ResponsesCheckpointRecoveryTests` persists a two-call batch with the first effect already complete, reopens SQLite and executes only the unanswered call; both counters end at one and the original continuation and receipt identities survive. Native recovery tests preserve blocked-effect fences, pending corrected browser closure, dialog wait/resume, requested cleanup and finalization budgets.

## Live Synthetic UI and Impeccable pass

Used API `127.0.0.1:5188` and Vite `127.0.0.1:5189` with a new disposable SQLite database and separate attachment/workspace/artifact/resource/credential-key roots. Browser and OpenSandbox providers were disabled in this UI host; native browser integration tests used their own local fixtures. No user's active host or database was restarted/reset. Both verification hosts were stopped after the journey.

1. Created General Assistant v31 Instance Riley. Saved Agent instructions override at revision 2; helper text explains Runtime scope and immutable Core permission/approval boundaries.
2. Completed a Chat turn. Admin Run details showed Instance instruction source revision 2 and no editable/raw context fields.
3. Reset instructions, discarded the unsaved reset and confirmed the override remained. Reset again and saved revision 3; the inherited Definition instructions returned.
4. Reopened the disposable SQLite host and continued the **same Session**. Its next completed Run showed Instance revision 3, persona revision 1 and Definition instruction source `general-assistant:31`; the original Run retained its old pin.
5. Created Definition draft `context-verification`; saved Form instructions at draft revision 2. Advanced JSON showed schema 2 and only `agentInstructions`. Invalid JSON disabled save and preserved the revision. Repaired JSON, saved revision 3 and returned to Form: the exact edited instruction value was present.
6. Reopened again after manifest refinement and completed another turn. Run `01a13f28-436d-7017-b89c-8a77e15387e4` displayed Core 9 / Runtime 10 / Evidence 9, including `CoreSemanticResponse`, with no context editor/raw instruction or user text in the provenance group.
7. Checked Definition/Instance instruction and provenance layouts at **320, 390, 768 and 1440 px**: no document horizontal overflow. Long IDs wrap. Disclosure controls support keyboard Enter, visible focus and Escape return to the initiating Run link. Existing typography/tokens/AntD controls were reused; the provenance item spacing uses `Flex`/`token.paddingXS`.

Evidence includes `definition-instructions-{320,390,768,1440}.png`, `instance-instructions-{320,390,768,1440}.png`, `provenance-final-{320,390,768,1440}.png` and `provenance-final-protocol.png` in the local verification directory. Desktop and narrow/tablet screenshots were visually inspected. No shared primitive/product concept changed, so `.agents/context/DESIGN.md` and `PRODUCT.md` remain unchanged.

The intentional disposable-host restarts briefly produced proxy connection errors; readiness and affected reads recovered to 200. The final stable Admin journey reported zero console errors. These interruptions did not alter the user's application.

## One-way data/checkpoint proof and deployment actions

`ContextCutoverTests` verifies published/draft/Instance conversion, original backup, unchanged revisions/ownership, invalid/mixed payload rollback, active retired-checkpoint refusal, completed receipt retention, canonical configuration/admission hashes, reopening and a zero-change repeat.

The **documented CLI itself** was also run on an isolated populated old-schema fixture copied from the Synthetic UI database: one Session, three sealed Runs, one authored draft and one Instance. It converted **five retained records**, preserved all row counts and produced an original schema-1 backup. `PRAGMA quick_check` returned `ok` for both databases. Repeating with a new backup converted **zero records**. This did not convert the user's database.

Before deployment to populated storage:

1. Use the previous application to drain/complete or deliberately cancel incompatible nonterminal Runs. Stop all writers and back up SQLite plus associated data roots.
2. Run `dotnet run --project src/AgentCore.Api --no-launch-profile -- migrate-context /absolute/database.db /absolute/new-backup.db` with the new build.
3. Start the new build. Current schema/checkpoint preflight must pass. Unsupported active formats are refused; they are never translated/replayed. Completed retired checkpoints remain readable inert history.
4. Declare verified separate-role support for custom model/transport routes. The variable Free router is ineligible. Changed/missing pinned routes fail explicitly; no fallback or pin rewrite occurs. Downgrade requires restoring the matching previous application and complete backup.

No deployment blocker remains in local implementation. Applying conversion to real data, restarting the user's host, hosted CI and live provider qualification are **unperformed operator/opt-in actions**, not local acceptance claims.

## Provider coverage and documentation closure

Actually exercised: Synthetic Scripted Alpha through live Chat/Admin; offline scripted conversation/background/Automation/initiative/completion/compaction; fake-provider Chat Completions and Responses HTTP/SSE serialization, native/function/text semantic channels, multipart images, tool IDs and durable opaque continuation. Catalog tests cover Alpha/Beta/Vision capabilities and explicit selection/ineligibility. No live DeepSeek, GPT-6 Luna, Haiku, GPT-6.1 Sol, Free router or hosted speech request was made. Catalog support declarations do not establish untested remote hierarchy semantics.

Architecture, interfaces, backend implementation, frontend behavior, protocol, persistence/configuration, testing, operations, implementation plan, README and TODO describe the single current path. Historical reports/migrations remain historical. Final static audit permits the retired JSON key only in the explicit offline converter/preflight rejection and intentional historical/negative fixtures; no current alias/read path survives. The Compose fixture was updated and shell-syntax checked; Compose itself was not run.
