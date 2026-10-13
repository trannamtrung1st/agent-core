# Preview/Drive consistency review — 2026-10-14

Frontend fixes are committed on `0763058db8113f57b85f096a59616eea2738396d`. Reviewed the frontend polish and design-document synchronization after `b7911b79`, including shared preview ownership, responsive source identity, source cancellation, Drive search, destination state, receipt recovery and static design-context parity. The later hosted-history check also corrected the Automation reliability fixture described below. This is a bounded successor follow-up; historical hosted acceptance and P10/P11 status remain unchanged.

## Findings corrected

1. **Drive search clear retained the old filter.** Input.Search emits the new value during its clear event, while React may still hold the old draft. The handler ignored that emitted value. Browser reproduction showed an empty textbox, a request containing `query=q3-sales` and one remaining row. It now uses the emitted value; Clear Drive search has an explicit accessible name. The regression failed on the previous code and passes after the fix.
2. **The folder picker bypassed the save form lock.** Its explicit `disabled=false` for an available account overrode the surrounding Form's disabled context. Upload, pending recovery and verified completion could leave this one destination control editable. It now consumes the same shared `formLocked` condition as the Form, plus account availability. A regression covers all three states and ensures only one upload.
3. **Loading changed recovery action names.** The native loading icon could remain in the accessible name as `loading Check operation`, including its invisible exit transition. Save new file, Check operation and More available folders now have explicit stable names. Loading feedback remains visible.
4. **Design sidecar freshness.** The guide was last touched after the preceding sidecar generation, producing a hook freshness warning despite narrative parity. The current behavioral notes and sidecar timestamp were synchronized together. Existing palette, type, spacing and radius primitives remain unchanged.

5. **Automation reliability fixture raced Runtime owners.** The prior hosted run `38478119257` failed its task/history isolation assertion, and that failure reproduced locally. The fixture constructed a second standalone Runtime for chat edits while the SessionHost already owned occurrence execution. It now uses the same managed SessionHost through authenticated attach/send/detach and its manual dispatcher, then waits for accepted work to settle before advancing logical time. An internal observer delegates to the existing Runtime settlement barrier; it adds no public endpoint or execution policy. Historical evidence is asserted as decoded JSON with the complete expected text. The three unique completed occurrences, two Hello replies, one Welcome after the owner edit, historical-data isolation and no further occurrence after disable remain required.

Dependencies, storage, authority, public API/protocol and presentation remain on their existing owners. Server receipt inspection was traced to the existing Succeeded-or-error contract; authenticated source/version/binding/epoch guards remain in place.

## Runtime verification

Playwright MCP used the known disposable local Synthetic SQLite/fixture host.

- **Search:** five available files → search `q3-sales` → one row → clear → five rows. The restored request omitted `query`, the field was empty, and no page exception occurred.
- **Response-loss recovery:** the test held an actual Synthetic upload request, observed disabled account/folder controls, then let the backend create `review-recovery-copy.docx`. It substituted an uncertain response using the real durable operation/file IDs. The UI offered only Check operation; the actual read-only receipt lookup returned Succeeded and displayed Verified in Synthetic Drive. Folder remained locked through upload/pending/completion, exactly one upload occurred, and closing restored source focus. No page exception occurred. No Google request or credential was used.
- Existing shared-preview checks retain CSV source/exact downloads, safe format rendering/native media, worksheet range/formula inspection, resizing and isolated Word-frame keyboard recovery. Their focused browser regressions were rerun below.

## Checks

- Before the fix: the two new regressions failed for retained search `q3` and an enabled folder input; the two existing destination tests passed.
- Final focused Vitest: **17 passed across six files in 15.32s**.
- Frontend build: passed; existing SignalR annotation/large-chunk warnings remain.
- Final four Synthetic browser scenarios: **4 passed in 57.0s**, using a fresh isolated database. Includes the new clear-search and verified-completion folder-lock assertions.
- Design YAML/JSON, unchanged token/component primitives, narrative/rule parity, sidecar freshness, source targets, **72 relative links/fragments** and `git diff --check`: passed. The canonical docs checker also passed 21 files with zero issues.

The prior full frontend run remains **1,004 passes on `8e053018`**; it is not relabeled as a fresh full-suite result. The new changes add two focused regressions. Full backend and Compose were not rerun locally; targeted backend checks are recorded below. Fresh hosted workflow status after the authorized push is separate from local evidence. Real Google, native browser zoom, physical touch/screen-reader and installed Office-client fidelity retain the limitations in the [successor ledger](office-file-preview-drive-verification-2026-10-13.md).

## Prior hosted-failure follow-up

- The earlier 390px Email editor failed waiting for Last checked. A fresh local Synthetic rerun passed in **36.1s**; that does not establish the failure's root cause or guarantee hosted stability. No assertion timeout was increased and no outcome was weakened.
- The Automation test failed on its historical-data predicate locally. Parsing JSON alone passed in some diagnostic runs but did not establish causality, so the duplicate-owner fixture was corrected instead. An intermediate managed fixture exposed the need to detach for its disabled/manual dispatcher and to await accepted-work settlement rather than mailbox drain. Temporary evidence-file instrumentation was removed.
- Final managed-owner Automation case: **1 passed in 6s**.
- Unchanged prompt-context boundaries: **21 passed**.
- Complete reliability/SessionHost race filter: **43 passed in 1m 15s**.
- The first authorized push reached `7106e330`; its hosted workflow is superseded by the subsequent fixture correction push. Hosted acceptance is not claimed while the final run is pending.
