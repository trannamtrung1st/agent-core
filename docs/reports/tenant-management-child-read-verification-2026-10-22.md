# Canonical Skill and Resource reads — 2026-10-22

This module follows `a288c4b8` and integrates the existing `skills.list`, `skills.inspect`, `resources.list` and `resources.inspect` tools through canonical tenant management reads. The original IAM goal remains active. Harness authoring, Skill/Resource writes, private Skill activation, typed remedy execution, approval-default review, broader dependency/schema consolidation and proposal-wide acceptance remain open.

## Delivered boundary

The incumbent capability discovery, claimed Runtime, management-read store, exact `inspect_management` consent, checkpoint source and receipt owners handle all four tools. Strict arguments select only the executing Agent and canonical origin-qualified child keys. Current raw Agent and child reads, both admitted/current capabilities and delegated Skills/Resources areas, and a real conversational human root remain mandatory. No tenant/principal/Agent selector, arbitrary path or caller-supplied witness is accepted. This closes the canonical read conversion for these tools without exposing the legacy mutable owner path.

Lists return at most 12 independently readable metadata records. Filtering precedes that bounded window; unreadable names/counts, procedures, bytes, private dependency names and Credentials are absent. Exact Skill inspection returns its complete bounded procedure and required capabilities as untrusted data. The new `skillProcedure` witness binds exact text and requirements separately from metadata-only `skill` witnesses. Validation binds witness kind/key/cardinality to the actual read selection. Procedure text is complete or withheld by the existing 16,000-byte observation budget; it is never silently shortened to the generic field excerpt. Inspection does not activate the Skill, alter the pinned catalog or grant any required capability.

Exact Resource inspection uses the existing immutable content-store port, verifying owned content hash and length. Supported UTF-8 text returns at most 2048 bytes with explicit provided/excerpt status; Unicode boundaries remain intact. Missing content returns unavailable. Corrupt content aborts the Run before model reentry or adoption of an observation. Unsupported binary formats return current readable metadata without fetching or returning bytes. Their review says **Not provided** for absent text. An excerpt is not a complete replacement value; download and other resource operations keep their independent authority.

Child read Review withholds the whole saved observation from model messages. Approval authorizes that exact saved read only, creates no exceptional grant and supplies no raw permission, write or future read. Current source/reviewer checks apply during decisions, receipt settlement and retained use. Source changes and revoked child reads withhold observations before model reentry/protected output. Exact child scopes filter Inbox/conversation counts and detail access before paging. Current Instance configuration and Run pins stay unchanged.

## Persistence and restart

`20261022100000_TenantManagementChildReads` extends the existing closed read payload. Frozen Up/Down SQL preserves all **117** incumbent trigger definitions and indexes; no table, column, permission or execution framework is added. Existing immutable-read guards freeze procedure/content witnesses. Downgrade refuses retained new requests and checkpoint references before lossy conversion. Historical migration execution files remain unchanged.

The schema roundtrip checks guard/index preservation, current model and foreign-key violations. A real SQLite application-host recreation retains pending private-procedure consent and child witnesses, rejects a lossy downgrade, authenticates in the recreated host and completes the exact read through the normal successor/receipt path. Agent revision remains 6 and no grant is created. The prior inspection migration fixture restores the latest schema before its current-model assertion, preserving its original version-specific checks.

## Presentation and observed journeys

The shared Ant Design saved-read inspector labels Skill identity, full procedure and capability requirements, or resource metadata/text and excerpt status. Values render as escaped data. The existing drawer owns wrapping, scrolling and its reachable action footer; no product TSX, CSS or UI kit was added. [Skill desktop](assets/tenant-management-child-read-2026-10-22/skill-desktop.png), [Skill phone](assets/tenant-management-child-read-2026-10-22/skill-narrow.png), [Resource desktop](assets/tenant-management-child-read-2026-10-22/resource-desktop.png) and [Resource phone](assets/tenant-management-child-read-2026-10-22/resource-narrow.png) capture the exercised inspectors.

Native CLI journeys use actual Synthetic login and visible Chat, exact review and approve/reject confirmation for all four new tools plus Harness/configuration regressions, at 1440×1000 and 390×844. Expected and observed: only the selected inspect exposes its saved procedure/text to the qualified reviewer; lists and other inspections withhold those bodies; secrets remain absent; script-like names stay literal; footer actions remain in view; no horizontal overflow occurs. Approval yields **Read authorized** and a factual inspection reply, rejection yields an unauthorized-read reply, and canonical Agent revision stays **3**. No page or non-aborted request failure was recorded.

Playwright MCP independently signed in, opened a real fixture organization, sent an exact Definition Skill inspection through the composer, inspected the saved procedure and confirmed approval. The procedure appeared as text with zero script elements and no resource bytes or Credential secret. Chat returned **Read authorized** and “The exact saved Agent configuration was inspected. No settings were changed.” A subsequent exact Resource inspection completed directly under its ordinary read policy. Canonical readback remained revision **3**, with two factual completed replies, zero console errors and zero non-aborted failed/API error requests. These Synthetic replies are observed behavior, not a claim of a universal free-text disclosure filter.

## Executed checks

**230 distinct focused cases passed, plus 24 native browser journeys.** Successful identities combine grouped runs and focused repair rechecks; repeated identities are counted once.

| Check | Result |
| --- | --- |
| Serial solution build, followed by final affected API-project build | Passed, zero warnings/errors, using `--no-restore --nologo -m:1 -nodeReuse:false -p:UseSharedCompilation=false`. |
| New API/Runtime/store module | **61 distinct passed**: 16 direct/readability cases, 16 exact approve/reject cases, 16 source/permission races, 10 content cases, 2 scoped visibility cases and 1 actual SQLite host recreation. Both stores; procedures stay inactive and revisions/pins remain exact. |
| Adjacent compound inspection API regressions | **47 passed**: owned metadata/non-disclosure, exact read policy, source races, diagnosis/refusal, visibility and restart. |
| Application contracts/projection | **77 passed**: `ManagementChildReadContractTests`, `ManagementInspectionContractTests`, `ManagementReadContractTests`, `ManagementProjectionTests`, `ManagementMutationContractTests`. |
| Infrastructure schema/history | **45 passed**: new child-read migration, existing inspection/read/mutation migrations and all frozen metadata/hash/string-heap tests. Actual current schema retains 117 guards, original indexes and no foreign-key violations. |
| Native Synthetic CLI | **24 passed**, 1.7 minutes, no retries: 16 new child-read journeys plus 8 Harness/configuration regressions. |
| Playwright MCP | Actual private procedure Review/approval and direct Resource inspection completed; revision unchanged, no console or non-aborted request failures. |
| Final binary saved-preview check | Both stores passed exact Review/approval with **Not provided** text and zero blob reads. These are included in the ten content cases above. |
| Documentation/Git | Changed local links, new anchors, JSON examples, fences and staged whitespace checked. |

The representative API command selected `FullyQualifiedName~Canonical_management_child_reads_use_exact_current_owned_sources` (**16 passed**). The grouped follow-up selected `(FullyQualifiedName~Canonical_management_child&FullyQualifiedName!~Canonical_management_child_reads_use_exact_current_owned_sources)|FullyQualifiedName~Canonical_management_inspection` (**82 passed, 10 initial fixture failures**). The content group then qualified all ten scenarios; its final binary Review identities were checked with `FullyQualifiedName~Canonical_management_child_binary_Review`. All tests used `dotnet test <project> --no-build --no-restore --nologo --filter <filter>` against disposable Synthetic stores.

The retained browser command is `pnpm exec playwright test -c playwright.tenant-chat.config.ts tenant-management-inspections.spec.ts` from `web/`. Qualification used a temporary one-worker configuration against owned Synthetic ports 5096/5186. The temporary configuration was removed, MCP closed and only those owned listeners stopped.

## Repairs and limits

Qualification corrected the test data construction and byte-store fault decorator to forward its incumbent verified-source facet. Binary fixtures now use a supported PNG media type; corruption asserts the actual fatal integrity boundary rather than expecting a recoverable tool message. The reviewed binary fixture reads the existing nested authorization-result envelope. Final review corrected the binary preview's null-text label from **Inherit Agent default** to **Not provided**, with both-store exact-consent verification. These are repaired findings, not ignored failures; the original grouped run is not represented as entirely green.

This is focused local module qualification. No product frontend source files changed, so frontend build/Vitest suites were not repeated; native browser interactions exercised the shared presentation. CI, hosted provider calls, binary download, private Skill activation and full proposal/milestone acceptance were not run. Remaining work stays in the [delivery ledger](multitenant-iam-delivery-2026-10-16.md).
