# Gmail OAuth migration verification — 12 October 2026

Status: implementation candidate; final exact-source hosted acceptance is pending. Draft [PR #11](https://github.com/trannamtrung1st/agent-core/pull/11) contains the reconciled implementation. Live Gmail UAT is **blocked/deferred**, not passed. No Google application configuration, dedicated mailbox or real-recipient send authorization was supplied. Synthetic consent/read/send evidence does not prove Google consent or delivery. P10/P11 and historical freezes remain unchanged.

## Baseline and scope

Started on `974159eb348b68188e34dfb602824f15db096d51`, matching origin/main after fetch. During the work, main advanced through diagnostics commits `d7febd4b` and `9e730081`; both were reconciled into the candidate, preserving safe handled-failure diagnostics and adding OAuth diagnostic IDs. The earlier baseline Synthetic run [38219185415](https://github.com/trannamtrung1st/agent-core/actions/runs/38219185415) failed `SpeechIngressTests.Typed_user_text_in_voice_abandons_in_progress_speech_input`. Its focused rerun passed without changing the assertion; this is not an OAuth fix claim.

Gmail now uses owner Authorization Code + PKCE S256 + offline refresh, verified Google identity and account-scoped Gmail REST. Custom TLS/password transport remains independent. Account IDs/bindings, exact content approval, Core drafts, immutable send claims and key-free Synthetic behavior remain. There is no provider Draft sync, global mailbox-token fallback, new email tool family, new UI kit or destructive database reset.

## Executed local evidence

All commands ran in the repository unless stated otherwise. Frontend reruns explicitly prepend `/Users/trungtran/.nvm/versions/node/v22.18.0/bin` to PATH. Full broad checks below preceded the final main reconciliation; final exact-source hosted evidence will be recorded separately.

| Check | Observed result |
| --- | --- |
| Focused baseline speech test | 1 passed; earlier hosted failure retained |
| GmailOAuthStorageTests, first migration/CAS gate | 3 passed |
| GoogleEmailOAuthTests, initial OAuth security gate | 14 passed |
| GoogleEmailOAuthApiTests, owner/public callback journey | 1 passed |
| Gmail API + Custom transport + durable account slice | 37 passed |
| Expanded email/credential/browser secret slice | 72 passed |
| Consent/reopen/refresh/read/send/disconnect + race storage slice | 24 passed |
| Final API/selection slice | 13 passed |
| Custom approval compatibility + durable workflows | 22 passed |
| Full .NET solution, initial broad pass | Domain 195, Application 1772, Infrastructure 1254, API 556, plugin 4 passed; 21 explicit live opt-in skips |
| Full frontend, Node 22 / two workers | 116 files, 943 tests passed; final added recovery cases tested separately |
| Final focused dialog recovery tests | 6 passed |
| Reconciled email/transport diagnostics | 72 passed |
| Reconciled OAuth/Gmail/Custom integration | 75 passed |
| Extended grant lifecycle security | 31 passed; consent expiry during identity verification, unsafe origins, transient profile failure versus revocation |
| Reconciled Application diagnostics/attention/speech | 46 passed |
| HTTPS callback + detached Automation runtime | 13 passed |
| Frontend TypeScript/Vite build | Passed; existing large-bundle and SignalR annotation warnings retained |
| Affected Playwright journeys | 8 passed, including 1440/768/390/320 Gmail lifecycle/chooser, exact-approved Synthetic send and adjacent Credentials |
| Isolated Compose SQLite/key-ring survival | Passed in `agent-core-gmail-oauth-verification`, port 5380; extended gate preserved connected Synthetic OAuth grant and non-sending check across recreation |
| EF pending model changes | None; local EF tool 10.0.5 reports older than runtime 10.0.12 |
| Event-storage monitor tests | 3 passed |
| Documentation consistency | 16 changed Markdown files, 217 anchors, 14 complete JSON examples and design sidecar passed |
| Impeccable doctor | Empty findings; sidecar JSON schema 2 parses |
| Diff whitespace | `git diff --check` passed |

Representative commands: `dotnet test AgentCore.sln --nologo -m:1`; focused Infrastructure/API filters named above; `pnpm run test --run --maxWorkers=2 --minWorkers=1`; `pnpm run build`; affected Playwright specs through a temporary config pointing to disposable Synthetic API 5280 / Vite 5373; `COMPOSE_PROJECT_NAME=agent-core-gmail-oauth-verification AGENTCORE_COMPOSE_PORT=5380 bash scripts/compose-sqlite-volume.sh`; `dotnet ef migrations has-pending-model-changes`; `.agents/skills/impeccable/scripts/impeccable doctor --json`.

## Exercised scenarios and outcomes

- Owner creates Gmail without password/endpoints, starts correlated consent and returns through a one-use cookie-bound callback. Expected protected grant and verified identity, no binding/send side effect; observed. Forged owner/browser/state, expiry, disabled account and replay reject before exchange. Missing offline token/scopes, denial and wrong identity remain distinct safe outcomes. Duplicate provider subjects fail atomically.
- Fake Google token/profile/MIME transport follows the Real account-scoped provider. After reopening SQLite and the original key ring, a new provider refreshes; replacement token changes only SecretRevision. Draft hash remains unchanged. Exact From/To/Cc/Bcc/subject/body send receives provider evidence; replay after reopen returns the original receipt with one send. Disconnect removes use from two bound agents while preserving bindings/receipt.
- A paused refresh completes after disconnect: CAS rejects it and cached use stays blocked. Parallel callbacks exchange/commit once. Metadata writes use secret-generation concurrency checks, preventing old refresh material from overwriting a rotated token. Auth failure or global send disable before outbound dispatch leaves the draft unclaimed. Unknown post-dispatch results remain Indeterminate and are never automatically retried.
- Migration converts only old Gmail Password rows, preserving Credential/binding identity and advancing revision/epoch while clearing active obsolete material. Custom protected values remain. A frozen pre-OAuth Custom approval hash remains compatible; Gmail hashes additionally bind authorization epoch. Historical backups/WAL are not claimed erased.
- Visible UI Add → Synthetic consent → authoritative return → Test access → disconnect confirmation → disconnected → reconnect passed at all four widths. The owner chooser required explicit radio choice, survived reload, continued in one fresh Run with one original user entry, and cancelled without granting. Separate send preview showed verified sender and Bcc before explicit approval. Adjacent general Credentials CRUD, Instance bindings/profile reset and mobile navigation passed.
- Reconnect preserves only safe unsaved name/status in same-tab sessionStorage, restoring them after modal fields mount. No token or protected value enters that storage. Blocking storage cleanup cannot prevent dismissal or turn a committed save into a reported failure. Add/Edit Escape restores trigger focus. Status filtering uses an accessible native Select in the shared collection toolbar, avoiding AntD sizing-row hidden focus stops.

## Impeccable provenance and responsive evidence

Operate mode; develop/frontend/testing/Impeccable, PRODUCT/DESIGN and existing surface briefs were read. Context ran once. Incumbent Credentials/chooser screenshots were inspected before editing. Direct AntD v6, AgentConfigurationPanel, the existing bounded dialog/details/confirmation, shared toolbar and token-owned 8/12/16 spacing were reused. No separate design system was added.

The user authorized two independent review agents. Assessment A (design only, independent of detector/B) scored 30/40, Good. Assessment B (detector + live DOM/keyboard, independent of A) scored 15/20, Good before corrections. Narrow detector returned `[]`, exit 0. The review identified commitment clarity, weak check feedback, missing next-step guidance, adjacent alert spacing, inaccessible Status filtering and lost dismissal focus. Corrections were made in one coherent batch; subsequent probes/tests corrected the hidden filter clone, guarded storage cleanup and modal-mount restoration race. Scores were not silently upgraded after fixes.

The detector overlay server timed out; its stop reported no running server. CLI detector and live DOM/geometry/keyboard evidence are the recorded fallback; no overlay pass is claimed. The bounded visual cycle used a batched desktop/tablet/mobile/narrow inspection and confirmation. Functional recovery/regression checks remained independent of the discretionary polish limit.

| Viewport | Dialog width / scroll width / client width | Page horizontal overflow |
| --- | --- | --- |
| 1440 × 900 | 640 / 640 / 640 | None |
| 768 × 900 | 640 / 640 / 640 | None |
| 390 × 900 | 374 / 374 / 374 | None |
| 320 × 900 | 304 / 304 / 304 | None |

Long verified Synthetic address wrapping, semantic status, local scrolling and pinned footer were observed. Mobile buttons retained the shared 40px baseline. Root MCP checked the host health profile as Synthetic, performed actual consent/check/disconnect/reconnect and inspected console/network: zero errors/warnings; relevant connection reads/checks returned 200. Physical touch, screen-reader speech, 200% zoom, performance profiling and real Google UI are not claimed verified.

Before: [existing email editor](assets/email-account/email-edit-1440.png), [existing mobile inventory](assets/email-account/email-admin-390.png), and existing chooser captures. After: [1440](assets/gmail-oauth/connected-1440.png), [768](assets/gmail-oauth/connected-768.png), [390](assets/gmail-oauth/connected-390.png), [320](assets/gmail-oauth/connected-320.png), [narrow action/focus state](assets/gmail-oauth/actions-320.png), [disconnected state](assets/gmail-oauth/disconnected-320.png). DESIGN/PRODUCT and the sanctioned schema-2 sidecar describe observed OAuth presentation; doctor returned no drift findings.

## Acceptance mapping

| Criteria | Evidence / limitation |
| --- | --- |
| OA-01, OA-24 | Add/config guidance component tests and actual labeled Synthetic consent; Custom remains tested |
| OA-02, OA-07, OA-08 | Protected callback grants, safe errors, verified identity/scope checks and duplicate/reconnect identity fixtures |
| OA-03, OA-04 | Real-adapter fake transport with SQLite/key-ring reopen/refresh and immutable hash; Compose Synthetic grant/key-ring recreation |
| OA-05, OA-06 | Logical/secret CAS, paused refresh/disconnect, one-use parallel callback, owner/browser/state/TTL, PKCE challenge/verifier and nonce checks; production JWT malformed-input rejection plus maintained Google signature-validation library |
| OA-09–OA-11 | Account-prefixed REST IDs, bounded inbox/read, plain text, immutable Core MIME send; provider Draft methods reject |
| OA-12, OA-13 | Durable replay/reopen and uncertainty tests, token preflight/global policy before claim |
| OA-14, OA-15 | Custom loopback TLS/PEEK/UID/SMTP suites and Browser secret isolation regressions |
| OA-16, OA-17 | Live eligibility/profile fencing and owner chooser API/runtime/browser tests |
| OA-18 | Synthetic Automation runtime tests prove missing inventory/direct disconnected reads durably become NeedsAttention without browser consent; 11 selection-runtime cases passed |
| OA-19–OA-22 | Global disconnect, retained two bindings/receipt, original subject reconnect, forward migration and store/reopen CAS parity |
| OA-23 | Safe API projections and known-value token checks; callback/named-client query/body logging disabled; diagnostic successor reconciliation passed with safe IDs and redacted provider snapshots |
| OA-25, OA-26 | Four-width actual interaction/geometry plus explicit chooser/approval; verified sender/Bcc preview separate from connect |
| OA-27 | Fixed configured HTTPS/loopback origin, same-origin Vite callback exercised; HTTPS callback fixture proves Secure cookie and rejects forwarded-host redirect poisoning. Deployed Real Google redirect remains deferred |
| OA-28 | Final merged-source hosted jobs pending; no closure claim yet |
| OA-29, OA-30 | Canonical owners, design sidecar, report/screenshots and independent review/fixes; local links, anchors, fences and complete JSON examples checked |
| OA-31 | **Blocked/deferred**: no configured real Google application, dedicated mailbox/recipient opt-in or delivery evidence |

## Failure and fallback ledger

- Baseline hosted speech assertion failure is retained; focused unchanged rerun passed. It was not attributed to OAuth.
- Sandbox MSBuild local sockets were blocked initially; authorized escalated execution ran the checks. Generated files/migrations did not touch a user database.
- A broad web shell selected Node 26 and failed 77 tests through the known storage environment mismatch. Node 22 unrestricted parallel execution had four failures (three timeouts and one visibility assertion). The full unchanged assertions passed with bounded two-worker execution; hosted workflow already uses one worker per shard.
- TypeScript verification caught invalid RTL exact options and a missing React useRef initial argument; corrected and rebuilt. An initial Compose build failed those checks. The extended Compose proof then caught a script helper NameError; corrected, rerun, and OAuth/grant/key-ring survival passed. Failed attempts are not counted as passes.
- Browser probes exposed a decorative icon accessible-name mismatch, lost modal focus, inaccessible/hidden-cloned filter controls and a pending-name restoration race. Fixed and checked with actual interaction plus repeatable tests. Some probe resets/request URL inputs also timed out or failed in the automation harness; corrected probes are recorded separately from passing product outcomes.
- The Impeccable overlay launcher timed out; narrow detector/live DOM evidence was used and the failed overlay was not represented as successful.
- A detached direct-read regression initially exposed an incorrect assumption about the flat Core error envelope; the trusted top-level classifier was corrected and all 11 runtime cases passed. Mailbox body content cannot set Run outcomes.
- The first reconciled full backend run exposed two isolated model-catalog fixture containers missing the new main IDiagnosticIdSource dependency. That dependency was registered in the fixtures; production diagnostics and capability assertions remain unchanged. This is reconciliation of the newer diagnostics, not an OAuth transport failure.
- Live Gmail and recipient delivery remain deferred. No real email was sent, no provider policy was bypassed and no existing database/credential catalog was reset.
