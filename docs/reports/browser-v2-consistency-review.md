# Browser v2 consistency review

Review date: 2026-10-08. Starting commit: `885f759e`. This follow-on reviews the completed Browser v2 and [capability audit](browser-v2-completeness-and-polish.md); it adds no tools, features, grants, provider ports or UI surfaces.

## Findings and corrections

| Finding | Operational reproduction | Correction |
| --- | --- | --- |
| Protected-field checks differed between snapshots, typed interactions and native/coordinate actions. Cached ordinary refs could survive a SPA metadata change. | Three Chromium cases changed an already discovered Title field's name to `otp`, `passcode` or `apikey`. Sequential native type accepted the protected target before the fix. | Reuse the snapshot classifier at execution time for ordinary Locators, typed actions, focused keyboard, coordinate targets/linked labels and drag destinations. Password injection stays with the separate credential sink. |
| Storage masked serialized JSON after clipping values. JSON escaping and truncation could hide a known protected match. | Local/session storage contained a quote/newline/HTML-sensitive value or a 300-character value. Native get returned the decoded secret or its 256-character prefix before the fix. | Mask decoded key/value strings before clipping and serialization. Keep the existing 20-entry, 128-character key and 256-character value output limits. Cookie metadata uses the same mask-before-clip ordering. |
| Cookie deletion used the current host as an exact native domain; listing used the root URL. Parent-domain and nested-path cookies were missed. | A locally fulfilled `app.review.test` page received `.review.test` cookies at `/` and `/account`, plus an unrelated `other.review.test` cookie. Delete reported success while retaining the parent-domain cookie before the fix. | Select current-host/parent-domain cookies across all paths, then delete/clear exact native domains and optional names. Preserve unrelated host cookies. A shared parent-domain cookie is inherently shared across sibling hosts within the owner's context. |
| Storage mutation cancellation did not track the pending native page action. | A deliberately non-cooperative native `setItem` signaled entry and remained pending. Cancellation returned with its page still open before the fix. | Use the existing tracked page-action fence for set/delete/clear; close the pending page before releasing the gate. Cookie mutations use the existing context-level fence. |

No capability authorization is widened. Definition/context projection, provider feature negotiation, origin restrictions, exact privileged approval, direct-turn credentials, profile ownership and durable non-replayable effects remain authoritative. Fixed provider scripts remain internal; model-supplied evaluation and arbitrary Playwright code remain disabled/excluded. Advanced decisions in the coverage matrix are unchanged.

## Verification

`BrowserV2BoundaryReviewTests` has seven real Chromium cases. Expected and observed after correction:

- Each of the three metadata changes rejects ordinary native/typed fill, form fill, targeted/focused keyboard, drag destinations and coordinate clicks. The original value remains unchanged; a fresh snapshot omits the protected field.
- Both local and session storage get/list return `[redacted]` for JSON-sensitive and over-limit values. Cookie name/path reflections are also masked before clipping.
- Parent-domain deletion succeeds; all-path listing/clear includes the nested cookie and retains the unrelated host's cookie. The test domain is fulfilled locally without DNS or external traffic.
- Canceling the pending storage mutation closes its original page; subsequent navigation and snapshot succeed.

| Check | Result |
| --- | --- |
| New Chromium boundary suite | 7 passed, zero skips. |
| Intermediate full Infrastructure browser filter | 96 passed, 2 explicit live-demo skips; includes credentials, profile persistence, SPA forms, frames, tabs, dialogs, downloads and cancellation. Final solution gate below also covers the additional storage cancellation fix. |
| Key-free solution gate | `dotnet test AgentCore.sln --no-restore --nologo -v minimal`: 2,683 passed (Domain 152, Application 1,318, Infrastructure 838, API 371, OrderEvents 4), 20 skips (14 explicit live opt-ins and six unrelated missing-image Docker prerequisites). No browser cases skipped. Final browser rerun below covers the subsequent rejection-guidance adjustment. |
| Final browser filter | `dotnet test tests/AgentCore.Infrastructure.Tests/AgentCore.Infrastructure.Tests.csproj --no-restore --filter FullyQualifiedName~Browser -v minimal`: 97 passed, two explicit live-demo skips. Includes all seven new cases and the final rejection-guidance adjustment; protected targets no longer advertise cached ordinary actions in denial guidance. |
| Documentation synchronization | Interface behavior, backend implementation and testing strategy updated; all relative file links and Markdown fences in five changed documents plus `git diff --check` passed. No configuration/secret path changes. |
| Hosted Synthetic CI | Pending publication of the reviewed behavior commit; all five existing jobs required for a passing hosted gate. |

No frontend behavior or layout changed during this review. The previous completeness audit owns its UI polish evidence. Original development hosts, databases and browser profiles were untouched. Live-provider tests remain explicit opt-ins; historical full-cutover Journey L is still unrun and this review does not claim migration freeze or open P10/P11.
