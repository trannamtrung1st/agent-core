# Browser implementation review follow-up

Date: 2026-10-10. Reviewed baseline: `9da5a43a` on `main`. Scope: the pasted Browser implementation review; user authorized implementation, commit and push without waiting for CI. Historical browser freezes and P10/P11 are unchanged.

## Changes

- Keep conservative Canvas/SVG masking by default. `Browser:TrustedVisualCaptureOrigins` is an empty-by-default host setting accepting exact HTTP(S) origins only. Matching graphics can remain visible, while known sensitive text, sensitive DOM regions and whole child frames remain masked. This explicitly accepts opaque-graphics confidentiality risk on those origins; it grants no navigation/interaction authority and cannot override `ScreenshotPrivacy=Disabled`.
- Optional semantic collection has at most one second or one quarter of the existing operation budget. Failure returns masked image context with `observationUnavailable=true`, empty semantic fields and fresh screenshot/tab identity. Independent privacy inspection/masking remains mandatory; privacy failure returns no bytes. Cancellation and the existing overall deadline remain authoritative.
- Coordinate evidence requires both successful automatic settlement and unchanged independently observed visual state. Semantic unavailability alone does not invalidate otherwise valid evidence. Failed settlement, full-page/target captures and stale/consumed evidence never authorize coordinates.
- Host-disabled screenshots suppress proactive capture and coordinate guidance. Existing configuration reports effective availability and trusted-origin count.
- Native capture logs safe semantic, masking and encoding timings. Dense-page trials separately time actual SQLite/blob storage. Measurements did not justify changing whole-DOM traversal.
- The live acceptance fixture now honors the model catalog's production `PreferResponseFunction` setting and records latency/tool counts. Missing browser context produces an assertion rather than an uninformative null dereference.

## Offline execution

Commands from the repository root:

```sh
dotnet test tests/AgentCore.Infrastructure.Tests/AgentCore.Infrastructure.Tests.csproj --filter 'FullyQualifiedName~AdaptiveBrowserEvidenceTests|FullyQualifiedName~BrowserHardeningTests' --nologo -m:1 --logger 'console;verbosity=detailed'
dotnet test tests/AgentCore.Application.Tests/AgentCore.Application.Tests.csproj --filter 'FullyQualifiedName~AdaptiveBrowserRuntimeTests|FullyQualifiedName~BrowserScreenshotRecoveryTests|FullyQualifiedName~BrowserHardeningContractTests' --nologo -m:1
```

Infrastructure: **44 passed**. Application: **33 passed**. A final `--no-build` offline repeat added the live-test filter without opt-in: 33 passed and one live theory skipped, confirming default execution makes no paid calls. Real Chromium exercised the native provider; owned Synthetic SessionRuntime journeys exercised model/tool/artifact/provider boundaries without paid inference. The initial sandbox could not create .NET named-pipe/test sockets; execution outside that sandbox succeeded.

| Scenario and actions | Expected | Observed |
| --- | --- | --- |
| Force semantic timeout, capture, inspect protected pixels, then click using screenshot identity | Image and explicit unavailable receipt; masking and independent coordinate authority survive | Passed; masked pixels decoded as RGB 17/17/17, authorized click selected PUMP-1042 |
| Force independent privacy inspection failure after semantic failure | No image bytes or retained coordinate authority | Passed; structured failure with no bytes |
| Capture graphics on an untrusted/mismatched origin, then on an exact host-trusted origin | Default graphics hidden; trusted red Canvas/blue SVG visible; sensitive Canvas still masked | Passed; decoded pixel assertions and mask-removal checks |
| Capture with a 100 ms automatic-settle budget, below the required quiet interval | Context image only even when visual state is unchanged | Passed; unsettled receipt, no coordinate evidence, mouse refused |
| Navigate, resize, switch tabs, mutate layout, scroll or interact after capture | Old evidence rejected; new capture recovers | Passed across all six boundaries; independent status verification and consumed-evidence rejection |
| Execute accessible form, dashboard and weak-control Synthetic journeys | Semantic preference, real image delivery only for vision, successful independent verification | Passed; accessible form needed no capture; scripted dashboard/weak trials used one useful capture |
| Force semantic failure through screenshot tool dispatch with vision and text-only admissions | Correct receipt, artifact ownership, image delivery, redaction and quotas | Passed; text-only received no image parts and no coordinate authority |
| Project guidance with host capture disabled | No proactive screenshot/coordinate recommendation | Passed |
| Existing screenshot formats/errors, context initialization, download/network, lease and budget hardening | No regression | Passed |

## Dense-page benchmark

10,000 ordinary elements plus a protected visible region, three trials each. The continuously updating variant mutates every 20 ms; it remained capturable and withheld coordinate authority. Every image passed decoded privacy-pixel assertions and cleanup checks. Actual SQLite/blob stores used disposable databases and directories, removed after execution.

Final combined regression run:

| Variant | Total capture ms | Semantic ms | Masking ms | Native capture/encoding ms | SQLite/blob storage ms |
| --- | --- | --- | --- | --- | --- |
| Static, 3 trials | 923.4–960.2 | 72.7–83.7 | 15.8–18.8 | 35.7–61.1 | 2.2–2.5 |
| Updating, 3 trials | 1739.0–1769.5 | 72.1–90.4 | 16.9–17.5 | 39.5–46.8 | 3.2–169.7 |

Images were 69,111 bytes. Total capture also includes settlement, mask cleanup, decode and freshness checks. First storage measurements include cold costs; an earlier isolated run reached 981.7 ms on its first write and 70.9 ms on its first masking pass. These are small local samples with concurrent development activity, not enterprise performance guarantees. The measured traversal cost does not justify a speculative rewrite or another global timeout.

## Live model acceptance

Explicitly enabled `AGENTCORE_ADAPTIVE_BROWSER_LIVE=1` for `AdaptiveBrowserLiveTests`, loading only the configured model/key from the gitignored `.env` into the child process environment. Credentials were never printed or committed. Model: `deepseek/deepseek-v4.1-flash` through OpenRouter. Command: the Application test project filtered to `FullyQualifiedName~AdaptiveBrowserLiveTests`, with detailed console output.

The initial two cases failed before browser creation. A diagnostic repeat reached the project in the weak case but stopped before the asset; the dashboard case ended without a navigation. Inspection found the fixture omitted the catalog's production response-function preference. After correcting that fixture mismatch, **both cases passed**:

| Mode | Model journey ms | Requests | Captures | Observed outcome |
| --- | ---: | ---: | ---: | --- |
| Dashboard | 21,556.3 | 6 | 1 | Opened Cooling project, scoped Inspect to PUMP-1042, used semantic clicks, independently verified selection and 4.2 bar |
| Weak control | 36,003.5 | 10 | 3 | Interpreted blue arrow, used fresh screenshot identity for coordinate click, independently verified PUMP-1042 and 4.2 bar |

The weak trial included a screenshot after verification and two ineffective discovery calls related to a perceived dialog warning. Both final answers also discussed a dialog restriction absent from the fixture's UI. Passing outcome assertions establish actual vision/tool/verification success on these fixtures, not perfect observation economy or perfectly grounded narration. The prompt explicitly requests a screenshot, so these trials do not establish autonomous proactive screenshot selection. Provider billing totals were not captured; no cost claim is made.

## Remaining verification limits

A real enterprise-dashboard journey remains **unrun**: no target URL/task was supplied. The user was asked for that target while implementation continued. Broader statistical model-effectiveness comparisons and production-scale performance remain open. This targeted change does not claim an entire browser milestone or enterprise visual acceptance. No frontend/product UI changed, so separate frontend/Compose suites were not required for this slice. Hosted CI is deliberately not awaited under the user's instruction.

Raw local logs: `/tmp/browser-followup-infrastructure.log`, `/tmp/browser-followup-application.log`, `/tmp/browser-followup-scenarios.log`, `/tmp/browser-followup-live.log`. These temporary logs are supporting local evidence, not repository artifacts. Concurrent event-filter/storage/workflow work was separately committed as `766b2f0e` while verification ran; it is excluded from this browser commit.
