# Unified Events and multi-trigger Automation follow-up review

Reviewed implementation baseline `dc5b4870` against the final proposal and canonical contracts. The original implementation evidence remains in [the implementation report](unified-events-multi-trigger-verification.md); this report records the subsequent corrections and local checks.

## Findings and corrections

- Canonical Automation requests containing null child records could dereference null and return 500. Validate missing, empty, oversized and null-containing collections before constructing children. Integration regressions send null, empty and null-containing collections, observe 400, and confirm that the owned Automation collection remains empty.
- Built-in subscriber inspection retained its initial embedded Instance and could retain an inventory error after successful retry. Resolve the current Instance on every render, discard old asynchronous reads, and track inventory and subscriber errors independently. Tests verify changed owner scope, late-response suppression, successful retries and exact owner links.
- The design sidecar still described a separate Core Event trigger. Synchronize its narrative and component description with the existing canonical design context, preserving tokens and previews. Document subscriber scope and read recovery in the frontend specification.

## Local verification

- `dotnet test tests/AgentCore.Api.Tests/AgentCore.Api.Tests.csproj --no-restore --nologo -m:1 -p:UseSharedCompilation=false --filter 'FullyQualifiedName~MultiTriggerAutomationJourneyTests|FullyQualifiedName~CoreEventAutomationJourneyTests|FullyQualifiedName~CoreEventStabilizationTests'`: **23 passed**, including mixed-source persistence/recovery, chat authoring, causation and four invalid-collection cases.
- `pnpm --dir web exec vitest run src/features/admin/BuiltinEventSubscribers.test.tsx src/features/admin/EventsSection.test.tsx --maxWorkers=1 --testTimeout=90000`: **9 passed**. The initial 30-second run timed out in one existing EventsSection test; the unchanged assertions passed with the established local timing allowance. An existing Ant Design `NaN` height warning remains.
- `pnpm --dir web exec vitest run src/features/admin/InstanceAutomationsSection.test.tsx --maxWorkers=1 --testTimeout=90000`: **22 passed**, including schedule authoring, source navigation, policy feedback, revisioned edits, stale mutation recovery and draft preservation.
- `pnpm --dir web build`: **passed**, including TypeScript. Vite reports its existing large-bundle warning.
- `pnpm --dir web exec playwright test --config playwright.unified-events.config.ts e2e/multi-trigger-automation.spec.ts e2e/shared-events.spec.ts e2e/core-event-presets.spec.ts`: **6 passed**. Observed mixed Event drafts and retained identities after reload, preset/disabled authorization behavior, nested webhook management, one-time credentials, dedupe and lifecycle links.
- Playwright MCP against a disposable native Synthetic host on API 5280 / Vite 5273: injected Instance-inventory 503 responses, observed Retry Instances, restored service and observed error clearance; created an owned Secretary Instance, injected subscriber-read 503, selected that Instance, observed Retry subscribers, restored service and observed the correct empty subscription state. The Built-in read-only definition remained visible. No page errors occurred in the final subscriber recovery journey. Expected 503 console entries came from injected failures. Early fixture attempts targeted the wrong request or used an unconfigured relative request URL; these setup attempts were corrected before claiming success.

No hosted-provider calls or CI waiting were required. The original full implementation and migration/Compose checks are historical evidence, not rerun claims for this follow-up. The follow-up changes do not modify persistence schema or deployment configuration.
