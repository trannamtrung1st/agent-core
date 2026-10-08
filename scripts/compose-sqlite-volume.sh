#!/usr/bin/env bash
set -euo pipefail

root="$(cd "$(dirname "$0")/.." && pwd)"
cd "$root"

export AGENTCORE_COMPOSE_PORT="${AGENTCORE_COMPOSE_PORT:-5080}"
compose=(docker compose)
"${compose[@]}" up --build -d

ready=0
for _ in $(seq 1 90); do
  if curl -fsS http://127.0.0.1:${AGENTCORE_COMPOSE_PORT}/health >/tmp/agent-core-health.json 2>/dev/null; then
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
import os
port = os.environ["AGENTCORE_COMPOSE_PORT"]
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
    f"http://127.0.0.1:{port}/api/v1/local/owner-capability",
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

_, initial_body = request(f"http://127.0.0.1:{port}/api/v2/admin/agent-instances", method="POST",
    data=json.dumps({"definitionId": "examiner", "version": 1}).encode(), headers=owner_headers)
initial_instance = json.loads(initial_body)["instanceId"]

status, created_body = request(
    f"http://127.0.0.1:{port}/api/v2/sessions",
    method="POST",
    data=json.dumps({"agentInstanceId": initial_instance, "mode": "text"}).encode(),
    headers=owner_headers,
    dest="/tmp/agent-core-session.json",
)
assert status == 201, (status, created_body)
created = json.loads(created_body)
assert created["agentId"] == "examiner", created
print("created", created["sessionId"])

status, catalog_body = request(
    f"http://127.0.0.1:{port}/api/v2/sessions",
    headers={"X-AgentCore-Owner-Capability": token},
    dest="/tmp/agent-core-catalog.json",
)
assert status == 200, (status, catalog_body)
catalog = json.loads(catalog_body)
assert any(item["sessionId"] == created["sessionId"] for item in catalog["items"]), catalog
print("catalog", created["sessionId"])

marker = "compose-admin-survival"
status, fork_body = request(
    f"http://127.0.0.1:{port}/api/v2/admin/definition-drafts/fork",
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
    f"http://127.0.0.1:{port}/api/v2/admin/definition-drafts/{draft_id}",
    method="PUT",
    data=json.dumps({"expectedRevision": fork["revision"], "candidate": candidate}).encode(),
    headers=owner_headers,
)
updated = json.loads(updated_body)
resource_path = "knowledge/compose-policy.md"
resource_bytes = b"compose resource survival"
content_headers = {**owner_headers, "Content-Type": "text/plain"}
status, stored_body = request(
    f"http://127.0.0.1:{port}/api/v2/admin/definition-drafts/{draft_id}/resources/content",
    method="POST",
    data=resource_bytes,
    headers=content_headers,
)
stored = json.loads(stored_body)
status, _bind_body = request(
    f"http://127.0.0.1:{port}/api/v2/admin/definition-drafts/{draft_id}/resources",
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
    f"http://127.0.0.1:{port}/api/v2/admin/definition-drafts/{draft_id}",
    headers=owner_headers,
)
draft_after_bind = json.loads(draft_body)
status, pub_body = request(
    f"http://127.0.0.1:{port}/api/v2/admin/definition-drafts/{draft_id}/publish",
    method="POST",
    data=json.dumps({"expectedRevision": draft_after_bind["revision"]}).encode(),
    headers=owner_headers,
)
publication = json.loads(pub_body)
pub_version = publication["version"]
assert pub_version > 1, publication

status, inst_body = request(
    f"http://127.0.0.1:{port}/api/v2/admin/agent-instances",
    method="POST",
    data=json.dumps({"definitionId": "examiner", "version": pub_version}).encode(),
    headers=owner_headers,
)
instance = json.loads(inst_body)
assert "compatibility" not in instance, instance

status, managed_body = request(
    f"http://127.0.0.1:{port}/api/v2/sessions",
    method="POST",
    data=json.dumps({"agentInstanceId": instance["instanceId"], "mode": "text"}).encode(),
    headers=owner_headers,
    dest="/tmp/agent-core-managed-session.json",
)
managed = json.loads(managed_body)
assert managed["agentInstanceId"] == instance["instanceId"], managed
assert managed["agentVersion"] == pub_version, managed

status, events_body = request(
    f"http://127.0.0.1:{port}/api/v2/admin/events?limit=50",
    headers=owner_headers,
)
events_text = events_body.decode("utf-8") if isinstance(events_body, bytes) else events_body
assert marker not in events_text, "draft marker leaked into admin events"
status, pub_resources_body = request(
    f"http://127.0.0.1:{port}/api/v2/admin/definitions/examiner/publications/{pub_version}/resources",
    headers=owner_headers,
)
pub_resources = json.loads(pub_resources_body)
assert any(item["logicalPath"] == resource_path for item in pub_resources["items"]), pub_resources
content_sha256 = stored["contentSha256"]
assert any(item["contentSha256"] == content_sha256 for item in pub_resources["items"]), pub_resources

agent_resource_path = f"/agent/resources/{resource_path}"
workspace_url = (
    f"http://127.0.0.1:{port}/api/v2/sessions/{managed['sessionId']}/workspace/content?path="
    + urllib.parse.quote(agent_resource_path, safe="")
)
status, workspace_body = request(workspace_url, headers=owner_headers)
workspace_text = workspace_body.decode("utf-8") if isinstance(workspace_body, bytes) else workspace_body
assert resource_bytes.decode("utf-8") in workspace_text, workspace_text

# New-mode binary home writes and scratch retain their distinct lifetimes after recreation.
status, v2_owner_body = request(f"http://127.0.0.1:{port}/api/v2/admin/agent-instances", method="POST",
    data=json.dumps({"definitionId": "general-assistant", "version": 16}).encode(), headers=owner_headers)
v2_owner = json.loads(v2_owner_body)["instanceId"]
skills_url = f"http://127.0.0.1:{port}/api/v2/admin/agent-instances/{v2_owner}/skills"
_, skill_body = request(skills_url, headers=owner_headers)
source = next(row for row in json.loads(skill_body) if row["origin"] == "Definition")
_, copied_body = request(skills_url + "/" + urllib.parse.quote(source["key"], safe="") + "/customize", method="POST",
    data=json.dumps({"expectedRevision": source["revision"]}).encode(), headers=owner_headers)
customized = json.loads(copied_body)
copied_skill = customized["instanceSkill"]
assert customized["definitionSkill"] == {"key": source["key"], "enabled": False, "revision": source["revision"] + 1}
assert copied_skill["origin"] == "Instance" and copied_skill["sourceDefinitionSkillId"] == "browser.record.lookup"
_, accounting_body = request(skills_url, method="POST", headers=owner_headers,
    data=json.dumps({"name": "Accounting", "description": "Check totals", "procedure": "COMPOSE_ACCOUNTING_PROCEDURE",
        "projection": "OnDemand", "enabled": True, "requiredCapabilities": ["workspace.read"]}).encode())
accounting = json.loads(accounting_body)
_, skill_body = request(skills_url, headers=owner_headers)
skill_rows = json.loads(skill_body)
assert not next(row for row in skill_rows if row["key"] == source["key"])["enabled"]
json.dump({"instanceId": v2_owner, "rows": skill_rows, "accounting": accounting, "copy": copied_skill}, open("/tmp/agent-core-skills-survival.json", "w"))
status, v2_session_body = request(f"http://127.0.0.1:{port}/api/v2/sessions", method="POST",
    data=json.dumps({"agentInstanceId": v2_owner, "mode": "text"}).encode(), headers=owner_headers)
v2_session = json.loads(v2_session_body)["sessionId"]
v2_bytes = bytes([0, 255, 128, 13, 10])
for v2_path in ("/home/binary.dat", "/working/binary.dat"):
    status, _ = request(f"http://127.0.0.1:{port}/api/v2/sessions/{v2_session}/workspace/content?path={v2_path}",
        method="PUT", data=v2_bytes, headers={**owner_headers, "Content-Type": "application/octet-stream"})
    assert status == 204, status
req = urllib.request.Request(f"http://127.0.0.1:{port}/api/v2/sessions/{v2_session}/workspace/content?path=/home/binary.dat",
    method="PUT", data=b"unguarded", headers={**owner_headers, "Content-Type": "application/octet-stream"})
try:
    urllib.request.urlopen(req)
    raise AssertionError("unguarded durable replacement succeeded")
except urllib.error.HTTPError as error: assert error.code == 409
json.dump({"instanceId": v2_owner, "sessionId": v2_session}, open("/tmp/agent-core-v2-survival.json", "w"))

# Durable home outlives its originating Session and container recreation.
status, home_source_body = request(
    f"http://127.0.0.1:{port}/api/v2/sessions", method="POST",
    data=json.dumps({"agentInstanceId": instance["instanceId"], "mode": "text"}).encode(), headers=owner_headers)
home_source = json.loads(home_source_body)["sessionId"]
home_bytes = b"compose durable workspace exact bytes\r\n"
request(f"http://127.0.0.1:{port}/api/v2/sessions/{home_source}/workspace/content?path=/home/reports/report.txt",
    method="PUT", data=home_bytes, headers=owner_headers)
_, page_body = request(f"http://127.0.0.1:{port}/api/v2/agent-instances/{instance['instanceId']}/workspace", headers=owner_headers)
home_item = next(item for item in json.loads(page_body)["items"] if item["logicalPath"] == "/home/reports/report.txt")
assert home_item["byteSize"] == len(home_bytes), home_item
request(f"http://127.0.0.1:{port}/api/v2/sessions/{home_source}", method="DELETE", headers=owner_headers)
with open("/tmp/agent-core-home-survival.json", "w") as target:
    json.dump({"instanceId": instance["instanceId"], "item": home_item, "text": home_bytes.decode()}, target)

# Ordinary Automation survives recreation; obsolete cadence resources are absent.
status, profile_body = request(f"http://127.0.0.1:{port}/api/v2/profile", headers=owner_headers)
profile = json.loads(profile_body)
request(f"http://127.0.0.1:{port}/api/v2/profile", method="PATCH", headers=owner_headers,
    data=json.dumps({"expectedRevision": profile["revision"], "values": {"timeZone": "UTC"}}).encode())
from datetime import datetime, timedelta, timezone
status, automation_body = request(f"http://127.0.0.1:{port}/api/v2/admin/agent-instances/{v2_owner}/automations",
    method="POST", headers=owner_headers, data=json.dumps({"expectedRevision": 0, "enabled": True,
        "name": "Review completed work", "instructions": "Review observable completed work; do nothing when no change is useful.",
        "trigger": {"kind": "schedule", "schedule": {"kind": "fixedInterval", "interval": 3600,
            "anchorAtUtc": (datetime.now(timezone.utc) + timedelta(days=1)).isoformat()}}}).encode())
assert status == 200, status
json.dump({"instanceId": v2_owner, "automation": json.loads(automation_body)}, open("/tmp/agent-core-unified-automation.json", "w"))

# Reusable credentials and resource grants survive the same volume recreation.
_, credential_body = request(f"http://127.0.0.1:{port}/api/v2/admin/credentials", method="POST",
    data=json.dumps({"displayName": "Compose shared credential", "kind": "Password",
        "metadata": {"username": "compose@example.test"}, "allowedOrigins": ["https://store.example.test"],
        "protectedValue": "compose-private-value-7b3f"}).encode(), headers=owner_headers)
credential = json.loads(credential_body)
assert "compose-private-value-7b3f" not in credential_body.decode()
_, binding_body = request(f"http://127.0.0.1:{port}/api/v2/admin/agent-instances/{instance['instanceId']}/credential-bindings",
    method="POST", data=json.dumps({"credentialId": credential["credentialId"], "reference": "store-admin",
        "expectedInstanceRevision": instance["revision"]}).encode(), headers=owner_headers)
json.dump({"credentialId": credential["credentialId"], "instanceId": instance["instanceId"],
    "bindingId": json.loads(binding_body)["bindingId"]}, open("/tmp/agent-core-credential-survival.json", "w"))

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
credential_state = json.load(open("/tmp/agent-core-credential-survival.json"))
protected = con.execute("SELECT ProtectedPayload FROM Credentials WHERE CredentialId=?", (credential_state["credentialId"],)).fetchone()
assert protected and "compose-private-value-7b3f" not in str(protected), "Credential payload must be protected"
assert b"compose-private-value-7b3f" not in open(db, "rb").read(), "SQLite contained plaintext"
assert not con.execute("SELECT 1 FROM sqlite_master WHERE type='table' AND name='ApplicationConnections'").fetchone()
credential_state["protectedPayload"] = protected[0]
json.dump(credential_state, open("/tmp/agent-core-credential-survival.json", "w"))
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
completed_id, approval_run_id = str(uuid.uuid4()), str(uuid.uuid4())
approval_id, generation = str(uuid.uuid4()), str(uuid.uuid4())
far_future = now + 86_400_000 * 30
from datetime import datetime, timezone
import hashlib
def utc(ms): return datetime.fromtimestamp(ms / 1000, timezone.utc).isoformat()
def insert_row(table, values):
    keys = list(values)
    con.execute(f'INSERT INTO "{table}" (' + ','.join('"' + k + '"' for k in keys) + ') VALUES (' + ','.join('?' for _ in keys) + ')', tuple(values.values()))
def entry(entry_id, sequence, role, text, response=None):
    insert_row("ConversationEntries", dict(EntryId=entry_id, SessionId=session_id, EntrySequence=sequence,
        Role=role, Text=text, ResponseId=response, Status="Completed", DeliveryMode="Text", HeardTextEndExclusive=0,
        ReceivedTextEndExclusive=len(text), CreatedAtUtc=now))
def insert_run(run_id, status, sequence):
    activation_id, input_id, response_id = str(uuid.uuid4()), str(uuid.uuid4()), str(uuid.uuid4())
    entry(input_id, sequence, "User", "Compose persistence fixture")
    activation = dict(activationId=activation_id, sessionId=session_id, kind=0, sourceEntryIds=[input_id],
        sourceEventId=input_id, triggerOccurrenceId=None, sourceSessionId=None, sourceAgentRunId=None,
        dedupeKey="compose:" + run_id, admittedAtUtc=utc(now), evidenceJson='{"fixture":"SECRET_EVIDENCE"}')
    admission = dict(activation=activation, definitionId="examiner", definitionVersion=1,
        pinnedPersona=json.loads(con.execute("SELECT DefinitionJson FROM Sessions WHERE SessionId=?", (session_id,)).fetchone()[0])["identity"], responseId=response_id)
    payload = dict(admission=admission, pinnedModel=dict(catalogKey="scripted-alpha", providerAlias="primary-llm", modelId="scripted-alpha", reasoningEffort="medium"),
        attemptCount=1, maxAttempts=3, claim=None, cancellationRequested=False, cancellationRequestedAtUtc=None, knownEffectSummary=None,
        progress=None, checkpoint=dict(payloadJson='{"fixture":"SECRET_CHECKPOINT"}', stepCount=1, outputBytes=0, remainingOverallBudgetMs=180000),
        result=None, failure=None, sideEffect=dict(disposition=0, toolCallId=None, actionHash=None, updatedAtUtc=None), approval=None,
        pinnedSkillCatalog=[], activeSkillKeys=[], skillLoadCount=0, loadedCapabilityIds=[], capabilityLoadCount=0)
    if status == 4:
        outcome_id = str(uuid.uuid4())
        entry(outcome_id, sequence + 1, "Assistant", "Compose result survived.", response_id)
        payload["result"] = dict(text="Compose result survived.", completedAtUtc=utc(now), attentionRequired=False, outcomeKind=0, outcomeEntryId=outcome_id)
    else:
        action_hash = "b" * 64
        payload["sideEffect"] = dict(disposition=1, toolCallId="compose-http", actionHash=action_hash, updatedAtUtc=utc(now))
        payload["approval"] = dict(approvalId=approval_id, agentRunId=run_id, executionGeneration=generation, checkpointRevision=1,
            toolName="http.request", preparedActionJson='{"body":"SECRET_BODY"}', actionHash=action_hash,
            preview="POST https://example.invalid/compose", expiresAtUtc=utc(far_future), decision=0, decidedAtUtc=None,
            consumed=False, revision=1, createdAtUtc=utc(now))
    insert_row("Activations", dict(ActivationId=activation_id, SessionId=session_id, AgentInstanceId=owner[0], ProfileId=owner[1],
        DedupeKey=activation["dedupeKey"], BackgroundSourceKey=None, AdmissionHash=hashlib.sha256(run_id.encode()).hexdigest(),
        PayloadJson=json.dumps(activation), AdmittedAtUtc=now))
    insert_row("ActivationSourceEntries", dict(ActivationId=activation_id, EntryId=input_id, SessionId=session_id, Ordinal=0))
    insert_row("AgentRuns", dict(AgentRunId=run_id, ActivationId=activation_id, SessionId=session_id, AgentInstanceId=owner[0], ProfileId=owner[1],
        Status=status, Revision=2, NextRetryAtUtc=None, LeaseExpiresAtUtc=None, ApprovalExpiresAtUtc=far_future if status == 2 else None,
        PayloadJson=json.dumps(payload), CreatedAtUtc=now, UpdatedAtUtc=now))
insert_run(completed_id, 4, 1)
insert_run(approval_run_id, 2, 3)
con.execute("UPDATE SessionSnapshots SET LastEntrySequence=3 WHERE SessionId=?", (session_id,))
assert not con.execute("SELECT 1 FROM sqlite_master WHERE name IN ('WorkItems','WorkApprovals','WorkCaptures','WorkAttentionAlerts','ConversationTurnExecutions')").fetchone()
assert not con.execute("PRAGMA foreign_key_check").fetchall()
con.commit()
con.execute("PRAGMA wal_checkpoint(TRUNCATE)")
con.close()
json.dump({"completedId": completed_id, "approvalRunId": approval_run_id}, open("/tmp/agent-core-runs.json", "w"))
print("seeded AgentRuns", completed_id, approval_run_id)
PY
docker cp "$seed_dir/agent-core.db" "$cid":/data/agent-core.db
volume="$(docker inspect -f '{{ range .Mounts }}{{ if eq .Destination "/data" }}{{ .Name }}{{ end }}{{ end }}' "$cid")"
docker run --rm --user root --entrypoint sh -v "$volume":/data agent-core:synthetic -c 'rm -f /data/agent-core.db-wal /data/agent-core.db-shm && chown 1654:1654 /data /data/agent-core.db && chmod 755 /data && chmod 644 /data/agent-core.db'
rm -rf "$seed_dir"

docker cp "$cid":/data/credential-protection-keys "$seed_dir-keys"
find "$seed_dir-keys" -type f -name '*.xml' -exec shasum -a 256 {} \; | awk '{print $1}' | sort > /tmp/agent-core-credential-key-hashes.txt
[[ -s /tmp/agent-core-credential-key-hashes.txt ]]
rm -rf "$seed_dir-keys"

"${compose[@]}" up -d --force-recreate --no-deps agent-core

ready=0
for _ in $(seq 1 90); do
  if curl -fsS -H "X-AgentCore-Owner-Capability: ${owner_token}" \
    "http://127.0.0.1:${AGENTCORE_COMPOSE_PORT}/api/v2/sessions/${session_id}" >/tmp/agent-core-session-reopen.json 2>/dev/null; then
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
import os
port = os.environ["AGENTCORE_COMPOSE_PORT"]
import json, urllib.request

session_id = "${session_id}"
token = open("/tmp/agent-core-owner-token.txt").read()
body = json.load(open("/tmp/agent-core-session-reopen.json"))
assert body["sessionId"] == session_id, body
assert body["agentId"] == "examiner", body

req = urllib.request.Request(
    f"http://127.0.0.1:{port}/api/v2/sessions",
    headers={"X-AgentCore-Owner-Capability": token},
)
with urllib.request.urlopen(req) as response:
    catalog = json.load(response)
assert any(item["sessionId"] == session_id for item in catalog["items"]), catalog
print("survived", body["sessionId"], body["status"])

runs = json.load(open("/tmp/agent-core-runs.json"))
def get(url):
    req = urllib.request.Request(url, headers={"X-AgentCore-Owner-Capability": token})
    with urllib.request.urlopen(req) as response:
        return response.status, response.read().decode("utf-8")
status, listed = get(f"http://127.0.0.1:{port}/api/v2/sessions/{session_id}/agent-runs")
assert status == 200, listed
for secret in ("SECRET_BODY", "SECRET_EVIDENCE", "SECRET_CHECKPOINT"):
    assert secret not in listed, secret
items = json.loads(listed)["items"]
assert {item["agentRunId"] for item in items} >= {runs["completedId"], runs["approvalRunId"]}, items
approval = next(item for item in items if item["agentRunId"] == runs["approvalRunId"])
assert approval["status"] == "needsApproval", approval
assert approval["approval"]["preview"] == "POST https://example.invalid/compose", approval
status, detail = get(f"http://127.0.0.1:{port}/api/v2/sessions/{session_id}/agent-runs/{runs['approvalRunId']}")
assert status == 200, detail
for secret in ("SECRET_BODY", "SECRET_EVIDENCE", "SECRET_CHECKPOINT"):
    assert secret not in detail, secret
status, result = get(f"http://127.0.0.1:{port}/api/v2/sessions/{session_id}/agent-runs/{runs['completedId']}")
assert status == 200, result
assert json.loads(result)["outcome"]["summary"] == "Compose result survived.", result
for secret in ("SECRET_BODY", "SECRET_EVIDENCE", "SECRET_CHECKPOINT"):
    assert secret not in result, secret
print("AgentRuns survived", runs["completedId"], runs["approvalRunId"])
PY

python3 - <<PY
import os
port = os.environ["AGENTCORE_COMPOSE_PORT"]
import json, urllib.error, urllib.parse, urllib.request

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
    f"http://127.0.0.1:{port}/api/v2/admin/instances/{instance_id}/effective-config"
)
assert status == 200, config_json
config = json.loads(config_json)
assert config["definitionVersion"] == pub_version, config
assert "compatibility" not in config, config

status, managed_json = get(f"http://127.0.0.1:{port}/api/v2/sessions/{managed_session_id}")
assert status == 200, managed_json
managed = json.loads(managed_json)
assert managed["sessionId"] == managed_session_id, managed
assert managed["agentInstanceId"] == instance_id, managed
assert managed["agentVersion"] == pub_version, managed

status, events = get(f"http://127.0.0.1:{port}/api/v2/admin/events?limit=50")
assert status == 200, events
assert marker not in events, events
assert "PublicationCreated" in events or "publication.created" in events.lower(), events

status, publications = get(f"http://127.0.0.1:{port}/api/v2/admin/definitions/examiner/publications")
assert status == 200, publications
assert f'"version":{pub_version}' in publications.replace(" ", "") or f'"version": {pub_version}' in publications, publications

automation_seed = json.load(open("/tmp/agent-core-unified-automation.json"))
status, automations_json = get(f"http://127.0.0.1:{port}/api/v2/admin/agent-instances/{automation_seed['instanceId']}/automations")
rows = json.loads(automations_json)["items"]
saved = automation_seed["automation"]
reopened = next(row for row in rows if row["automationId"] == saved["automationId"])
for key in ("revision", "name", "instructions", "enabled", "status", "trigger", "authorizationOrigin", "sourceSessionId", "sourceEventId", "createdAt", "nextRunAt", "modelKey", "reasoningEffort"):
    assert reopened[key] == saved[key], (key, reopened[key], saved[key])
assert reopened["effectiveModelKey"] == "scripted-alpha", reopened
print("automation survived", reopened["automationId"])

resource_path = admin["resourcePath"]
content_sha256 = admin["contentSha256"]
status, pub_resources = get(
    f"http://127.0.0.1:{port}/api/v2/admin/definitions/examiner/publications/{pub_version}/resources"
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
    f"http://127.0.0.1:{port}/api/v2/sessions/{managed_session_id}/workspace/content?path="
    + urllib.parse.quote(agent_resource_path, safe="/")
)
status, workspace = get(workspace_url)
assert status == 200, workspace
assert resource_text in workspace, workspace
home = json.load(open("/tmp/agent-core-home-survival.json"))
status, retained = get(f"http://127.0.0.1:{port}/api/v2/agent-instances/{home['instanceId']}/workspace/{home['item']['itemId']}/content")
assert status == 200 and retained == home["text"], (status, retained)
status, listed_home = get(f"http://127.0.0.1:{port}/api/v2/agent-instances/{home['instanceId']}/workspace")
assert any(item["sha256Hex"] == home["item"]["sha256Hex"] and not item["directory"] for item in json.loads(listed_home)["items"])
assert any(item["logicalPath"] == "/home/reports" and item["directory"] for item in json.loads(listed_home)["items"])
assert len(json.loads(listed_home)["treeSha256"]) == 64
v2 = json.load(open("/tmp/agent-core-v2-survival.json"))
skills_seed = json.load(open("/tmp/agent-core-skills-survival.json"))
skills_url = f"http://127.0.0.1:{port}/api/v2/admin/agent-instances/{skills_seed['instanceId']}/skills"
status, skills_json = get(skills_url)
assert status == 200 and json.loads(skills_json) == skills_seed["rows"]
for saved in (skills_seed["accounting"], skills_seed["copy"]):
    status, reopened_skill = get(skills_url + "/" + urllib.parse.quote(saved["key"], safe=""))
    assert status == 200 and json.loads(reopened_skill) == saved
print("instance Skills, Definition disabled state and copy provenance survived recreation", skills_seed["instanceId"])
v2_bytes = bytes([0, 255, 128, 13, 10])
for path in ("/home/binary.dat", "/working/binary.dat"):
    req = urllib.request.Request(f"http://127.0.0.1:{port}/api/v2/sessions/{v2['sessionId']}/workspace/content?path={path}", headers={"X-AgentCore-Owner-Capability": token})
    with urllib.request.urlopen(req) as response: assert response.read() == v2_bytes
req = urllib.request.Request(f"http://127.0.0.1:{port}/api/v2/sessions/{v2['sessionId']}", method="DELETE", headers={"X-AgentCore-Owner-Capability": token})
with urllib.request.urlopen(req) as response: assert response.status == 204
req = urllib.request.Request(f"http://127.0.0.1:{port}/api/v2/sessions", method="POST",
    data=json.dumps({"agentInstanceId": v2["instanceId"], "mode": "text"}).encode(),
    headers={"X-AgentCore-Owner-Capability": token, "Content-Type": "application/json"})
with urllib.request.urlopen(req) as response: fresh = json.load(response)["sessionId"]
req = urllib.request.Request(f"http://127.0.0.1:{port}/api/v2/sessions/{fresh}/workspace/content?path=/home/binary.dat", headers={"X-AgentCore-Owner-Capability": token})
with urllib.request.urlopen(req) as response: assert response.read() == v2_bytes
req = urllib.request.Request(f"http://127.0.0.1:{port}/api/v2/sessions/{fresh}/workspace/content?path=/working/binary.dat", headers={"X-AgentCore-Owner-Capability": token})
try:
    urllib.request.urlopen(req)
    raise AssertionError("new Session inherited scratch")
except urllib.error.HTTPError as error: assert error.code == 404
print("managed v2 binary home and nested scratch survived recreation; fresh scratch isolated", v2["instanceId"])
print("agent workspace survived source deletion and container recreation", home["instanceId"])
print("admin survived", instance_id, managed_session_id, pub_version, resource_path)
credential = json.load(open("/tmp/agent-core-credential-survival.json"))
status, safe_credential = get(f"http://127.0.0.1:{port}/api/v2/admin/credentials/{credential['credentialId']}")
assert status == 200 and json.loads(safe_credential)["bindingCount"] == 1
assert "compose-private-value-7b3f" not in safe_credential
status, grants = get(f"http://127.0.0.1:{port}/api/v2/admin/agent-instances/{credential['instanceId']}/credential-bindings")
assert status == 200 and any(item["bindingId"] == credential["bindingId"] and item["reference"] == "store-admin" for item in json.loads(grants)["items"])
print("credential and binding survived with safe projection", credential["credentialId"])

PY

recreated_cid="$("${compose[@]}" ps -aq agent-core)"
keys_after="$(mktemp -d)"
docker cp "$recreated_cid":/data/credential-protection-keys "$keys_after/keys"
find "$keys_after/keys" -type f -name '*.xml' -exec shasum -a 256 {} \; | awk '{print $1}' | sort > /tmp/agent-core-credential-key-hashes-after.txt
cmp /tmp/agent-core-credential-key-hashes.txt /tmp/agent-core-credential-key-hashes-after.txt
rm -rf "$keys_after"
echo "credential protection key ring survived container recreation"

spa="$(curl -sS -o /dev/null -w '%{http_code}' http://127.0.0.1:${AGENTCORE_COMPOSE_PORT}/)"
api_missing="$(curl -sS -o /dev/null -w '%{http_code}' http://127.0.0.1:${AGENTCORE_COMPOSE_PORT}/api/v1/missing)"
[[ "$spa" == "200" ]]
[[ "$api_missing" == "404" ]]

"${compose[@]}" down
echo "compose sqlite volume check passed"
