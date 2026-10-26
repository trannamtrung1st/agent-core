# Clean baseline delivery — 26 October 2026

The bounded stabilization is locally complete on .NET 10. Canonical configuration, explicit retained-grant reconnect, corrected Core workspace ownership, General Assistant v34, actual local Sandbox PNG publication and observed file UX fixes are delivered. MarkItDown is deferred with no runtime dependency or advertised converter. Decision Intelligence is not started. This is focused local verification, not application milestone, deployment or live-provider acceptance.

Baseline: `42258913c0f4176bf218673df747c57e6f0469cf` on `main`. The [source manifest](clean-baseline-source-manifest-2026-10-26.json) identifies tested file bytes independently of this report's eventual commit. Previous immutable delivery reports retain their own source claims.

## Configuration and installed-state impact

Use [the cutover runbook](../clean-baseline-cutover.md) and [host reference](../host-configuration-reference.md). The [public source inventory](clean-baseline-configuration-inventory-2026-10-26.json) records readers, shapes, public defaults, classifications, precedence and mappings. It never reads effective private values. Focused fixtures enforce duplicate, retired, malformed and dual-input refusals; diagnostics contain names only.

- Runtime accepts `GoogleOAuth`, `Providers:Speech:Recognition`, `Providers:Speech:Synthesis`, `GoogleDrive:Enabled` and `FilePreview:Limits:DelimitedBytes`. Retired OAuth clients, speech maps, CsvBytes and untranslated orchestration inputs fail before opening host state. There is no legacy OAuth client or unstamped-token fallback.
- Native dotenv is parsed as data, preserves inherited → dotenv → launcher-derived precedence and translates the four documented orchestration inputs once. Application Compose uses `python3 scripts/compose.py`; raw Docker Compose bypasses this preflight. Arbitrary native options are not automatically forwarded into containers. Compose inherits environment over dotenv before its explicit service mapping.
- `.env.example` is a startup preset; optional shapes live in the host reference. `Security:EphemeralBootstrap` remains active for explicit InMemory test/demo initialization; it is not retired. Durable bootstrap remains the one-use five-field command. Hosted speech retains its documented external key convention only when explicitly selected.
- Read-only local impact inspection found 72 private dotenv assignments, zero retired inputs, and zero stored Credential rows in `data/native/core.db`. No existing account, key root, database or workspace was reset. No live Google reconnect was necessary or attempted. Private values were not recorded.
- The offline `audit-google-grants` command opens the database read-only and disables Data Protection key generation. It classifies canonical/other-client/unstamped/invalid/unreadable/synthetic/empty grants, active/retired accounts, affected tenants and stored Instance binding counts. It neither restamps nor migrates tokens. Future operator audits must use a coherent database/root/key backup; these local zero-row observations do not establish another installation's safety.
- Check/runtime use of an unproven grant returns `oauth_reconnect_required` before HTTP, preserving ciphertext, connection profile and revisions. Explicit original-owner disconnect/reconnect remains audited. Canonically stamped grants continue with the canonical client. Offline SQLite reopen and fake-transport tests cover both Gmail and Drive; live consent/refresh remains external UAT.

## Core and versioned Definition

Core now names captured Space `/home`, Instance `/private`, and Session `/working`, with independent source authorization. It removes persona-imposing/discretionary judgment wording while preserving Core → Runtime → Evidence authority, current task and exact approvals. Context assembly/capability tests cover separate Spaces and fresh-Run context changes.

General Assistant v34 is a new template: instructions shrink from 7,614 to 1,053 characters (about 1,904 to 264 tokens by characters/4, an estimate). Operational rules remain at Core/tool owners. Persona, capabilities and policies retain v33 values. v33 bytes are unchanged; published pins do not auto-upgrade. Latest-template and historical-pin tests pass.

## Document extraction decision

**Defer runtime installation.** The [MarkItDown 0.1.9 benchmark](clean-baseline-markitdown-feasibility-2026-10-26.json) used only a disposable Python environment, with plugins/LLM/cloud disabled and socket connections refused. Existing specialized Office and source-specific readers remain authoritative. There is no host subprocess fallback, runtime pip installation, new file authority or partially enabled tool.

The matrix covered simple/complex DOCX, sparse/multisheet XLSX, text/scanned PDF, PPTX, unsupported binary, corrupt/mislabeled input, oversized package, timeout/cancellation, malicious link/instruction text, denied Attachment and unavailable Sandbox. DOCX tables, sparse sheets, text PDF and PPTX retained expected sentinels; scanned PDF returned no text. Unsupported binary refused; a corrupt DOCX was treated as short text and a DOCX labeled PDF was accepted by content, demonstrating the need for strict source admission. Injection strings remain untrusted Evidence. Oversize and authorization cases were harness admission refusals; they are not a production converter implementation.

Forty-two installed distributions occupied about 332 MB; workers reached about 159 MB RSS. No network attempts were observed for these fixtures. Main package license is MIT; transitive license/security/image inventory and strong production process limits remain gates. Benchmark socket blocking is not a production isolation certificate. Current authorized byte handoff and hard process/archive/output limits must be designed and qualified before a later go decision. See [Microsoft's ingestion guidance](https://learn.microsoft.com/en-us/dotnet/ai/conceptual/data-ingestion) and [the converter project](https://github.com/microsoft/markitdown). No framework upgrade was needed.

## Real local PNG journey

The disposable Synthetic API used SQLite, fresh keys/file roots and its own workspace namespace. The existing local Sandbox service had no running workloads before this test; its restart was explicitly approved. Native user application ports and installed storage were left intact. The actual cached non-root Workbench image was `sha256:985c3477b1bcf54385a33c048e465ed6bf7f590d09e9dd4bbfae06d62af91561`.

1. Provision a tenant/profile/Definition/Instance through canonical APIs. Verify the profile and effective Instance setup against the real daemon in read-write, read-only, disabled-home and read-only-working cases. All passed, including owner/mount/Git/cleanup probes.
2. Send the opt-in Synthetic PNG request in Chat. Review and approve the exact Python stdlib command through the existing approval drawer and confirmation. The real command completed with exit 0 and `Validated PNG 64x32`, writing `/working/plot.png` and `/home/plot.png`.
3. Require exact publication review and approve each saved 135-byte source separately. Both immutable Artifact references appear in the same conversation. An initial fixture tried the review interface under an Allowed policy and correctly stopped without publication; publication resumed through a new explicit request over existing files, without replaying the completed command. The committed setup script configures review before starting the journey.
4. Playwright MCP opens both previews: natural dimensions 64 × 32, image/png, 135 bytes; downloads succeed. API reads show both equal the authorized working source. Their SHA-256 is `adee397aaa6aaaaae217c5f0b2cd278e9b114f79b7bce066354d9e62ad29bbab`.
5. Restart the disposable API and read/download the immutable Artifacts again. Alter only the disposable working source through the human workspace endpoint; both Artifacts retain original bytes. A foreign tenant gets 403 and an anonymous recipient gets 401. [Machine-readable proof](clean-baseline-png-proof-2026-10-26.json) records these facts.
6. Request canonical reset using the generation/lease/last-execution witnesses. Readback confirms `Terminated`, cleanup not pending and owned retirement confirmed. Canonical files and durable outcomes remain; the user's Sandbox service remains running.

This proof exposed three integration defects that were corrected at their owners: retirement still expected filesystem contract 4 while provisioning uses 5; host readiness depended on a retired global profile catalog; and `/home` plus `/private` aliases collided on the same probe directory. Unique per-root probe paths now permit valid aliases without weakening cross-root link checks. Failed/indeterminate earlier attempts remain recorded and were not silently replayed. The current proof used contract 5 throughout.

Reproduce fixture setup with explicit opt-in:

```sh
AGENTCORE_PNG_QUALIFICATION=1 python3 scripts/qualification/png-artifact-setup.py \
  --api http://127.0.0.1:5280 --image sha256:VERIFIED_LOCAL_IMAGE --output /tmp/png-journey.json
```

Use a deliberately disposable Synthetic host and matching trusted adapter roots. The script creates new records, verifies the selected image and never resets installed state. In that conversation send `synthetic-png`, then review exact command and two publication requests. A paused/refused operation must be inspected before another request. The optional `synthetic-png-publish-existing` fixture publishes only already-existing sources and never runs a command.

## Observed Chat/file corrections

The shared file card/preview now distinguishes sign-in expiry, permission loss, missing files and ordinary failure without displaying arbitrary exception text. Lost access retires Artifact metadata and revokes cached Attachment thumbnail URLs. Retry remains an explicit user action. A focused image Slider tooltip caused document overflow at narrow widths; its redundant portal tooltip is suppressed while visible percentage and accessible values remain.

The browser pass uploaded a PNG, verified an incompatible model refuses send while retaining the draft, selected Synthetic Vision, sent, previewed, used keyboard zoom/fit/Escape with focus return, downloaded identical bytes, injected a refused read, retired the stale link and explicitly retried. It copied the original into working files plus an Artifact, then previewed/downloaded that immutable copy. This exposed an audit bug: the copy's Session-owned workspace used the Instance revision. It now uses the Session snapshot revision and fences Session changes before commit. SQLite integration tests include a renamed Session and retained permission/race/restart cases.

Viewport widths 1440, 768, 390 and 320 had no document horizontal overflow in the final preview pass. Image zoom reached 200%; this is distinct from actual browser 200% zoom, which remains external manual acceptance. No design tokens, fonts, palette, UI kit or page architecture changed. Existing stale Impeccable briefs for deleted surfaces were observed and left outside this bounded pass.

## Focused verification ledger

Logs were captured under `/private/tmp/clean-baseline-*`; they are local task evidence, not portable CI artifacts. Counts overlap across runs and must not be added into a unique-test total. SDK 10.0.401 serviced the repository's 10.0.400/net10 baseline.

| Check | Result / exercised outcome |
| --- | --- |
| `dotnet test` Infrastructure filter: OAuthConfiguration, OAuthProof, DriveHostGate, AgentDefinitionStore | 36 passed; canonical client/proof/gates and template selection |
| Infrastructure tenant OAuth consent/check/runtime/revocation filters | 17 passed; additional final host/OAuth filter 20 passed |
| Infrastructure separate Spaces, fresh-Run Session context, denied Artifact/Attachment and cancellation boundaries | 31 passed |
| Application ContextAssemblerTests + CapabilityProjectionTests | 55 passed; root guidance and authority composition |
| API SpeechProviderBinder, HostingTopologyOptions, FilePreviewPolicy | 21 passed; independent speech and safe host/default policy |
| Infrastructure final TenantPngArtifactScriptTests, OpenSandboxReadinessTests, MountOwnershipProbeTests, CanonicalSandboxMountResolverTests, Cutover_audits | 20 passed; no replay, disabled gate, source mounts, retained encrypted grant/reopen |
| API `Human_materialization` | 21 passed; current Session revision, permissions, races, partial/uncertain outcomes and restart |
| API `Tenant_resource_preview_rejects_unsupported_or_malformed_source_with_safe_error` | 2 passed; corrupt/misleading Office source refused |
| Historical retained-unowned migration fixture | Initially failed because current store wrote later model columns into predecessor schema. Fixture now seeds predecessor columns explicitly; 1 passed and migration still refuses retained unowned state without deletion |
| Python test_dev_real | 26 passed; actual launcher/shell behavior, paths, inheritance and no-eval dotenv |
| Python configuration cutover + Sandbox retirement/compatibility | Final 27 passed; names-only refusal and current contract fencing |
| `python3 scripts/qualification/configuration-compose.py` | Four Synthetic/Real/HTTPS overlay combinations resolve; three retired/dual-input cases refuse. Placeholder certificate tests configuration only |
| Frontend ArtifactView, AttachmentPreview, FilePreview, previewPolicy | 40 passed; precise feedback, metadata/thumbnail retirement, preview policy |
| Frontend typecheck and `pnpm build` | Passed; existing large-chunk build warning remains |
| Playwright chat-retention | 2 passed; Saved final completion before/after reload, Temporary long text failed upload/Retry/exact bytes/direct reload and expiry cleanup |
| Playwright clean-baseline-files | Final 1 passed in 9 seconds; complete original/copy preview/download, refusal/retry, keyboard and four widths. Earlier fixture assertions were corrected for 200% slider ceiling and sanitized copied filename |
| Playwright MCP + real Sandbox / HTTP | Completed command, two exact publications, both previews/downloads, restart, immutable source separation and recipient denial as above |
| `git diff --check`, v33 unchanged, JSON/source inventory | Passed |

CI was not requested and was neither dispatched nor awaited. The full CI suite was not run locally. Live Google reconnect/Drive/mail, hosted speech/models, production TLS/backup/restore, physical devices and actual browser 200% zoom remain their existing TODO obligations. No optional external check is represented as passed. These limits do not start a new milestone or reopen unrelated historical acceptance.
