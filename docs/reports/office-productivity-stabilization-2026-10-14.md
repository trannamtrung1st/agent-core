# Office productivity stabilization — 2026-10-14

Scope: the owner-requested implementation review on `d279abfe`, applied after the documentation review `aef7ffe5`. This is a bounded stabilization pass: empty spreadsheet ranges, dependency auditing, a dense CSV profile, and an explicitly authorized live DOCX-to-Drive workflow. No new Office feature, parser architecture, UI kit, storage model or hosted CI run is introduced.

## Empty spreadsheet range correction

The Office processor formerly returned overlapping rows with zero cells when a requested column lay beyond the sheet's used columns. `OfficeReadResults.Workbook` then attempted to parse a null last-cell address. The processor now returns an empty row collection for zero-column intersections; continuation calculation also requires actual first/last cell addresses.

Both `attachments.read` and `office.spreadsheet.inspect` retain SHA, selected range, worksheet list/extents and used range. An empty intersection has `rows:[]`, `returnedRange:null` and `nextRange:null`. `truncated` still describes incomplete coverage of a nonempty selected sheet. Six integrated regression cases cover `Z1:Z10`, `A20:B30`, and `Z20:Z30` against used range `A1:B10`, bounded 4096-byte results, then recovery to a populated range with unchanged original bytes/hash.

## Dependency repair and audit

The baseline fresh restore reproduced NU1902/NU1903 failures for PKCS, SignalR MessagePack and the SignalR client/Common graph. Changes:

- SignalR MessagePack and the test SignalR client: `10.0.13`.
- MessagePack in API, Contracts and API tests: `3.1.9`.
- Infrastructure explicitly pins the transitive MimeKit CMS dependency `System.Security.Cryptography.Pkcs` to `10.0.13`.

Patched versions follow the maintainers' [SignalR advisory](https://github.com/dotnet/aspnetcore/security/advisories/GHSA-86xp-94pg-8r6r), [MessagePack advisory](https://github.com/MessagePack-CSharp/MessagePack-CSharp/security/advisories/GHSA-qhrr-8q5h-9q3h), and [PKCS advisory](https://github.com/dotnet/runtime/security/advisories/GHSA-2fmv-rv2c-59x7). NuGet auditing and `TreatWarningsAsErrors` remain enabled; there is no suppression or WarningsNotAsErrors override.

`dotnet restore AgentCore.sln --force --no-cache --packages /tmp/agent-core-stabilization-nuget` restored all ten projects into a new package directory. The subsequent solution build had zero warnings/errors. `dotnet list AgentCore.sln package --vulnerable --include-transitive --no-restore` reported no vulnerable packages for all ten projects against the current feeds. These are package-graph results; they do not certify the machine's shared runtime. The installed native .NET/ASP.NET runtime remains `10.0.12`; deployment should use the patched `10.0.13` runtime/SDK and restart its host. The already-running Real app was not restarted or replaced in this task.

## Dense CSV profile

Chromium through Playwright MCP, native Synthetic .NET/Vite, warm preview modules, 4× CDP renderer CPU throttling. Source: 5,000,000 UTF-8 bytes, 50,000 rows × 50 single-character fields (2.5 million cells), near the default 5 MiB limit. The same production worker/parser and FortuneSheet composition were exercised. CPU throttling is an emulation; it is not a physical low-powered-device result or a claim about worker CPU scaling.

| Measurement | Observed |
| --- | --- |
| Worker parse + main-thread transfer, 3 samples | 200.9 / 201.6 / 194.4 ms |
| Main-thread long task during those samples | 109 / 115 / 110 ms |
| Main-thread heap increase during those samples | approximately 15.7 MiB |
| First actual grid ready, 1 sample | 1664 ms |
| Selected-cell navigation to AX200, 1 sample | 474 ms; correct literal value `9` |
| Main-thread heap increase for first grid | approximately 25.2 MiB |
| Largest first-grid long task / timer gap | 467 / 490.6 ms |

The renderer remains windowed to 200 rows. Memory was bounded and parsing completed well inside the 5-second worker deadline. There is measurable main-thread jank; smooth interaction is not claimed. The full-collection handoff contributes about 110 ms in this emulation, while the largest measured stall belongs to the grid-open path. For this v1 ceiling, retain the simpler terminable-worker implementation: persistent worker windows alone would not remove the larger canvas/composition cost. Revisit only if a target device/customer workload requires tighter latency or larger limits. No speculative worker-state refactor is introduced.

## Authorized Real DOCX-to-Drive UAT

The owner selected the Real profile, `agent-core-drive`, `agent.core.001@gmail.com`. The saved credential is displayed as **Agent Core Drive**, connected with `synthetic:false`. A new General Assistant v29 Instance was created with only that explicit Drive binding for this test. Existing user Sessions/Instances were not reset. The Real host at `localhost:5173` reported build source `d279abfe`; its DOCX/download/Drive behavior is unchanged by the empty-spreadsheet/package patch. Final patched-source behavior is separately covered by local integration tests; this live run is not an exact-candidate deployment claim.

Live Instance: `01a13a01-93d8-7705-9f7a-6034114ee46d`; Session: `de6b2494-ef59-4be8-bbd8-46c44a43cd89`.

1. The actual model used `web.download` for the [GOV.UK public DOCX form](https://www.gov.uk/government/publications/the-right-to-request-flexible-working-form). Intake saved 20,535 bytes, SHA-256 `055fe6227daa62d63d0764158214c9a7bdac55427b37cd27229118efa65bc673`. Office inspection correctly reported `office_feature_unsupported` for external relationships, without calling the valid file corrupt. The original remained unchanged.
2. The actual model downloaded the compatible [Apache POI public SampleDoc.docx](https://github.com/apache/poi/blob/trunk/test-data/document/SampleDoc.docx), inspected the original and replaced only its first paragraph using the exact selector/current SHA. It did not reconstruct the document. The source was 11,611 bytes with SHA-256 `ea2c517bd7be4628887eb97f630334b93b894f4522d2c0adae263e602d76df92`.
3. The edited original was published as `office-uat-20261014.docx`, Artifact `01a13a05-3c41-77ad-8769-3eac230adb42`: 12,130 bytes, SHA-256 `ce04d76378ed82f519d6fb5016d0145b43edeeae5605cda48068d648a11b5e73`. Browser preview rendered two pages and the exact replacement text. Independent ZIP/XML comparison found the same package-part set, identical styles and all six other paragraphs unchanged; only `word/document.xml` and `word/settings.xml` differed. Run-format normalization remains the documented editing limit.
4. The model uploaded the exact edited bytes as one new binary DOCX, without Google Docs conversion. Independent protected remote download and Artifact download were byte-for-byte equal, with the expected SHA/size. Remote file: `12Rrluez2PepBUONn7rF1Lh2V7Ir3umfE`.
5. A settled exact rename approval was rejected with Escape. Fresh remote metadata remained unchanged. A subsequent exact proposal was approved with Enter, renaming only that test file to `AgentCoreUat-20261014-114ee46d-renamed.docx`, version 3→4. An old-version content read returned 409; reading with the fresh version returned 200 and unchanged bytes. This proves the exercised rejection/version recovery; it does not close the earlier batch-approval timing regression.
6. Exact single-file Trash approval moved only this new test file to Trash. Independent metadata showed `trashed:true`, version 5, same size/MD5; the active app-authorized list no longer contained it. No permanent deletion occurred. Original/edited local files and the published Artifact remain recovery material. Restoring the remote item from Google Trash was not exercised; native untrash is not added.
7. Browser reload reattached the same Session and recovered the Artifact card. Read-only operation inspection returned `Succeeded` for upload `3ca8391d-2e41-5241-8bfa-3e0942e44dd8`, rename `db128706-adcd-774a-01c0-d9150cee4b93`, and Trash `49ed4abb-a10d-02d2-0897-fd6591cb47b3`. No operation was replayed to verify its receipt.

Live model prose is not the verification oracle: one later answer misstated the untouched government template's hash/size, and upload-time version 1 had advanced to 3 by an independent read. Recorded hashes/versions above come from protected metadata, downloaded bytes and durable receipts.

## Focused checks and limits

| Check | Result |
| --- | --- |
| Empty-cache restore, normal audit policy | Passed, all 10 projects |
| Solution build, `--no-restore` | Passed, 0 warnings/errors |
| Direct/transitive vulnerable-package query | No reported vulnerabilities in the restored graph |
| API: empty ranges, Office/attachments/download journeys and MessagePack wire | 66 passed, 0 failed/skipped; includes all 6 new cases |
| Infrastructure: Office processor/presentation, Drive and IMAP/SMTP adapter | 68 passed, 0 failed/skipped |
| File preview Vitest | 22 passed across 6 files |
| Frontend build | Passed; existing large-chunk/SignalR annotation warnings remain |
| Synthetic Office and CSV/format Playwright | 4 passed; exact CSV Text/copy/download, guarded Office preview/download and spreadsheet read-only/focus behavior |
| Real MCP workflow above | Passed for the compatible DOCX; explicit unsupported-template boundary retained |

API filter: `FullyQualifiedName~outside_used_range|FullyQualifiedName~OfficeJourneyTests|FullyQualifiedName~AttachmentApiTests|FullyQualifiedName~PublicDownloadJourneyTests|FullyQualifiedName~SignalRMessagePackTests`. Infrastructure filter: `FullyQualifiedName~OfficeProcessorTests|FullyQualifiedName~OfficePreviewPresentationTests|FullyQualifiedName~ImapSmtpEmailProviderTests|FullyQualifiedName~GoogleDrive`. Tests used `--no-build --no-restore` after the audited build, with no warning overrides.

Browser command: `PLAYWRIGHT_API_PORT=5280 PLAYWRIGHT_WEB_PORT=5376 PLAYWRIGHT_FAITHFUL_MANUAL=1 pnpm exec playwright test e2e/file-preview-formats.spec.ts e2e/office-preview.spec.ts --project synthetic`. Disposable Synthetic storage used `/tmp/agent-core-stabilization`; only task-started backend/Vite processes are stopped. The Real host and retained UAT Session/files are left available.

Documentation checks passed for local links/anchors, balanced fences and `git diff --check`. The normal-cache restore afterward is logged separately in `/tmp/agent-core-normal-restore.log`.

Logs: `/tmp/agent-core-baseline-restore.log`, `/tmp/agent-core-clean-restore.log`, `/tmp/agent-core-stabilization-build.log`, `/tmp/agent-core-stabilization-audit.log`, `/tmp/agent-core-stabilization-api-tests.log`, `/tmp/agent-core-stabilization-infrastructure-tests.log`; profile measurements: `/tmp/agent-core-csv-profile.json`.

Hosted CI/full suites/milestone acceptance were not requested or run. Desktop Word/Excel fidelity, physical low-powered device performance, screen-reader canvas navigation, real bulk Trash, provider-loss/uncertain-upload fault injection and remote Trash restoration remain unverified. The previous [UI review](recent-git-ui-design-review-2026-10-14.md) preserves its historical XLSX clipping-notice and batch-approval keyboard findings. Incoming main subsequently corrected both; see the final-main recheck below. Legacy DOC remains downloadable but is not natively edited; charts/pivots/conditional-formatting fidelity, Google-native editing, PDF preview and browser authoring remain outside the accepted feature boundary.

## Reconciliation with advancing main

During the pass, `origin/main` advanced from `aef7ffe5` to `c279e21a` with the separate accepted OpenSandbox cutover, overlapping SignalR/PKCS security pins and the earlier XLSX notice/approval-tooltip corrections. This stabilization commit was rebased onto that main without replacing its sandbox code, immutable v30 Definition, UI fixes or acceptance evidence. MessagePack `3.1.9` and the test SignalR client `10.0.13` remain the additional consistent pins from this pass. The empty-range correction/tests were unchanged. CSV parser/viewer behavior used for profiling did not change in the incoming work.

The final graph was restored into a second empty package directory, `/tmp/agent-core-stabilization-final-nuget`, using the official public feed via `--source https://api.nuget.org/v3/index.json`. All ten projects restored, the solution built with zero warnings/errors, and the direct/transitive query again reported no vulnerable packages. Logs are `/tmp/agent-core-final-restore.log`, `/tmp/agent-core-final-build.log` and `/tmp/agent-core-final-audit.log`. An intermediate normal-cache restore waited on the additional feed and was cancelled; the public-feed fresh restore succeeded without changing auditing or global feed configuration. Final affected regression results are recorded below, separately from the earlier counts and pre-existing Real-host UAT.

Final reconciled-source checks: **66 API passes, 68 Infrastructure passes, 44 frontend unit passes and 6 Synthetic browser passes**, no failures/skips in the focused filters. The browser run includes the incoming explicit XLSX shortening notice and complete Drive reject/approve journey, so both historical UI findings are verified corrected on final main. Frontend build passed; pre-existing Composer `act(...)` warnings, SignalR annotations and large-chunk warnings are disclosed. No broad OpenSandbox reacceptance or hosted run was performed. The final browser command adds `e2e/drive-office.spec.ts` to the earlier command. Final API/Infrastructure logs are `/tmp/agent-core-final-api-tests.log` and `/tmp/agent-core-final-infrastructure-tests.log`.

Final normal-cache restore also passed against the official feed (`-m:1 /nodeReuse:false`), returning project assets to the regular package cache; log `/tmp/agent-core-final-normal-restore.log`. No feed configuration or audit setting was changed.
