# Tenant Browser Artifact drop qualification — 2026-10-20

This slice converts the remaining Artifact-backed `browser.drop` path through the existing tenant Browser source/consent/dispatch/result owners. It also exercises the incumbent upload path through the actual model-worker/HTTP flow. It does not claim full IAM proposal or milestone acceptance.

## Implemented contract

One exact same-Session Artifact ID identifies a file drop. It is exclusive with literal text and caller MIME overrides. The saved action includes immutable name/type/size/hash and exact destination frame origin; no file bytes, physical path or caller-supplied authority is retained. The model's nested native schema exposes this closed choice under the existing `run_browser_command` branch. Text drops retain their separate literal behavior.

`TenantBrowserUploadRules` and `TenantBrowserUploadTarget` now serve both file sinks. Each actual source requires current raw Artifact read plus Session/Browser reads and nondelegatable Browser use. Reviewer/source-policy scope, source-aware inbox totals, saved metadata, decision/start fences and historical observation checks reuse the incumbent owners. A Browser decision cannot supply missing file read. The single drop source uses the existing 50 MiB file-transfer ceiling.

The private nonserialized file callback reads and hash-verifies original canonical bytes. Native code requires tenant ownership, physical witness, exact frame origin and current dispatch callback. It repeats page/frame/generation and current authority after source I/O, pins the actual element/context so a locator cannot redirect bytes into a replacement document, and constructs one in-memory File/DataTransfer for one drop event. SDK completion remains separate from remote server acceptance or business verification. Bytes never enter saved consent, tool/checkpoint JSON, receipts or audit. Receipt inspection and recovery cannot repeat the external effect.

Migration `20261020040000_TenantBrowserArtifactDrops` extends the incumbent source insert/update guards. It requires exact actual owned source facts and matching arguments, blocks tool/argument/source/origin replacement, and refuses downgrade with retained file-drop requests. Empty downgrade restores the previous upload-only guards. No table, column, model snapshot, source resolver API or effect ledger is added. Final clean-schema consolidation is still open.

Canonical owners: [ports](../04-backend-interfaces.md#tenant-browser-artifact-drop-contract), [implementation](../12-backend-implementation-spec.md#tenant-browser-artifact-drop-implementation), [model intent](../14-api-and-realtime-protocol.md#exact-browser-artifact-drop-intent), [storage](../15-persistence-and-configuration.md#tenant-browser-artifact-drop-persistence), [test matrix](../16-testing-strategy.md#tenant-browser-artifact-drop-checks), [saved consent UX](../19-multi-tenant-iam-ux.md#browser-artifact-drop-consent).

## Executed use cases

All cases use key-free Synthetic models, disposable IAM/Session/Run/Artifact stores and local native Chromium fixtures. They never use an external account, hosted model or user database.

| Expected behavior | Observed evidence |
| --- | --- |
| Prepare, review and decide an exact file transfer in both stores | Shared source tests cover uploads and drops: exact readable source facts/hash/origin, independent raw read before provider observation, source-filtered totals and every source's approval scope |
| Execute actual native upload or drop once | Original UTF-8 bytes, name, MIME and size reach the selected native file input or drop event in both stores; clearing native state and repeating the saved operation never restores it |
| Reject wrong Session source and absent source read | No provider target observation for denied preparation; an Artifact from another actual Session cannot be used despite broader read authority |
| Revoke source read before native dispatch or after effect | Pre-dispatch refusal causes no native effect; post-effect receipt remains factual while its observation is withheld |
| Resume retained model observation after source-read loss | Original source reads remain independently required; receipt lookup supplies no new read capability or replay |
| Change a cross-origin destination before source resolution | Exact input/drop frame origin drift is refused before the private resolver runs |
| Navigate or revoke authority during source resolution | Native transfer is refused without an attempted effect; subsequent repeated request does not resolve private bytes again |
| Full HTTP/model-worker binary transfer in memory and SQLite | Actual HTTP input leads to model authorization, exact human decision, canonical worker successor and native file transfer; all 65,536 bytes (including NUL and every byte value) match the source; drop count remains one after receipt read |
| Closed input and durable mutation boundaries | Noncanonical IDs and text/MIME overrides are rejected; missing/mutated sources, changed saved arguments and lossy rollback are rejected; empty downgrade/upgrade restores executable guards |

The native event only proves file delivery to the actual local page handler. It does not prove a third-party website's eventual processing or remote upload acceptance.

## Focused checks

All commands ran from the repository root with `AGENTCORE_CHROMIUM_ISOLATION_TESTS=1`.

- `dotnet test tests/AgentCore.Infrastructure.Tests/AgentCore.Infrastructure.Tests.csproj --filter FullyQualifiedName~Tenant_Browser_upload_native_consent --nologo` — **4 passed**, no skips. Initial smallest integrated use case, upload/drop in both stores.
- `dotnet test tests/AgentCore.Infrastructure.Tests/AgentCore.Infrastructure.Tests.csproj --no-build --filter 'FullyQualifiedName~TenantBrowserApprovalTests|FullyQualifiedName~TenantBrowserOwnershipTests' --nologo` — **254 passed**, no skips. This is the affected Browser family, not the full CI suite.
- `dotnet test tests/AgentCore.Infrastructure.Tests/AgentCore.Infrastructure.Tests.csproj --filter 'FullyQualifiedName~Tenant_Browser_upload_native_consent|FullyQualifiedName~Tenant_upload_' --nologo` — **10 passed**, no skips, on final pinned-element native code. Includes the repeated original transfer cases and six origin/authority/source-resolution races across the two sinks.
- `dotnet test tests/AgentCore.Api.Tests/AgentCore.Api.Tests.csproj --filter FullyQualifiedName~Tenant_Browser_model_HTTP_upload_and_Artifact_drop --nologo` — **4 passed**, no skips. Actual binary model-worker/HTTP/native cases. The affected cases were rerun after native element-context pinning: **4 passed**, no skips.

Repeated cases count once: **258 distinct passing cases** (254 Infrastructure plus four API). The broader family preceded the final native element-context pinning refinement; all ten affected native cases and all four worker/HTTP cases then passed on final code. Local whitespace checks passed; **1,194 relative links across ten Markdown files** resolved without missing targets/fragments.

## Remaining work and limits

The existing shared Browser parameter summary renders saved file facts and limited drop consequence; no frontend code/layout changed. This slice qualifies backend preview content and actual native/model/HTTP execution, not a new Playwright MCP visual Drop-consent gate. Prior shared inspector presentation evidence remains [separately scoped](tenant-browser-files-ui-verification-2026-10-20.md).

No Real provider, external website, Docker, physical device, hosted CI or full-suite acceptance is claimed. Browser privacy/reset/decommission and autonomous origins, other resource/effect/cleanup families, voice/rich Chat/live input arbitration, autonomous/background/event/transport integration, platform/system guardrails, general audit/reconciliation/notifications, final per-requirement isolation/UX acceptance and obsolete-authority/schema cutover remain open. The full original goal remains active.
