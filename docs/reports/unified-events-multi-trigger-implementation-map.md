# Unified Events and child-trigger cutover map

Baseline: `38a610071a7a472506d98ab466ee45503f7a9466` (2026-10-10). The approved local proposal authorizes this successor enhancement; it does not reopen frozen milestones or P10/P11. Two pre-existing untracked BrowserPrivacy files are outside this change.

The canonical Automation owns a child collection. Schedule callers construct one child; event fan-out enumerates enabled children by typed source. Admin and agent authoring share atomic parent CAS and stable child revisions. Source eligibility governs admission separately from disabled authoring.

SQLite migrates each existing single trigger to a child whose TriggerId equals its existing AutomationId. This deterministic, collision-free mapping within the separate child identity namespace preserves prior delivery keys and occurrence dedupe strings. New children use generated UUIDs. Core/Webhook delivery uniqueness and recovery cursors use TriggerId. Snapshot JSON, buckets and historical occurrences gain exact identity without rewriting filter decisions or retry state. Historical evidence already at its 8 KiB ceiling stays byte-for-byte unchanged; owned Run projection still recovers its exact source from the archived summary/context. Retired parent source/filter/schedule fields are removed. Historical migration files remain immutable.

Global catalog reads combine immutable code-owned Built-in descriptors and managed Webhook metadata without credentials or private occurrences. Built-in details expose only safe examples/schema; selected Instance activity remains owner scoped. Webhook payload causation remains untrusted evidence, with server-derived trigger lineage.

Verification order: focused mixed-source admission and populated SQLite upgrade/reopen; frontend draft/disabled save tests; actual Synthetic browser authoring, nested picker and responsive inspection; full applicable local suites and isolated Compose survival. CI is explicitly deferred by the user. Verification outcomes will be recorded separately from this implementation map.
