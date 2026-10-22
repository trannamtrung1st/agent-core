# Tenant model Automation read verification — 2026-10-22

The canonical Automation inspection/read-consent module is locally qualified. This completes `automation.list`/`automation.inspect` through the existing tenant Runtime, stores, approval machinery and shared inspector. Model mutation, Harness/Self-Management, approval-default review, remaining internal/schema cutover and proposal-wide acceptance remain open.

## Delivered behavior

The actual model reads only its executing Agent's currently readable Automation configuration. Permission filtering precedes counts/paging; bounded excerpts, status, immutable recipe origin, destinations, triggers and readable event-source metadata remain untrusted evidence. Source Run content needs separate authority. Independent scheduled reads require no invented human or unrelated conversation, Agent or Memory read grant.

Configured read Review withholds metadata until an exact qualified decision. The saved observation binds parent configuration/event witnesses; current raw source and reviewer access remain mandatory. Review cannot supply a missing raw read permission. Rejection carries no adopted source; approved read consent can finish a background task with `NoAction`. Read consent grants no mutation, execution or future read authority.

Captured witnesses survive checkpoint compaction and are checked before later model use, protected reporting and outcome commit. A source revision or permission change prevents substituted/captured metadata from reaching the answer. The protected-output refusal path also settles the Run and fences late worker output. Budget exhaustion adopts no witness and metadata inspection does not acknowledge Experience coverage.

Chat and Inbox use the existing Ant Design inspector, with labeled saved facts, literal instructions and explicit read-only confirmation/result wording. Source visibility reaches the conversation approval list before paging. [Desktop](assets/tenant-automation-read-2026-10-22/read-desktop.png) and [narrow](assets/tenant-automation-read-2026-10-22/read-narrow.png) evidence show the actual Synthetic flow.

## Consolidation and persistence

`TenantRunSourceAuthority` replaces repeated Profile, Memory, Experience, historical Session and Automation source checks in three existing consumers. The existing Automation operation target supplies its store/read services without duplicating the Automation-store dependency in the approval service. Protected-output refusal uses one source description instead of parallel Boolean flags.

`TenantAutomationReadConsent` extends existing approval storage without a new table, queue or executor. Its frozen SQL preserves all 104 incumbent trigger definitions and indexes, adds exact owned-parent insertion and immutable-read guards, and checks foreign keys. Frozen constraint resources avoid the generated C# string-heap limit; historical migrations retain their behavior. Upgrade/downgrade/re-upgrade and model consistency pass. An actual persisted Synthetic read-consent fixture was copied through a read-only connection: EF refused downgrade with the expected guard failure, retained its request and latest migration, and left zero foreign-key violations. Original fixture databases were untouched.

README, TODO, the implementation plan and this initiative's ledger now point to one current delivery summary instead of repeated superseding status paragraphs. Canonical architecture, ports, implementation, protocol, persistence, presentation and testing owners are synchronized. Changed-document file links, fragment links, fences and the new complete JSON example were checked; no new missing links were found.

## Verification

| Check | Observed result |
| --- | --- |
| .NET solution and final affected API/test-project builds | Pass, zero warnings/errors. Serial builds used `--no-restore --nologo -m:1 -nodeReuse:false -p:UseSharedCompilation=false`. |
| API Runtime and source regressions | **90 distinct cases qualified across grouped runs:** final **22** Automation cases plus **68** affected Profile/Memory/history/Experience/event cases. |
| Final Automation matrix | **22 passed**: actual model inspect/list, scoped filtering, exact approval/refusal, Chat visibility, changed configuration, revocation settlement, insufficient output budget, stale generation, SQLite adapter recreation and scheduled autonomous/quiet completion. |
| Event matrix | Final **12 passed**: actual accepted Core-event roots, remaining-source metadata, independent current event reads, read-policy withholding, no metadata-only coverage acknowledgment, source retention and recovery in both stores. |
| Infrastructure source/consent/schema matrix | **72 passed**, including real SQLite migration round trips, unchanged incumbent guards/indexes, current model and foreign keys. |
| Application contracts/completion | **52 passed**, including typed read-only `NoAction` and adjacent continuity/Experience contracts. |
| Shared inspector Vitest suite | **124 passed** using `NODE_OPTIONS=--no-experimental-webstorage pnpm exec vitest run src/features/access/TenantApprovals.test.tsx`. |
| Frontend production build | `pnpm build` passed. Existing bundle-size warnings remain. |
| Native Playwright CLI | Final **4 passed**: approve/reject at **1440** and **390** px, real login/Chat/decision/continuation, literal instructions, visible footer controls, no document overflow, unchanged Automation revision and no captured page/request failures. |
| Playwright MCP | Independently signed in, started a fresh `automation.list` task, inspected and confirmed its exact read consent, then observed the factual reply and **Read authorized** in Chat. |

Automated totals are **338 distinct focused cases plus four browser journeys**. API filters selected `Tenant_model_Automation`, `Tenant_independent_Automation_metadata_read`, `Tenant_Run_Profile`, `Tenant_learned_memory`, `Tenant_continuity_actual`, `Tenant_experience_current_suppression` and `Tenant_event_Experience`; subsequent runs selected only changed/failing boundaries. Infrastructure filters selected tenant Experience/continuity and `TenantAutomationReadMigrationTests`; Application filters selected `WorkCompletionRequestTests`, `TenantContinuityContractTests` and `TenantExperienceContractTests`. Tests used Synthetic adapters, both owning stores and disposable SQLite/host data. CI was neither requested nor run.

Earlier qualification exposed missing saved-review classification, receipt validation/audit mapping, protected-output failure classification, Chat operation mapping and conversation-list source visibility. Those defects were fixed and the affected journeys rerun. An initial combined 88-case API run also stalled once in the existing event-review rejection retry path; two later complete 12-case event matrices passed. That stall's cause is unconfirmed and must be revisited before proposal-wide acceptance; no separate recovery fix is claimed for it.

## Remaining boundary

This report does not accept model Automation authoring/mutation, Harness/Self-Management, mandatory approval defaults, exhaustive Webhook/Email inspection combinations, complete internal/schema retirement or the full security/isolation/design matrix. The boundary inventory's historical missing-file references and the observed retry reliability issue remain final-audit work. Existing versioned evidence remains historical; no full milestone/proposal acceptance is claimed.
