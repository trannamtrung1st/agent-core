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
import json, urllib.request
health = json.load(open("/tmp/agent-core-health.json"))
assert health["status"] == "healthy", health
assert health["profile"] == "Synthetic", health
req = urllib.request.Request(
    "http://127.0.0.1:5080/api/v1/sessions",
    data=json.dumps({"agentId": "examiner", "mode": "text"}).encode(),
    headers={"Content-Type": "application/json"},
    method="POST",
)
with urllib.request.urlopen(req) as response:
    created = json.load(response)
open("/tmp/agent-core-session.json", "w").write(json.dumps(created))
print("created", created["sessionId"])
PY

session_id="$(python3 -c 'import json; print(json.load(open("/tmp/agent-core-session.json"))["sessionId"])')"

"${compose[@]}" up -d --force-recreate --no-deps agent-core

ready=0
for _ in $(seq 1 90); do
  if curl -fsS "http://127.0.0.1:5080/api/v1/sessions/${session_id}" >/tmp/agent-core-session-reopen.json 2>/dev/null; then
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
import json
body = json.load(open("/tmp/agent-core-session-reopen.json"))
assert body["sessionId"] == "${session_id}", body
assert body["agentId"] == "examiner", body
print("survived", body["sessionId"], body["status"])
PY

spa="$(curl -sS -o /dev/null -w '%{http_code}' http://127.0.0.1:5080/)"
api_missing="$(curl -sS -o /dev/null -w '%{http_code}' http://127.0.0.1:5080/api/v1/missing)"
[[ "$spa" == "200" ]]
[[ "$api_missing" == "404" ]]

"${compose[@]}" down
echo "compose sqlite volume check passed"
