# Capability authorization and projection verification

This follow-on implements `local/proposals/agent_core_capability_authorization_projection_final_proposal.md` on the reviewed workspace baseline `d78aaa35`. It composes with the accepted Agent Workspace refinement and does not reopen frozen milestones or start P10/P11. Local gates are verified below. Hosted Synthetic acceptance is pending the enhancement's own behavior commit and run; earlier workspace receipts are not evidence for this change.

## Authority and projection

Selected and All authoring persist exact registered names and a SHA-256 authorization fingerprint. All resolves the current valid registry snapshot for the Definition's workspace contract before draft persistence/publication. Runtime never resolves All again, so another registered name cannot widen an old snapshot. Published legacy files are unchanged and continue using their prior tool offers. The 32-grant ceiling is removed, without a replacement authority or projected-tool count ceiling. A 1 MiB candidate document bound and existing request/output/checkpoint limits remain operational guards.

Always projection is a separate authorized subset. Existing ToolPolicy eligibility precedes the deterministic union of Core bootstrap, always policy, trusted context, active Skill requirements and execution-loaded interfaces. Context-only attachment, continuity, maintenance, harness, occurrence-completion and intermediate-message tools remain controlled by their original context predicates; discovery and always policy cannot force them. Skills and loading never grant authority. Providers still receive ordinary model tool definitions, including the existing workspace/browser compatibility rewrites.

`capabilities.load` searches only authorized, discoverable, eligible interfaces using trusted names, categories, tags and bounded summaries. Exact-name/category/tag/description matching has deterministic ordinal ties, a 200-character query, default four / maximum eight matches and eight valid load invocations per execution. Results contain compact loaded metadata and already-projected names, with no configuration details or schema bodies. Real schemas appear on continuation. Execution-time checks retain configuration, approval, scope, owner, lifecycle, trust and replay policy.

Live user-turn admission commits through the Session mailbox and conversation execution store, fenced by epoch, response, revision, claim generation, Running state and cancellation. SQLite adds `LoadedCapabilityIdsJson` and `CapabilityLoadCount` through `20261006182813_CapabilityExecutionLoads`; historical rows default empty. EnsureCreated upgrade detection stamps only complete schemas. Durable WorkItems checkpoint loaded IDs/count explicitly, so compacted tool results cannot erase their state. Both paths recover the same execution's interfaces and start empty on a new turn/WorkItem. Already-accepted live occurrences use bounded response/epoch-owned mailbox state under the existing non-replayable AcceptedLive receipt. Admission rechecks the current connection; new occurrences cannot inherit completed user-turn loads. Continuations retain their state, while crash recovery does not replay accepted live receipts. Legacy checkpoint serialization and reserve calculations stay compatible.

Admin retains legacy-selectable `toolNames` and exposes the full registered `capabilities` catalog for new-mode authoring, grouped metadata, Selected/All access, authorized-only always choices, availability labels, counts and derived on-demand information. Diff and published review distinguish authorization from projection. Synthetic ToolOffered/ToolNotOffered checks use the projected interfaces; ExternalActionDenied continues checking execution authority. No new public load-state endpoint, realtime DTO, provider authorization logic, vector search or user-keyword capability router is introduced.

Projection telemetry records authorized/eligible/projected counts, actual normalized schema bytes, bootstrap/always/context/Skill/loaded sources, load calls and matches, safe outcomes, configured provider alias and model catalog key. Raw query text is not a default log or metric tag. Source counts can overlap and remain informational.

## Executed gates

All commands use installed dependencies. Backend commands ran with `--no-restore --nologo -m:1 -p:UseSharedCompilation=false`; frontend commands used Node 22.18.0.

| Gate | Result |
| --- | --- |
| `dotnet test AgentCore.sln` | Domain 145 passed; Infrastructure 771 passed / 9 opt-in skips; Order Events 4 passed. Application/API were rerun after final review changes below. |
| Full Application suite | 1,302 passed / 1 opt-in skip. Includes live load/write/reset, non-cooperative late callbacks after cancellation/supersession, recovered stored pins, deterministic discovery, projection security, telemetry, malformed authoring, durable WorkItem reclaim/reset, and actual connected/unconnected live occurrence routing after a persisted user turn. |
| Full API suite | 387 passed / 2 opt-in skips. Includes >32 Selected and All publication snapshots, exact grants/fingerprints and always subset. |
| Frontend Vitest `--run --maxWorkers=1` | 94 files / 705 tests passed. Includes authoring above 32 grants and Selected projection-policy round-trip. |
| TypeScript `tsc -b` and Vite production build | Passed. Existing bundle-size advisory remains; no compilation errors. |
| EF `migrations has-pending-model-changes --project src/AgentCore.Infrastructure --startup-project src/AgentCore.Infrastructure --no-build` | No pending model changes. Installed EF tool 10.0.5 reports its version advisory against runtime 10.0.12. |
| Focused migration / BrowserSettle / SQLite pin regressions | 10 passed. SQLite reopen, stale revision/claim, cancellation and recovery are also included in the full Infrastructure gate. |
| New capability publication / Chat journey | Passed with real Admin and Chat controls; exact always policy, publication snapshot, next-continuation schema admission, exact 31-byte UTF-8/CRLF file and new-turn reset. |
| Legacy catalog follow-up | Preserved `toolNames` subset and complete new-mode metadata: 3 API tests and 15 focused frontend tests passed; All publication/Chat rerun passed. MCP confirmed legacy continuity exclusion, full-catalog inclusion, bootstrap metadata, 54 authorized grants and a 390px document at 390px viewport. |
| Existing Playwright workspace / authoring journeys | `agent-workspace.spec.ts`, `agent-workspace-v2.spec.ts`, `z-admin-definition-lifecycle.spec.ts` and all four `secretary-demo.spec.ts` journeys passed. |
| `COMPOSE_PROJECT_NAME=capability-projection-review bash scripts/compose-sqlite-volume.sh` | Passed: SQLite, durable work, home/scratch and Artifact bytes survived container recreation and source deletion. Only the disposable project's volume was removed afterward. |
| Documentation / compatibility | Canonical owners updated; 353 local links, 125 fragment links, balanced fences and 15 complete JSON examples checked; `git diff --check` passed; no published `agents/` file changed. |

The broad suites cover registry/policy, Skills, browser, email, scheduling, attachments, continuity/maintenance, harness, app messaging, WorkItems, approvals, workspace, Synthetic tool loops and provider mappings. No hosted provider, live speech or real-store probe was enabled. Those explicit opt-in skips do not block key-free acceptance.

## Observed browser actions

The disposable Synthetic host used API 5086 / Vite 5176 and isolated storage under `/tmp/capability-projection-review`. No user catalog or running nopCommerce service was modified.

CLI Playwright created a self-contained Selected draft, confirmed an unauthorized `workspace.write` was absent from the always selector, switched to All, checked 1440px and 390px widths, saved, validated, ran the required Synthetic scenario and published. The resulting managed Chat first exposed bootstrap/always/context tools without `workspace.write` or browser schemas. A load request admitted exactly `workspace.write`; the next generation wrote `Loaded exact capability café\r\n`. HTTP download matched all 31 UTF-8 bytes. A new inspect turn omitted the loaded interface.

Playwright MCP independently operated All access and Save in Admin, checked the narrow form, created a managed session pinned to the new publication, and sent inspect → write → inspect through the visible Chat composer. The write result contained loaded-name metadata and a successful home write. Byte comparison matched the expected hexadecimal content; the following turn again omitted `workspace.write`. The final Chat console contained zero errors. The narrow Admin form had document width 390px at viewport 390px after the normal closing Select transition.

An early UI check sampled the closing dropdown animation and falsely reported overflow; polling the final layout resolved it without a CSS patch. An initial copied fixture lacked its own Knowledge resource bindings; the fixture was corrected rather than weakening publication validation. The broad backend gate identified duplicate migration-column application for EnsureCreated databases; complete-schema migration stamping fixed that upgrade path. A timing-sensitive BrowserSettle assertion passed its focused recheck and the full Infrastructure rerun without a browser implementation change.

## Hosted receipt

The initial behavior commit `6bd7b17299bb4878b89eb04cc97de60e9ce8e088` and [Hosted Synthetic run 37516499376](https://github.com/trannamtrung1st/agent-core/actions/runs/37516499376) exposed a legacy `toolNames` compatibility regression. Commit `bcb7749358d0f862c8419acad5a10077e477af85` preserves the original selectable subset while new-mode authoring/evaluation consumes the complete metadata catalog. Final review also corrected connected live-occurrence load admission and response isolation; the routed regression exercises both connection states after a persisted user turn. Acceptance remains pending all five jobs on that final behavior commit. Earlier workspace runs are separate evidence.
