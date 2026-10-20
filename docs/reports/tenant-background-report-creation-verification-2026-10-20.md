# Tenant completion-report creation consent verification — 2026-10-20

This successor qualifies independent exact report-Run creation consent for human-attributed immediate-background completions. It supersedes report-creation-Review-pending notes in the [child creation checkpoint](tenant-background-creation-verification-2026-10-20.md) at that boundary only. Inbox/wait tools, native background effects, autonomous origins, Interrupt/Steer/voice arbitration, remaining System/audit families and final authority/schema consolidation remain open. The full proposal and original goal remain active; no milestone or proposal-wide acceptance is claimed.

## Implementation

The existing completion coordinator retains the original explicit report intent. It prepares a typed `BackgroundReport` through the incumbent approval owner only when current policy or missing delegatable `agent-runs.create` requires consent. There is no public/model operation for initiating an arbitrary completion report. The immutable terminal child owns the request's Run/Session; the parent destination is separately bound. Core freezes the actual source revision/evidence, parent revision/input boundary, report Activation/Run/Response identities, model and effective configuration witness. No report Run exists while consent is pending, and the finished child never becomes WaitingForApproval.

Report creation requires its own actual Tenant Run-creation authority. A prior child-creation grant cannot authorize it, and no second Session-creation grant is required for the existing parent. Readers/reviewers independently need raw Run-creation permission and reads of the child Session, exact child Run and destination Session. Current decision/grant and independent-review rights remain separate. Protected inbox filtering precedes totals/pagination; parent Chat can show minimal source-owned status without granting detail access.

The incumbent approval processed signal routes the exact operation to the existing completion-inbox admission transaction. It repeats current original-human root communication, executor/capability, reviewer/grantor, source/configuration/model/parent witnesses. Busy parents defer; changed witnesses refuse instead of adapting consent. Atomic settlement creates the frozen graph, accounts the inbox, consumes or retires the exact grant, records the canonical operation receipt/security revision/audit and processes the signal. Negative decisions account only the requested delivery and produce no effect receipt. Approved but unavailable effects record a denied receipt. Source Run/Session identities remain on the receipt; the action/inbox retain the created report identities. Replay creates no second graph or revision change. The ordinary direct path cannot bypass an existing request, including after policy relaxation.

Migration `20261020120000_TenantBackgroundReportReview` extends the existing typed approval constraint with a frozen SQLite rebuild. It preserves the seven incumbent guards, adds two report-source/immutable-payload guards and restores nine guards in total. It checks actual terminal source ownership/revision and exact payload shape; Application also validates bounded, structured completion evidence. Retained report requests prevent downgrade. Empty downgrade/upgrade, old guards, model agreement and reopen are tested using disposable databases only.

The shared AntD inspector presents exact saved completion, destination and model as inert text. Optional `destinationSessionId` links the destination while source Run/Session identities remain unchanged. Malformed/missing preview or destination disables Approve/Reject while authorized Cancel remains. Protected confirmation grants one exact report Run. **Report Run created** is separate from eventual delivery and read access.

## Exercised workflows

| Scenario | Expected and observed result |
| --- | --- |
| Actual model child completion under Review policy, Memory and SQLite | Separate source-owned report request; no report before approval. Exact frozen Run admitted, receipt recorded and factual parent text delivered. Source remains terminal at its original revision. |
| Raw Run-creation permission missing | Child consent and report consent are separate grants. The report grant covers only its exact creation and is consumed once. |
| Reject, cancel, configuration change or executor revocation | No report graph or delivery; finished child remains unchanged. Stale requests preserve cancellation. |
| Direct transaction replay | One report/inbox disposition/receipt/security revision; repeat returns no second effect. |
| Forged report graph, expired request, changed configuration or revoked authority | Both stores reject substitution or settle a factual denied result without partial graph publication. |
| Source Run read missing, including after approval | Source filtering occurs before totals and current reviewer qualification is repeated before effect. |
| Actual SQLite application host recreated with pending consent | Exact saved action survives; no report exists before consent; approval resumes through incumbent owners and delivers once. |
| Existing direct report and child-creation/file consent regressions | Direct authority still works; prior child grant no longer implies report authority. |
| Browser Review and separate one-time report authorization | Actual Chat requests, shared inspector and protected confirmations create/deliver two report Runs. |
| Browser third report after configuration change | Authorize/Reject disable; Cancel succeeds with no operation result. Canonical totals are three completed children, two approved reports and one cancelled report; parent contains exactly two completion paragraphs. |

## Focused local verification

Commands used `dotnet test <project> --filter '<selection>' --logger 'console;verbosity=minimal'`, with focused regressions using `--no-build`, and `dotnet build AgentCore.sln --no-restore`. Local .NET IPC/loopback required sandbox escalation. No CI/full CI suite, Real host, live provider, deployment, installed-data migration/reset or external account operation was run. API fixtures observed zero outbound provider HTTP attempts.

| Selection; log suffix under `/private/tmp/tenant-background-report-` | Final evidence |
| --- | --- |
| Model/HTTP report matrix; `module.log` | 12 passed: both stores × Review, grant, reject, cancel, changed configuration and executor revocation. |
| Pending-consent host recreation and affected regressions; `api-recovery-regression.log` | Seven passed: one new recovery, two prior child grants, two direct model reports and two file Review cases. |
| New store settlement/schema; `store2.log` | 11 passed: ten transaction cases and one retained SQLite downgrade/immutability/model/reopen case. |
| Source-reader/reviewer, existing creation and direct completion; `authority-regression.log`, `migration-corrective.log` | 18 distinct passed: 17 grouped successes plus the corrected empty-migration guard-count case. |
| Report and preserved creation Application contracts; `contracts2.log` | 22 passed. |
| Final strengthened evidence parser; `evidence-final.log` | One representative SQLite Review passed, overlapping the matrix above. |
| Inspector tests; `ui-unit.log`, `ui-new2.log` | 71 distinct passed: 66 existing plus five report cases. |
| Solution build; `final-build.log` | Passed, zero warnings/errors. |
| TypeScript/Vite build; `ui-build.log` | Passed; existing chunk-size and dependency pure-comment advisories remain. |

These are **141 distinct passing local cases**. Corrective and representative reruns are not counted twice. Passing groups were not repeatedly rerun after fixture-only fixes. The existing JSDOM height warning is unchanged.

Initial integrated execution exposed a missing report-target DI registration and an overstrict parent Chat/source Session equality assumption; both were corrected before the grouped matrix. Store fixtures now receive the concrete fake clock, the new UI fixture uses its own saved server time, and the structured evidence validator uses the actual Application-port trigger enum. The old empty-migration fixture now expects the two additional guards. An initial EF command used the API startup project without EF.Design; generation then used Infrastructure's existing design-time factory. The new migration was ordered after the incumbent child-creation migration without modifying that immutable predecessor. All reported test/build processes ended with terminal outcomes.

Useful focused selectors:

```text
API report matrix/recovery:
FullyQualifiedName~Tenant_background_report_

Store report transaction/authority/schema:
FullyQualifiedName~Tenant_background_report_

Application contracts:
FullyQualifiedName~TenantBackgroundReportContractTests|FullyQualifiedName~TenantBackgroundCreationContractTests

API affected regression/recovery:
FullyQualifiedName~Tenant_background_report_pending_consent_|FullyQualifiedName~Tenant_background_creation_consent_&DisplayName~grant-run|FullyQualifiedName~Tenant_background_model_starts_|FullyQualifiedName~Tenant_background_file_read_Review

Frontend:
cd web && npx vitest run src/features/access/TenantApprovals.test.tsx
cd web && npm run build
```

## Browser and presentation evidence

The owned disposable Synthetic Memory API on 7158 and Vite on 7159 exercised actual login, three visible Chat sends, source child consent, independent report consent, protected confirmations, refreshed canonical detail, configuration change and cancellation. Fixture setup and authority/configuration changes used canonical same-origin HTTP. No app state injection or invented receipt was used. An initial loopback origin configuration blocked SignalR attachment; restarting only the owned API with explicit local origins corrected setup before the qualified journeys. The final browser source predates the last structured-evidence parser hardening; its inspector/valid workflow is unchanged, and the final SQLite integration separately passed that stricter parser.

Console warnings/errors were zero. Final network evidence has no HTTP 4xx/5xx and nine observer-change `ERR_ABORTED` reads; the exercised mutations and canonical reads succeeded. Final canonical state contains two approved/completed report requests and one cancelled request with null operation result, three child Sessions and exactly two delivered completion paragraphs.

All thirteen captures under `.impeccable/review/tenant-background-report/` were opened: `review-1440.png`, `review-390-top.png`, `review-390-bottom.png`, `confirm-1440.png`, `confirm-390.png`, `created-1440.png`, `created-390.png`, `grant-390.png`, `grant-confirm-390.png`, `grant-created-390.png`, `unavailable-1440.png`, `unavailable-390.png`, `cancelled-1440.png`. At 1440×1100 the shared Drawer is 736px wide; at 390×1000 it fills the viewport with independent content scrolling and a wrapped reachable footer. Neither viewport has horizontal page overflow. Finite animations settled before capture and scroll-position pairs are intentional.

The detector ran once on the two product owners and returned `[]`. A fresh shipped finish reviewer returned **ship**, with all five contract sections and no material fixes. This ordinary incumbent extension needs no new visual world, comp or QUALITY BAR card. The shipped documenter records only durable shared-surface facts. Pre-existing DESIGN/sidecar staleness remains outside this implementation; a separate document refresh can preserve DESIGN while refreshing the sidecar.

Final documentation checks passed: all 1,894 relative links across the thirteen changed Markdown files resolve, fences balance, and the staged diff (including the new migration) passes whitespace checks. Both owned disposable host processes exited at completion.
