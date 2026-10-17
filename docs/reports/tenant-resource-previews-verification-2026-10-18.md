# Tenant Instance exact resource reads and Office preview verification — 2026-10-18

This backend successor to the [Skills UI checkpoint](tenant-instance-skills-ui-verification-2026-10-18.md) supplies the remaining exact-resource/Office HTTP prerequisites for the tenant Resources page. It advances the existing Phase 4/7 cutover without accepting the complete proposal or a milestone. The tenant Resources frontend, rich-file/bulk adapters, other file-source families and the remaining proposal obligations are still open.

## Delivered behavior

- Exact `GET .../agents/{instance}/resources/{key}/editor` returns the safe Instance, one readable item, current security witness and independent manage/delete/copy hints. It shares the filtered catalog with the paged editor, so collection search/paging cannot redirect an open draft. Hidden keys deny, dependencies remain independently permission-filtered and archived files have no mutation hints.
- Canonical `GET .../resources/{key}/office-preview` uses current cookie authentication and independent exact parent/resource reads. It reuses the existing real Office processor, effective preview policy and safe response mapper. DOCX returns validation proof without base64 source; XLSX returns bounded typed cells, sheet/range and presentation. Original bytes remain separately downloadable, unchanged.
- Fresh authorized metadata determines the byte limit before blob allocation. The existing byte owner still verifies length/hash and current exact permission. The original-human facade surrounds byte I/O and projection together; logout/replacement login, exact revocation and parent/security changes during processing cannot release the captured preview. Recovery is an explicit new read.
- `GET /api/v2/tenants/{tenant}/file-preview/options` provides only effective host policy to an authenticated organization member. It grants no file access. Canonical routes have no owner-token or global-file fallback. No schema, new store, DI lifetime or mutation owner was introduced.

## Executed checks

All commands ran locally with Synthetic test fixtures and disposable data, without provider credentials, live-provider calls or CI.

| Check | Command from repository root | Result |
| --- | --- | --- |
| HTTP resource detail/preview and download regression | `dotnet test tests/AgentCore.Api.Tests/AgentCore.Api.Tests.csproj --no-build --no-restore --nologo --filter 'FullyQualifiedName~Tenant_resource_\|FullyQualifiedName~Child_editor_HTTP_reads\|FullyQualifiedName~Resource_download_fences'` | **29 passed**, 46 seconds |
| Both-store detail/permission and capture races | `dotnet test tests/AgentCore.Infrastructure.Tests/AgentCore.Infrastructure.Tests.csproj --no-restore --nologo --filter 'FullyQualifiedName~Tenant_child_editors_\|(FullyQualifiedName~Human_agent_reads_reject_captured_configuration&DisplayName~resource-detail)'` | **10 passed**, 13 seconds |

The API assembly was built by an earlier focused size-boundary run: the same three threshold cases passed before expanding to the complete affected filter. They are included in the 29 above, not counted again. **39 distinct focused cases passed.** The builds implicit in these checks succeeded.

HTTP actions and observations:

1. Upload an actual DOCX/XLSX through the canonical Instance command, read exact detail and preview, then download. Preview hashes match the uploaded bytes; DOCX contains validation/empty-body proof without source base64; XLSX preserves `00123` as text. Reads do not advance the tenant revision. Responses retain no-store and preview nosniff.
2. Request an oversized XLSX window, then a permitted smaller range. The first returns 413 and the smaller range returns the expected source value. Byte thresholds immediately below/equal/above the package length reject before content-store I/O only when over limit; downloads still return the complete original.
3. Give a minimally privileged member exact parent read and one exact resource read, for both local and owned-publication inherited files. Real preview and download succeed without IAM-inspector permission; mutation hints are false. Hidden siblings, anonymous requests carrying only a legacy owner header and foreign-tenant addresses deny independently.
4. Revoke that exact resource grant after the real Office processor has returned its result. The captured HTTP preview returns 409 without cells; subsequent exact detail/content/preview reads return 403 while the independently readable parent remains available.
5. Intercept after real byte reads and after real DOCX/XLSX processing. Logout and same-Account replacement login return `initiator_unavailable`; concurrent security and lifecycle changes return conflict without releasing the old projection. Explicit current reads recover when authentication and access still permit them. Existing protected download race cases continue to pass.
6. Request preview of unsupported text or malformed XLSX. Safe 400 errors contain no private sentinel; separately authorized download preserves the original bytes.

InMemory and SQLite cases compare exact detail with the collection's complete serialized item projection, including readable dependency arrays and independent action hints. They exercise inherited/local permissions, sibling-creation denial, archived inspection, unreadable keys, stale command recovery and original logout/parent/security changes during Definition I/O. The first run had two test-assertion failures because record equality compared separately materialized dependency arrays by identity. Comparing the complete serialized projection corrected the assertion; no product change was needed, and the final ten cases passed.

Terminal local logs: `/private/tmp/tenant-resource-preview-first.log`, `/private/tmp/tenant-resource-preview-api.log`, `/private/tmp/tenant-resource-detail-infra.log` (initial assertion failure), `/private/tmp/tenant-resource-detail-infra-final.log`.

Documentation validation: `git diff --check` passed; all 868 relative file targets in modified Markdown resolve. A broader scan found 13 missing historical report targets (nine local captures and four retired source-file references), identical to the `HEAD` baseline. This change introduces no missing targets and does not claim those older references were repaired.

## Verification limits and remaining work

The real HTTP workflows above ran through WebApplicationFactory; exact detail and capture recovery additionally ran against both stores. This is backend integration evidence. No new frontend control, rendered rich preview, browser bulk-import journey or visual acceptance is claimed. The tenant Resources page must still wire shared upload/inspection/rich-preview compositions through these canonical contracts and exercise create/edit/choices/reset/copy/delete/download, retained drafts, partial-batch recovery and revoked access in Synthetic browser interactions.

Sandbox catalogs/decommission, other resource Access/effect families and Governance/platform surfaces, autonomous origins, tenant voice/rich delivery/live input arbitration, obsolete owner authority/legacy approval/schema consolidation and the earlier SQLite direct-preference reliability follow-up remain open in the [delivery ledger](multitenant-iam-delivery-2026-10-16.md). No existing installation was reset, no Real host was restarted and no deployment, hosted CI, full local CI suite or milestone acceptance was performed. The full proposal remains active.
