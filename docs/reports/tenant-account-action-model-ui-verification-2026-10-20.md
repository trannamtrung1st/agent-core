# Tenant account-action model and shared inspector verification — 2026-10-20

## Scope and status

All nine exact account-action methods now pass through the capability-filtered model authorization schema, existing durable consent, successor Run, canonical effect and factual result owners. The shared Organization/Chat inspector presents complete saved Email/Drive actions and separates human decisions from execution facts. This advances the [internal-owner checkpoint](tenant-account-action-owner-verification-2026-10-20.md); it does not close connected-account module acceptance or the [full proposal](../../local/proposals/agent-core-multitenant-iam-approval-final-proposal.md).

Account discovery, read-only operation-ID reconciliation, abandoned dispatch across a cold application host, dedicated background revocation/recovery variants and remaining proposal families stay open. Pending exact send across an actual disposable SQLite process restart is qualified below. No hosted CI, real mail delivery, external Google account, deployment or push was requested or run. Work after `4273ac17` remains uncommitted.

## Implementation

`TenantApprovalToolContract`, `ToolCatalog` and `TenantTaskExecutionContract` expose only selected exact nested methods under `use_connected_account`. Core-owned immutable draft creation and separate exact send use independent consent. Drive actions require already approved exact `drive.get` target/folder observations, original upload bytes and a new download path. Primary grants never supply independent raw account/source/workspace authority. Unsupported autonomous origins and completion-report execution remain outside this offering.

`AccountActionScript` is an offline fixture that uses ordinary offered schemas, consent and result paths. It preserves its original plan across approval successors and resolves `$draft` only from the previously retained created-draft result. Its explicit marker is selected before the generic background NoAction fixture. A denial/unknown result stops further actions and offers no retry. No product debug endpoint or database fixture mutation was added.

`TenantAccountActionResults` reads optional factual details from the exact existing receipt and retained Run tool-result provenance after current compound human scope. Local atomic draft facts survive checkpoint compaction; missing remote details never prove nonexecution. Result validation refuses corrupted/null batch items, target/order changes and contradictory completion prefixes. The optional `operationResult.accountAction` HTTP projection introduces no persistence owner.

`AccountActionProposal`, `AccountActionPreview` and `AccountActionResult` extend the existing native inspector. Account name, friendly type and bound reference precede complete inert Email sender/To/Cc/Bcc/subject/body/draft identity, or exact ordered Drive target metadata and transfer paths/bytes/checksum. Recoverable Trash, possible completed prefixes, fresh-source requirements and independent scopes remain explicit. Malformed evidence disables approval/rejection while retaining authorized cancellation. `SavedApprovalText` shares the existing native literal composition with Workspace mutation consent. No UI kit, CSS or design-token change was introduced.

The central SQLite API fixture now binds Email drafts and native/Synthetic Drive ledgers to SQLite, matching production. Previously it combined SQLite IAM with in-memory Email drafts; the composed HTTP test correctly exposed that fixture inconsistency.

## Focused verification

Eighty-five distinct backend cases and 114 distinct frontend cases pass in the qualification groups below. These counts are not added to the earlier 133 internal-owner cases, which overlap. Passing independent groups were preserved after fixture-only corrections.

| Group | Cases | Result and retained log |
| --- | ---: | --- |
| Closed account arguments/hash, selected-tool model offering and exact factual receipt/result refusal | 33 | Pass: 32 in `/private/tmp/tenant-account-action-contract-results-tests.log`, corrected single projection fixture in `/private/tmp/tenant-account-action-schema-fixed.log` |
| All seven Drive actions through actual hosted model → exact read/action consent → native Synthetic effect → public factual details, Memory/SQLite | 14 | Pass: `/private/tmp/tenant-account-drive-model-tests.log` |
| Existing actual hosted account-read regressions plus immutable draft → separate exact send, Memory/SQLite | 34 | Pass: `/private/tmp/tenant-account-action-model-regression-tests.log` |
| Dedicated immediate-background Email draft/send and Drive read/rename, Memory/SQLite | 4 | Pass: `/private/tmp/tenant-account-action-background-fixed.log` |
| Nine-method preview, malformed evidence, ordered twenty-file manifest and partial-result presentation | 22 | Pass after final Account type change: `/private/tmp/tenant-account-action-preview-final-tests.log` |
| Existing shared approval consumers, including Workspace exact literals | 85 | Pass with the initial22 preview cases: `/private/tmp/tenant-account-action-web-tests.log` |
| New account Review/primary-only confirmation, malformed decision/cancel, draft-result separation and read-loss removal | 7 | Five pass in `/private/tmp/tenant-account-action-ui-decisions-tests.log`; two modal-animation fixtures corrected and pass in `/private/tmp/tenant-account-action-ui-confirm-tests.log` |

The Drive model cases compare exact binary uploads/downloads, SHA-256, remote name/parent/trash state and ordered item identities. Prior exact source-change, destination-occupied, partial native HTTP and no-replay checks remain in the internal-owner report. Background cases inspect the real child Session, its two separate consents, exact successors and final BackgroundOutcome; no account direct mutation tool or child fan-out is offered. Every actual hosted case observes zero outbound HTTP attempts.

Fixture corrections: the model projection test initially assumed removing account bindings removed unrelated background-creation authorization; it now checks only the account-action branch. The background fixture originally reached the generic Synthetic NoAction fallback before the explicit account plan; ordering was corrected and all four cases re-executed. UI confirmation assertions now wait for settled native entrance motion. These failures were not suppressed or counted as passing evidence.

## Actual browser and cold-process journey

A disposable SQLite Synthetic host and Vite app were used at localhost ports5080/5173. The fixture account was connected and bound through canonical authenticated HTTP/OAuth routes. Agent raw family, account-use and Session permissions were explicitly assigned. No private real account or direct database row editing was used.

1. Through the visible Chat composer, submit an exact two-step draft/send plan. Inspect the full saved sender, To/Cc/Bcc, subject, immutable draft and911-character UTF-8 body, including literal script text. The body remains read-only; script content never executes. Desktop1440×1000 and mobile390×844 retain scrolling and reachable wrapping footer actions without horizontal overflow.
2. Approve only draft creation. Observe its completed local draft receipt and a distinct pending exact send request. The draft request is `01a1591a-9da3-723f-b90a-99884cf2385b`; send request `01a1591f-4730-7182-9758-e96abae1bd7f` binds the same immutable draft `tenant-draft-01a1591a9d807f09b07ddf3ace29db07`.
3. Stop only the owned backend process and recreate the application against the same disposable SQLite database, key root and files. Reload the independent pending send inspector. Request identity, exact saved recipients/body/draft and decision capability survive. This is an actual process recreation, not only store reopen.
4. Confirm send once through the ordinary protected confirmation. Automatic approval review initially classified it as possible real email delivery. Inspection of the healthy Synthetic profile and `AccountEmailProviderFactory`/`ScopedSyntheticEmailProvider.SendDraftAsync` established that this adapter returns a local `syn-sent` receipt without SMTP/Google HTTP. The evidence-backed retry was accepted; no bypass or alternate execution path was used.
5. Refresh the separate factual operation result. It records confirmed acceptance and the exact draft, without claiming recipient reading. Return to the original Chat and observe the final saved answer, “The provider confirmed acceptance of the exact saved email draft,” with both approvals completed. The observed send decision POST returned200. Current-flow console inspection returned zero errors.

The disposable database is under the root recorded in `/private/tmp/tenant-account-actions-ui-root.txt`; host setup/readiness/restart logs are in `/private/tmp/tenant-account-actions-ui-host*.log`. Initial stale cookies, pre-readiness connection errors and controlled host outage are fixture/restart context, not a claim of a globally error-free browser session. Tests exercise normalized current-source and scope checks independently.

## Bounded visual evidence and handoffs

Eighteen settled initial captures are in `.impeccable/review/tenant-account-actions/`: draft desktop/mobile/body, protected confirmation desktop/mobile, recovered send, send result desktop/mobile, twenty-item manifest beginning/tail at both widths, upload/download at both widths, and malformed preview at both widths. All were opened and validated. Drive manifest/transfer/malformed responses are explicitly controlled presentation fixtures; they qualify no provider effect. The twenty-item fixture retained an irrelevant unavailable-reason value from the terminal source detail, despite an enabled decision flag; that disclosed fixture-only contradiction is excluded from product findings.

The fresh independent finish reviewer found one material omission: validated account type was not displayed. The one correction retains and renders friendly Account type immediately after Account. Four final `account-type-{email,drive}-{desktop,mobile}.png` captures were validated. The same reviewer scored that sole fix **resolved** and returned **ship** at fix-list scope only; it is not a new full-surface verdict. The one detector invocation returned `[]` in `/private/tmp/tenant-account-action-ui-detector.json`. The independent documenter compared the incumbent system, source, four final captures and mobile send result, confirmed agreement and appended the ordinary-extension surface brief. PRODUCT, DESIGN and the sidecar were preserved; pre-existing sidecar drift remains uncorrected. No shipping raster asset was created.

Dedicated visual acceptance for every other action/result variant, actual Drive twenty-item browser effects, background browser interactions and real-provider delivery remain outside these captures. Unit/hosted evidence is recorded at its own scope.

## Final checks and remaining scope

The final web TypeScript/Vite build passes in `/private/tmp/tenant-account-action-web-build-qualified.log`, with existing dependency annotation/chunk-size warnings. The final solution build passes with zero warnings/errors in `/private/tmp/tenant-account-action-model-ui-final-build.log`. Changed-document verification checks21 Markdown files and1987 relative link targets with no missing links or fence/untracked whitespace errors; `git diff --check` passes. No milestone acceptance or full-proposal completion follows from this checkpoint.
