# Skill identity and forms verification

## Scope

New Instance Skills accept an optional readable ID; empty IDs generate from the name with an owner-local collision suffix. IDs remain fixed after creation. Definition and Instance IDs are unique only within their respective owners. Origin-qualified execution keys remain separate. Existing UUID identities and pinned execution catalogs remain readable without rewriting IDs.

SQLite migration `20261007171704_InstanceSkillOwnerIdentity` changes the local Skill primary key to `(AgentInstanceId, SkillId)`. Tests migrate an existing cutover database, preserve its content/revisions/UUID identity, and then reuse that ID under another owner. In-memory and SQLite parity tests check duplicate rejection and independent update/delete across owners.

Definition authoring and Instance authoring reuse the same Ant Design Skill drawer. The table shows the ID; inline format/duplicate feedback blocks invalid saves. Details use a read-only drawer. Unique control IDs and retained closing presentation preserve labels and prevent mode/content changes during dismissal.

## Runtime evidence

Playwright MCP against disposable Synthetic SQLite on API 5120 / Vite 5200 created `review-evidence`, rejected a duplicate in that Instance, then saved the empty ID as `review-evidence-2`. Reload/Edit preserved `review-evidence` as read-only. Earlier Definition authoring rejected duplicate `review` and saved corrected `review.second`. Screens were checked at 1440px and 390px; the mobile drawer filled the viewport with no page overflow. No console errors or failed application requests occurred in the completed flow; earlier connection-refused navigation was before test hosts started.

`PLAYWRIGHT_API_PORT=5120 PLAYWRIGHT_WEB_PORT=5200 PLAYWRIGHT_FAITHFUL_MANUAL=1 PLAYWRIGHT_SQLITE_PATH=/private/tmp/skills-readable-final.sqlite pnpm test:e2e e2e/instance-skills.spec.ts e2e/p8-skill-journey.spec.ts e2e/p8.5-messaging-skill-journey.spec.ts --project=synthetic`: **6 passed**. These cover self-management authorization, next-turn loading, customization, upgrade/rollback, drawer failure retention, Definition publication and messaging/load behavior.

## Checks

- Targeted API Skill journey: **3 passed**.
- Targeted Application runtime/Skill tests: **24 passed**.
- Targeted persistence/startup/pin tests: **13 passed**, including the migration and owner-scoped parity checks.
- `pnpm run build`: passed; existing SignalR annotation and bundle-size warnings remain.
- `dotnet ef migrations has-pending-model-changes --project src/AgentCore.Infrastructure --no-build`: no pending model changes; installed EF tools 10.0.5 report their existing version warning against runtime 10.0.12.

- `dotnet test AgentCore.sln --nologo`: **2,658 passed**, **13 skipped**, zero failures (Domain 151, Application 1,306, Infrastructure 826, API 371, Order Events 4). After the final shared ASCII generation correction, Domain reran with **152 passed** and API Skill integration reran with **3 passed**, including Unicode name generation.
- Focused frontend coverage: **47 tests passed** across serial runs: candidate/editor 34, Instance Skills 7, shared drawer 4, published-version viewer 2. The earlier concurrent run had three 30-second test timeouts; all affected files passed isolated with the same timeouts and assertions. Ant Design measurement and React act warnings occurred in jsdom; completed browser flows had no console errors.
- The final production build passed after the ASCII generation correction. Browser publication coverage exercises the shared read-only Skills tab, nested detail drawer, Escape/focus return and 1440/768/390px layouts.
- Documentation check: seven Markdown files, 101 local links/anchors and 13 complete JSON blocks passed; staged whitespace check passed.

The final commit excludes the other task's Event source changes. This follow-up does not change the historical Instance Skills milestone freeze SHA or claim a new hosted CI freeze.

## Dedicated Skill ID column review (2026-10-08)

Definition draft, published-version and Agent Instance tables expose Skill ID in a separate column at every viewport width. Existing local scrolling and wrapping contain long readable IDs and legacy UUIDs. The frontend specification now names that column explicitly.

Playwright MCP verified 1440/768/390px layouts, long-ID wrapping, Instance search by ID, read-only saved IDs during editing, published Skill inspection and Escape returning to the version drawer. The completed flows had no console errors or failed application requests. The repeat Synthetic browser command above passed all **six** journeys against disposable SQLite; the unchanged column implementation also passed the prior **nine** focused unit tests and production build. No functional defect or additional code correction was found in this review.
