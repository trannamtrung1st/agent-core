import path from "node:path";
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

export function seedActiveAutomation(
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
    """INSERT INTO Automations (
        AutomationId, AgentInstanceId, ProfileId, Status, Name, Instructions, TriggerKind, ScheduleJson,
        TriggerRevision, NextOccurrenceAtUtc, OccurrenceCount, Revision, AuthorizationOrigin,
        SourceSessionId, CreatedAtUtc, UpdatedAtUtc, RequiresVision
    ) VALUES (?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?)""",
    (
        registration_id, instance_id, profile_id, 0, intent, intent, 0, schedule_json,
        1, now + 86_400_000, 0, 1, 0, session_id, now, now, 0,
    ),
)
con.commit()
con.close()
print(registration_id)
`,
    [instanceId, sessionId, intent]
  );
}

/** Current-schema background Session and historical AgentRun. */
export function seedCompletedHistoricalAgentRun(instanceId: string, sessionId: string): string {
  void instanceId;
  return execFileSync('python3', [path.resolve('e2e/support/seed-agent-run.py'), dbPath, sessionId, 'completed'], { encoding: 'utf8' }).trim();
}
