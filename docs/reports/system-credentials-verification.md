# System Credentials verification

Date: 2026-10-07. Implements the authorized System Credentials proposal as a bounded migration after P9.10. Earlier milestone freeze evidence remains historical; P10/P11 remain unopened.

## Candidate status

**Closed/frozen on verified behavior `55d31056b871e05c553f09aa2946815280add3e3` (`55d31056`), 2026-10-07.** [Hosted Synthetic/Compose workflow `37584505116`](https://github.com/trannamtrung1st/agent-core/actions/runs/37584505116) completed successfully on that exact SHA; all five jobs are green. This bookkeeping commit changes documentation only and does not move the behavior freeze. The System Credentials / Application Connection migration is complete; historical milestone freezes are unchanged and P10/P11 remain unopened.

## Delivered behavior

Global Admin owns reusable protected Credentials with Core-defined immutable kinds, bounded dynamic safe metadata, status and exact origins. Explicit instance bindings grant aliases independently of Definition capabilities. Safe projections never return protected material; replacement retains resource identity and grants. SQLite stores Data Protection ciphertext scoped to CredentialId and protection version. The persisted key ring survives restart and Compose recreation.

`credentials.list` is contextual safe metadata, excluded from Definition authorization/discovery fingerprints. A binding does not grant Browser. Generic authorized Browser works without a connection row. Interactive `fill_credential` accepts only a current active bound Password alias, an observed existing-password ref, and an origin allowed by both Browser policy and Credential policy. The provider rechecks the actual page/ref/type around resolution, registers protected values for redaction before the effect, and keeps them outside model arguments and the domain mailbox. Ordinary password fill, detached protected use, account creation/change/reset and human verification are denied or require intervention.

Browser state remains instance-owned. Unbind/disable/rotation preserve it; confirmed reset deletes only that instance's profile and preserves grants. Old Application Connection production types, routes and table are retired. The migration does not import old rows, bootstrap environment values, or delete browser directories. Events move to Automation → Events; legacy instance `/connections` navigation redirects to `/credentials`. Secretary v4 uses knowledge, generic Browser and the `store-admin` alias. It requests the site's Remember me behavior when reusable authentication is requested; site cookie expiry still governs reuse.

## Acceptance mapping

Every AC below refers to the proposal's numbered functional criteria; grouped rows identify the exercised test or review owner.

| Criteria | Evidence |
| --- | --- |
| AC1–AC10: create, bounds, safe readback, ciphertext, replacement, disabled resolution, immutable kind, bound deletion | `CredentialApiTests`; `SystemCredentialTests` InMemory/SQLite parity, protection/reopen/corruption/missing-key tests; `CredentialsSection.test.tsx`; `system-credentials.spec.ts` |
| AC11–AC18: shared resource, per-agent aliases, uniqueness, isolation, unbind/delete/archive | `SystemCredentialTests.Shared_credential_rotation_isolation_disable_unbind_and_bound_delete`; `CredentialApiTests.Owner_shared_resource_journey_has_no_value_readback_and_enforces_lifecycle`; UI shared binding/archive journey |
| AC19–AC22: contextual safe listing and fingerprint separation | `SystemCredentialTests.Discovery_pages_complete_records_under_execution_budget_and_bindings_do_not_change_fingerprint` (InMemory/SQLite); `AdminApiTests` All/Selected publication and selectable catalog; `CapabilityProjectionTests`; Chromium safe-list/unbind journey |
| AC23–AC27: capability independence and no harness credential authority | capability projection/load, routing, policy and unattended-browser suites; harness tools remain limited to their existing definition surface; Secretary v4 capability assertions |
| AC28–AC32: connection retirement | production symbol/route scan; `CredentialApiTests` old route 404; `SystemCredentialTests.Upgrade_drops_legacy_rows_without_importing_or_touching_profiles`; AgentContext/prompt/policy review |
| AC33–AC35: generic browser and owner profile isolation | `AgentBrowserProfileTests`; `BrowserCredentialSinkTests.Authenticated_owner_profile_reopens_isolates_another_owner_and_detached_login_requires_attention`; Real nopCommerce restart journey |
| AC36–AC44: password sink, alias/kind/origin/policy checks and reflection | `BrowserActContractTests`; `BrowserAdapterTests`; `SystemCredentialTests` wrong sink/origin/disabled cases; `BrowserCredentialSinkTests` actual Chromium ordinary-fill denial, stale/wrong ref, direct fill, mixed-text reflection, screenshot masking; Real model/argument/plaintext scan |
| AC45–AC46: registration, new/change/reset, verification | five actual-page theory cases in `BrowserCredentialSinkTests.Registration_and_verification_do_not_resolve_protected_material`; existing Browser intervention fixtures |
| AC47–AC50: detached eligibility, authenticated reuse, no protected injection, login-wall attention | `UnattendedBrowserTests`, durable occurrence suites; actual Chromium detached boundary and owner-profile restart; Real nopCommerce detached product review then reset/login intervention |
| AC51–AC54: responsive collection, binding tab, masked input, transient clearing | eight `CredentialsSection.test.tsx` cases; full CRUD/shared-binding UI journey at 1440/768/390; Playwright MCP create, failed replacement, close/reopen, bindings and reset; visually inspected desktop/mobile screenshots |
| AC55–AC58: Events placement/source navigation, redirect, generic reset | `AdminInstanceNavigation.test.tsx`, route tests, `credential-bindings-work.spec.ts`, owner API/reset and Chromium profile tests; Playwright MCP Events/redirect/reset |
| AC59: migration preserves browser directories | predecessor migration test with legacy row and sentinel profile file; profile reopen tests |
| AC60: prepared Secretary generic nopCommerce sign-in | opt-in `NopCommerceCredentialJourneyTests` with real OpenRouter model, disposable pinned nopCommerce store, Secretary v4 Skill and two aliases; no Application Connection |

## Local execution

All servers and credentials for this work used isolated task-owned paths/ports. Existing user host/store data and bootstrap environment were not imported.

| Gate | Result |
| --- | --- |
| Full backend solution | PASS: Domain 148; Application 1,307 + 1 opt-in skip; Infrastructure 803 + 9 opt-in skips; API 386 + 3 opt-in skips; OrderEvents 4. 2,648 passing tests, zero failures. |
| Final focused credential/profile/Chromium boundaries | PASS: 15, including three additional new/change/reset form cases added after the broad run. |
| Full frontend, Node 22, one worker | PASS: 95 files, 719 tests. |
| Frontend production build | PASS: TypeScript and Vite. Existing chunk-size advisory remains. |
| Synthetic browser core | Initial broad run: 104/105 passed; transient Running tools observation failed although the expected final order response arrived. Focused rerun passed. Final exact-candidate CI must pass the full gate. |
| Browser acceptance | Initial combined local run: 14 passed, two failed, one not run. Continuity used a retired View run row label; Secretary raced a pending schedule dismissal/navigation. Tests now expand the current Thought row and wait for confirmation closure/selected tab. Recheck: all five remaining continuity/Secretary tests passed; the mobile continuity test passed separately after waiting for dialog closure. CI retains separate project/database lifecycles. |
| Final credential UI browser journey | PASS: both final collection/shared binding/reset and quiet background work journeys (16.7 seconds). |
| Compose SQLite/key-ring survival | PASS: isolated project, safe Credential/grant survive recreation, ciphertext database contains no known value, key XML hashes unchanged, legacy table absent. Existing workspace/resource/run checks retained. |
| Real nopCommerce | PASS: one real-model journey, 70 seconds; details below. |
| Responsive Impeccable/MCP pass | PASS: existing AntD tokens/layouts reused; desktop 1440 and mobile 390 screenshots inspected, document width equals viewport, protected inputs absent from resting projection. Functional journey also covers 768. |

Local logs: `/tmp/credentials-backend-freeze.log`, `/tmp/credentials-final-boundaries.log`, `/tmp/credentials-vitest-final.log`, `/tmp/credentials-build-freeze.log`, `/tmp/credentials-playwright-core.log`, `/tmp/credentials-progress-recheck.log`, `/tmp/credentials-playwright-acceptance.log`, `/tmp/credentials-acceptance-recheck.log`, `/tmp/credentials-ui-final.log`, `/tmp/credentials-compose.log`, `/tmp/credentials-real-nopcommerce4.log`. These ephemeral paths are reproducibility evidence for this session; CI supplies durable exact-commit results.

The first frontend run used local Node 26 and failed service tests because jsdom's localStorage differed. Node 22 matches CI. A CPU-contended Node 22 broad run had three existing UI timing failures; the final serialized run passed all 719. The Real journey first exposed nopCommerce's session-cookie expiry on host restart; Secretary's normal Remember me procedure fixed durable authentication, followed by the complete passing journey. No timeout increases or skipped assertions were used to claim acceptance.

## Real nopCommerce journey

The opt-in test provisions two Secretary v4 identities against a fresh disposable pinned nopCommerce installation on port 5098. It creates one explicitly supplied Password Credential and binds `store-admin` and `ecommerce-admin`. Real model `deepseek/deepseek-v4.1-flash` loads the sign-in Skill, obtains safe username/alias metadata, invokes `fill_credential`, checks Remember me and confirms Admin access. Another Chat reviews products without password filling; the host then restarts with the same SQLite/key-ring/profile and another Chat reviews products without filling again. A single resource replacement preserves both aliases; agent B signs in with its own alias. Detached Browser reviews products while authenticated; after confirmed profile reset, it returns authentication intervention without protected injection.

The live replacement intentionally retains the disposable site's current password; a different-value shared rotation is separately proven by both stores. Captured model requests, tool arguments and SQLite are searched for the known protected value using boolean assertions that do not print it. Live credentials are supplied explicitly through opt-in environment variables and are never taken from the normal demo bootstrap environment. Default CI skips the opt-in Real test and remains key-free. See [testing strategy](../16-testing-strategy.md) for prerequisites and the invocation.

## Storage, redaction and operational review

Known-value scans cover SQLite/WAL, host logs, test logs, workspaces and artifacts. Profile files and Data Protection keys are excluded because they are the intentional site-authentication/protection stores. Two earlier failed browser harness logs contain fabricated password literals only in displayed test-source code frames; they are classified separately and are not model, host, or product output leaks. No known protected values were found in ordinary product storage or host output. Final bounded scan: 102 files, zero matches; the two classified test-source excerpt logs are excluded from that count.

Canonical ownership is synchronized in architecture/ports, browser implementation, Admin specification, wire protocol, persistence/configuration, operations, demo, testing, implementation plan, README and TODO. Historical P9.5/P9.6 freeze reports and historical EF migrations retain their names; current schema adoption legitimately references the retired table to identify/drop predecessor schema. Generic networking/database/SignalR connection terminology is unchanged.

Back up the SQLite database and persistent credential key directory together. Losing keys makes values unavailable; no readback/reveal recovery exists. Database rollback needs a pre-migration backup because the old rows are intentionally dropped. Browser profile reset is independent and must be explicitly confirmed. Provider/LLM keys and Event Source verification tokens retain their separate owners. Future Application Binding denotes a client application embedding/invoking an Agent Core identity; further secret sinks, multi-user ownership and cloud vault integration require separately authorized design.

## Second review

A second whole-output review found and fixed a JavaScript dynamic-metadata edge: `__proto__` now serializes as an own dictionary key using a null-prototype map. Credential collection, binding and mutation errors preserve safe server diagnostic IDs through the existing shared Admin error-details component. The Save button retains a stable accessible name after loading. Current frontend/deferred-work docs no longer name the retired Application Connection surface. Historical milestone evidence is unchanged.

Verification: five focused frontend tests pass, including reserved object keys and failed replacement diagnostic details; production build passes; the full credential UI journey passes with actual API create/edit serialization of `__proto__`, replacement retry/diagnostic details, shared grants and mobile widths. Playwright MCP also reopened/saved that key and exercised a diagnostic-bearing replacement failure, observing an empty protected input. The first browser assertion transported objects through Playwright and lost the `__proto__` key in its bridge; transporting response JSON as text fixed the harness while preserving the API round-trip assertion. No product assertions were dropped.

## Hosted freeze gate

**Closed/frozen on verified behavior `55d31056b871e05c553f09aa2946815280add3e3` (`55d31056`), 2026-10-07.** [Hosted Synthetic/Compose workflow `37584505116`](https://github.com/trannamtrung1st/agent-core/actions/runs/37584505116) completed successfully on that exact SHA; all five jobs are green. This bookkeeping commit changes documentation only and does not move the behavior freeze.

| Exact-candidate hosted job | Result |
| --- | --- |
| Synthetic backend | PASS |
| Synthetic frontend (unit tests and production build) | PASS |
| Synthetic Playwright core | PASS |
| Synthetic Playwright acceptance | PASS |
| Synthetic Compose smoke | PASS |

GitHub reports `status=completed`, `conclusion=success` and the full candidate SHA above. Local and Real journey evidence in this report complements this key-free hosted gate. Credential Admin history receipts remain explicitly deferred as recorded in Technology Decisions; no further behavior change belongs to this closure.

## Final review corrections

The external review correctly found that the earlier AC22 assertion had not exercised All resolution: `ToolRegistry.All` leaked `credentials.list` into grant snapshots. Definition resolution now uses explicit authorizable descriptors for All and Selected, and the Admin selectable catalog uses the same set. Runtime policy retains context-owned eligibility without requiring accidental Definition grants. Credential binding/unbinding fingerprint regressions run against both stores.

Credential listing now retrieves only the requested eligible page plus one record for hasMore, paginates by ordinal alias and fits complete records to the remaining output budget, with a bounded output-limit result when no record fits. Browser redaction state deduplicates variants and fails closed at 256 variants or 1 MiB without evicting earlier values. Chromium exercises rejection and repeated reuse. Credential Admin history receipts are explicitly deferred in the Technology Decisions owner; no protected payload history is introduced.

The previous candidate c4b0d4b0 hosted attempt passed backend, Compose, 17 acceptance and 105 core browser tests, but its frontend job hit an existing 30-second Admin test timeout; that candidate was not frozen. The final exact-candidate hosted gate above verifies these fixes.

Review-focused verification: 18 Infrastructure tests passed, including actual Chromium redaction-capacity rejection/reuse and both discovery stores; the complete Application suite passed 1,307 with one opt-in skip, and all 70 Admin API tests passed. The complete corrected backend and all exact-candidate hosted gates passed as recorded above. Changed Markdown links/anchors/fences and `git diff --check` pass.

## Credential UI polish and hosted regression repair

The authorized polish uses existing Ant Design v6/Admin panels, collection toolbar, small tables and responsive detail-label layout. Credential/binding rows show bounded safe summaries and expand complete metadata and origins. Search includes safe policy fields and aliases; no-match and unavailable inventory states are distinct. Tall create/edit forms scroll inside the dialog without moving the action footer outside the viewport. Metadata row spacing has one owner (8px parent gap, zero child item margin); mobile dialog controls follow the existing 40px policy. Replacement names its subject, remains masked and clears transient input on cancel/failure. Binding failures show diagnostic-bearing feedback inside the dialog, retain the selected credential/alias and permit explicit retry. External AgentPicker empty-state copy and its assertion are included as requested.

Playwright MCP exercised actual create, shared binding, injected 409 conflict/retry, search/no-match recovery, expanded details, profile-reset cancellation and replacement cancel/reopen against the isolated Synthetic host at 5087/5187. At 1440/768/390px, document width equaled viewport width, tables owned horizontal scrolling, and the tall form retained header/footer inside the 900px viewport with 8px metadata gaps. The final navigation had zero console errors; the deliberately injected binding conflict is the expected 409 network entry. Screenshots are local evidence under `local/verification/system-credentials/credentials-polish-*`; protected inputs are never persisted in screenshots.

Candidate `634d16683a5fcb7a96cd663b17b885809d0face4` [hosted run](https://github.com/trannamtrung1st/agent-core/actions/runs/37581945780) passed backend, acceptance (17) and Compose; frontend (718/720) hit two instance-creation timeouts, and core (104/105) retained a stale evaluation selector for context-owned `capabilities.load`. It is not frozen. The core regression now evaluates an ordinary Definition-authorizable tool and explicitly asserts both `capabilities.load` and `credentials.list` are absent from saved All grants, while still checking contextual bootstrap availability in Chat. Instance-creation tests now enter the Instances route directly, avoiding unrelated Definitions inventory mounting before the tested dialog journey; queries are scoped to the intended dialog and all creation/persona assertions remain, with unchanged timeouts. The combined candidate `55d31056` passed every hosted job as recorded above.

Local polish validation: `pnpm run test --run src/features/admin/AdminApp.test.tsx --maxWorkers=1` passed all 39; the final scoped rerun passed the two previously timed-out cases in 5.8s/2.7s. Credential/AgentPicker unit tests passed all 14; strict TypeScript/Vite production build passed (existing bundle-size advisory only). Isolated Playwright passed seven affected scenarios: four approval regressions, capability projection, quiet binding/background work, and the complete shared-credential CRUD/reset journey. The added reset-cancel check initially raced the closing confirmation; explicitly awaiting dismissal fixed the test, and the complete journey then passed in 10s. Markdown relative links/fences and `git diff --check` passed. Hosted validation subsequently passed all five jobs on `55d31056`; the frozen behavior is recorded above.
