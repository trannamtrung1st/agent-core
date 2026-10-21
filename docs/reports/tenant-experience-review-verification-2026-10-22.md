# Explicit tenant Experience review qualification — 2026-10-22

## Delivered scope

An original human can inspect one stable conversation or substantive completed Run, confirm its exact source, and queue one generic background Experience review. The existing tenant worker, protected source/record tools, approval continuations and factual background Activity execute it. The actual human retains private User ownership; independent Automation ManualInvocation retains separate Instance ownership.

Both stores repeat original-login, current source/read/creation rights, actual private User, opt-in, security and Instance revisions in the Session/Run admission transaction. Deterministic graph IDs bind the actual User and full selected-source witness. An unchanged retry returns the existing factual graph without executing again. Changed sources, another selected source, opt-out or lost raw reads cannot substitute new evidence into the admitted task. No placeholder Experience row, migration, scheduler, semantic outbox, global Profile adoption or automatic Chat report is introduced.

The two-phase closed HTTP interface returns metadata and an opaque source fingerprint before exact admission. The existing Continuity panel reuses the tenant conversation picker and Ant Design confirmation. Created and existing work link to the continuation-aware Activity result. Human task purpose is plain text; internal model-only witness JSON remains immutable and is omitted from that purpose projection. Scope changes retire pending work, and stale or lost commands require deliberate inspection without replay.

## Executed local evidence

The final focused groups total **232 distinct passing cases**, with no selected skips. Repeated runs are not counted again. These qualify this module, not the full proposal or a milestone.

| Check | Result |
| --- | --- |
| Solution build | Pass; zero warnings/errors; 47.83s |
| Experience, continuity and protected Memory store/transaction regressions | 86 passed; 2m50s |
| Independent task, background creation/report, Experience, continuity, approval and checkpoint contracts | 73 passed; 169ms |
| Actual HTTP/model, review boundaries, canonical route inventory and SQLite foreground/Activity recreation | 15 passed; 47s |
| Review, Experience, Memory, tenant picker and background-result frontend group | 56 passed; 19.89s |
| Native desktop1400/narrow390 journeys | 2 passed; settled-capture repeat29.8s |
| Frontend build | Pass; 7.41s; existing chunk-size warning only |

Commands used `dotnet build ... --no-restore --nologo -m:1 -nodeReuse:false -p:UseSharedCompilation=false`, then project-specific `dotnet test ... --no-build --no-restore --filter ...`. Infrastructure filter: `Tenant_experience_|Tenant_continuity_|Tenant_memory_operation_`. Application selected IndependentTask, BackgroundCreation, BackgroundReport, Experience, Continuity, Approval and AgentRunToolCallCheckpoint contract classes. API selected Experience/manual review, the canonical retired-route inventory and the existing SQLite foreground/Activity recreation case. Results: `/private/tmp/tenant-experience-review-results/{experience-review-store-final,experience-review-contracts,experience-review-http-qualified}.trx`; logs have the matching `/private/tmp/tenant-experience-review-` prefix.

Frontend uses `NODE_OPTIONS=--no-experimental-webstorage pnpm exec vitest run` for `TenantExperienceReview`, `TenantAgentExperience`, `TenantAgentMemory`, `AdminSessionPicker` and `BackgroundWorkDrawer` tests. Logs: `/private/tmp/tenant-experience-review-ui-final.log`, `/private/tmp/tenant-experience-review-web-final-build.log`. Build: `pnpm run build`.

The 16 new store cases cover atomic human-root graph creation, normal protected private recording, idempotency/reopen, stale-source refusal with no partial Session/Run/audit, invalid original login, post-admission source change/different-source/opt-out/read loss, unreceived/nonterminal sources and refusal to adopt another User’s private Run even with raw read and creation permissions. Four new HTTP cases cover actual model execution and continuation-aware Activity purpose/result, strict request shape and original-human boundaries. Six UI cases exercise exact preview/confirmation, source retirement and stale/lost-result recovery.

Native fixture `web/e2e/tenant-experience-review.spec.ts` uses disposable configuration `/private/tmp/tenant-experience-review-native/playwright.config.mjs`. At both widths it signs in, creates an enabled tenant Agent, delivers and ends a source conversation, selects it with the visible keyboard picker, inspects the exact preview, confirms admission, approves source inspection and recording separately, opens the factual background result through approved successors, then inspects private retained provenance and reloads. Both journeys pass without page/request errors or document overflow. Playwright MCP returned `Transport closed`; Playwright CLI exercised the running Synthetic app as the practical fallback. Owned ports5096/5186 require no provider credentials; user hosts5080/5173 were untouched. The runner stops its own hosts.

## Corrections and visual evidence

Qualification corrected whole-Run serialization of a legacy Profile getter, direct Synthetic Run-task extraction, narrow recognition of the explicit human review root, continuation-aware receipt navigation and plain human task purpose. These corrections preserve independent Automation and agent-created background authority. The Inbox fixture deliberately refreshes its static list for each next approval; virtual picker options are selected through the visible combobox keyboard interaction. Confirmation removal and disabled screenshot animations produce settled evidence; an earlier desktop result capture caught the drawer transition and was replaced.

A fresh Impeccable reviewer returned **disposition: ship** for this bounded extension after opening all eight desktop/narrow captures. Its five contract sections record incumbent-system fidelity, reachable narrow source actions, truthful admission/result copy and no material fixes. One detector scan returned `[]`; no second detector ran.

[Desktop exact source](assets/tenant-experience-review/desktop-review-source-preview.png), [desktop admission](assets/tenant-experience-review/desktop-review-admitted.png), [desktop factual result](assets/tenant-experience-review/desktop-manual-review-result.png), [narrow source actions](assets/tenant-experience-review/mobile-review-source-actions.png), [narrow admission](assets/tenant-experience-review/mobile-review-admitted.png), [narrow factual result](assets/tenant-experience-review/mobile-manual-review-result.png). The narrow source-actions view deliberately scrolls the native modal; its companion top preview is retained in the local review packet.

The fresh documenter individually reopened all eight final captures and compared source, direction and shared controls. It recorded the manual-review extension in the existing surface brief and verified that PRODUCT, DESIGN and `.impeccable/design.json` remain byte-for-byte unchanged. Historical sidecar provenance drift is reported separately and preserved. Changed-document relative links and Markdown fences pass; `git diff --check` passes.

## Remaining scope

This closes explicit human background Experience review convenience only. Autonomous maintenance orchestration, independent Automation Experience execution/source qualification, remaining Harness/internal/schema consolidation and exhaustive proposal inventory/qualification remain open. The prior substrate report remains historical evidence, superseded here only for its manual-review-pending scope. No full local CI suite, hosted Actions, live-provider or milestone-wide acceptance was requested or run. The original goal remains active.
