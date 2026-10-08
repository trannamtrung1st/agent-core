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

The final privacy check also normalizes separator/camel-case credential names in URL projection and the existing protected-field classifier, preserving its original case-insensitive checks. URL coverage includes `api_key`, `privateKey`, bare `key` and signed-URL signatures; protected-field regressions add `api_key`, `privateKey`, `one_time_code` and mixed-case `oTp`. This preserves one snapshot/execution taxonomy and prevents spelling variations from bypassing it.

One intermediate solution run, while another native suite was running, also exposed a late popup-result race in the existing generic SPA journey: the forbidden popup stayed blocked, but interaction returned success because it checked denial before snapshot settlement. Recheck the denial after final capture/popup settlement; the existing assertion remains unchanged. An isolated final backend run verifies this correction alongside the normalized privacy checks.

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
| Final key-free solution | `dotnet test AgentCore.sln --no-restore --nologo -v minimal`: 2,697 passed (Domain 152, Application 1,322, Infrastructure 848, API 371, OrderEvents 4); 20 skips (14 explicit live opt-ins and six unrelated absent-image Docker prerequisites). Includes credential spelling/case variants and the late popup-result guard. The subsequent permission-effect description edit is covered by the focused policy/catalog rerun. |
| Final all-browser filter | `dotnet test tests/AgentCore.Infrastructure.Tests/AgentCore.Infrastructure.Tests.csproj --no-restore --filter FullyQualifiedName~Browser -v minimal`: 107 passed, two explicit live-demo skips, including existing credentials/profiles/SPA/tabs/dialogs/downloads/cancellation and all final privacy/name-variant tests. Final solution above also covers the later popup-result guard. |
| Hosted exact-SHA Synthetic | All five jobs passed on `3b87934abf657aa7e411f06464a050fcc4029e5f` in [run 37734899571](https://github.com/trannamtrung1st/agent-core/actions/runs/37734899571), before the final credential-name normalization. This is historical checkpoint evidence; the final exact-SHA result is recorded below. |
| Documentation | Interface/backend behavior, capability matrix and testing strategy synchronized; relative links/fences in six changed documents and `git diff --check` passed. No local configuration/secret paths changed. |

Historical full-cutover real-model Journey L remains an explicit open gate. No live-provider inference is authorized or executed by this review, and no full migration freeze is claimed. Previous enhancement/review evidence remains historical at its named SHA.

## Exact-candidate CI follow-up

The backend job on `59406e9406feebebf434f8c507924926b87bf6ba` in [run 37738079867](https://github.com/trannamtrung1st/agent-core/actions/runs/37738079867) failed the generic native-form cancellation recovery assertion. Its fixed 200 ms cancellation timer could expire during validation before a pending provider action existed; that boundary correctly invalidates refs without resetting the page. The test nevertheless expected a fresh page. Replace the timer with an internal action-entry acknowledgement, then cancel the genuinely pending disabled-button click. Keep the stale-ref and fresh-page assertions unchanged. This hook is test-visible only and adds no public debugger or execution capability. All four native Browser v2 journey tests passed locally after this correction; a replacement exact-SHA hosted run was required at that point.

On `276227d7`, all 107 browser-filter tests passed (two live-demo skips). A subsequent parallel solution run timed out on scoped snapshot capture (2,702 passes, one failure, 14 live skips; the six Docker cases now ran). Hosted [run 37739664370](https://github.com/trannamtrung1st/agent-core/actions/runs/37739664370) passed the corrected cancellation case but failed an existing bounded automatic-settle assertion. Both timeout cases passed together in isolation (two passed). The newer native browser classes had not joined the existing `browser-chromium` xUnit collection, so their Chromium instances competed with the older serial browser fixtures. Join all native browser classes to that existing collection; preserve product deadlines, settle bounds and every assertion. This is test scheduling, not an expanded browser capability or a retry policy. The complete Infrastructure suite then passed: 854 tests, nine explicit live skips, including all six Docker cases, every browser case and both timeout reproductions. Other backend suites on the same behavior passed: Domain 152, Application 1,322 (two skips), API 371 (three skips), OrderEvents four. Combined local evidence is 2,703 passes and 14 live opt-in skips; exact-candidate hosted verification was still required at that point.

## Final hosted acceptance

All five jobs passed on exact source/test SHA `c9375843845ec08cc3bb596d02d7ba0fca7b6f9f` in [Synthetic run 37740946992](https://github.com/trannamtrung1st/agent-core/actions/runs/37740946992):

| Gate | Observed result |
| --- | --- |
| Backend | Domain 152, Infrastructure 848, Application 1,322 and API 371 passed: 2,693 total, 20 documented skips (14 live opt-ins, six absent-image Docker prerequisites). All native browser tests executed. |
| Frontend | 96 files / 739 tests passed; production build passed. |
| Core Playwright | 116 passed. |
| Playwright acceptance | 16 passed across seven journey steps. |
| Synthetic Compose | Owner-capability, SQLite volume and durable resource survival passed. |

The bounded five-finding follow-up and its CI corrections are verified. The subsequent report-only commit changes no source or tests; the verified behavior remains the SHA above. Journey L is explicitly unverified, the PR remains draft, and the broader migration freeze remains open.

## Remaining fixture collection follow-up

The next review identified `LoopbackBrowserFixtureHostTests` in `BrowserAdapterTests.cs` as one remaining native Chromium class outside `browser-chromium`. Commit `232cad67` adds the existing collection annotation to this class. No production source, assertions, deadlines, grants or architecture change. Its focused six-test run passed with no skips, exercising assigned loopback ports, same-origin navigation, accepted/rejected/oversized downloads, denied navigation without launch and safe fixture bind/missing-page failures. The full browser-filter rerun passed 107 tests with two explicit live-demo skips.

Current `main` advanced through the separate execution/background-session cutover and now conflicts with this Browser branch, so GitHub does not schedule its pull-request workflow. Commit `f8e4179b` enables manual dispatch on the existing Synthetic workflow to verify the branch through the same five key-free jobs. A subsequent uncommitted merge attempt was aborted at the owner's request while main stabilizes; no integration changes were committed or pushed. Branch-source verification does not resolve the PR's merge prerequisite.

All five jobs passed on exact SHA `f8e4179bce301f75704a45e22b249947b4a62666` in [manual Synthetic run 37746737917](https://github.com/trannamtrung1st/agent-core/actions/runs/37746737917): backend 2,693 passed / 20 documented skips (14 live opt-ins, six absent-image Docker prerequisites), frontend 96 files / 739 tests and production build passed, core Playwright 116 passed, acceptance 16 passed, and Compose survival passed. This closes the fixture-collection key-free gate. Integration with main is deferred; Journey L remains a separate real-model acceptance gate.
