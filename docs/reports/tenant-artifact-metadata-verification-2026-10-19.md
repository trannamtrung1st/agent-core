# Tenant Artifact metadata verification — 2026-10-19

The existing `artifacts.verify` tool now reads exact tenant-owned Artifact metadata under current Allowed policy, or obtains exact durable Review through the existing approval, successor Run and local consent receipt owners. This extends `57b09936`; the full multi-tenant proposal remains active.

## Implemented boundaries

- Strict arguments identify one Artifact in the current Session. The actual canonical source, current claimed Run, admitted/current capability and active Session are required. Independent raw `sessions.read` and `artifacts.read` permissions and both operation policies govern the read. Missing raw read authority cannot be supplied by this consent.
- Output contains stored filename, type, byte size, checksum, creation time and typed source identity. It contains no body, download authority, workspace path or attachment provenance. Post-I/O source/security/Run fences and retained checkpoint checks prevent stale metadata from being reused after authority changes.
- `verify_artifact` prepares the exact immutable metadata witness. Review uses existing scoped reviewer eligibility, independent-review policy, expiry, root suppression and current source/configuration checks. A completed local receipt authorizes this inspection only; it creates no file, role or grant. Rejection suppresses the same exact action even after policy relaxation.
- Approval visibility checks current human Session and Artifact reads and the actual source before collection counts/paging. Unreadable detail returns the incumbent `approval_unavailable` response. The shared inspector removes private facts and gives recovery guidance.
- The migration expands the existing typed prepared-operation CHECK with a metadata-only Review shape. It adds no table or receipt engine. Its downgrade guard rejects retained Artifact-read requests. Generation used the existing Infrastructure design-time factory; installed data was not migrated or reset.
- Organization Approvals and Chat reuse `TenantApprovalDetail`, shared one-sided facts, collapsed technical references, native confirmation and existing spacing. Stored bytes differs from publication input bytes. The result says Metadata inspection authorized or Metadata not authorized, separately from the human decision. No new UI kit, CSS or token system was added.

## Focused local execution

Synthetic adapters used no provider credentials or outbound provider HTTP. Logs below are machine-local `/private/tmp/agent-core-artifact-verification-*.log` evidence.

| Check | Observed result |
| --- | --- |
| `final-store-fixed` | 49 pass: 23 new verification cases, two existing actual-source reference checks and 24 Credential metadata Review regressions |
| `api-regression` | 18 pass: six actual model Allowed/Review/rejection cases across memory and SQLite, eight publication Review/grant cases and four reviewed rich continuations |
| `host-restart` | One pass: actual SQLite application host disposal/recreation with pending consent, fresh login, decision and successor metadata delivery |
| `final-web-unit` | 30 pass: shared inspector and identity behavior |
| `final-web-build-2` | Pass, with the existing large-chunk warning |
| `copy-build` | Backend build pass, zero warnings/errors after normalizing the read-loss response |
| `copy-ui-host` and `recapture-host` | Six pass each: the real separately scoped reviewer decision and exact read revocation include a 403 `approval_unavailable` assertion and zero visible inbox items |

Store checks cover exact source/foreign Session/tenant/claim, independent raw reads, bounded metadata/no body, compound policy changes, checkpoint reuse, reviewer independence, scoped visibility before totals, receipt/successor identity, root suppression, source/security races, upgrade/reopen/CHECK constraints and downgrade refusal. SQLite store reopen and actual application host recreation are separate checks.

The final runtime proof ran `AGENTCORE_ARTIFACT_VERIFICATION_UI_PROOF=1 dotnet test tests/AgentCore.Api.Tests --no-build --filter 'FullyQualifiedName~Tenant_Artifact_verification_model_uses_independent_read' -m:1 -nodeReuse:false --logger 'console;verbosity=minimal'`. Its opt-in fixture waits for actual browser decisions and explicit revocation/release markers; those markers are not product endpoints. Backend compilation used `dotnet build tests/AgentCore.Api.Tests --no-restore -m:1 -nodeReuse:false`. Frontend checks used `pnpm --dir web exec vitest run src/features/access/TenantApprovals.test.tsx src/services/identity.test.ts` and `pnpm --dir web build`. The independent host-recreation test is `Tenant_Artifact_verification_pending_consent_survives_actual_SQLite_application_host_recreation`.

Intermediate failures were corrected and rechecked: nested-array record equality in the SQLite fixture, a plural decision URL in test setup, an assertion expecting access denial where the current revision fence correctly returned conflict, and a confirmation visibility assertion running before native animation settled. EF generation initially used the wrong startup project; the existing Infrastructure factory succeeded. A sandboxed test retry could not bind the test runner socket and was rerun with local socket permission. These failures are not counted as passes.

## Browser evidence

Playwright MCP exercised a disposable Synthetic Kestrel host through Vite. A separately scoped human reviewed a real model-created request with a long stored filename, safe type and **56 stored bytes**. Technical checksum remains collapsed initially. The actual body, including literal Markdown and Vietnamese text, never appeared in the metadata inspector.

Escape and Keep reviewing returned to Approve without sending a decision. Deliberate confirmation sent one decision POST, then returned focus to Refresh details. The actual successor Run and local receipt produced Metadata inspection authorized. The ready Chat showed the truthful final reply and the same shared inspector through Review details. Revoking only the reviewer's exact Artifact read closed the Chat inspector and removed its entry point while preserving conversation/status text. The canonical detail returned 403; the unfiltered approval inbox total was zero.

Captures under `.impeccable/review/artifact-verification/` cover pending at 1440/1024/768/390/320px, narrow scrolled facts and technical references, desktop/mobile native confirmation, decision/result, Chat and read-loss recovery. Native animation must finish before capture: Playwright visibility alone does not imply nonzero opacity. The final confirmation and technical captures replace early invalid evidence. No document or Drawer horizontal overflow was measured at the five widths. Drawer controls retain the existing 32px desktop/40px mobile sizing; the native consequential confirmation retains Ant Design's 32px control baseline.

The final real denied response showed “This approval is unavailable or you no longer have access. Return to the inbox or contact an organization owner.” Private filename count was zero. The final console contained the expected denied 403. Two later mobile screenshots initially caught the Drawer during resize; those captures were replaced after verifying x=0 and width=320. Root opened all sixteen final captures. Only task-owned test/Vite hosts were stopped, with terminal completion confirmed.

A fresh no-history Impeccable finish reviewer inspected all sixteen captures and returned **ship**, with no material fixes across persistence, fidelity, ceiling, material fixes and keep. It accepted the incumbent mobile confirmation sizing and disclosed the slightly transitional desktop read-loss width with fully visible content. This is an ordinary code-led extension; no new comp, visual-world reproduction or QUALITY BAR card was supplied. No current detector report was supplied for these edits, so no scan is claimed and no redundant detector pass was run.

A separate fresh documenter compared the actual shared source, theme, CSS, incumbent context and six representative captures, then extended [the existing surface brief](../../.impeccable/surfaces/web-src-features-access-tenantapprovals-tsx.md) while preserving its publication contract/evidence. PRODUCT, DESIGN and sidecar remain unchanged. The documentation handoff checked its paths/fragments and all sixteen capture paths. Changed-document relative paths, the two new owning anchors and Markdown fences were checked; `git diff --check` passed after removing an extra trailing blank line.

## Limits and remaining work

CI was not requested or run. Installed/live data, Real hosts and providers were untouched. These focused checks do not establish full proposal or milestone acceptance, native-device accessibility or general file-body disclosure filtering. Expected denied requests and aborted obsolete reads are distinct from application defects.

This converts the existing metadata verification tool; it does not create a new generic Artifact read/list tool or make one an additional requirement. Earlier reports' inferred generic-read/list-pending wording is superseded at this scope. Browser/Workspace/Office publication adapters, owned-blob cleanup, remaining resource/transport/autonomous families and final authority/schema cutover remain open. The previously observed sign-in return-path and SQLite preference reliability follow-ups remain recorded. Pre-existing design-context fingerprint drift is reported without incidental repair.

The unfinished credential-check draft is absent from the working tree. Its machine-local backup remains `/private/tmp/agent-core-credential-check-draft-20261017`; no supported flow was removed.
