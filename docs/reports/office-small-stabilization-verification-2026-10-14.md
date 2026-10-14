# Small Office hyperlink and deployment stabilization — 2026-10-14

Implemented against `main` at `aa467ecd` (the requested reviewed baseline was `3119e00a`). Existing sparse XLSX reading is a separate completed enhancement and is not changed. Concurrent Sandbox/Workspace/frontend work was present before this task and was left intact. This ordinary correction does not reopen milestone acceptance; no hosted CI, manual browser checks, real-file UAT or real Drive operations were requested or performed.

## Changes and supported behavior

The package guard now permits ordinary external DOCX hyperlinks only as inert relationship metadata. The relationship must use a standard OPC `Relationships` root/direct child namespace, an ID, explicit external mode and the exact Transitional or Strict OOXML hyperlink type. The relationship part must belong to existing Word content XML with a standard document/header/footer/footnotes/endnotes/comments root; non-Word, missing, styles or malformed owners are not admitted.

Targets must be well-formed absolute HTTP/HTTPS URIs with a host and no userinfo, or a valid single-recipient mailto URI. Metadata is limited to 2,048 characters. Raw whitespace, backslashes, controls, percent-encoded controls, relative/protocol-relative/file/data/javascript/FTP targets and broader unsupported mailto forms remain blocked. No fetch, DNS lookup, URI navigation or remote part execution is added. Targets remain document metadata rather than execution authority. Unsupported relationships still return `office_feature_unsupported`; malformed package/XML safety checks retain their existing invalid-package classification and processing limits.

Inspection exposes hyperlink text while leaving original bytes unchanged. Supported edits to unrelated simple paragraphs preserve hyperlink XML, relationship identity/URI, paragraph/run styling and style definitions. Hyperlink-containing paragraph/cell edits remain unsupported under the existing complex-target rule. Remote templates/images, OLE, macros, alternate HTML chunks and other active/external relationships remain rejected. All strict/sparse XLSX external relationship restrictions are unchanged. Word fidelity beyond these bounded operations is not promised.

The existing preview renderer strips `href`, `target` and navigation attributes and uses a scriptless sandbox/CSP. Its behavior is unchanged; only its now-outdated source comment was corrected. No UI redesign or new capability/schema/library was added.

Docker's publish stage now copies `global.json` alongside `Directory.Build.props`, so native and container builds follow the same SDK policy. Maintained .NET 10 image tags remain unpinned to an exact servicing version; operators rebuild with `--pull`. [Operations](../17-observability-and-operations.md#serviced-net-runtime-and-safe-redeployment) is the canonical owner of the **10.0.13-or-later serviced .NET/ASP.NET 10.0 runtime floor** and safe update/restart procedure. Package references, auditing and warnings-as-errors are unchanged.

## Executed scenarios and results

- Generated schema-validated HTTP, HTTPS and single-recipient mailto DOCX: inspection returned the hyperlink text. An unrelated edit changed only the requested simple paragraph, preserving links, relationships and existing styles; editing the linked paragraph rejected without changing original bytes.
- Header hyperlink: a body edit preserved the header XML and relationship URI. The standard Strict relationship-type metadata passed the guard; this is not a claim of full Strict Word-document fidelity.
- No-network check: inspection and editing used a link to a loopback listener; the listener observed no connection attempts.
- Negative cases: unsafe schemes/targets, encoded controls, credential-bearing URLs, relative/protocol-relative targets, invalid mailto/multi-recipient forms, oversized targets, wrong namespace/external mode, invalid/non-Word/non-content relationship parts and non-hyperlink external relationship types all remained blocked.
- XLSX: a generated external hyperlink was rejected by both strict and sparse guard profiles. Existing Office/empty-range regression checks remained passing; no XLSX reading/pagination code was changed.
- Owned Synthetic API journey: write a generated hyperlink DOCX into `/home`, inspect with `office.document.inspect`, reject an exact-SHA edit to its linked paragraph, safely edit an unrelated paragraph, request protected no-store preview metadata, download/reopen the bytes and observe the same hyperlink URI/text plus the requested updated text. Existing Office create/edit/preview/download and empty-range tests passed.
- Existing Synthetic Drive approval journey: unbound access and stale source were rejected; exact bytes were uploaded/downloaded in the offline Synthetic provider, Trash required the existing exact approval, and archive/disconnect fences remained intact. No real Google calls occurred.
- Existing preview unit tests: policy/source/rendering and inert spreadsheet projections remained passing. Browser runtime/manual verification was explicitly excluded and is not claimed.

Focused commands:

```sh
dotnet test tests/AgentCore.Infrastructure.Tests/AgentCore.Infrastructure.Tests.csproj --no-restore --filter 'FullyQualifiedName~WordHyperlinkTests|FullyQualifiedName~OfficeProcessorTests|FullyQualifiedName~OfficePreviewPresentationTests' --nologo -m:1 -p:UseSharedCompilation=false

dotnet test tests/AgentCore.Api.Tests/AgentCore.Api.Tests.csproj --no-restore --filter 'FullyQualifiedName~OfficeJourneyTests|FullyQualifiedName~DriveJourneyTests.Synthetic_connect_bind_upload_verify_download_and_epoch_fences_share_only_explicit_grants' --nologo -m:1 -p:UseSharedCompilation=false

# from web/
pnpm exec vitest run src/features/filePreview/FilePreview.test.tsx src/features/filePreview/previewPolicy.test.ts src/features/filePreview/spreadsheetAdapter.test.ts

dotnet list src/AgentCore.Api/AgentCore.Api.csproj package --vulnerable --include-transitive --no-restore --source https://api.nuget.org/v3/index.json
```

Final results: **Infrastructure 51 passed; API 7 passed; frontend unit 14 passed** — 72 distinct focused cases, no failures/skips. The supported DOCX subset also passed **3/3** on patched Linux .NET 10.0.13. The official-feed audit reported no vulnerable packages in the API's direct/transitive graph. Existing SignalR `10.0.13`, PKCS `10.0.13` and MessagePack `3.1.9` pins remain, with `TreatWarningsAsErrors` and normal auditing retained. No full solution suite or CI was run.

## Deployment evidence

Fresh official image checks, in disposable read-only/no-network containers:

- `mcr.microsoft.com/dotnet/aspnet:10.0`, digest `sha256:d5633778c754e89a386882eddf8dbee6942447a8a4b8267c19cf1e1158d0b0ac`: both Microsoft.NETCore.App and Microsoft.AspNetCore.App **10.0.13**.
- `mcr.microsoft.com/dotnet/sdk:10.0`, digest `sha256:0d99ebf1ccad15037b5c98c9fe8979d6b8dbac23812feae2277a0b277b5c5ffa`: SDK **10.0.402**, host/shared runtimes **10.0.13**. With repository `global.json` mounted read-only, SDK selection succeeded and reported `/verification/global.json`.
- `docker build --pull --target build --tag agent-core-office-stabilization-build:local --progress=plain .`: **passed**, fresh restore and Release API publish completed at `/app/publish`, using the changed shared-policy COPY step. This exercised the .NET build stage; the complete final SPA/application image and a deployed service were not rebuilt/recreated. The temporary validation image was removed afterward; application containers/volumes were not changed.

Native inventory (`dotnet --list-runtimes`) still contains latest installed .NET/ASP.NET **10.0.12**, with SDK **10.0.401**. Native test success does not certify that unpatched host runtime. The existing Real host was not updated, restarted or supplied new deployment data. Fresh official images and patched-runtime tests establish a supported deployment path, while the native update/restart remains an operator action.

The patched-runtime test used the compiled Infrastructure test output mounted read-only in SDK 10.0, ran the VSTest console directly, used a non-root UID with all capabilities dropped, disabled network and kept scratch/results in disposable `/tmp`. The direct runner avoided SDK first-use NuGet migration writes under the read-only restriction. An earlier SDK `--info` attempt needed disposable `/tmp`, and a first `dotnet vstest` wrapper attempt could not create its first-use migration mutex; the bounded fallback then passed. These were container setup issues, not product test failures. The first image-cleanup approval review timed out; one permitted retry removed only the task's temporary image.

## Operator action and remaining limitations

Install the current compatible .NET 10 SDK or both .NET/ASP.NET runtimes for the deployment architecture, verify both runtime entries meet the Operations floor, preserve the existing profile/secrets/store/key-ring paths, and restart only the known application owner after saving work and taking a supported backup. For the managed native Real stack, `./scripts/dev-real.sh restart --api-only` and `./scripts/dev-real.sh status` preserve nopCommerce; independently managed services require their own owner-controlled restart. For Compose, use the same Real overlay/environment for `build --pull`, the disposable `--list-runtimes` check and `up --force-recreate`; retain named volumes. See the exact commands in [Operations](../17-observability-and-operations.md#serviced-net-runtime-and-safe-redeployment).

Ordinary hyperlink support does not allow editing complex linked paragraphs/cells or active external content. Strict metadata acceptance is not full Word fidelity. Long/broader URI forms may remain unsupported. The previously rejected public government DOCX was not rerun; verification uses deterministic generated files. Full final-image/browser/manual/customer-file checks remain owner-run, and no live provider/Drive action or hosted CI was performed.

## Changed files

- [OfficePackageGuard.cs](../../src/AgentCore.Infrastructure/Office/OfficePackageGuard.cs): bounded DOCX-only inert hyperlink exception and target/owner validation.
- [Dockerfile](../../Dockerfile): shared SDK policy copied into the publish stage; no runtime/package pin changes.
- [WordHyperlinkTests.cs](../../tests/AgentCore.Infrastructure.Tests/WordHyperlinkTests.cs) and [OfficeJourneyTests.cs](../../tests/AgentCore.Api.Tests/OfficeJourneyTests.cs): local security/preservation/no-network and owned integration coverage.
- [WordPreview.tsx](../../web/src/features/filePreview/WordPreview.tsx): comment correction only; existing sanitizer/sandbox behavior unchanged.
- [README](../../README.md), canonical [ports](../04-backend-interfaces.md), [decisions](../10-technology-decisions.md), [backend](../12-backend-implementation-spec.md), [testing](../16-testing-strategy.md), [operations](../17-observability-and-operations.md), [historical stabilization addendum](office-productivity-stabilization-2026-10-14.md) and this ledger.

The owner subsequently requested commit and push to `main` of these scoped changes. Concurrent unrelated work is not part of this file inventory. Applicable diff/Markdown target/fence checks passed; runtime/browser/manual limitations above remain explicit.
