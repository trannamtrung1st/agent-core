# Canonical tenant public HTTP qualification — 2026-10-23

Status: the public HTTP module is locally qualified. The original IAM proposal remains open.

## Delivered behavior

The preserved `http.request` capability uses the existing typed tenant approval, one-off grant, Run continuation, external dispatch fence, receipt and recovery owners. It adds one explicit agent-scoped Sensitive `http.request` permission with mandatory exact review for every method. Existing 77 classifications remain unchanged; the catalog now contains 78 permissions (24 ReadOnly, 36 Mutation, 18 Sensitive). Existing Contributor and operational roles gain no HTTP permission. The human-only Owner receives the new catalog member through the guarded forward upgrade.

Model consent is `approvals.request_authorization` with `operation=send_http_request` and one exact `request`. The prepared operation binds the actual Session, normalized public method/URL, the three permitted header names, exact bounded UTF-8 body and digest, plus Instance/persona revisions. GET, HEAD, POST, PUT, PATCH and DELETE retain the native adapter. Only GET/HEAD follow redirects. Cookies, credential headers and embedded URL credentials remain unavailable. Direct HTTP tool calls return consent metadata and cannot dispatch.

Raw executor Agent/Session reads remain independently necessary. Current compound human authority and independent-review controls apply before decision, dispatch and body delivery. The new paginated visibility facet filters both scopes before count/paging. The shared public-web target consolidates existing search/fetch and HTTP source checks. Prepared-operation variant selection is simplified without changing old serialized payloads or hashes; inert Harness bytes remain unchanged.

Reservation precedes native I/O. The existing sockets adapter invokes the current-authority callback before sending, after DNS, before each permitted connection and after connection before HTTP bytes. A failed authority check stops address fallback and disposes a connected stream. Explicit Content-Type is preserved even for an empty body. Non-idempotent requests have no automatic retry. Timeout, cancellation, abandoned reservation and transport loss after entering the native owner settle conservatively as Indeterminate and never dispatch again.

A completed HTTP observation retains factual status, public final URL, type, projected UTF-8 byte count/hash and truncation in the immutable receipt. It does not claim remote business success. Response text is bounded to 64 KiB of UTF-8, marked untrusted and saved only in the authorized existing continuation checkpoint. Authority loss retains the factual receipt but withholds the body. Reentry and atomic response/background/report output independently recheck current sources, saved call/receipt/decision/grant ownership and the projected body hash. Unmarked legacy HTTP results, added fields and changed content cannot become model context. Late noncooperative results are discarded.

## Persistence

`20261023190000_TenantPublicHttp` adds nullable `TenantOperationReceipts.HttpJson` and the closed HTTP approval branch. The prior variants require the HTTP payload to be absent. The predecessor's 135 guard bodies and all indexes remain unchanged. Three HTTP receipt guards bind completed facts to the approved owned reserved dispatch and reject receipt updates/deletes; the resulting schema has 138 guards. Frozen schema composition is reused instead of copying another large schema resource; predecessor migration SQL remains equivalent. The Owner-only permission insertion advances affected tenant security revisions. Contributor, custom and Agent roles are unchanged.

Empty Down/Up is supported. Retained HTTP requests, receipts, checkpoint markers or explicit non-Owner HTTP role permissions block lossy Down before foreign keys are disabled. The guard deliberately does not treat an older Definition's `http.request` capability string as a new authority fact.

## Local evidence

**421 distinct focused cases passed:**

- Application: 125 contract, risk-floor/catalog, approval-intent, public-read and existing HTTP-normalizer/tool cases.
- Infrastructure: 56 cases across both stores, including exact completed receipt/checkpoint reopen, current source-loss refusal, checkpoint-field tampering, abandoned reservation recovery, noncooperative timeout/late-result discard, pre-count approval visibility, Owner-only upgrade/security revisions, empty Down/Up, populated Down refusal, immutable HTTP receipt update/delete refusal, all prior guard/index preservation, public-read source regressions, DNS/address fallback, redirects and native HTTP transport.
- API: 88 distinct cases. The 84-case group covered 32 HTTP Chat cases and 52 existing Chat/Automation/reopen public-research cases. After adding the receipt guards, a 36-case HTTP group rechecked all 32 HTTP Chat cases and added four actual scheduled/manual Automation cases across both stores. HTTP cases exercise every admitted method, exact review, exceptional grants, source-policy review, rejection, direct-call refusal, missing raw reads, stale consent, revocation before/after native I/O, uncertainty, factual receipts and duplicate/recovery non-replay.
- UI: 135 existing approval checks plus 17 HTTP preview checks. The first grouped run's new preview test incorrectly counted Ant Design's information icon as an injected image; that assertion was corrected, and all 17 final HTTP checks pass. The existing 135 approval checks passed without rerunning the unrelated group after the HTTP-only preview fixes.

The serial solution build passed with zero warnings/errors. The frontend build passed with its existing large-chunk advisory. Focused .NET tests used `--no-build --no-restore`; CI was not requested or run.

Playwright MCP exercised the real shared inspector against a temporary Synthetic Kestrel host and Vite on port 5179. At mobile width 390 the saved URL, headers and body remained literal/read-only and the drawer had no horizontal overflow; no source URL link or injected image was created. At desktop width 1440, Approve opened the separate exact-action confirmation and the actual decision became Approved before any effect result existed. The controlled native adapter then dispatched once, the Run completed, and refreshed details showed HTTP 409, “HTTP response observed,” bounded projection metadata and the business-success caveat, with no approval button remaining. The receipt contains no response body. No browser console errors/warnings occurred after signing into the current temporary host. Screenshots were inspected locally; temporary task servers were stopped after verification.

Two defects found during qualification were fixed: the new variant initially lacked semantic root correlation, and empty-body GET/HEAD preview/header handling disagreed with the exact request. The grouped method and wire checks now cover both. Recovery-test setup also had to preserve checkpoint accounting and inspect the old Owner row through raw persistence while the previous catalog shape was installed.

The controlled model/native fixtures require no provider credentials or public network calls. The browser uses an isolated temporary Synthetic Kestrel fixture and Vite on port 5179. Existing Real hosts and installed databases are untouched.

## Remaining acceptance boundary

This qualifies the preserved public HTTP module locally; it does not establish proposal-wide acceptance. Generic Composer Resource references, the canonical long SSO/large-discovery/reserved-cleanup combinations, final remaining authority/dependency inventory, safe inert Harness retirement and the final proposal-wide security, isolation, functional and rendered UI pass remain tracked by the delivery ledger. Live provider/site behavior and optional CI are unrun.
