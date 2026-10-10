# Dynamic-page browser evidence follow-up — 2026-10-10

Starting revision: `ad156a06cc59d612e25e0b13c32ad139fdf0d30b`, branch `main`.

## Delivered behavior

Successful screenshots now explain unavailable coordinate evidence with bounded codes and content-free counters/flags. Settlement reports DOM-change polling samples, peak/last observed fetch/XHR activity and observation availability. Before/after capture checks identify font loading, running animations, observer availability and page/generation, viewport/scroll or visual fingerprint changes. No page text, URLs, font/resource names or private fingerprints are added to these diagnostics. The existing settlement requirement, native stale-coordinate checks, privacy masking and limits remain intact.

The Application tool receipt transports these diagnostics with the screenshot identity and actual vision image. Output fitting can drop detailed counters before identity and reason codes when space is limited. `BrowserEvidenceProgress` rebuilds explicitly unusable snapshot IDs from durable tool receipts and suppresses mouse against them before native execution (`coordinate_evidence_unavailable`, `strategySuppressed=true`, `effectAttempted=false`). Unknown IDs still reach native freshness validation. Guidance directs the model toward authorized semantic evidence/actions or justified bounded waiting, advises ending equivalent capture strategies after repeated unavailable evidence, and retains independently requested browser closure after unsuccessful logout.

`GET /health` adds optional `buildVersion`, captured from the loaded API assembly informational version at startup. SDK builds normally append the source revision; missing metadata requires independent artifact provenance. It does not establish that the source tree was clean. Reading the running process's metadata avoids inferring its loaded revision from a checkout or replaced DLL on disk.

## Executed scenarios

`BrowserCaptureDiagnosticsTests` uses disposable actual Chromium:

- A page with a static semantic button and continuous DOM updates returns an image without coordinate authority and identifies DOM-change samples/settlement deadline. A pending fetch identifies in-flight activity. A running CSS animation identifies animation state.
- Mouse from each unusable image is rejected before execution. The semantic button still changes to `Done`, and requested closure closes the page without claiming application/logout verification.
- Deterministic internal post-raster barriers inject DOM changes, viewport resizing, navigation, font loading and lost observer state. Each returns the matching limitation, invalidates previous coordinate authority, and permits recovery after a fresh stable capture. These barriers are internal test hooks, not public browser tools/endpoints.

`BrowserDynamicRecoveryRuntimeTests` executes an owned Synthetic Session/AgentRun against the real continuously updating native fixture. A scripted vision model receives the screenshot/image and DOM limitation, attempts mouse, receives Application suppression without an effect receipt, and closes. Durable assistant output is `Logout unverified. Browser closure confirmed.` Closure has its independent effect receipt. This proves runtime/transport mechanics, not autonomous real-model behavior.

`BrowserEvidenceProgressTests` rebuilds the guard from checkpoint messages, preserves independent close, escalates guidance for repeated unavailable captures, permits a new eligible image and keeps old unusable IDs fenced. Compact output preserves identity/false-coordinate flag/reason codes when they fit. Existing privacy, freshness, deadline/cancellation, image rehydration, cleanup and API lifecycle regressions are included in the affected suites.

`HealthAndSessionLifecycleTests` checks the endpoint against the loaded API assembly version and asserts no outbound HTTP.

## Local checks

Paid inference and live AHI retries are excluded.

| Command | Result |
| --- | --- |
| `dotnet test tests/AgentCore.Infrastructure.Tests/AgentCore.Infrastructure.Tests.csproj --filter 'FullyQualifiedName~BrowserCaptureDiagnosticsTests' --nologo --no-build --no-restore` | 8 passed, 0 failed (31 seconds). |
| `dotnet test tests/AgentCore.Application.Tests/AgentCore.Application.Tests.csproj --filter 'FullyQualifiedName~Browser\|FullyQualifiedName~SessionCaptureRecoveryTests\|FullyQualifiedName~TerminalDisplayRepairTests' --nologo --no-restore` | 348 passed, 4 live-provider tests skipped, 0 failed (46 seconds). |
| `dotnet test tests/AgentCore.Api.Tests/AgentCore.Api.Tests.csproj --filter 'FullyQualifiedName~HealthAndSessionLifecycleTests\|FullyQualifiedName~BrowserPrivacyApiTests' --nologo --no-restore` | 14 passed, 0 failed (4 seconds). |
| `dotnet test tests/AgentCore.Infrastructure.Tests/AgentCore.Infrastructure.Tests.csproj --filter 'FullyQualifiedName~Browser' --nologo --no-restore` | 207 passed, 0 failed (6 minutes 36 seconds). |
| `git diff --check`; changed-document relative-link and code-fence validation | Passed. |

The three affected suites passed 569 tests in total, with four explicit live-provider skips. The targeted eight-case native run is a subset and is not added to that total. Build-backed tests use .NET 10 with warnings treated as errors. No frontend behavior or deployment configuration was changed.

## Remaining acceptance limits

The historical AHI UAT screenshots do not contain these reason fields. They establish failed settlement/coordinate authority and pre-effect mouse rejection, but cannot retrospectively distinguish DOM mutation, network, fonts/animation or capture changes. No actual AHI cause or successful UAT logout is claimed.

The existing Real host from the sibling checkout was not restarted or changed. Its historical loaded revision remains unestablished. Before any future explicitly authorized acceptance, deploy a reviewed build, record its `/health.buildVersion` and build provenance, then collect the new screenshot diagnostics. The user's stop instruction remains in force: no live UAT or paid vision-model attempt was made here.

Hosted CI was not awaited, as requested. Local verification does not establish hosted exact-HEAD acceptance or close enterprise browser acceptance.
