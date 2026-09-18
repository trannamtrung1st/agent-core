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
import json, urllib.error, urllib.request

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
open("/tmp/agent-core-owner-token.txt", "w").write(token)
PY

session_id="$(python3 -c 'import json; print(json.load(open("/tmp/agent-core-session.json"))["sessionId"])')"
owner_token="$(cat /tmp/agent-core-owner-token.txt)"

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
PY

spa="$(curl -sS -o /dev/null -w '%{http_code}' http://127.0.0.1:5080/)"
api_missing="$(curl -sS -o /dev/null -w '%{http_code}' http://127.0.0.1:5080/api/v1/missing)"
[[ "$spa" == "200" ]]
[[ "$api_missing" == "404" ]]

"${compose[@]}" down
echo "compose sqlite volume check passed"
