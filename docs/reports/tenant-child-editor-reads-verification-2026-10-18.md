# Tenant Instance child editor reads verification — 2026-10-18

This checkpoint supplies coherent scoped editor reads for the pending tenant Skills and Resources pages. It advances their backend prerequisite and records their interaction contract before UI implementation. It does not implement or visually accept those pages, accept a milestone or complete the multi-tenant proposal. Skills/resources UI, rich/bulk adapters, Sandbox and remaining resource/effect families, voice/rich delivery, autonomous origins and obsolete-authority/schema consolidation remain open. The earlier SQLite direct-preference reliability follow-up is unchanged.

## Implementation

The existing original-human agent facade now exposes `/skills/editor` and `/resources/editor`. Shared private child catalogs retain the existing owning snapshot/resolvers, permission filtering and readable dependency redaction for both old raw reads and new editor reads. Editors return safe Instance identity/current version/lifecycle, readable items, visible matching totals, bounded page information, current security revision, tenant-wide read coverage and advisory create/item action hints. No schema, provider, storage owner, mutation route or authorization engine is added.

Filtering precedes case-insensitive metadata search, stable ordering, count and paging. Search is at most 128 UTF-16 units; page is 0–100000 and page size is 1–100 (default 30). Skill order is origin/name/key; resource order is logical path/key. Unreadable names, procedures, files, dependencies and hidden totals are absent. Coverage flags describe permission scope rather than the number of hidden items. Exact parent read remains required independently of typed child read.

Existing-item manage is independent of parent update or IAM inspection. Local resource delete is a separate permission from manage. New-child destinations require tenant-wide typed read and manage; a source/item grant cannot create siblings. Customize applies only to inherited Definition Skills, Copy only to inherited resources, and both require a valid destination grant. Active lifecycle supplies all write hints; archived content remains inspectable with those hints false. Built-in/inherited content remains immutable even when enablement choices are manageable. These hints do not authorize commands: existing stores recheck original human/IAM and exact security/Instance/child witnesses atomically.

All editor I/O, including hint lookups, is inside the existing original Account/session/Principal facade and final security/Instance fence. A retired session cannot be replaced by a later login. The API maps the agent safely and shares the existing Skill DTO mapper with the administrative consumer, preserving string origin/projection/author fields. Resource views retain numeric kind and their existing fields. The new [Skills/Resources interaction contract](../19-multi-tenant-iam-ux.md#instance-skills-and-resources-interaction-contract) specifies the forthcoming shared fields, independent drafts, confirmations, lifecycle, stale/denied recovery and responsive behavior; no global frontend fallback is permitted.

## Observed workflows

- InMemory and SQLite: create local Skill/resource, read filtered one-item editor pages and exact current witnesses, and verify no read changes security/audit. A Skill toggle from those facts saves; using the old resource witnesses rejects stale. Refresh plus an explicit resource toggle saves, preserving the intended configuration.
- Owned inheritance with private children: a human with exact parent read, one Skill read/manage grant, one inherited file read and one local file read/delete grant sees only those rows. Searching for a private sentinel yields no match. Readable dependencies omit the private Skill. Item manage allows its exact operation, resource delete remains independent of manage, and sibling creation/customization is denied.
- Tenant manage alone still cannot create siblings without tenant typed read. After explicit tenant read, creation/Customize/Copy hints become available. An inherited Copy commits from the displayed witnesses and its downloaded bytes match the source. Manage does not imply delete on that new copy. Archive retains inspection with every write hint false; parent-read revocation denies both editors.
- Public authenticated HTTP repeats local creation, metadata paging/search, no-store/revision assertions and invalid query checks, then exercises an exact scoped human without role-inspector permission. Its permitted Skill choice saves; stale resource deletion rejects, an explicit read refresh allows deletion, and the saved Skill choice reads back disabled. Revoked, foreign and anonymous reads fail. No owner capability or global endpoint is used in this canonical journey.
- Existing raw Skills/resources and new editor reads reject parent/security changes during Definition I/O in both stores. Logout rejects captured results, including logout followed by a replacement login. The existing administrative Skill journey still passes with the shared pure wire mapper.

## Focused local checks

| Check | Result |
| --- | --- |
| Initial editor read → stale write → refresh → save | Two passed, InMemory and SQLite |
| Scoped child editor scenarios | Four passed, including the repeated normal cases |
| Final Infrastructure regression: new editors, changed raw child consumers and human/security/parent read races | 30 passed |
| Initial authenticated HTTP workflow | One passed before final mapper reuse |
| Final API regression: canonical workflow, changed child logout/relogin reads and shared administrative Skill consumer | Ten passed |

Total distinct final focused cases: 40 (30 Infrastructure and ten API). Initial/diagnostic reruns are not added. Both final process handles completed with exit code 0. Test commands build the affected .NET projects; all execution uses Synthetic/deterministic adapters or disposable InMemory/SQLite fixtures without provider calls.

Final Infrastructure command:

```sh
dotnet test tests/AgentCore.Infrastructure.Tests/AgentCore.Infrastructure.Tests.csproj --no-restore --nologo --filter 'FullyQualifiedName~Tenant_child_editors_|(FullyQualifiedName~Human_agent_reads_reject_captured_configuration&(DisplayName~skill|DisplayName~resource))|FullyQualifiedName~Tenant_instance_children_preserve_dependencies_choices_bytes'
```

Final API command:

```sh
dotnet test tests/AgentCore.Api.Tests/AgentCore.Api.Tests.csproj --no-restore --nologo --filter 'FullyQualifiedName~Child_editor_HTTP_reads|(FullyQualifiedName~Agent_read_does_not_release_captured_data&(DisplayName~skills|DisplayName~resources))|FullyQualifiedName~AgentInstanceSkillTests.Owner_skill_journey'
```

Logs are `/private/tmp/tenant-child-editors-infra.log` and `/private/tmp/tenant-child-editors-api.log`. The first test compile used an incorrect session-property name; the trusted initiator itself is now passed through its existing base type. The initial HTTP fixture sent a numeric Skill projection, which the existing string-only contract correctly rejected; the fixture now sends `OnDemand`. These were test-harness corrections, not passing evidence for the failed attempts.

Canonical interfaces, backend/frontend prerequisites, protocol, testing and UX owners are synchronized. README/TODO/plan/delivery ledger retain the full goal and pending frontend explicitly. No frontend source, visual design token, component, asset or rendered surface changed. Browser/Impeccable acceptance belongs to the pending pages, not these backend tests. No hosted CI, full CI suite, Real provider, installed-data reset or deployment was run.
