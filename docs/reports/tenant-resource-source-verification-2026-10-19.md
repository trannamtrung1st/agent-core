# Tenant pinned-resource source verification — 2026-10-19

## Scope and outcome

This prerequisite advances the resource family of the full tenant IAM proposal from `828bcd53`. It implements an internal exact source boundary for admitted `/agent/resources/...` and `/agent/instance/resources/...` files. It does not complete resource model tools, namespace browsing/search, Office conversion, durable resource-read Review, recipient delivery or the full proposal.

`TenantRunConfigurationResolver.CurrentResourcesAsync` now supplies the same current tenant/Instance resource graph used by Run configuration, without requiring unrelated Credential binding or Sandbox readiness reads. Run admission retains those independent configuration steps. The helper returns trusted internal source metadata, not a public authorized resource inventory.

`TenantResourceSourceTarget` requires a current claimed conversational Run and exact actual Session/Instance ownership; both admitted and current read capability; an exact admitted virtual file; and raw current `sessions.read`, `agents.read` and exact Instance-child `resources.read`. Every compound source policy applies. A source requiring Review returns no captured body; missing raw read authority cannot be substituted by a grant or by the requester's Owner role. Inherited content retains its admitted Definition/version origin. Source records, resource revision, security, Instance, Session and Run facts are checked after blob I/O. A final authority/claim read closes changes during the final metadata lookup. Returned bytes are defensively copied and carry no authority, callback, approval, receipt or role grant.

The new `ITenantResourceSourceContentStore` is a facet of the existing immutable resource store. Memory/native adapters verify expected length and SHA-256. Native reads reuse existing descriptor-relative no-follow file primitives, owned hard-link checks, a bound before allocation and a bounded fresh hash. The 8 MiB resource ceiling applies. Reads create neither a blob root nor a workspace layout marker. The content-addressed store retains its incumbent ownership model: the scoped resource graph selects the digest before byte access; no public digest-selected read endpoint was added. Ordinary concurrent filesystem semantics remain; no cross-process lock or snapshot is claimed.

## Executed checks

All final commands reached exit 0: **84 distinct focused passes, zero skips**. Exploratory/subset reruns are not counted again.

| Check | Command / selection | Result |
| --- | --- | --- |
| Scoped source and byte facet | `dotnet test tests/AgentCore.Infrastructure.Tests/AgentCore.Infrastructure.Tests.csproj --filter 'FullyQualifiedName~Tenant_resource_source_\|FullyQualifiedName~TenantResourceSourceContentTests\|FullyQualifiedName~DefinitionResourceInfrastructureDiTests' --nologo --no-restore` | 58 passed, 1m9s |
| Incumbent resource contracts | Same project, filter `FullyQualifiedName~DefinitionPublicationResourceWorkspaceTests\|FullyQualifiedName~Content_store_enforces_hash_integrity_and_defensive_reads`, `--nologo --no-build --no-restore` | 7 passed, 4s |
| Actual model/HTTP admission and publication regression | `dotnet test tests/AgentCore.Api.Tests/AgentCore.Api.Tests.csproj --filter FullyQualifiedName~Tenant_model_workspace_Artifact --nologo --no-restore` | 19 passed, 1m27s |

The source selection comprises 48 both-store application integration cases, nine byte-store/native cases and one production in-memory DI check. SQLite fixtures migrate temporary databases and assert no pending model changes. No schema or migration was changed.

Documentation validation passed for all seven changed Markdown files: 1,012 local links/fragments and balanced code fences; no complete JSON examples were present. `git diff --check` passed.

## Actions and observed results

- Created a resource through authenticated tenant configuration, using the actual original human session, then admitted and claimed a fresh Run carrying its immutable manifest. Actual memory/SQLite metadata plus memory/native blob capture returned exact bytes, tenant/Instance/Session identity and resource revision. Mutating a returned byte copy did not alter subsequent copies. No approval request was created.
- Omitted each independent read in turn: capture failed before blob I/O. Required Review on each source permission in turn: capture failed before blob I/O with `resource_read_review_required`. Exact resource-child permission succeeded with independent parent reads and no workspace grant; revocation then denied capture.
- Captured inherited sources under their actual admitted Definition/version and exact child scope. Unsupported, unadmitted, traversal, alias/glob and malformed paths did not reach blob I/O. Disabling the saved binding prevented capture.
- Changed content or disabled its binding during byte I/O: old bytes were withheld. Revoked authority or changed security during I/O: capture failed. Revocation during the final metadata lookup was caught by the final authority fence. Expiry at that same late point and cancellation during byte I/O returned no captured body.
- One Account owned two tenants with different human principals and agents. Both used the same virtual display path with different private bytes. Alpha's executor role did not authorize Beta; after separate Beta assignment each Run obtained only its own bytes. A mixed tenant/Run frame failed before blob I/O. Additional forged tenant, Instance, Session and generation frames were denied.
- Native blob reads preserved opaque binary bytes and rejected wrong declared length, oversized, corrupt and truncated content, a symbolic-link leaf/root and an external hard link. Missing or over-bound reads did not initialize a root. The native read added no `.canonical-layout` marker.
- Verified production DI reuses the same byte-store object for the new facet and resolves the source target without a cycle. Existing resource publication/workspace behavior and immutable content contracts passed. Actual model-to-HTTP workspace Artifact publication, primary/source Review, grant/refusal and pending-consent SQLite host recreation passed through the shared configuration resolver.
- A negative model-boundary check confirms that internal source capture does not enable unconverted tenant virtual-namespace reads or expose source contents. Those public consumers remain deliberately unfinished; the next conversion must use this source and the incumbent consent/checkpoint owners.

## Findings corrected

Initial resource-binding setup correctly failed because the credential-focused fixture lacked the production resource backing. The fixture now wires its actual resource/content stores and current original-human identity services. Production guards were retained. The first native missing-root test exposed an incorrect error-code comparison (`not_found` versus the incumbent `NotFound`); corrected before the final run. Review identified the late-metadata revocation window, which is now fenced and directly exercised. Earlier failing/subset commands are not counted as acceptance.

## Limits

This is backend source qualification, not public model/HTTP resource-read acceptance. The API regression verifies shared admission/publication behavior, not the new source through a resource endpoint. There is no new UI, browser presentation or Impeccable cycle in this change. The byte facet introduces no new write/cleanup behavior and does not claim that legacy resource APIs or authority paths have been removed. Production multi-instance, live provider, hosted CI and full-suite verification were not run. Native safety evidence is from the current supported Unix host; no new Windows claim is made.

The full goal stays active. Resource namespace projection, exact durable read Review/checkpoint delivery, remaining publication adapters, autonomous/resource/transport families and final legacy authority/schema cutover remain pending.
