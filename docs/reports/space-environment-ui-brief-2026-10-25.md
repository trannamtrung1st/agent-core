# Space and Environment UI direction and state coverage

This is the implementation brief for the 2026-10-25 proposal, not proposal or milestone acceptance. Canonical behavior belongs to [Frontend](../13-frontend-implementation-spec.md), [Protocol](../14-api-and-realtime-protocol.md) and the proposal. The [implementation ledger](space-environment-implementation-2026-10-25.md) owns observed evidence and remaining gaps.

The surface extends the established dark Ant Design Operate UI. Its primary tasks are choosing context for future work, managing shared knowledge/files, setting membership instructions/availability and administering separately protected resources. Existing AdminShell, collection toolbar, table/file preview, responsive Drawer, confirmation and unsaved-draft owners supply the composition. No new UI kit or visual identity is introduced.

| Surface | Information and actions | Required boundaries and recovery |
| --- | --- | --- |
| Spaces collection | Readable Private/Shared rows, name/description/kind, explicit Shared create, bounded pages | Empty/search/loading/reload; scoped reads; no fabricated Private create |
| Space overview | Context, owner link, gate, edit and allowed Shared delete | Private owner/deletion protection; future-Run explanation; stale draft and uncertain-save recovery |
| Members | Responsibility/instructions, inherited exclusions, protected additions, effective preview | Membership is not IAM; metadata permissions; stable aliases; read-only and actual source changes |
| Workspace | Exact Space `/home`, shared upload/list/preview/download/delete; bounded immutable knowledge/resource snapshots registered from an authorized home file | Separate read/write/delete; no listing for write-only; no paths on read loss; existing live-file and unknown-effect handling |
| Environments/variables | Saved profiles, typed metadata, explicit Plain lookup, safe reference choices | Gate-off management; no protected read-back; read-only/resolve denial; exact revision and draft recovery |
| Secrets | Metadata/status, create/replace, scoped access | Write-only values; current independent manage/use/read; no generic secure-use claim |
| Scoped access | Space context/home-file separation, human/agent roles and source assignments | Current scope authority; human-only filtering; inherited source revoke check; exact confirmation and CAS |
| Chat | Per-Session current Space, optional Environment, pending/stale state | Safe next-turn apply; no response-refresh draft loss; gate-off hidden selector; explicit stale clear; same history/identity |
| Automation | Exact context pins beside execution destination | No implicit retarget; ExistingSession agreement; distinct completion destination; missing pin recovery |

Use the existing 16px section and 8px compact gaps, responsive footer wrapping and native keyboard/focus behavior. Small link and selected-tab text use the existing lighter blue at the shared AntD theme owner; filled primary colors remain native. Verify content/actions at 320/390/768/1024/1440, keyboard editing/discard, known conflicts, original-human read loss, gate-off and protected-value denial. Screenshots supplement actual interactions and rendered measurements.

The current implementation has Synthetic browser evidence for the main management, Chat, file, Secret, pin, scope and read-only journeys. Source-qualified resources, protected consumers and conflict/unknown-outcome recovery have separate executed evidence in the implementation ledger; no live-provider, hardware or optional CI acceptance is implied.

Space source UI extension: keep immutable snapshots below the existing Workspace file browser, with explicit “Register snapshot”, source path, logical path, supported kind, revision and refresh/delete. Membership additions select these actual Space source IDs. Reuse the existing draft guard, editor recovery and scoped access composition. Snapshot availability and `resources.read` remain separate; home file changes never silently refresh saved content. Inspector actions use current per-source permissions; hidden metadata has no selector entry. This extension was recorded before component coding. Its upload/register/membership/access/delete-refusal/refresh and responsive browser journeys are recorded in the implementation ledger.
