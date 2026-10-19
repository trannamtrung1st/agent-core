# Human tenant Attachment materialization backend verification — 2026-10-19

This checkpoint implements the canonical original-human materialization backend: one explicit copy action creates an exclusive working file and an immutable Artifact from verified original Attachment bytes. It extends the incumbent Attachment/IAM, working filesystem and Artifact owners. It does not create an AgentRun, exceptional grant, second file ledger or automatic retry loop.

## Implemented behavior

- Original live HumanSession, actual same-tenant Session/Instance/source, independent Session/Attachment reads, working write and Session-scoped Artifact creation are checked before copying and immediately before each protected effect. Security and Instance revisions plus current lifecycle fence changes; owned Runtime progress revisions are not human authorization.
- Existing sanitized names and up to 128 atomic create-new attempts preserve occupied files, directories and symlinks. The original is unchanged. Working bytes and Artifact bytes match its checksum. A second explicit action is a distinct copy, rather than an idempotent retry.
- `ArtifactRecord.CreatedByPrincipalId` identifies the actual tenant human. Human materializations have exact source/working provenance and no Run. Model publications retain actual Run ownership with null human/source/working provenance. Immutable metadata, tenant revision and authorization audit publish together through the existing IAM transaction. Private staged bytes alone are not publication.
- The shared SQLite owned Artifact query also supplies exact IAM scope ownership. Exact read grants, permission-filtered listing and downloads therefore work for either provenance variant. Creation gives no read grant. Existing source cleanup does not remove the independent Artifact; committed Session deletion drives existing Artifact content cleanup and retains immutable metadata.
- Known working-file completion followed by publication refusal returns `attachment_materialization_incomplete`. Lost filesystem/publication acknowledgements return `attachment_materialization_unknown`. Both require reload and a deliberate new decision. Completed files are not automatically removed or replayed; private exception details are withheld.

## Integrated checks

`dotnet test tests/AgentCore.Api.Tests/AgentCore.Api.Tests.csproj --no-restore --nologo --filter 'FullyQualifiedName~Human_materialization_'`

**18 passed, 0 failed, 54 seconds.** Log: `/private/tmp/tenant-materialization-module.log`.

The exercised journeys were:

1. Both stores: authenticated upload, two explicit materializations, exact original/working/Artifact downloads and actual human/source/path metadata. Expected and observed: distinct `source.txt` / `source-2.txt`, identical original bytes, immutable Artifacts, no model request or Run.
2. Both stores: progressively grant exact source read, working write and primary Artifact creation; revoke source read and then restore it. Expected and observed: each missing permission prevents either copy, complete creation does not grant workspace/Artifact reads, and a later exact Artifact read grant permits only that Artifact. Anonymous and missing-CSRF actions are refused.
3. Both stores: replace the original login immediately before file commit and immediately before the Artifact transaction; archive after a completed working copy. Expected and observed: pre-effect refusal or explicit incomplete materialization, no Artifact and no replay. New login cannot replace the initiating HumanSession.
4. Both stores: lose the working-file acknowledgement and lose the Artifact acknowledgement after real metadata commit. Expected and observed: explicit unknown result, one dispatch attempt, factual working bytes retained, zero or one actual Artifact according to the reached boundary, unchanged original and no Run/model call.
5. Real SQLite application-host recreation: publish, abort/clean the pending source, stop the first host, reopen and download the independent working/Artifact copies with unchanged provenance; delete the Session, stop/reopen again and run cleanup. Expected and observed: source content becomes unavailable without invalidating the Artifact, then deleted-owner Artifact bytes are purged while factual metadata remains.
6. Real SQLite: forged human, source, path, mixed Run, checksum, runless and foreign-tenant rows, immutable updates and lossy downgrade all fail. The valid original remains downloadable; current model and snapshot agree.
7. Both stores: stale witness, occupied symlink/directory and exhausted 128-name collision set. Expected and observed: no stale write, no outside-target overwrite, first free sanitized name used, and no extra Artifact after exhaustion.

## Failures found and corrected

The first upload/copy/read representative flow passed both stores (2 cases, 9 seconds; `/private/tmp/tenant-materialization-flow.log`). The first boundary fixture failed to start 10 cases because it requested an unregistered concrete workspace service; its logout route was also corrected before exercise. Three other cases passed; the same SQLite exact-scope lookup gap described below also failed. That failed setup is not counted as boundary verification (`/private/tmp/tenant-materialization-boundaries.log`).

The corrected fixture then exposed one product integration gap: SQLite's separate exact Artifact scope lookup still required an AgentRun, so assigning a read role to an actual human materialization failed. Thirteen cases passed and one failed (`/private/tmp/tenant-materialization-boundaries-corrected.log`). The lookup now delegates to the same canonical owned query as Artifact reads. The final 18-case module selection above passes with that correction.

## Incumbent regressions

These are focused local selections, not the full CI or milestone suite.

| Selection | Result | Evidence |
| --- | --- | --- |
| Artifact ownership/download/paging/quotas, direct Run publication/rollback/races, exact Attachment export/consent, source migrations and deleted-owner cleanup | 78 passed; 1 intentional-downgrade fixture failed, corrected below; 79 cases / 2m6s | `/private/tmp/tenant-materialization-artifact-regression.log` |
| Failed Artifact downgrade/reopen plus metadata-consent upgrade/reopen | 2 passed / 9s | `/private/tmp/tenant-materialization-narrowing-regression.log` |
| Exact approved Artifact recovery after refused downgrade | 1 passed / 6s | `/private/tmp/tenant-materialization-approval-regression.log` |
| Legacy Artifact/materialization API, canonical Attachment upload/retry/abort/restart and deleted-Artifact host recreation | 8 passed / 23s | `/private/tmp/tenant-materialization-http-regression.log` |

The store failure was a fixture assumption exposed by the new additive migration: EF commits each successful narrowing step before a later downgrade guard refuses. The fixture then reused the latest adapter against the narrowed schema and received `no such column: a.CreatedByPrincipalId`. That fixture and the related exact-approval fixture now explicitly restore the current schema after the expected refusal before continuing current-adapter checks. No guard was weakened, and current data/provenance remains intact. Both corrected paths pass. The metadata-consent migration scenario also passes unchanged.

There are **107 distinct focused cases with a passing final observation**: 18 new materialization cases, 79 incumbent selected cases (including the corrected failure), two additional metadata/approval migration cases and eight HTTP cases. Earlier representative/failed/corrected runs overlap these cases and are not added again. Backend builds complete without warnings/errors. Changed-document validation checked 1,496 relative Markdown targets with none missing; `git diff --check` passes.

## Scope and remaining work

The shared frontend materialization action, its stale/unknown recovery UX and fresh Synthetic browser/Impeccable qualification remain pending. Other resource/Browser lifecycle, autonomous/background/event/transport families, voice/rich interaction, final obsolete-authority/schema removal and proposal-wide qualification remain open. No full Attachment, milestone or tenant IAM proposal acceptance is claimed. No CI, real-provider calls, live Docker or physical process-kill qualification was requested or performed for this backend module.
