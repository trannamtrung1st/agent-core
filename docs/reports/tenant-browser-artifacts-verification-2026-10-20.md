# Tenant Browser capture/download publication — 2026-10-20

This checkpoint converts Browser captures and explicitly requested downloads through existing tenant Browser consent, native execution, Artifact, receipt and checkpoint owners. The full multi-tenant IAM goal remains active. Dedicated frontend publication-result presentation, Artifact-backed drops, Browser privacy/reset/decommission administration, autonomous origins, other effect/transport/voice families and final authority/schema/isolation gates remain open.

## Implemented behavior

- `browser.screenshot` is a sensitive Browser-use operation with independent Session/Browser reads and raw Session Artifact creation. Download publication requires explicit `command.downloadArtifacts=true`; ordinary Browser commands retain their prior no-file behavior. Neither Browser nor Credential-use exceptional authority supplies missing secondary creation/read permissions.
- Exact consent includes publication choice, narrower host byte/task limits and capture privacy mode/hash. Existing same-root deduplication/refusal semantics remain; page drift alone does not authorize repeating an identical command. Durable published-file usage and in-flight reservations use existing receipt/Run owners, without another allowance ledger.
- Accepted native bytes are frozen, bounded and staged privately. Both stores publish immutable same-Session/current-execution Artifact metadata with factual Browser receipt, eligible checkpoint, security and audit. Whole-batch quota/collision checks precede blob writes. Failed batches expose no partially published metadata.
- A completed native command can have unavailable file publication. The new nullable receipt result contains a closed status/reason and file kind/ID references only. No bytes, private names, paths or read authority enter that result. Partial limits remain distinguishable. Duplicate receipt lookup and recovery never repeat SDK work.
- Tenant native captures reuse the interactive hold already held by execution, fixing the nested acquisition that previously made this unsupported path unrunnable. Current privacy remains host-startup-pinned and cannot be widened by capture arguments.
- Captures reload actual owned/hash-verified image bytes before each model generation under independent current Session/Artifact read and Allowed read policy. Metadata reports whether pixels were supplied; policy-required reads withhold pixels instead of borrowing creation consent. A final provider-boundary fence checks private image hashes/current authority and an unchanged security revision across other input-source checks. It fences canonical parts even when a provider projects tool images to another role. Bytes stay out of checkpoints.
- Approval HTTP result DTOs expose body-free publication facts. Existing canonical Artifact content routes independently authorize recipients; an Agent read revocation does not remove an authorized human's file access. Independently authorized Browser capabilities can use the incumbent Artifact reference preflight.

## Executed use cases

All new cases use Synthetic configuration and actual memory/temporary SQLite IAM, Session, Run, Artifact and receipt stores. Native cases use disposable local Chromium/loopback fixtures. No hosted model, external account or provider credential is used.

| Scenario and expected result | Observed evidence |
| --- | --- |
| Capture → saved exact Review → decision → publication → reopen/duplicate | Both stores retain actual owned PNG bytes and immutable body-free receipt; repeated lookup creates no file or native effect |
| Missing independent creation, then scoped inbox inspection | Refused before target observation; creation scope filters totals before paging; preview explains creation/read separation |
| Revoke creation before native gate or after confirmed native work | First is denied without effect; second keeps completed native facts but publishes no Artifact and withholds observation |
| Download opt-in off/on | Off publishes nothing; on stores the original UTF-8 bytes and returns only factual references |
| Exhaust two published downloads, then propose another action in the same root task | No unfulfillable second consent or native replay; durable root budget refuses preparation |
| Native masked capture in both profiles | Actual PNG with positive dimensions/redaction count; owned bytes persist; native gate returns without reentry/deadlock |
| Native download click in both profiles | Exact `browser-note.txt` bytes (`Generic browser fixture note.`) reach owned storage under opted-in consent |
| Failed second SQLite private blob | Native completion remains factual; neither member's metadata is visible; unrelated symlink target remains unchanged; duplicate does not dispatch |
| Aggregate batch quota in both backings | Whole batch refused before any member becomes visible; SQLite creates no tenant blob directory |
| Forged receipt references, receipt mutation and downgrade | Storage guards reject; rollback cannot erase retained publication facts; current model/bytes remain readable |
| Model capture → checkpoint → current read grant/revoke | Pixels appear only under actual child read; stale loaded parts fail the provider fence; revoked rehydration clears parts; checkpoints contain no image payload |
| Real Runtime/HTTP vision capture with read revocation between generations | Initial provider request receives image parts; later request after revocation contains none; one file remains; human content GET independently succeeds |
| Existing Browser/Artifact/Attachment HTTP paths | Current exact Review, bounded observations, Artifact readback and Attachment read Review remain qualified by the focused runs below |

Publication can leave an unreachable private staged blob after I/O/metadata failure. The existing scoped deleted-Session cleanup owns these bytes; this checkpoint does not introduce an independent orphan ledger or claim a new cleanup scheduler. No physical display fidelity, universal pixel-secret masking or semantic application-outcome verification is claimed.

## Local verification

The broad affected storage run selected **232 cases**:

```sh
AGENTCORE_CHROMIUM_ISOLATION_TESTS=1 dotnet test tests/AgentCore.Infrastructure.Tests/AgentCore.Infrastructure.Tests.csproj --filter 'FullyQualifiedName~TenantBrowserApprovalTests|FullyQualifiedName~TenantBrowserOwnershipTests|FullyQualifiedName~ArtifactStoreContractTests' --nologo
```

Its initial 227 passes and five failures identified four SQLite downgrade translation failures plus an obsolete assertion that capture intent remained unsupported. The migration now uses guarded native column removal after its preceding trigger-only migration, and the assertion covers the still-unconverted geolocation adapter. Focused rechecks passed all affected cases. A later precision check caught returned receipt timestamps differing from SQLite by sub-millisecond ticks; settlement now preserves millisecond normalization. These development failures were corrected, not counted as passes.

The final focused storage/reference command passed **32/32**:

```sh
AGENTCORE_CHROMIUM_ISOLATION_TESTS=1 dotnet test tests/AgentCore.Infrastructure.Tests/AgentCore.Infrastructure.Tests.csproj --filter 'FullyQualifiedName~Tenant_Browser_capture_|FullyQualifiedName~Tenant_Browser_download_|FullyQualifiedName~Tenant_Browser_file_batch_|FullyQualifiedName~Tenant_Browser_SQLite_upgrade|FullyQualifiedName~Credential_metadata_SQLite_upgrade|FullyQualifiedName~Tenant_Browser_upload_SQLite_guards|FullyQualifiedName~Tenant_Browser_intent_classification|FullyQualifiedName~Artifact_reference_preflight' --nologo
```

This establishes **236 distinct passing Infrastructure cases** across the broad run plus corrected/new focused paths: 24 new publication cases, the corrected existing cases and four additional Artifact-reference cases; repeated runs count once.

Final API qualification command:

```sh
AGENTCORE_CHROMIUM_ISOLATION_TESTS=1 dotnet test tests/AgentCore.Api.Tests/AgentCore.Api.Tests.csproj --filter 'FullyQualifiedName~Tenant_Browser_model_capture_HTTP|FullyQualifiedName~Tenant_Browser_model_requests_exact_Review|FullyQualifiedName~Tenant_Artifact_HTTP_download|FullyQualifiedName~Tenant_attachment_model_exact_Review_HTTP_decision' --nologo
```

The final API command passed **24/24**, including all four new full Runtime/HTTP capture cases, six existing Browser model Review cases, two independent Artifact readback cases and twelve Attachment read-Review cases. The union is **260 distinct focused passing cases** (236 Infrastructure + 24 API); repeated checks count once. Production compilation completed through the focused builds; the initial Infrastructure build had zero warnings/errors. EF generation used the existing Infrastructure design-time factory (API has no Design package); the installed EF tool reported 10.0.5 against runtime 10.0.12. Disposable actual migration/rollback and `HasPendingModelChanges=false` assertions qualify this generated model.

No frontend code/layout or browser UI journey was changed/claimed. The new publication DTO's dedicated shared UI presentation remains open. CI, full local CI, Docker, external providers, physical devices and full proposal/milestone acceptance were not run or claimed. Fixtures stop/dispose their owned hosts/profiles; no live user database/profile was reset.

## Contract owners

[Ports](../04-backend-interfaces.md#tenant-browser-capture-and-download-publication), [backend](../12-backend-implementation-spec.md#tenant-browser-capturedownload-implementation), [wire facts](../14-api-and-realtime-protocol.md#browser-artifact-publication-results), [persistence](../15-persistence-and-configuration.md#tenant-browser-artifact-publication-persistence), [testing](../16-testing-strategy.md#tenant-browser-capturedownload-checks), [UX contract/status](../19-multi-tenant-iam-ux.md#browser-capture-and-download-facts).
