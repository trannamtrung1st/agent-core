---
name: persistence
description: "Maintain Agent Core IMemoryStore, EF Core/SQLite mappings, snapshot revisions, durable receipts, migrations and recovery."
---

# Persistence

Read model/recovery rules in [Persistence and Configuration](../../../docs/15-persistence-and-configuration.md), ports in [Backend Interfaces](../../../docs/04-backend-interfaces.md) and the affected [milestone](../../../docs/18-implementation-plan.md).

- Keep EF Core 10/SQLite behind IMemoryStore in Infrastructure. Use per-operation DbContext, explicit UTC millisecond/GUID conversions, foreign keys, WAL and the specified busy timeout. Never share a context among workers.
- Preserve atomic Session/Snapshot/entry upserts, expectedRevision checks, same-content retry idempotency and dedupe. Do not overwrite stale revisions or delete unseen entries. InMemoryMemoryStore enforces equivalent semantics.
- Serialize writes per runtime while the mailbox remains responsive. Freeze snapshots, acknowledge only saved revisions and coalesce checkpoints without overwriting newer heard/received or terminal state. A 1-second streaming checkpoint upserts changed/current entries and snapshot metadata; do not rewrite every historical immutable row.
- Pin identity versions; distinguish generated text from received and heard prefixes. Public history and future context follow their respective conservative projections; late receipts cannot revise superseded responses.
- Persist semantic continuity, never PCM, credentials, provider bodies, tasks/timers/streams. Recover unfinished sessions/entries as specified without replaying output or initiating speech; Ended remains terminal.
- Introduce migrations at the durable-storage milestone. Use temporary SQLite databases for transaction/conflict/WAL/reopen checks via [testing](../testing/SKILL.md), not EF's nonrelational in-memory provider.
- Read [operations](../operations/SKILL.md) for startup migrations, backup/restore or volume changes.
