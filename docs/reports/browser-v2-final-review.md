# Browser v2 final bounded review

Date: 2026-10-08. Starting commit: `68940e7b` on `develop/branch-1`. This pass validates the five pasted review findings and retains the completed provider-neutral architecture. It does not repeat the migration or add grants, a permission subsystem, CDP access or a new runtime.

## Findings and decisions

| Finding | Reproduction / evidence | Correction and boundary |
| --- | --- | --- |
| Dialog message privacy | Three real Chromium alert/confirm/prompt cases returned cookie, local-storage, session-storage and password values before correction. | Fully mask the message; expose kind and `messageRedacted=true`. A native modal blocks the JS reads required to gather live storage/password secrets. Reading from a stale pre-action cache would miss values added immediately before the modal. Kind inspection, accept/dismiss and explicit prompt replies remain operational; unrestricted modal message inspection is deliberately unavailable. |
| Tab URL privacy | Real Chromium tab listing exposed a stored token and an unknown `access_token` query value. | One snapshot/tab URL projection masks recognized secrets and their URL encoding, sensitive query/fragment parameters and user information. Collect across permitted open tabs, including tab-specific session storage. Disallowed tabs expose origin-only metadata; diagnostics already use origin-only URLs. Execution retains actual internal URLs for policy/navigation. |
| Geolocation projection parity | Policy source admitted/offered geolocation to an eligible occurrence, while the executor required an attached UserTurn. | Add Geolocation to the existing direct-turn feature policy in both projection and execution. Four cases cover attached UserTurn, attached occurrence, detached schedule and detached event. Attached use still requires exact approval; unattended use is neither offered nor admitted. |
| Unrelated permissions | The old method cleared all overrides before every geolocation set. | Initial/same-origin updates preserve existing overrides; Chromium notification permissions are verified before/after. Clear and switching origin must still reset all overrides: Playwright offers no selective revoke or override enumeration. Report `permissionsReset=true`, disclose the limitation in configuration/contract/matrix, and verify notification/geolocation return to prompt after clear. |
| Pending tab/resize cancellation | A stalled native tab navigation left the new tab open after cancellation. Resize called the SDK without tracked-action fencing. | Fence new-page creation at context scope; close canceled/failed navigation's pending tab before releasing the gate, preserving the original. Resize uses the existing tracked page action and recovery fence. Its deterministic internal fault-injection test delays the provider call, then proves the old page is closed and a late native resize fails without affecting the replacement. Normal native resize remains covered by environment journeys. |

The permission limitation is confirmed in the installed-version [Playwright 1.63 context implementation](https://github.com/microsoft/playwright/blob/v1.63.0/packages/playwright-core/src/server/browserContext.ts#L373-L387) and [official .NET API](https://playwright.dev/dotnet/docs/api/class-browsercontext#browser-context-clear-permissions). Grant is additive at each origin; clear removes all overrides. No unsupported selective-revocation guarantee is claimed.

## Operational verification

`BrowserV2FinalReviewTests` launches isolated Chromium with disposable loopback fixtures. After correction:

- Each modal contains real cookie/local/session-storage values and a password injected through the existing secure sink. Inspection contains none of them, dismissal unblocks the native call, and normal snapshot recovery succeeds.
- Tab listing and snapshot omit recognized storage values and unknown sensitive query values while preserving `view=summary`. A second permitted tab verifies its own encoded session-storage value is also masked.
- A deliberately stalled HTTP navigation is canceled after its native route is entered. Only the pending new tab closes; the original remains active and snapshot succeeds.
- A fault-injected delayed resize is canceled after entry. The original page closes; after release, its native resize faults rather than changing recovery state. Snapshot succeeds.
- The existing environment test observes real notification permission preservation on initial/same-origin set and the documented global reset on clear. Geolocation remains exact-origin, approval-gated and coordinate-free in output.

| Check | Result |
| --- | --- |
| Initial focused privacy/tab reproductions | Five failures before correction: three modal kinds, URL privacy, stalled tab navigation. |
| Focused native final-review + geolocation | Seven passed, no skips, including actual secure-sink fill and the second tab's encoded secret. |
| Application policy + Browser v2 contracts | Eight passed; no skips. |
| Key-free full solution | `dotnet test AgentCore.sln --no-restore --nologo -v minimal`: 2,693 passed (Domain 152, Application 1,322, Infrastructure 844, API 371, OrderEvents 4); 20 skips (14 explicit live opt-ins and six unrelated absent-image Docker prerequisites). Final browser rerun also covers the subsequent URL masking-order adjustment. |
| Final all-browser filter | `dotnet test tests/AgentCore.Infrastructure.Tests/AgentCore.Infrastructure.Tests.csproj --no-restore --filter FullyQualifiedName~Browser -v minimal`: 103 passed, two explicit live-demo skips, including existing credentials/profiles/SPA/tabs/dialogs/downloads/cancellation and all final privacy tests. |
| Hosted exact-SHA Synthetic | New candidate run required after publication; five existing jobs remain the hosted gate. |
| Documentation | Interface/backend behavior, capability matrix and testing strategy synchronized; relative links/fences in six changed documents and `git diff --check` passed. No local configuration/secret paths changed. |

Historical full-cutover real-model Journey L remains an explicit open gate. No live-provider inference is authorized or executed by this review, and no full migration freeze is claimed. Previous enhancement/review evidence remains historical at its named SHA.
