#!/usr/bin/env bash
# Emit one allowlisted order.placed webhook payload.
# This script only POSTs that JSON. It does not call an agent API, open a browser, or run SQL.
set -euo pipefail

: "${AGENTCORE_WEBHOOK_URL:?Set AGENTCORE_WEBHOOK_URL to the source hook URL.}"
: "${AGENTCORE_WEBHOOK_TOKEN:?Set AGENTCORE_WEBHOOK_TOKEN to the bearer token.}"
: "${SOURCE_EVENT_ID:?Set SOURCE_EVENT_ID to 1..64 printable ASCII characters.}"
: "${ORDER_REFERENCE:?Set ORDER_REFERENCE to 1..64 characters.}"

if [[ "${#SOURCE_EVENT_ID}" -lt 1 || "${#SOURCE_EVENT_ID}" -gt 64 ]]; then
  echo "SOURCE_EVENT_ID must be 1..64 characters." >&2
  exit 2
fi
if [[ "${#ORDER_REFERENCE}" -lt 1 || "${#ORDER_REFERENCE}" -gt 64 ]]; then
  echo "ORDER_REFERENCE must be 1..64 characters." >&2
  exit 2
fi

payload="$(python3 -c 'import json, os, sys
body = {
    "eventId": os.environ["SOURCE_EVENT_ID"],
    "type": "order.placed",
    "data": {"orderReference": os.environ["ORDER_REFERENCE"]},
}
occurred = os.environ.get("OCCURRED_AT_UTC", "").strip()
if occurred:
    body["occurredAt"] = occurred
sys.stdout.write(json.dumps(body, separators=(",", ":")))')"

curl --fail-with-body --silent --show-error \
  --max-time 10 \
  -X POST \
  -H "Authorization: Bearer ${AGENTCORE_WEBHOOK_TOKEN}" \
  -H "Content-Type: application/json" \
  --data "$payload" \
  "$AGENTCORE_WEBHOOK_URL"
echo
