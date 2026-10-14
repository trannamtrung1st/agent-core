# Public binary file downloads — local verification, 2026-10-14

The approved enhancement adds `web.download` to the existing public-web transport and owned Workspace path. General Assistant v29 explicitly authorizes it; older published versions and existing Instance pins are unchanged. Adopt v29 or publish an expressly authorized Definition to use the capability. No UI, database migration, new storage engine, conversion stack or additional provider is part of this change.

## Implemented behavior

- Public HTTP(S) GET, per-hop URL validation and connection-bound public DNS/IP filtering; no browser credentials, cookies, bearer tokens or automatic execution.
- Exact bounded bytes independent of MIME, including missing/generic/inaccurate declarations. Default 10 MiB / 15 seconds; configurable up to 50 MiB / 30 seconds. Text fetch remains 2 MiB with 256 KiB-character projection.
- Owned `/home` or `/working` destinations, optional safe `/home/downloads/` filename from final URL or Content-Disposition, create-only commits and explicit current-hash replacement. Invalid known destinations/guards fail before HTTP; atomic storage checks cover races and late filename collisions.
- Metadata-only result with completion, logical path, source/final URL, actual/declared content type, byte size, SHA-256 and untrusted classification. Obvious HTML/error/login and basic known-format mismatches fail before commit. Successful download does not certify full file validity or editing support.
- Normal authorization/discovery/projection and non-replayable Run effect fences. Completed receipts restore from checkpoints without replaying the download. Unknown effects retain the existing recovery fence; never blindly retry an uncertain commit.

## Exercised functional scenarios

| Scenario | Expected and observed outcome |
| --- | --- |
| Controlled official-link discovery → original DOCX download → inspect → fill → publish → protected readback | Found the fixture URL through `web.search`; saved original bytes/hash exactly; selected and replaced the existing form paragraph; retained styles, untouched instructions and table content; returned an Artifact with exact edited bytes and matching protected preview hash. Passed. |
| Actual owned Synthetic model/Session/Run loop | Agent tools downloaded, inspected, edited and published the original; final response referenced the real Artifact. No file bytes/base64 entered model messages or the bounded checkpoint. Passed. |
| Provider retry after a committed download | Waiting Run checkpoint retained metadata; no pending download remained; continuation completed with one HTTP dispatch total. Passed. |
| Binary formats and MIME | Real DOCX/XLSX packages, PNG fixture, PDF/legacy-DOC signature fixtures, ZIP and arbitrary bytes retained exact bytes/SHA-256 with missing, octet-stream and inaccurate MIME. XLSX inspection succeeded; legacy DOC editing was explicitly rejected. Passed. |
| Ownership and isolation | Home file survived a new owned Session; scratch was absent in the next Session; another Instance could not read/write it. Old v28 did not gain authorization. Passed. |
| Header filename, redirects and guarded writes | Header traversal was reduced to a safe leaf under `/home/downloads`; public redirect metadata was retained; default/stale replacements conflicted before HTTP; current-hash replacement succeeded. Passed. |
| Transfer/security boundaries | Private initial/redirect URLs, private/mapped DNS answers, redirect loops, partial/error/encoded responses, declared/chunked oversize, timeout and cancellation produced bounded failure without file bytes in results. Existing mixed-address/rebinding socket tests now inject a deterministic connection failure instead of attempting an unbounded external connection. Passed. |
| Storage failure and restart | Quota and post-transfer cancellation left no file/blob; forced SQLite metadata failure removed new bytes; host restart read back the original home bytes without network dispatch. Passed. |
| Discovery | Authorized v29 loaded `web.download` on demand and projected its schema; v28 could not discover/load the new grant. Passed. |

Existing Office behavior preserves supported package/paragraph properties. Paragraph replacement normalizes individual run formatting and returns that warning; complex unsupported features remain explicit errors. No desktop Word rendering or legacy DOC conversion/editing is claimed.

## Commands and results

Final per-project regression results:

| Check | Passed | Failed | Opt-in skipped |
| --- | ---: | ---: | ---: |
| Domain | 196 | 0 | 0 |
| Application | 1,809 | 0 | 8 |
| Infrastructure | 1,404 | 0 | 10 |
| API full run | 613 | 2 | 3 |
| API focused recheck (download + recovery + wire cases) | 45 | 2 | 0 |
| Pre-download baseline comparison | 2 | 1 | 0 |

All **32 newly added download tests** passed (21 Infrastructure, 2 actual Run variants, 9 API scenarios); all nine API download scenarios passed again in the focused recheck. These counts are not inflated by repeat execution. The full API command is **not green**: `SendText_killed_before_persist_is_not_silently_dropped` reached its 30-second deadline and the `structured-composer` MessagePack fixture disconnected. The focused recheck passed that SendText case, but another existing 30-second recovery fixture (`Lost_text_ack_retry_with_changed_payload_after_host_reconstruction_is_rejected`) timed out; structured-composer failed again. A read-only pre-download baseline (`a04e6a76`, no enhancement files) reproduced the structured-composer failure with the same closed-connection error at `tests/realtime-js/client.mjs:244`, while both recovery cases passed. Both recovery cases then passed alone on the download candidate (2 passed / 0 failed, 16 seconds), matching the passing baseline comparison. Their broad-suite timing failures are retained rather than erased from the ledger. The composer failure is pre-existing; a complete green broad API gate is not claimed.

 Earlier focused runs passed the original Office journey, six then eight download API scenarios, 49 focused Infrastructure/public-fetch/Workspace scenarios and both owned Run variants. Early failures were corrected: an asynchronous assertion, preview metadata expectation, missing checkpoint-test namespace and v29 catalog version/count expectations.

The shared checkout received concurrent unrelated UI/Office-preview edits while work was in progress. To verify only this change, a disposable snapshot at `/tmp/agent-core-public-download-candidate` was built from committed main `a04e6a76` plus the explicitly listed task files. Concurrent uncommitted files were preserved and excluded from this commit. Subsequent shared-checkout commits `d2bada99` and `4aaee854` change only unrelated UI/docs; the verified backend source remains the same. Shared canonical documents received concurrent edits after verification; only this task’s verified document bytes are staged, leaving the other work intact in the checkout. A compiler PathMap normalizes macOS `/private/tmp` source paths so pre-existing log-redaction fixtures do not mistake the path word `private` for a leaked test secret.

A fresh default restore hit pre-existing NuGet advisory errors (NU1902/NU1903) for System.Security.Cryptography.Pkcs 10.0.0 and ASP.NET SignalR 10.0.12 packages. The functional regression command retains these as visible warnings using `WarningsNotAsErrors`; no package versions or repository warning policy were changed. This is a remaining default fresh-restore gate, not a passing dependency-security gate.

```sh
dotnet test AgentCore.sln --nologo -m:1 /nodeReuse:false \
  '-p:WarningsNotAsErrors=NU1902%3BNU1903' \
  '-p:PathMap=/private/tmp/agent-core-public-download-candidate=/tmp/agent-core-public-download-candidate' \
  --blame-hang --blame-hang-timeout 2m --logger trx \
  --results-directory /tmp/agent-core-public-download-final-results
```

The solution invocation supplied the passing Domain/Application/Infrastructure evidence. Its duplicate API process was stopped once the standalone API run was active. API was run independently with the same MSBuild flags. The focused recheck used `--no-build --no-restore` and selected `PublicDownloadJourneyTests`, the two recovery cases and `SignalRMessagePackTests.JavaScript_messagepack_scenarios`. Baseline comparison selected only the two recovery cases and DisplayName `structured-composer`.

Logs: `/tmp/agent-core-public-download-final.log`, `/tmp/agent-core-public-download-final-api.log`, `/tmp/agent-core-public-download-recheck.log`, `/tmp/agent-core-public-download-baseline.log`; structured results: the corresponding `/tmp/agent-core-public-download-*-results/` directories. The final standalone recovery command used the same no-build API invocation filtered only to the two recovery method names; log `/tmp/agent-core-public-download-recovery.log` and results `/tmp/agent-core-public-download-recovery-results/`. Documentation checks cover relative links/anchors, balanced fences, complete new JSON examples, immutable old Definition authority and `git diff --check`.

## Acceptance boundaries

This is local implementation/functional evidence, not a new historical milestone freeze. The user explicitly authorized commit/push to main and waived waiting for external CI; hosted results are not claimed. No live official website discovery/download, real model choice/content fidelity, browser login/CAPTCHA, desktop Office-client formatting verification or legacy DOC editing was run. No UI source is changed by this task, so frontend/build/Playwright/Compose gates are not required for this backend-only extension. Existing dependency advisories and the pre-existing structured-composer failure remain independent gates; broad API recovery timing failures are reported above. Local functional download acceptance is demonstrated, but full regression/milestone acceptance is not claimed.

Subsequent stabilization: the [2026-10-14 Office productivity pass](office-productivity-stabilization-2026-10-14.md) resolves the fresh-restore package audit blocker on newer source and records focused empty-range checks plus dedicated Real DOCX/Drive evidence. This report's original source, failures and acceptance boundaries remain historical.
