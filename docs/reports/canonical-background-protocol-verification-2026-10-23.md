# Canonical background protocol and Runtime fixture reconciliation — 2026-10-23

This follows `bed4517c` and advances the [IAM delivery ledger](multitenant-iam-delivery-2026-10-16.md). It reconciles the **19 inherited failures** reproduced against `7da7986f` in the [Sandbox report](global-sandbox-authority-retirement-verification-2026-10-23.md). Canonical tenant execution supplies the qualification; no unowned execution or fabricated authority adapter is restored. Product code, permissions, approval defaults, schemas and consent hashes are unchanged. Other authority roots, internal/schema consolidation and final proposal-wide acceptance remain open.

## Current owning evidence

A new 34-case API/model matrix uses authenticated human setup, an actual owned published Definition/Agent/Automation, current executor grants and canonical manual occurrence/Run admission with InMemory and SQLite. The existing fixture/setup is reused; its clock parameter is generalized to `TimeProvider` so deterministic timer cutoffs can also run. Semantic-capability variants use the existing production `SemanticResponseLanguageModel` wrapper.

Recovery cases first execute an authorized workspace write and read of real owned bytes. Their actual tool receipts, source witnesses and Run checkpoints are retained. Assertions cover:

- No conversational response schema/function/protocol on initial background work, with native and marker-compatible capabilities. Plain prose cannot substitute for completion; the bounded final request offers only named `work.complete`.
- One completion-only repair for invalid blocks, JSON, response-function arguments, marker envelope and output limit. Successful format repair truthfully yields NeedsAttention; repeated semantic failure and invalid completion cannot obtain another generation.
- A transport retry during correction retains the same Run, Activation, response, evidence and allowance. The early generation is fully released before retry; SQLite recreates the application host.
- Exact saved-completion and interrupted-recovery checkpoint boundaries. These cases explicitly seed only a crash-point checkpoint through the scoped owning store on an actually admitted Run, retaining its real read/source/file evidence, then retry/reopen. A saved pending completion executes without another model call; a started recovery with no pending completion fails as interrupted. This is scoped checkpoint simulation plus actual SQLite host recreation, not an operating-system process-kill claim. The seeded cases use three attempts; the natural transport case uses two.
- A provider held during work is interrupted at the Standard 150-second cutoff and receives one completion-only request. Advancing to 181 seconds fails the hard deadline without another generation. Fake time fires the real runtime cancellation timers.
- Response, NoAction and NeedsAttention retain their exact outcomes. Authorized HTTP history verifies the assistant-entry count. Input to a background-only Session is refused without another Run; `continue-in-chat` adds Chat visibility while preserving Background Work, owner and immutable origin. A new authenticated human message then completes in the same Session, with one additional assistant entry and exactly two Runs.

Actual protected reads and task evidence are not inferred from a literal `web.fetch` transcript. Source write/read dispatch each occurs once; completion/retry/checkpoint cases do not replay those tools. Existing independent current source/permission fences remain in production owners.

## Retired fixture scope

**Eight methods / 21 parameterized cases across three files** are retired after qualification: 19 failed in the previous baseline, while the exhausted-repair and hard-deadline cases already passed. Both previously passing boundaries are also represented in the current matrix. Five unused native model/brain helpers are removed; the deadline-only fixture file is removed. Original bodies are recoverable from `bed4517c`. Exact preimage paths/hashes and method names are recorded in `/private/tmp/canonical-protocol-recovery-retirement-20261023.json` and its text manifest.

| Former unowned method | Executed current qualification |
| --- | --- |
| `Headless_background_completion_commits_one_outcome_and_continues_in_same_session` | Six outcome cases: three outcomes in both stores, actual history, explicit visibility transition and same-Session human follow-up. |
| `Headless_retry_retains_ownership_while_the_next_brain_decision_is_pending` | Both-store canonical generation-retirement barrier tests plus natural protocol correction retries; the existing owning host retains the claimed retry rather than an unowned Runtime flag. |
| `Execution_wait_releases_worker_and_resumes_same_response_or_cancels` | Thirteen authenticated wait cases: same Run/attempt/response, released worker, duration/background wait, timeout, cancellation, queued input, read handoff and SQLite restart. Explicit interruption of continued background work separately creates an independent human objective in both stores. Default input follows the current Queue contract; bare unowned attachment is not restored. |
| `Background_work_has_no_conversational_contract_on_either_transport` | Four native/marker-capability cases through the production semantic wrapper and actual tenant worker. |
| `Background_format_failure_uses_one_durable_completion_only_recovery` | Ten successful channel/reason cases and two exhausted repairs, with actual owned evidence and no source-tool replay. |
| `Semantic_correction_cannot_obtain_a_second_generation_through_completion_validation` | Two invalid-completion cases after an actual owned read. |
| `Restart_continues_only_known_transport_retry_or_saved_completion` | Natural correction transport retry and explicit saved-completion checkpoints in both stores; SQLite host recreation and zero extra generation for the saved call. Interrupted reserved recovery is additionally qualified. |
| `Background_work_cutoff_preserves_completion_tool_but_hard_deadline_fails` | Four timer-driven cutoff/deadline cases with real owned source receipts. |

The methods were in `AgentRunDurabilityTests.cs`, `BackgroundSemanticRecoveryTests.cs` and `BackgroundCompletionDeadlineTests.cs`. Remaining native user-turn/controller and checkpoint/provider refusal predicates stay isolated unit evidence. They do not establish tenant resource authority or protected read execution.

## Observed checks

| Focused local check | Result |
| --- | --- |
| First actual owned format recovery / NoAction continuation | 2 passed / 2 failed, 24 s. Format recovery passed. NoAction correctly refused input because the new fixture omitted the documented Chat visibility action; the fixture was corrected without changing product admission. |
| Complete new protocol/outcome matrix | 30 passed / 4 failed, 2 m 16 s. All recovery, checkpoint and timer cases passed. Four outcome assertions incorrectly inspected metadata snapshots, which omit history. |
| All three outcomes using authorized history and explicit continue-in-chat | **6 passed**, 26 s; supersedes those four failures and overlaps two already passing NoAction cases. |
| Existing canonical wait and retry-generation handoff | **15 passed**, 2 m 5 s. |
| Explicit interruption of continued background work | **2 passed**, 31 s. |
| Exact former Application probe after fixture reconciliation | **46 passed**, 2 s, zero failed/skipped; formerly 67 cases, with the 21 obsolete unowned cases replaced above. |
| Final serial solution build | **Passed**, zero warnings/errors, 6.15 s. |

The qualifying union is **97 distinct focused cases passed**: 34 new API cases, 17 existing wait/retry/interruption cases and 46 remaining Application cases. Representative/correction overlaps are counted once. The prior inherited-failure gap is resolved; this does not claim that every other historical fixture or the full proposal has been accepted.

Build: `dotnet build AgentCore.sln --no-restore --nologo -m:1 -nodeReuse:false -p:UseSharedCompilation=false`. Tests use `dotnet test <existing project> --no-build --no-restore --nologo --filter '<filter>'`:

```text
API new: FullyQualifiedName~Tenant_background_protocol_recovery_
API outcome correction: FullyQualifiedName~Tenant_background_protocol_recovery_&(DisplayName~response|DisplayName~needs-attention|DisplayName~no-action)
API wait/retry: FullyQualifiedName~Tenant_wait_keeps_one_Run_and_attempt_|FullyQualifiedName~Tenant_completion_retry_drains_previous_generation_
API explicit interrupt: FullyQualifiedName~Tenant_interruption_of_continued_background_work_
Application: FullyQualifiedName~AgentRunDurabilityTests|FullyQualifiedName~TenantSandboxAuthorizationContractTests|FullyQualifiedName~TenantSandboxInspectionContractTests|FullyQualifiedName~TenantSandboxReadAuthorizationContractTests|FullyQualifiedName~TenantSandboxControlContractTests|FullyQualifiedName~SandboxFailureSummaryTests|FullyQualifiedName~SandboxSetupResolverTests
```

Logs use `/private/tmp/canonical-protocol-recovery-`: `first-runtime-20261023.log`, `api-20261023.log`, `outcomes-final-20261023.log`, `wait-retry-20261023.log`, `interrupt-final-20261023.log`, `application-final-20261023.log` and `build-final-20261023.log`. An initial compile found a nonexistent test cleanup helper; cleanup now follows the established fixture lifecycle. Changed-document links/fences and whitespace checks pass. Verification is Synthetic with disposable roots and no hosted provider, installed-data reset, Real restart, Docker rerun or CI. No product UI changed; rendered UI and full proposal acceptance remain separate.
