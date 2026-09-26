#!/usr/bin/env bash
set -euo pipefail

root="$(cd "$(dirname "$0")/.." && pwd)"
cd "$root"

compose=(docker compose)
"${compose[@]}" up --build -d

ready=0
for _ in $(seq 1 90); do
  if curl -fsS http://127.0.0.1:5080/health >/tmp/agent-core-health.json 2>/dev/null; then
    ready=1
    break
  fi
  sleep 2
done
if [[ "$ready" -ne 1 ]]; then
  echo "health did not become ready" >&2
  "${compose[@]}" logs
  exit 1
fi

python3 - <<'PY'
import json, urllib.error, urllib.parse, urllib.request

def load(path):
    return json.load(open(path))

def request(url, method="GET", data=None, headers=None, dest=None):
    req = urllib.request.Request(url, data=data, headers=headers or {}, method=method)
    try:
        with urllib.request.urlopen(req) as response:
            body = response.read()
            if dest:
                open(dest, "wb").write(body)
            return response.status, body
    except urllib.error.HTTPError as error:
        detail = error.read().decode("utf-8", "replace")
        raise SystemExit(f"{method} {url} -> {error.code}: {detail}") from error

health = load("/tmp/agent-core-health.json")
assert health["status"] == "healthy", health
assert health["profile"] == "Synthetic", health

status, issued_body = request(
    "http://127.0.0.1:5080/api/v1/local/owner-capability",
    method="POST",
    dest="/tmp/agent-core-owner.json",
)
assert status == 200, (status, issued_body)
issued = json.loads(issued_body)
token = issued["token"]
assert token, issued
owner_headers = {
    "Content-Type": "application/json",
    "X-AgentCore-Owner-Capability": token,
}

status, created_body = request(
    "http://127.0.0.1:5080/api/v2/sessions",
    method="POST",
    data=json.dumps({"agentId": "examiner", "mode": "text"}).encode(),
    headers=owner_headers,
    dest="/tmp/agent-core-session.json",
)
assert status == 201, (status, created_body)
created = json.loads(created_body)
assert created["agentId"] == "examiner", created
print("created", created["sessionId"])

status, catalog_body = request(
    "http://127.0.0.1:5080/api/v2/sessions",
    headers={"X-AgentCore-Owner-Capability": token},
    dest="/tmp/agent-core-catalog.json",
)
assert status == 200, (status, catalog_body)
catalog = json.loads(catalog_body)
assert any(item["sessionId"] == created["sessionId"] for item in catalog["items"]), catalog
print("catalog", created["sessionId"])

marker = "compose-admin-survival"
status, fork_body = request(
    "http://127.0.0.1:5080/api/v2/admin/definition-drafts/fork",
    method="POST",
    data=json.dumps(
        {"definitionId": "examiner", "sourceVersion": 1, "sourceKind": "ForkBuiltIn"}
    ).encode(),
    headers=owner_headers,
)
fork = json.loads(fork_body)
draft_id = fork["draftId"]
candidate = fork["candidate"]
candidate["systemInstructions"] = candidate.get("systemInstructions", "") + "\n" + marker
status, updated_body = request(
    f"http://127.0.0.1:5080/api/v2/admin/definition-drafts/{draft_id}",
    method="PUT",
    data=json.dumps({"expectedRevision": fork["revision"], "candidate": candidate}).encode(),
    headers=owner_headers,
)
updated = json.loads(updated_body)
resource_path = "knowledge/compose-policy.md"
resource_bytes = b"compose resource survival"
content_headers = {**owner_headers, "Content-Type": "text/plain"}
status, stored_body = request(
    f"http://127.0.0.1:5080/api/v2/admin/definition-drafts/{draft_id}/resources/content",
    method="POST",
    data=resource_bytes,
    headers=content_headers,
)
stored = json.loads(stored_body)
status, _bind_body = request(
    f"http://127.0.0.1:5080/api/v2/admin/definition-drafts/{draft_id}/resources",
    method="PUT",
    data=json.dumps(
        {
            "expectedRevision": updated["revision"],
            "resourceId": None,
            "logicalPath": resource_path,
            "kind": "Knowledge",
            "mediaType": stored["mediaType"],
            "contentSha256": stored["contentSha256"],
            "byteLength": stored["byteLength"],
        }
    ).encode(),
    headers=owner_headers,
)
status, draft_body = request(
    f"http://127.0.0.1:5080/api/v2/admin/definition-drafts/{draft_id}",
    headers=owner_headers,
)
draft_after_bind = json.loads(draft_body)
status, pub_body = request(
    f"http://127.0.0.1:5080/api/v2/admin/definition-drafts/{draft_id}/publish",
    method="POST",
    data=json.dumps({"expectedRevision": draft_after_bind["revision"]}).encode(),
    headers=owner_headers,
)
publication = json.loads(pub_body)
pub_version = publication["version"]
assert pub_version > 1, publication

status, inst_body = request(
    "http://127.0.0.1:5080/api/v2/admin/agent-instances",
    method="POST",
    data=json.dumps({"definitionId": "examiner", "version": pub_version}).encode(),
    headers=owner_headers,
)
instance = json.loads(inst_body)
assert instance.get("compatibility") is False, instance

status, managed_body = request(
    "http://127.0.0.1:5080/api/v2/sessions",
    method="POST",
    data=json.dumps({"agentInstanceId": instance["instanceId"], "mode": "text"}).encode(),
    headers=owner_headers,
    dest="/tmp/agent-core-managed-session.json",
)
managed = json.loads(managed_body)
assert managed["agentInstanceId"] == instance["instanceId"], managed
assert managed["agentVersion"] == pub_version, managed

status, events_body = request(
    "http://127.0.0.1:5080/api/v2/admin/events?limit=50",
    headers=owner_headers,
)
events_text = events_body.decode("utf-8") if isinstance(events_body, bytes) else events_body
assert marker not in events_text, "draft marker leaked into admin events"
status, pub_resources_body = request(
    f"http://127.0.0.1:5080/api/v2/admin/definitions/examiner/publications/{pub_version}/resources",
    headers=owner_headers,
)
pub_resources = json.loads(pub_resources_body)
assert any(item["logicalPath"] == resource_path for item in pub_resources["items"]), pub_resources
content_sha256 = stored["contentSha256"]
assert any(item["contentSha256"] == content_sha256 for item in pub_resources["items"]), pub_resources

agent_resource_path = f"/agent/resources/{resource_path}"
workspace_url = (
    f"http://127.0.0.1:5080/api/v2/sessions/{managed['sessionId']}/workspace/content?path="
    + urllib.parse.quote(agent_resource_path, safe="")
)
status, workspace_body = request(workspace_url, headers=owner_headers)
workspace_text = workspace_body.decode("utf-8") if isinstance(workspace_body, bytes) else workspace_body
assert resource_bytes.decode("utf-8") in workspace_text, workspace_text

admin_state = {
    "draftId": draft_id,
    "publicationVersion": pub_version,
    "instanceId": instance["instanceId"],
    "managedSessionId": managed["sessionId"],
    "marker": marker,
    "resourcePath": resource_path,
    "contentSha256": content_sha256,
    "resourceText": resource_bytes.decode("utf-8"),
}
json.dump(admin_state, open("/tmp/agent-core-admin.json", "w"))
print("admin", instance["instanceId"], managed["sessionId"], pub_version)

open("/tmp/agent-core-owner-token.txt", "w").write(token)
PY

session_id="$(python3 -c 'import json; print(json.load(open("/tmp/agent-core-session.json"))["sessionId"])')"
owner_token="$(cat /tmp/agent-core-owner-token.txt)"

cid="$("${compose[@]}" ps -aq agent-core)"
"${compose[@]}" stop agent-core
seed_dir="$(mktemp -d)"
docker cp "$cid":/data/agent-core.db "$seed_dir/agent-core.db"
docker cp "$cid":/data/agent-core.db-wal "$seed_dir/agent-core.db-wal" 2>/dev/null || true
docker cp "$cid":/data/agent-core.db-shm "$seed_dir/agent-core.db-shm" 2>/dev/null || true
python3 - "$seed_dir/agent-core.db" "$session_id" <<'PY'
import json, sqlite3, sys, time, uuid
db, session_id = sys.argv[1], sys.argv[2]
con = sqlite3.connect(db, timeout=30)
con.execute("PRAGMA wal_checkpoint(TRUNCATE)")
owner = con.execute(
    """SELECT s.AgentInstanceId, snap.ProfileId
       FROM Sessions s
       JOIN SessionSnapshots snap ON snap.SessionId = s.SessionId
       WHERE s.SessionId=?""",
    (session_id,),
).fetchone()
if owner is None or not owner[0] or not owner[1]:
    raise SystemExit("session owner was not stored")
now = int(time.time() * 1000)
completed_id = str(uuid.uuid4())
approval_work_id = str(uuid.uuid4())
approval_id = str(uuid.uuid4())
generation = str(uuid.uuid4())
far_future = now + 86_400_000 * 30
def insert_work(work_id, status, approval, result, checkpoint, created):
    con.execute(
        """INSERT INTO WorkItems (
            WorkItemId, AgentInstanceId, ProfileId, Status, Revision, AttemptCount, MaxAttempts,
            CancellationRequested, ProgressSummary, ProgressUpdatedAtUtc, CheckpointJson, ResultText, ResultCompletedAtUtc,
            SideEffectDisposition, SideEffectActionHash, SideEffectUpdatedAtUtc, CurrentApprovalId, SourceOccurrenceId, SourceKind, SourceSessionId,
            DedupeKey, ObservedAtUtc, EvidenceJson, DefinitionId, DefinitionVersion, PersonaName,
            ModelCatalogKey, ModelProviderAlias, ModelId, ModelReasoningEffort, CreatedAtUtc, UpdatedAtUtc
        ) VALUES (?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?)""",
        (
            work_id, owner[0], owner[1], status, 2, 1, 3,
            0, "Saved result" if result else "Waiting for approval", now, checkpoint, result, now if result else None,
            1 if approval else 0, approval and "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", now if approval else None, approval, str(uuid.uuid4()), 0, session_id,
            f"compose-{work_id}", created, "SECRET_EVIDENCE", "examiner", 1, "Examiner",
            "scripted-alpha", "primary-llm", "scripted-alpha", "medium", created, now,
        ),
    )
insert_work(completed_id, 4, None, "Compose result survived.", "SECRET_CHECKPOINT", now - 2000)
insert_work(approval_work_id, 2, approval_id, None, "SECRET_CHECKPOINT", now - 1000)
con.execute(
    """INSERT INTO WorkApprovals (
        ApprovalId, WorkItemId, ExecutionGeneration, CheckpointRevision, ToolName, PreparedActionJson,
        ActionHash, Preview, ExpiresAtUtc, Decision, Consumed, Revision, CreatedAtUtc
    ) VALUES (?,?,?,?,?,?,?,?,?,?,?,?,?)""",
    (
        approval_id, approval_work_id, generation, 1, "http.request", '{"body":"SECRET_BODY"}',
        "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
        "POST https://example.invalid/compose", far_future, 0, 0, 1, now,
    ),
)
con.commit()
con.execute("PRAGMA wal_checkpoint(TRUNCATE)")
con.close()
json.dump({"completedId": completed_id, "approvalWorkId": approval_work_id}, open("/tmp/agent-core-work.json", "w"))
print("seeded", completed_id, approval_work_id)
PY
docker cp "$seed_dir/agent-core.db" "$cid":/data/agent-core.db
volume="$(docker inspect -f '{{ range .Mounts }}{{ if eq .Destination "/data" }}{{ .Name }}{{ end }}{{ end }}' "$cid")"
docker run --rm --user root --entrypoint sh -v "$volume":/data agent-core:synthetic -c 'rm -f /data/agent-core.db-wal /data/agent-core.db-shm && chown 1654:1654 /data /data/agent-core.db && chmod 755 /data && chmod 644 /data/agent-core.db'
rm -rf "$seed_dir"

"${compose[@]}" up -d --force-recreate --no-deps agent-core

ready=0
for _ in $(seq 1 90); do
  if curl -fsS -H "X-AgentCore-Owner-Capability: ${owner_token}" \
    "http://127.0.0.1:5080/api/v2/sessions/${session_id}" >/tmp/agent-core-session-reopen.json 2>/dev/null; then
    ready=1
    break
  fi
  sleep 2
done
if [[ "$ready" -ne 1 ]]; then
  echo "session did not survive recreate" >&2
  "${compose[@]}" logs
  exit 1
fi

python3 - <<PY
import json, urllib.request

session_id = "${session_id}"
token = open("/tmp/agent-core-owner-token.txt").read()
body = json.load(open("/tmp/agent-core-session-reopen.json"))
assert body["sessionId"] == session_id, body
assert body["agentId"] == "examiner", body

req = urllib.request.Request(
    "http://127.0.0.1:5080/api/v2/sessions",
    headers={"X-AgentCore-Owner-Capability": token},
)
with urllib.request.urlopen(req) as response:
    catalog = json.load(response)
assert any(item["sessionId"] == session_id for item in catalog["items"]), catalog
print("survived", body["sessionId"], body["status"])

work = json.load(open("/tmp/agent-core-work.json"))
def get(url):
    req = urllib.request.Request(url, headers={"X-AgentCore-Owner-Capability": token})
    with urllib.request.urlopen(req) as response:
        payload = response.read()
        return response.status, payload.decode("utf-8")

status, listed = get(f"http://127.0.0.1:5080/api/v2/sessions/{session_id}/work-items")
assert status == 200, listed
for secret in ("SECRET_BODY", "SECRET_EVIDENCE", "SECRET_CHECKPOINT", "Compose result survived."):
    assert secret not in listed, secret
items = json.loads(listed)["items"]
assert {item["workItemId"] for item in items} >= {work["completedId"], work["approvalWorkId"]}, items
approval = next(item for item in items if item["workItemId"] == work["approvalWorkId"])
assert approval["status"] == "needsApproval", approval
assert approval["approvalPreview"] == "POST https://example.invalid/compose", approval
status, detail = get(f"http://127.0.0.1:5080/api/v2/sessions/{session_id}/work-items/{work['approvalWorkId']}")
assert status == 200, detail
for secret in ("SECRET_BODY", "SECRET_EVIDENCE", "SECRET_CHECKPOINT"):
    assert secret not in detail, secret
status, result = get(f"http://127.0.0.1:5080/api/v2/sessions/{session_id}/work-items/{work['completedId']}/result")
assert status == 200, result
assert "Compose result survived." in result, result
for secret in ("SECRET_BODY", "SECRET_EVIDENCE", "SECRET_CHECKPOINT"):
    assert secret not in result, secret
print("work survived", work["completedId"], work["approvalWorkId"])
PY

python3 - <<PY
import json, urllib.parse, urllib.request

token = open("/tmp/agent-core-owner-token.txt").read()
admin = json.load(open("/tmp/agent-core-admin.json"))
instance_id = admin["instanceId"]
managed_session_id = admin["managedSessionId"]
pub_version = admin["publicationVersion"]
marker = admin["marker"]

def get(url):
    req = urllib.request.Request(url, headers={"X-AgentCore-Owner-Capability": token})
    with urllib.request.urlopen(req) as response:
        return response.status, response.read().decode("utf-8")

status, config_json = get(
    f"http://127.0.0.1:5080/api/v2/admin/instances/{instance_id}/effective-config"
)
assert status == 200, config_json
config = json.loads(config_json)
assert config["definitionVersion"] == pub_version, config
assert config.get("compatibility") is False, config

status, managed_json = get(f"http://127.0.0.1:5080/api/v2/sessions/{managed_session_id}")
assert status == 200, managed_json
managed = json.loads(managed_json)
assert managed["sessionId"] == managed_session_id, managed
assert managed["agentInstanceId"] == instance_id, managed
assert managed["agentVersion"] == pub_version, managed

status, events = get("http://127.0.0.1:5080/api/v2/admin/events?limit=50")
assert status == 200, events
assert marker not in events, events
assert "PublicationCreated" in events or "publication.created" in events.lower(), events

status, publications = get("http://127.0.0.1:5080/api/v2/admin/definitions/examiner/publications")
assert status == 200, publications
assert f'"version":{pub_version}' in publications.replace(" ", "") or f'"version": {pub_version}' in publications, publications

resource_path = admin["resourcePath"]
content_sha256 = admin["contentSha256"]
status, pub_resources = get(
    f"http://127.0.0.1:5080/api/v2/admin/definitions/examiner/publications/{pub_version}/resources"
)
assert status == 200, pub_resources
resources = json.loads(pub_resources)
assert any(
    item["logicalPath"] == resource_path and item["contentSha256"] == content_sha256
    for item in resources["items"]
), resources

resource_text = admin["resourceText"]
agent_resource_path = f"/agent/resources/{resource_path}"
workspace_url = (
    f"http://127.0.0.1:5080/api/v2/sessions/{managed_session_id}/workspace/content?path="
    + urllib.parse.quote(agent_resource_path, safe="/")
)
status, workspace = get(workspace_url)
assert status == 200, workspace
assert resource_text in workspace, workspace
print("admin survived", instance_id, managed_session_id, pub_version, resource_path)
PY

spa="$(curl -sS -o /dev/null -w '%{http_code}' http://127.0.0.1:5080/)"
api_missing="$(curl -sS -o /dev/null -w '%{http_code}' http://127.0.0.1:5080/api/v1/missing)"
[[ "$spa" == "200" ]]
[[ "$api_missing" == "404" ]]

"${compose[@]}" down
echo "compose sqlite volume check passed"
