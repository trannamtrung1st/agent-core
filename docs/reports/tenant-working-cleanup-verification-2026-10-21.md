# Tenant working-file retention verification — 2026-10-21

Implemented on main from `dd128749`. This completes the canonical tenant working-file retention slice; the full multitenant IAM/approval proposal and milestone acceptance remain open.

## Behavior and ownership

The existing InMemory/SQLite IAM stores expose a technical retained-owner cleanup facet. Request isolation and working cleanup share bounded Session/Run/causal-child/completion facts and settlement predicates; active strict request completion retains its separate lifecycle/policy guard. No Profile fallback, ambient tenant, user-selected physical path, new Runtime, execution queue, schema or migration is introduced.

Default `OnSessionDeletion` retains working files after completion, including Stateless requests. `AfterTerminal` uses the latest admitted immutable Run policy, or the immutable creation policy when no Run was admitted. Later Instance configuration cannot retroactively select cleanup. Committed deletion authorizes cleanup under either retention policy. Archive alone retains files. Pending input, nonterminal or Prepared/InFlight/Indeterminate effects, causal child work, unaccounted reporting and unconfirmed owned Sandbox leases defer cleanup.

The exact target binds immutable tenant/Session/Instance/executor ownership, Session revision/deletion timestamp, policy Run/revision and lease generation/revision. The same FileSessionWorkspace owner repeats that proof under its provisioning gate and purges only the canonical Session tree, including the provisioning marker, using the existing handle-based no-follow implementation. A bounded shared purge gate serializes cleanup attempts; terminal/deleted candidates do not leave a permanent per-tombstone provisioning gate. Home, sibling and child working trees, external link targets, Artifact bytes and retained metadata/receipts remain independent.

The existing startup/five-minute WorkspaceCleanupHostedService scans up to 100 candidates per pass, advances/wraps its cursor and isolates target failures. Eligibility survives cancellation, failure and process restart; a missing already-purged tree converges idempotently. Human lifecycle commits do not wait for byte cleanup.

## Executed focused evidence

**54 distinct focused cases passed, zero skips**, counting each case once across the grouped runs and focused corrections:

| Cases | Scenario and observed outcome |
| --- | --- |
| 9 API retention/recovery | Both stores: actual HTTP creation, accepted Synthetic text, request completion, opposite current retention edits, exact replay and deletion. Admission pins decide retention; default files remain, AfterTerminal files disappear. History/replay remain factual. Workspace inspection returns empty without recreating files. Home/sibling/external link targets remain. Unused Session completion uses its creation pin; archive retains files. Actual SQLite application-host recreation across an unused Session deletion/bytes gap lets the startup worker purge the tree with zero admitted Runs. |
| 4 API causal children | Both stores, reporting/nonreporting: hold a real child model call, delete the parent, verify cleanup refuses while child work remains. Deleted-source authority correctly fences child continuation: one already-entered child call, no later generation, a terminal Failed Run with no pending effect and the precise expected authority error. Reporting stays blocked until the unavailable-parent report is accounted Skipped, without another model call. Parent files then disappear; child files remain. |
| 8 native/store boundaries | Both stores: repeated commit checks, forged/stale witnesses, cancellation, injected native failure, retry/idempotence and independent retained Artifact bytes. Four substituted root levels (Session, sessions parent, Agent and workspace root) refuse removal and preserve external files; safe restoration allows retry. All five tested non-Terminated lease states block cleanup; exact Terminated generation/revision permits it and changed revisions refuse stale native proofs, including SQLite adapter reopen. |
| 19 affected request regressions | Human exact retry/admission boundaries, no-op scan versus unrelated dispatch, actual JavaScript MessagePack terminal/reconnect and causal child/report/quiet/child-first settlement remain passing. |
| 14 affected Artifact regressions | Committed deletion, owner/deletion witness, archive/terminal retention, current grants, metadata preservation, paging/failure isolation, substituted roots and SQLite reopening remain passing. |

Commands used, from repository root (all test runs use `--no-build --no-restore --nologo`):

```sh
dotnet build AgentCore.sln --no-restore --nologo -m:1 -nodeReuse:false -p:UseSharedCompilation=false
dotnet build tests/AgentCore.Api.Tests/AgentCore.Api.Tests.csproj --no-restore --nologo -m:1 -nodeReuse:false -p:UseSharedCompilation=false
dotnet test tests/AgentCore.Api.Tests/AgentCore.Api.Tests.csproj --no-build --no-restore --nologo --filter FullyQualifiedName~TenantAgentAccessTests.Tenant_working_cleanup
dotnet test tests/AgentCore.Api.Tests/AgentCore.Api.Tests.csproj --no-build --no-restore --nologo --filter 'FullyQualifiedName~TenantSessionAccessTests.Tenant_working_cleanup|FullyQualifiedName~Tenant_request_isolation'
dotnet test tests/AgentCore.Infrastructure.Tests/AgentCore.Infrastructure.Tests.csproj --no-build --no-restore --nologo --filter 'FullyQualifiedName~Tenant_working_cleanup|FullyQualifiedName~Deleted_Artifact_cleanup'
```

Solution and final API-test-only builds pass with zero warnings/errors. Focused rechecks reran the eight new native cases and four causal cases after fixture corrections; unaffected passing regressions were not redundantly repeated. Initial corrections covered missing test imports, xUnit assertion overload, canonical layout initialization before seeding, and the held Synthetic call being recorded upon release. Deliberate original-source revocation is asserted explicitly rather than treated as successful child work or suppressed as an unexplained error.

Logs: `/private/tmp/tenant-working-cleanup-{api,causal,infrastructure,native-final,causal-final}.log`; TRX evidence: `/private/tmp/tenant-working-cleanup-results/`. The aggregate first runs retain their initial failures; final distinct outcomes supersede only corrected cases. Final build logs: `/private/tmp/tenant-working-cleanup-build.log` and `/private/tmp/tenant-working-cleanup-api-build.log`.

## Limits and remaining goal

The proof retains the existing conservative inventory limits: at most 100 parent Runs, 100 causal child Sessions, 1000 child Runs and 1000 receipts, with an overflow lookahead and incomplete-proof refusal. Oversized/incomplete owners remain retained and deferred; partial inventory never establishes cleanup permission. Cleanup is eventual and can wait for existing resource reconciliation or a later scan. The startup restart sample is an unused deleted Session; admitted terminal policy/replay are separately exercised on both stores. Metadata fixtures establish conservative lease gating, not live OpenSandbox termination or remote process absence. Native filesystem evidence is local macOS, not Linux/Windows platform acceptance.

No frontend source/contract changed, so backend HTTP/native integration supplies runtime evidence; no new browser visual acceptance, frontend build, physical audio, live vendor or hosted CI claim is made. Product/Design context and its existing sidecar are unchanged in this slice. Final proposal-wide design/context synchronization remains required.

Broader tenant/user-owned Profile, structured Memory and continuity effects/live narrowing; rich input and Skill/reference authoring/admission; conversation locale/model and draft/Drive previews; Harness/Experience/maintenance execution; remaining internal authority/store/schema consolidation and platform guardrails; and the exhaustive original-proposal requirements/routes/effects/security/design/commands/deliverables audit remain open. The original goal stays active.
