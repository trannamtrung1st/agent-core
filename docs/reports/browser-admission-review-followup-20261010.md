# Browser admission and Automation assertion review follow-up — 2026-10-10

Starting revision: `b95d252f53b5bd6552bd6b6dcefbec71800b3b24`, branch `main`.

## Findings and decisions

**A — Settlement observation availability:** reproduced in actual Chromium before the fix. A one-time getter failure during settle polling was caught by the provider, later reads recovered to a quiet interval, and the capture returned `coordinateEvidence=true` despite `settlement.observationAvailable=false`. The new native regression failed at that authority assertion; the two polling recovery cases passed, confirming the distinction between eventual quietness and complete observation.

Screenshot admission now requires both settlement quietness and availability of every polling read in that capture window, plus existing before/after freshness checks. A recovered quiet interval remains `captureDiagnostics.settlement.settled=true`, but the matching screenshot observation and coordinate authority remain false when the earlier gap is recorded. The reason is `state_observation_unavailable`, not an invented settlement deadline. Image context is retained; a new complete capture can restore coordinates. Ordinary bounded settle reporting still describes eventual quietness and does not erase the diagnostic gap.

**C — Automation child array ordering:** SQLite reconstructs children ordered by ID, while authoring responses can preserve a different order. The API defines stable child identity rather than positional persistence. The mixed enabled/disabled sibling Playwright scenario now compares projected child records sorted by `triggerId`. This preserves checks for IDs, enabled flags, source references and multiplicity. It forwards a real PUT to the local API, deliberately reverses the response's child array relative to the original saved order, and asserts the reverse occurred before comparing the retained children by ID. Sorting changes neither backend behavior nor saved values.

**B — Whole-page dynamic interaction limitation:** retained deliberately, as recommended. No localized-coordinate stability policy or new browser tool was added. Existing semantic fallback and independently requested closure remain supported. Historical AHI cause/logout acceptance is still unverified; no live AHI or paid model test was attempted.

## Exercised outcomes

- Native Chromium: capture a stable control, inject exactly one failed polling read, recover later quiet reads, retain bytes and observation-gap diagnostics, reject old and gap-capture mouse calls before effects, then take a new complete capture and click the intended control. Expected and observed final text: `Done`.
- Polling: both a Playwright error and an empty/malformed result followed by valid reads preserve `ObservationAvailable=false` while eventual quietness can be true. Deadline and cancellation boundaries remain covered.
- Synthetic Automation UI/SQLite: save a valid Schedule during unrelated Webhook catalog failure; save healthy enabled Built-in plus disabled unavailable Webhook with a reordered child response; retain disabled Event saving after catalog/collection read failure. All three visible-control scenarios passed.
- Owned runtime/checkpoint: the existing dynamic visual-recovery, unusable screenshot guard, requested closure and capture rehydration tests passed with the rebuilt provider. These are offline mechanics checks, not autonomous enterprise-model acceptance.

## Local verification

| Command | Result |
| --- | --- |
| `dotnet test tests/AgentCore.Infrastructure.Tests/AgentCore.Infrastructure.Tests.csproj --filter 'FullyQualifiedName~Recovered_settle_read\|FullyQualifiedName~Quiet_reads_after_a_failed_poll' --nologo --no-restore` | Before fix: native admission case failed; 2 polling cases passed. After fix: all 3 passed (5 seconds). |
| `dotnet test tests/AgentCore.Application.Tests/AgentCore.Application.Tests.csproj --filter 'FullyQualifiedName~BrowserDynamicRecoveryRuntimeTests\|FullyQualifiedName~BrowserEvidenceProgressTests\|FullyQualifiedName~SessionCaptureRecoveryTests' --nologo --no-restore` | 22 passed, 0 failed (4 seconds). |
| `pnpm exec playwright test e2e/automation-editor-recovery.spec.ts --project=synthetic --reporter=list` | 3 passed (23.8 seconds), including deliberately reversed child ordering. |
| `dotnet test tests/AgentCore.Infrastructure.Tests/AgentCore.Infrastructure.Tests.csproj --filter 'FullyQualifiedName~BrowserCaptureDiagnosticsTests\|FullyQualifiedName~BrowserPageSettleDeadlineTests\|FullyQualifiedName~AdaptiveBrowserEvidenceTests\|FullyQualifiedName~BrowserSettleTests\|FullyQualifiedName~BrowserPrivacyTests' --nologo --no-build --no-restore` | 54 passed, 0 failed (2 minutes 18 seconds), using assemblies rebuilt by the focused check. |
| `git diff --check`; changed-document relative-link and code-fence validation | Passed. |

Playwright used `CI=1`, `PLAYWRIGHT_FAITHFUL_MANUAL=1` to omit unrelated speech hosts, API/Vite/fixture ports `5228/5328/5428`, and a freshly created `/tmp/agent-core-review-admission-e2e.*` SQLite/root directory. Existing Real/Vite hosts and user data were left running and unchanged. CLI Playwright exercised the real UI; no separate MCP run or frontend product-code change is claimed.

The affected suites passed 79 tests in total; the focused three-case check is a subset of the 54 native tests and is not counted twice. The runner stopped its disposable servers; a listener check confirmed the test ports were released, and the temporary data directory was removed. Hosted CI was not awaited or claimed green. The new local fixes do not close AHI or enterprise browser acceptance; the earlier instruction stopping live attempts remains in effect.
