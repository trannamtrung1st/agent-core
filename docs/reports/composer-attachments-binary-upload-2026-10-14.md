# Composer attachment tiles and binary uploads — local verification

Date: October 14, 2026. Scope: requested composer presentation and unrestricted file-format intake. This records local working-tree evidence; it does not renew hosted milestone acceptance.

Pending uploads reuse `PendingAttachmentView` inside the incumbent Composer and native Ant Design controls. The attachment grid owns its 8px inset/gap and bounded vertical scrolling; each tile owns its 8px inset. Icon, truncated filename with full hover/keyboard tooltip, and icon-only Remove stay in one row. Upload progress, local errors and Retry remain available. Pending image Blob URLs and thumbnails are removed. Existing sent-file preview/download behavior remains.

HTTP intake stores all formats as inert originals regardless of the legacy unread-storage flag. Existing count, size, Session ownership/lifecycle, image signature and TTL limits remain. Legacy configuration fields are retained for serialization compatibility and hidden from active Admin controls. ZIP-based DOCX/XLSX route to the existing guarded Office reader: first 100 document elements or first worksheet A1:J50, under existing Office package/expanded-byte limits. Current-turn context and historical `attachments.read` use those bounded excerpts. Further authorized Office inspection uses an exact workspace copy; unknown binaries report no automatic reader and retain original bytes. Invalid UTF-8/control bytes are not mislabeled as extracted text. Upload storage never executes bytes.

## Checks and observed outcomes

- Domain `AttachmentClassificationTests`: 13 passed. Office MIME inference, inert archive/executable storage, unknown-binary detection, existing text/signature behavior.
- Infrastructure `AttachmentProcessorTests|AttachmentStoreContractTests`: 12 passed. Original bytes, unread metadata, extraction/cache, TTL and storage regressions.
- API `AttachmentApiTests`: 9 passed. Upload, bind and protected byte-for-byte retrieval for generated DOCX/XLSX, ZIP, opaque binary, executable bytes and corrupt Office containers without role opt-in. Office current-turn extraction and historical tool rereads return actual document/cell content; unknown binaries report original-byte access; corrupt Office extraction is unsupported. Existing archive/deletion/ended-session denials pass.
- Application `SessionAttachmentRecallTests|AttachmentProcessingRuntimeTests|AttachmentBindTests|QueuedAttachmentRuntimeTests`: 29 passed.
- Frontend Vitest: 49 passed across AttachmentPreview, Composer, attachment HTTP, Definition candidate editing and Instance settings. Native Node web-storage was disabled with `NODE_OPTIONS=--no-experimental-webstorage` to avoid the host runtime's localStorage conflict with jsdom.
- `pnpm run build`: passed; existing dependency annotation/chunk-size warnings remain.
- Browser: 3 distinct Synthetic scenarios passed. The new multi-file flow uploads eight files including real DOCX/XLSX and opaque binary; checks 1440/768/767/390/320 containment and editor availability; reveals a complete filename by keyboard focus; removes a file by Enter; sends seven targets; verifies a completed Synthetic response, bound originals and restored file actions after reload. Upload failure disables Send, Retry recovers, and Remove clears the remaining attachment. Existing CSV preview/download/error recovery remains functional.
- Console and network inspection: no browser page errors or unexpected failed requests. Chromium's bodyless-204 `ERR_ABORTED` reports and deliberate navigation cancellation are excluded from failure assertions.
- `git diff --check`: passed. Design sidecar JSON parses.

Browser command used `CI=1 PLAYWRIGHT_FAITHFUL_MANUAL=1`, isolated API/web/fixture ports 5280/5273/5291, and disposable SQLite files under `/private/tmp`. The existing runner started/stopped only its own Synthetic hosts. Playwright MCP navigation could not reach localhost (`ERR_CONNECTION_REFUSED`); CLI Chromium was the fallback. The existing Real host and its data were not changed. Restart the backend to load the new intake/reader code into that running process.

[Desktop capture](assets/composer-attachments-2026-10-14/desktop.png) · [Mobile capture](assets/composer-attachments-2026-10-14/mobile.png)

Full hosted CI, live-provider interpretation and physical-device checks were not run. Quotas and bounded Office excerpts remain intentional; accepting arbitrary files does not imply every binary format can be semantically extracted.

## Scoped review before commit/push

The user selected only this chat's attachment and approval changes; separate spreadsheet-preview and Drive work stays outside the commit. An isolated candidate was reconstructed from parent `4bd1e194` plus these changes and checked without the separate working-tree features.

Review fixes validate the complete text body before extraction/reread, report invalid UTF-8/control bytes as unsupported rather than replacement characters or base64, preserve the server's `readable` flag in composer/queue projections, and avoid requiring vision for images stored without extraction. Active Instance override badges ignore the retired unread-storage flag. Approval geometry waits for native entrance motion to settle before asserting width. The composer regression has its own small deterministic OOXML fixture, independent of the separate spreadsheet-formatting fixture.

Final scoped results: Domain 13, Infrastructure 12, API 11 and Application 32 passed (68 backend tests); seven focused frontend files passed 77 tests; the frontend build passed. Six distinct browser scenarios passed across the isolated checks: the two composer journeys, two approval geometry/keyboard scenarios, CSV preview/download/error recovery, and actual Drive Escape rejection/Enter approval. DOCX/XLSX originals survive bind/reload, a restored XLSX preview shows its real Revenue cell, and an unsupported BMP upload can retry and enable Send on the text-only Synthetic model. No unexpected browser page errors were observed. Native Playwright MCP still could not reach localhost, so CLI Chromium was used.

Verification uses separate ports 5380/5373/5391, disposable data and a temporary configuration that isolates Vite cache and uses existing restored .NET dependencies. The broad existing Drive/Office test refers to an uncommitted spreadsheet helper on the parent; its approval portion was exercised independently with its normal credential/fixture/decision controls. That temporary verification fixture is not shipped.

A **fresh NuGet restore remains blocked** by vulnerability-audit errors in existing `System.Security.Cryptography.Pkcs` 10.0.0 and SignalR 10.0.12 dependencies (`NU1902`/`NU1903`). Auditing was not disabled and package changes were excluded from this scoped commit. Backend build/tests and Synthetic browser hosts passed with the checkout's already-restored dependency graph (`--no-restore`). This is a fresh-restore/CI gap, not evidence of a clean dependency audit; no hosted acceptance is claimed.
