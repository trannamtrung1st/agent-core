# Tenant Browser Artifact upload qualification — 2026-10-19

This checkpoint converts canonical same-Session Artifact upload through the existing Browser authorization, Review, Run fence, receipt and model-result owners. It supersedes upload-adapter-pending notes only. The full multi-tenant IAM proposal remains in progress. Screenshot/download Artifact publication, Artifact-backed drops, privacy/profile reset/decommission, autonomous origins, other effect/transport/voice families, final isolation/UX gates and obsolete-authority/schema cutover remain open.

## Implemented boundary

- Closed `browser.upload` arguments accept one to eight distinct canonical Artifact IDs and an existing target. Model schemas expose that exact restriction and no raw byte/path owner selector.
- Core checks current parent Session read, Browser read/use and each independent Artifact read before source/target observation. Consent binds ordered real source IDs/names/MIME/sizes/hashes and the exact file-input origin. The shared saved Browser parameter summary includes those facts and says setting the input does not confirm server submission.
- Both stores repeat actual source facts and authority inside incumbent preparation, decision, start and eligible observation settlement. Source policy and reviewer qualification remain independent of primary Browser use. Inbox filtering checks every saved source and actual live provenance before counts/paging.
- A private nonserialized callback loads only canonical tenant bytes under native gates, verifies their hashes and repeats current claim/consent/IAM after reads. Native execution validates its physical witness and repeats live file-input semantics, page generation, frame URL/origin and current authority before transmission.
- Original byte payloads stay out of consent/receipts/audit/checkpoints. Existing bounded Browser observations remain untrusted page output; no semantic disclosure filter is claimed. Each retained upload result requires its own current source permissions. Factual unknown/recovery and duplicate receipt lookup never repeat the upload.
- Optional JSON witnesses omit null, preserving old Browser canonical serialization. Migration `20261019200000_TenantBrowserUploadSources` adds guards to the existing approval owner, without new tables, columns or file/effect ledgers. It rejects incomplete/non-owned sources and source/origin mutation. Downgrade refuses to erase the boundary while saved upload consent exists. Final canonical schema consolidation is still required.

## Executed scenarios

The new integration cases use actual in-memory and temporary SQLite IAM, Session, Run, Artifact, approval, receipt and audit stores with Synthetic built-ins. Native cases use disposable local Chromium and loopback fixtures; no external provider or account is contacted.

| Action / expected result | Observed |
| --- | --- |
| Save exact source consent, inspect its human preview, decide and dispatch | Actual source facts and input origin retained; preview includes filename/hash and submission caveat; ordinary Review creates no grant |
| Upload the original file into the actual native input | Both stores deliver `Private original thử nghiệm`, the exact original size and filename |
| Clear the input and inspect the same receipt again | Input stays empty; no second upload and no replayed observation |
| Remove independent source read before preparation or inside native gate | Refused before target observation or before native effect/byte resolution respectively |
| Remove source read after confirmed effect | Factual completed receipt retained; protected result withheld |
| Carry upload through the real model checkpoint/approval continuation, then revoke its original file read | First retained delivery succeeds; subsequent retained observation validation refuses; no redispatch |
| Reference another Session's actual Artifact under an otherwise valid exact child read | Refused before native target observation |
| Inspect inbox/detail without file scope, then add read and exact source approval scope | Counts/detail remain hidden without read; read alone exposes inspectable consent but cannot decide under independent source Review; qualified independent reviewer can approve |
| Retire the Run and delete its Session | Both stores omit the saved upload source before inbox totals |
| Change page/frame while file resolution awaits, or revoke authority during resolution | No native file-input effect; exact target/current-authority refusal |
| Replace a cross-origin iframe destination before resolution | Exact file-input origin witness refuses before resolver/bytes |
| Supply malformed/fixture/duplicate/raw sources or mutate SQLite saved source JSON | Closed validation/storage guards refuse; exact saved request remains intact |
| Attempt downgrade with retained upload consent | Guard refuses; latest schema/request remains readable and EF reports no pending model changes |
| Exercise incumbent native dialog/popup/upload/download/screenshot/storage journey | Existing native journey remains functional |

No frontend source/layout changed. Qualification covers shared Review data and backend/native behavior; no new upload-consent visual browser journey or full UX acceptance is claimed.

## Focused local commands and results

The affected Browser approval/ownership run passed **192/192** cases, including the initial 19 upload module cases, native Chromium ownership/password/race checks and current consent/claim/recovery/model-result regressions:

```sh
AGENTCORE_CHROMIUM_ISOLATION_TESTS=1 dotnet test tests/AgentCore.Infrastructure.Tests/AgentCore.Infrastructure.Tests.csproj --filter 'FullyQualifiedName~TenantBrowserApprovalTests|FullyQualifiedName~TenantBrowserOwnershipTests' --nologo
```

The final ownership-filter/schema/native-destination refinements were checked with:

```sh
AGENTCORE_CHROMIUM_ISOLATION_TESTS=1 dotnet test tests/AgentCore.Infrastructure.Tests/AgentCore.Infrastructure.Tests.csproj --filter 'FullyQualifiedName~Tenant_Browser_upload|FullyQualifiedName~Tenant_upload_|FullyQualifiedName~Generic_dialogs_popups_upload_download_screenshots_and_storage_boundaries' --nologo
```

This final command passed 28 cases and exposed two fixture precondition failures: Session deletion correctly refused while the Run remained active. The corrected fixture explicitly retires that Run; its focused two-profile recheck is recorded below. Earlier development also corrected a test command namespace and two fixture assumptions (current permission refusal precedes replay witness matching; creating independent Review requires a qualified reviewer). Those failures were not accepted as passing product behavior.

The corrected deletion fixture passed **2/2**:

```sh
AGENTCORE_CHROMIUM_ISOLATION_TESTS=1 dotnet test tests/AgentCore.Infrastructure.Tests/AgentCore.Infrastructure.Tests.csproj --filter FullyQualifiedName~Tenant_Browser_upload_inbox_omits_deleted --nologo
```

Production DI and authenticated HTTP/native Review qualification passed **4/4** (both stores, stable and drifted targets):

```sh
AGENTCORE_CHROMIUM_ISOLATION_TESTS=1 dotnet test tests/AgentCore.Api.Tests/AgentCore.Api.Tests.csproj --filter FullyQualifiedName~Tenant_Browser_Review_HTTP_uses_actual_native_target --nologo
```

The union is **207 distinct passing cases**: 173 existing Browser approval/ownership cases, 29 new upload cases, one incumbent native upload journey and four HTTP Review cases. Repeated checks are counted once. Final source compilation completed through these focused test builds. `git diff --check` passed; changed documentation relative links were checked locally. All disposable hosts/profiles were stopped/disposed by their fixtures; no user database/profile was reset.

CI, Docker, external providers, physical devices and full proposal/milestone acceptance were not run or claimed.

## Contract owners

[Ports](../04-backend-interfaces.md#tenant-browser-artifact-upload-contract), [backend](../12-backend-implementation-spec.md#tenant-browser-artifact-upload-implementation), [persistence](../15-persistence-and-configuration.md#tenant-browser-upload-source-persistence), [testing](../16-testing-strategy.md#tenant-browser-upload-checks), [consent UX](../19-multi-tenant-iam-ux.md#browser-upload-consent).
