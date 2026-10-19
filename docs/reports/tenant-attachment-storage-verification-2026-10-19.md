# Tenant human Attachment storage qualification — 2026-10-19

This checkpoint covers canonical human tenant upload, permission-filtered metadata, exact download and guarded Office preview, pending abort, expiry and deleted-Session byte cleanup. It reuses existing Attachment, IAM, canonical filesystem and supervised cleanup owners. Tenant message binding, processing/model tools, composer and Attachment-based Artifact publication remain unconverted. The full multi-tenant IAM proposal remains active; no milestone or production acceptance is claimed.

## Behavior and authority

- Actual retained Tenant/Session/Agent/executor ownership and the original current login/Account/principal govern access. Raw Session read remains independent of Session-primary creation and exact-child read/delete. Metadata pages filter before cursor/lookahead; cross-tenant IDs and hidden cursors disclose no child.
- Upload freezes bounded detected bytes, sanitized basename and immutable SHA-256, original uploader and request UUID. Current security/Session/Instance revisions and authority repeat in the canonical mutation transaction. Same-request/same-payload retry returns the original live ID; changed bytes or actor conflicts. The 201 acknowledgement contains only attachmentId and grants no later read.
- Current reads fence source/owner/login/IAM after I/O or guarded Office processing and withhold expired bytes. DOCX default preview is format/hash/warnings/empty-body metadata; XLSX returns bounded rows. Original download stays independent. No model/provider work or workspace copy is implied.
- Aborting a live unbound upload expires retained metadata and retires child assignments with tenant revision/audit. Archived/ended Sessions and inactive Agents remain read-only. Pending expiry and durable Session deletion drive bounded technical cleanup under existing supervision; deleted-Session cleanup removes the exact managed root, including private orphans, while retaining metadata/tombstones.
- Native bytes are staged before metadata/security/audit commit. A metadata failure publishes none of those facts but may leave private unpublished bytes. Physical quota includes resident expired/aborted/orphan files; exact deleted-Session cleanup removes unpublished remnants. This is not filesystem rollback.
- Existing unscoped Attachment calls reject actual tenant Sessions. The global legacy sweeper excludes owned rows. Tenant pending storage does not enable the old model/bind paths.

## Durable schema

`20261019092834_TenantAttachmentOwnership` extends incumbent rows with tenant/request/uploader attribution and content-cleanup time, exact owner/uploader foreign keys, owned paging and unique request indexes. CHECKs constrain source address/hash/size and Pending/Bound shape. Triggers reject adoption/reparenting/source changes, foreign/non-human uploader, foreign-Session bound entries, legacy shadow and TTL extension. The table is rebuilt explicitly before creating triggers; EF's deferred SQLite rebuild otherwise discards them. Downgrade refuses retained owned data. Optional null fields remain omitted from legacy serialized records. Only disposable databases/roots are used for qualification; no installed migration/reset was performed.

## Executed checks

Every .NET command uses `--nologo --no-restore -m:1 -nr:false -p:UseSharedCompilation=false`. These are focused local Synthetic/disposable checks, with no hosted CI or live providers.

| Project / filter | Terminal result | Evidence |
| --- | --- | --- |
| Infrastructure: `Tenant_attachment_`, `AttachmentStoreContractTests`, `TenantSessionLifecycleTests`, `TenantSessionIntegrationTests`, `IamIdentityIntegrationTests` | 81 passed, 0 failed, 0 skipped; 54 s | `/private/tmp/tenant-attachment-final-infrastructure.log` |
| Infrastructure: `Tenant_attachment_`, after expired-abort parity correction | 29 passed, 0 failed, 0 skipped; 48 s | `/private/tmp/tenant-attachment-final-expiry.log` |
| API: `Tenant_attachment_`, `Tenant_Artifact_HTTP_download_`, `Tenant_model_publication_returns_`, `Tenant_deleted_Artifact_cleanup_`, `Canonical_Session_HTTP_`, `AttachmentApiTests.Upload_bind_and_content_` | 18 passed, 0 failed, 0 skipped; 57 s | `/private/tmp/tenant-attachment-final-api.log` |
| Application: `AttachmentBindTests`, `AttachmentProcessingRuntimeTests`, `QueuedAttachmentRuntimeTests`, `SessionAttachmentRecallTests` | 30 passed, 0 failed, 0 skipped; 5 s | `/private/tmp/tenant-attachment-final-application.log` |

There are 129 distinct passing cases across the three projects. The second row rechecks the 29 attachment cases already contained in the first; it is not 29 additional distinct cases. Initial failures and fixes are retained below; final affected results supersede them. Documentation checks passed for 12 changed/new Markdown files, 1,697 local links/fragments and 27 complete JSON examples; `git diff --check` passed.

The HTTP scenarios uploaded exact UTF-8 originals, retried the same upload identity, rejected changed bytes and wrong Session IDs, fetched safe metadata/content, aborted twice without duplicate effects, verified empty discovery and ran technical cleanup. One SQLite scenario disposed and recreated the actual application host before retry/download. Office scenarios verified original DOCX/XLSX hashes, bounded metadata/rows (including numeric zero and Vietnamese text), byte-identical downloads, no workspace materialization and no model calls. Existing Artifact publication/delivery and legacy bind/content scenarios remained green through the changed host fixture.

Store scenarios checked filtered child pages and hidden cursors, same-Account tenant separation, independent raw permissions and scoped Session creation without implied child read. Revocation/logout/expiry during reads or intake withheld data/publication. Native metadata-failure injection left no row/audit/revision, permitted the same-request retry, and exact Session deletion purged both unpublished and published bytes. Attempted raw SQLite changes failed; retained downgrade failed without losing owned bytes, while an empty-owned downgrade/upgrade preserved legacy rows without adoption and left no model drift.

## Findings corrected during qualification

- Attachment scopes were missing from the IAM aggregate's external-resource shape validation; exact child assignment now uses the canonical typed scope and actual storage ownership.
- The SQLite API fixture constructed IAM without its actual attachment backing; it now shares the typed attachment store and disposable configured root with the host.
- DOCX preview assertions initially expected plaintext, contrary to the existing source projection. Tests now verify the actual metadata/hash contract and exact original bytes; XLSX checks preserve numeric zero and text rows.
- SQLite's deferred table rebuild removed triggers created earlier in the same migration. Explicit rebuild-before-trigger ordering now matches the incumbent Artifact migration pattern and is checked through raw attempted mutations.
- Native physical quota and in-memory resident-byte accounting now include expired/aborted bytes until cleanup. Memory cleanup continuation preserves remaining deleted roots. A fresh abort of an expired upload now denies consistently in both stores; factual completed-request replay remains available under current parent/login authority.

## Remaining verification and proposal boundaries

No frontend component/style or tenant composer was changed in this checkpoint; no new browser UX or visual finish acceptance is claimed. CI, live providers, physical process-kill recovery and installed-data cutover were not requested or run. HTTP integration exercises actual upload/download/preview and SQLite host recreation. Later attachment binding/model/processing/composer/publication work, other virtual resource/Browser adapters, autonomous/background/events/transport families and final obsolete-authority/schema removal remain open. Historical single-user attachment qualification does not accept those tenant paths.
