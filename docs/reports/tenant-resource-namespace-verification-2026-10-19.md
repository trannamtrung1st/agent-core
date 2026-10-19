# Tenant resource namespace verification — 2026-10-19

## Scope

This successor to `773499c7` converts tenant `workspace.list` and `workspace.search` for `/agent/resources`, `/agent/instance/resources` and exact descendants. Exact leaf/Office reads retain the prior source owner. The full tenant IAM proposal remains active; other virtual resources/publication adapters, autonomous/transport families and final legacy authority/schema cutover remain open.

The reader selects only admitted resources that still match the actual current scoped graph and inherited origin. Current independent Agent/Session reads qualify the observation. Raw child `resources.read` filters candidates before folder derivation, paging, scan bounds or results. Unreadable resources affect no names, counts, cursors or truncation facts. Virtual paths are literal metadata addresses; no writable filesystem fallback or directory IAM entity is introduced.

The collection variant of the existing `PreparedWorkspaceRead` uses optional canonical `ResourceMembersJson` (at most 257 key/revision witnesses, 64 KiB UTF-8). Its primary address is the actual Session observation. Every contributing child remains an independent read for executor and human reviewer, including page lookahead and scanned negative matches. An empty readable scope has no child authority and cannot reveal hidden-file existence; parent policy can still require Review. No role, later discovery or exceptional read grant is created.

Preparation, read/reviewer qualification, inbox counts, decision, the existing local consent transaction, successor result and retained checkpoint repeat source-set and authority checks. The private fingerprint binds current metadata/content digest and scan facts without saving names/snippets in approval previews. Semantic root refusal does not include mutable source witnesses, so policy relaxation cannot bypass rejection. Ordinary workspace/leaf payloads omit the collection field and retain their existing hashes and owners.

Search is bounded to 200 file scans, depth eight, 256 KiB per strict UTF-8 body scan and the caller's result bound. Binary and larger bodies are skipped; authorized filename matches remain possible. Listing derives bounded current children with an exact immediate-child cursor. Both produce untrusted observations with no mutation, publication, exhaustive-search or recipient-download claim.

## Executed checks

Initial representative integration passed six cases on memory/SQLite. The expanded namespace selection then passed **19 cases, 47s**, zero skips. Actual Synthetic model/HTTP namespace selection passed **8 cases, 45s**, including list and search pending-consent SQLite application-host recreation. Subset reruns are not additional distinct acceptance cases.

All final owning processes reached exit 0: **244 distinct focused passes, zero skips**. The representative six-case, expanded 19-case and initial eight-case API runs are subsets of these final selections, not additional distinct passes.

| Check | Selection | Result |
| --- | --- | --- |
| Resource and affected read owners | Infrastructure: resource/source/native/DI, workspace Review/search and Office reads | 171 passed, 4m6s, zero skips |
| Actual model/HTTP regression | API: namespace, leaf, workspace read/search and source publication | 48 passed, 3m48s, zero skips |
| Read contract/projection regression | Application: tenant workspace read/read-authorization contracts | 25 passed, 116ms, zero skips |

Commands use `dotnet test <project> --filter '<selection>' --nologo --no-restore -m:1 -nr:false -p:UseSharedCompilation=false`. The infrastructure build passed with no warnings/errors. EF generated `20261019075545_TenantResourceNamespaceReview` without applying it to an installed database. The installed EF tool emitted its known version warning; disposable migration/model checks determine correctness.

Exact final selections (projects are the existing Infrastructure/API/Application test projects):

```text
Infrastructure: FullyQualifiedName~Tenant_resource_|FullyQualifiedName~TenantResourceSourceContentTests|FullyQualifiedName~DefinitionResourceInfrastructureDiTests|FullyQualifiedName~Workspace_read_Review_|FullyQualifiedName~Tenant_Office_read_|FullyQualifiedName~Tenant_workspace_search_
API: FullyQualifiedName~Tenant_resource_namespace_|FullyQualifiedName~Tenant_resource_read_|FullyQualifiedName~Workspace_read_Review_model_HTTP_|FullyQualifiedName~Workspace_read_Review_pending_consent_|FullyQualifiedName~Tenant_workspace_search_Review_model_HTTP_|FullyQualifiedName~Tenant_workspace_search_pending_consent_|FullyQualifiedName~Tenant_model_workspace_Artifact
Application: FullyQualifiedName~TenantWorkspaceReadContractTests|FullyQualifiedName~TenantWorkspaceReadAuthorizationContractTests
```

## Observed workflows and boundaries

- Admitted several Instance resources, claimed the actual Run and granted only parent reads. Browsing returned an empty, untruncated view without borrowing the requester's Owner role. Granting one exact child exposed only its file/match; tenant-wide resource authority then enabled bounded paging and two filename/content matches.
- Required resource Review, saved the actual authorization tool call, prepared one exact collection request, freed the waiting claim, inspected a body-free preview, approved and dispatched one successor. Its saved checkpoint returned only the exact list/search observation; duplicate resume did not repeat the effect and no operation grant existed.
- A human with parent/approval reads and only one child read could neither see nor count the all-member request. Granting every child made it visible. Removing a contributing non-match hid it from Owner counts too. Changing a previously nonmatching file invalidated its saved search. A newly visible set invalidated a retained directory page.
- Rejected list/search consent remained suppressed after policy relaxation in both stores. SQLite rejected malformed collection/mixed/leaf/grant/scope shapes; Core rejected invalid, duplicated and negative-revision members. Failed CHECK narrowing preserved the exact saved request.
- Inherited and empty readable namespaces passed on both stores. Session policy could require Review even with no readable children. Oversized content was not opened, binary content produced no text snippet, and revocation during the first byte read withheld all results.
- Actual model/HTTP flows created authenticated bound sources before admission, offered the existing tools/authorization schema, inspected Organization and conversation approval projections, approved/rejected via HTTP and observed the exact successor list/search result. Both namespace tools retained pending consent across orderly SQLite application-host disposal/recreation using native durable resource bytes. No outbound provider attempts occurred.

Documentation validation passed for 11 changed Markdown files, 1,548 local links/fragments, balanced code fences and 27 complete JSON examples. `git diff --check` passed. All owned build/test/EF handles reached terminal state before staging.

## Limits

This change adds no frontend composition, CSS, theme or design sidecar. HTTP checks prove the shared preview data contract, not a fresh resource-specific browser presentation/interaction pass. Existing UI evidence remains scoped to its original report/commit. No physical application process-kill, Windows, live-provider, production multi-instance, full-suite or hosted CI acceptance is claimed. All database setup/recovery checks use disposable fixtures, with no installed-data reset or migration.
