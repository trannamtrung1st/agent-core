import { execFileSync } from "node:child_process";

const dbPath = process.env.PLAYWRIGHT_SQLITE_PATH ?? "";

function sqlite(script: string, args: string[] = []): string {
  if (!dbPath) {
    throw new Error("PLAYWRIGHT_SQLITE_PATH is required for admin-lifecycle sqlite seeds.");
  }
  return execFileSync("python3", ["-c", script, dbPath, ...args], { encoding: "utf8" }).trim();
}

export function seedIdentityLearnedMemory(
  instanceId: string,
  sessionId: string,
  subject: string,
  content: string
): string {
  return sqlite(
    `
import sqlite3, sys, time, uuid
db, instance_id, session_id, subject, content = sys.argv[1:6]
con = sqlite3.connect(db, timeout=30)
con.execute("PRAGMA busy_timeout=8000")
profile_id = con.execute(
    """SELECT snap.ProfileId FROM SessionSnapshots snap WHERE snap.SessionId=?""",
    (session_id,),
).fetchone()
if profile_id is None or not profile_id[0]:
    raise SystemExit("profile id was not stored for session")
profile_id = profile_id[0]
now = int(time.time() * 1000)
memory_id = str(uuid.uuid4())
subject_key = subject.lower().replace(" ", "-")
con.execute(
    """INSERT INTO StructuredMemories (
        MemoryId, SessionId, Scope, Kind, Status, Subject, SubjectKey, Content,
        Source, SourceEntryIdsJson, OwnerInstanceId, OwnerProfileId,
        CreatedAtUtc, UpdatedAtUtc, ProvenanceRecordedAtUtc
    ) VALUES (?,?,?,?,?,?,?,?,?,?,?,?,?,?,?)""",
    (
        memory_id, "00000000-0000-0000-0000-000000000000", 1, 0, 0,
        subject, subject_key, content, "admin-lifecycle-seed", "[]",
        instance_id, profile_id, now, now, now,
    ),
)
con.commit()
con.close()
print(memory_id)
`,
    [instanceId, sessionId, subject, content]
  );
}

export function seedActiveTriggerRegistration(
  instanceId: string,
  sessionId: string,
  intent: string
): string {
  return sqlite(
    `
import json, sqlite3, sys, time, uuid
db, instance_id, session_id, intent = sys.argv[1:5]
con = sqlite3.connect(db, timeout=30)
con.execute("PRAGMA busy_timeout=8000")
profile_id = con.execute(
    """SELECT snap.ProfileId FROM SessionSnapshots snap WHERE snap.SessionId=?""",
    (session_id,),
).fetchone()
if profile_id is None or not profile_id[0]:
    raise SystemExit("profile id was not stored for session")
profile_id = profile_id[0]
now = int(time.time() * 1000)
registration_id = str(uuid.uuid4())
schedule_json = json.dumps({"kind": "oneShot", "atUtc": now + 86_400_000, "timeZoneId": "UTC"})
con.execute(
    """INSERT INTO TriggerRegistrations (
        RegistrationId, AgentInstanceId, ProfileId, Status, Intent, ScheduleKind, ScheduleJson,
        ScheduleRevision, NextOccurrenceAtUtc, OccurrenceCount, Revision, AuthorizationOrigin,
        SourceSessionId, CreatedAtUtc, UpdatedAtUtc
    ) VALUES (?,?,?,?,?,?,?,?,?,?,?,?,?,?,?)""",
    (
        registration_id, instance_id, profile_id, 0, intent, 0, schedule_json,
        1, now + 86_400_000, 0, 1, 0, session_id, now, now,
    ),
)
con.commit()
con.close()
print(registration_id)
`,
    [instanceId, sessionId, intent]
  );
}

/** Domain-valid completed WorkItem pinned to the session's definition version and persona. */
export function seedCompletedHistoricalWorkItem(instanceId: string, sessionId: string): string {
  return sqlite(
    `
import json, sqlite3, sys, time, uuid
db, instance_id, session_id = sys.argv[1:4]
con = sqlite3.connect(db, timeout=30)
con.execute("PRAGMA busy_timeout=8000")
owner = con.execute(
    """SELECT s.AgentInstanceId, s.AgentId, s.AgentVersion, snap.ProfileId, s.PinnedPersonaJson
       FROM Sessions s
       JOIN SessionSnapshots snap ON snap.SessionId = s.SessionId
       WHERE s.SessionId=?""",
    (session_id,),
).fetchone()
if owner is None or not owner[0] or not owner[3]:
    raise SystemExit("session owner was not stored")
if owner[0] != instance_id:
    raise SystemExit("session instance mismatch")
definition_id = owner[1]
definition_version = owner[2]
profile_id = owner[3]
persona_name = "Alex"
if owner[4]:
    persona_name = json.loads(owner[4]).get("name") or persona_name
now = int(time.time() * 1000)
work_id = str(uuid.uuid4())
source_id = str(uuid.uuid4())
result_text = "P7G historical work preserved."
con.execute(
    """INSERT INTO WorkItems (
        WorkItemId, AgentInstanceId, ProfileId, Status, Revision, AttemptCount, MaxAttempts,
        CancellationRequested, ProgressSummary, ProgressUpdatedAtUtc, ResultText, ResultCompletedAtUtc,
        SideEffectDisposition, SourceOccurrenceId, SourceKind, SourceSessionId,
        DedupeKey, ObservedAtUtc, EvidenceJson, DefinitionId, DefinitionVersion, PersonaName,
        ModelCatalogKey, ModelProviderAlias, ModelId, ModelReasoningEffort, CreatedAtUtc, UpdatedAtUtc
    ) VALUES (?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?)""",
    (
        work_id, instance_id, profile_id, 4, 2, 1, 3,
        0, "Completed before archive", now, result_text, now,
        0, source_id, 0, session_id,
        f"p7g-historical-{work_id}", now, "{}", definition_id, definition_version, persona_name,
        "scripted-alpha", "primary-llm", "scripted-alpha", "medium", now - 1000, now,
    ),
)
con.commit()
con.close()
print(work_id)
`,
    [instanceId, sessionId]
  );
}
