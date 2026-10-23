# Canonical tenant public-download verification — 2026-10-23

This module restores preserved `web.download` behavior through the current tenant workspace effect boundary. It does not complete the IAM proposal or its final acceptance matrix. The prior Resource/workspace authority retirement remains historical evidence for its own version.

## Implementation

- The existing workspace mutation contract, policy, exact review, dispatch fence and settlement owners now accept public downloads. Primary permission remains `workspaces.write`; no new permission classification or execution framework is introduced. The normalized native `IPublicWebFetcher` remains independently replaceable and retains its existing public-network transport controls.
- URLs reject non-HTTP(S) schemes and embedded credentials. Explicit file paths are validated before dispatch; reviewed replacements bind current destination bytes. Omitted paths create a unique, safe, non-overwriting file under `/home/downloads`. Core rechecks cancellation and current authority after transfer and at file commit.
- Completed receipts contain requested/final URL, path, media type, byte count and SHA-256. They contain no file bytes and grant no file/Artifact read access. Direct and reviewed continuations receive the same metadata. Uncertain acknowledgements and noncooperative fetch deadlines settle without replay; late results cannot commit after cancellation.
- The additive guarded migration widens only the existing workspace tool admission rules and adds nullable factual receipt JSON. It composes existing frozen metadata, preserves historical migration SQL/resources, all indexes and the 133 unrelated guards, and retains the two widened direct-workspace guards. The predecessor class becomes inheritable only to reuse its frozen target model. Empty Down/Up matches the current model; retained download data blocks narrowing.
- The existing Ant Design workspace approval preview now shows the saved URL as plain text and distinguishes automatic creation from exact replacement. It uses the current saved-literal and responsive detail components.

## Local verification

All checks use disposable Synthetic data and no hosted provider calls. No user database was reset or adopted. No CI was requested or run.

| Check | Result |
| --- | --- |
| Serial solution build, `--no-restore -m:1 -nodeReuse:false -p:UseSharedCompilation=false` | Passed; 0 warnings/errors, 48.51 s |
| API `Tenant_public_download_model` | 24 passed, 0 failed/skipped |
| Application download/workspace/Office contracts | 46 distinct passed (45-case group plus one final URL-bound case), 0 failed/skipped |
| Infrastructure migration, workspace effects and public-network adapters | 68 passed; final focused effect rerun passed 21 including 4 additional download cases, for 72 distinct cases |
| `TenantApprovals.test.tsx` | 135 passed, including five new download review/refusal cases |
| Frontend `pnpm run build` | Passed |
| Playwright MCP, actual Synthetic approval and effect | Passed; separate repeated API fixture passed after browser approval |

There are 277 distinct focused cases across the four test groups. The 21-case final effect group repeats 17 already counted cases. The browser-driven fixture repeats one case and is separate rendered interaction evidence.

The API matrix creates an owned Agent and Session, assigns independent executor permissions, sends actual model tool calls through HTTP admission, and verifies native file/Office/Artifact effects. In memory and SQLite, the complete journey downloads exact DOCX bytes, inspects the original, replaces one paragraph, preserves another paragraph and downloads the resulting Artifact through authorized HTTP. Other cases cover explicit working destinations, automatic destinations, exact review for both forms, rejection, changed reviewed destination, missing permission, malformed path before fetch, normalized source failure, permission revocation during fetch and lost write acknowledgement. SQLite success cases retain identical receipts and file bytes after host disposal/reopen, run recovery without replay, and refuse downgrade with retained facts while retaining enabled foreign-key enforcement.

Native effect cases prove repeated source calls return the same receipt after one transfer. FakeTimeProvider advances a pending noncooperative fetch beyond its deadline: the operation settles indeterminate before the provider is released, retains no completed-transfer fact, refuses replay and leaves no late destination file. Neighboring workspace boundary cases and unchanged public download/fetch/socket adapter tests cover the affected native boundaries. Migration coverage compares every existing trigger/index definition, admits only the two intended trigger changes, checks empty Down/Up and current model parity, and runs SQLite foreign-key checks.

The first verification groups exposed an optional-path exact-field parser error and SQLite guards that omitted this tool. Later integration exposed observation changes caused by automatic directory creation; automatic non-overwriting destinations now use their existing initial observation while explicit replacements retain repeated checks. Three success cases also exposed a test-only disposed-host probe read, which was moved before disposal. All final groups pass after these corrections.

The focused commands were run from the repository root, except frontend commands from `web/`:

```sh
dotnet build AgentCore.sln --no-restore --nologo -m:1 -nodeReuse:false -p:UseSharedCompilation=false
dotnet test tests/AgentCore.Api.Tests/AgentCore.Api.Tests.csproj --no-build --no-restore --nologo --filter 'FullyQualifiedName~Tenant_public_download_model'
dotnet test tests/AgentCore.Application.Tests/AgentCore.Application.Tests.csproj --no-build --no-restore --nologo --filter 'FullyQualifiedName~TenantPublicDownloadContractTests|FullyQualifiedName~TenantWorkspaceMutationContractTests|FullyQualifiedName~TenantOfficeMutationContractTests'
dotnet test tests/AgentCore.Infrastructure.Tests/AgentCore.Infrastructure.Tests.csproj --no-build --no-restore --nologo --filter 'FullyQualifiedName~TenantPublicDownloadMigrationTests|FullyQualifiedName~Tenant_workspace_effects|FullyQualifiedName~PublicWebDownloadTests|FullyQualifiedName~PublicWebFetcherTests|FullyQualifiedName~SocketsPublicWebTransportTests'
dotnet test tests/AgentCore.Infrastructure.Tests/AgentCore.Infrastructure.Tests.csproj --no-build --no-restore --nologo --filter 'FullyQualifiedName~Tenant_public_download_effect|FullyQualifiedName~Tenant_workspace_effects|FullyQualifiedName~Tenant_approved_workspace_effects'
pnpm exec vitest run src/features/access/TenantApprovals.test.tsx
pnpm run build
```

After the final bounded-URL and downgrade-order review, the solution was rebuilt and the download contract, migration and four relevant SQLite journey/destination cases were rechecked. These repeats do not add duplicate cases to the totals. The browser fixture used `AGENTCORE_DOWNLOAD_UI_PROOF=1` with the single in-memory `default-review` case; approval was performed through Playwright MCP, not the test HTTP decision helper.

## Rendered review

Playwright MCP signed into the disposable Synthetic host, opened the actual saved download request, read the source URL and automatic destination disclosure, approved through the confirmation dialog, and refreshed the durable result to **Workspace changes completed**. The fixture independently checked the resulting exact bytes and completed successfully.

The existing drawer was inspected at 1440×1000 and 390×844. Document width matched the viewport; the mobile drawer fit at x=0, width=390 with no internal horizontal overflow. The saved source had no executable links. There were no console errors after successful sign-in; initial stale-cookie 401s and navigation-aborted reads were separate setup/cancellation events. Approval decision and result reads returned 200. The mobile screenshot was retained locally at `/private/tmp/tenant-public-download-review-mobile.png`. Shared spacing and saved literal components were reused without adding a UI kit or independent layout rules.

## Remaining qualification

The transfer is controlled/offline in the canonical model journey. Native adapter cases remain separate evidence for public-network policy; live official-site downloads and desktop Office fidelity were not run. This module does not qualify all cross-tenant combinations or the final proposal-wide security/UI matrix. Public search/fetch/generic HTTP dispatch, generic Composer Resource references, remaining authority/schema reconciliation and the long canonical browser journeys remain open. Historical acceptance claims are not extended by these checks.
