#!/bin/sh
set -eu
if [ "${1:-}" = download ]; then
  exec uv run --frozen --offline --extra serve python /opt/decision/download.py
fi
[ "${#KEV_API_KEY}" -ge 16 ] || { echo 'KEV_API_KEY must contain at least 16 characters.' >&2; exit 2; }
# No model download or automatic checkpoint fallback at service startup.
exec uv run --frozen --offline --extra serve python -m kev.serve --run jaredpalmer/kev-4b@6cfce5c2fa4b4bd64026336ab649c5ca78857d52 --host 0.0.0.0 --port 8009
