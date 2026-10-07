#!/usr/bin/env bash
# Native Real dev stack: http-openrouter API (:5080) + Vite (:5173).
# Optional nopCommerce demo (:5088) with AgentCore.OrderEvents plugin + webhook env from .env.
#
# Usage:
#   scripts/dev-real.sh start              # nopCommerce + API + web (default)
#   scripts/dev-real.sh start --api-only     # skip nopCommerce
#   scripts/dev-real.sh restart              # same as start after stop
#   scripts/dev-real.sh stop                 # API + web only
#   scripts/dev-real.sh stop --with-store    # also scripts/nopcommerce-demo.sh stop
#   scripts/dev-real.sh status               # health + nopCommerce plugin check
#
# Requires: .env with OPENROUTER_API_KEY (export or user-secrets). For the store demo,
# set NOPCOMMERCE_DB_PASSWORD and optionally AGENTCORE_WEBHOOK_URL / AGENTCORE_WEBHOOK_TOKEN
# (URL should use host.docker.internal:5080 from the nopCommerce container).
set -euo pipefail

root="$(cd "$(dirname "$0")/.." && pwd)"
state_dir="$root/local/dev"
api_log="$state_dir/api.log"
web_log="$state_dir/web.log"
api_pid_file="$state_dir/api.pid"
web_pid_file="$state_dir/web.pid"

api_health="http://127.0.0.1:5080/health"
web_health="http://localhost:5173/health"
store_origin="http://127.0.0.1:5088"

usage() {
  sed -n '2,12p' "$0" | sed 's/^# \{0,1\}//'
}

mkdir -p "$state_dir"

load_env() {
  if [[ -f "$root/.env" ]]; then
    set -a
    # shellcheck disable=SC1091
    source "$root/.env"
    set +a
  fi
}

process_running() {
  kill -0 "$1" 2>/dev/null && [[ "$(ps -p "$1" -o stat= 2>/dev/null)" != *Z* ]]
}

terminate_tree() {
  local pid="$1" child
  for child in $(pgrep -P "$pid" || true); do terminate_tree "$child"; done
  kill "$pid" 2>/dev/null || true
}

stop_service() {
  local file="$1"
  local pid group attempt cwd
  [[ -f "$file" ]] || return 0
  pid="$(cat "$file" 2>/dev/null || true)"
  if [[ "$pid" =~ ^[0-9]+$ ]] && process_running "$pid"; then
    # Do not signal a reused PID or another project's development server.
    cwd="$( { lsof -a -p "$pid" -d cwd -Fn 2>/dev/null || true; } | sed -n 's/^n//p')"
    if [[ "$cwd" != "$root" && "$cwd" != "$root/web" ]]; then
      echo "Ignoring stale PID $pid in $file (not this workspace)." >&2
      rm -f "$file"
      return 0
    fi
    group="$( { ps -p "$pid" -o pgid= 2>/dev/null || true; } | tr -d ' ')"
    if [[ "$group" == "$pid" ]]; then
      kill -TERM -- "-$pid" 2>/dev/null || true
    else
      # Compatibility with PID files created by the older launcher.
      terminate_tree "$pid"
    fi
    for attempt in $(seq 1 30); do
      process_running "$pid" || break
      sleep 1
    done
    if process_running "$pid"; then
      if [[ "$group" == "$pid" ]]; then
        kill -KILL -- "-$pid" 2>/dev/null || true
      else
        kill -KILL "$pid" 2>/dev/null || true
      fi
    fi
  fi
  rm -f "$file"
}

stop_api_web() {
  stop_service "$api_pid_file"
  stop_service "$web_pid_file"
}

launch_service() {
  local cwd="$1" log="$2" pid_file="$3"
  shift 3
  # A separate session survives the launching terminal/command's process-group
  # cleanup. stdin is detached too; logs and the leader PID stay inspectable.
  python3 - "$cwd" "$log" "$pid_file" "$@" <<'PYTHON'
import pathlib, subprocess, sys
cwd, log, pid_file, *command = sys.argv[1:]
with open(log, "wb") as output:
    process = subprocess.Popen(command, cwd=cwd, stdin=subprocess.DEVNULL,
        stdout=output, stderr=subprocess.STDOUT, start_new_session=True)
pathlib.Path(pid_file).write_text(str(process.pid) + "\n")
PYTHON
}

wait_for_url() {
  local url="$1"
  local label="$2"
  local pid_file="$3"
  local attempt
  for attempt in $(seq 1 120); do
    if ! process_running "$(cat "$pid_file")"; then
      echo "$label exited before becoming ready (see $api_log / $web_log)." >&2
      return 1
    fi
    if curl -fsS --max-time 3 "$url" >/dev/null 2>&1; then
      return 0
    fi
    sleep 1
  done
  echo "$label did not become ready at $url (see $api_log / $web_log)" >&2
  return 1
}

start_nopcommerce() {
  load_env
  (cd "$root" && ./scripts/nopcommerce-demo.sh start)
}

start_api() {
  load_env
  launch_service "$root" "$api_log" "$api_pid_file" \
    dotnet run --project src/AgentCore.Api --launch-profile http-openrouter
}

start_web() {
  launch_service "$root/web" "$web_log" "$web_pid_file" pnpm run dev
}

verify_nopcommerce_plugin() {
  if ! docker ps --format '{{.Names}}' 2>/dev/null | grep -qx nopcommerce-demo-web; then
    echo "nopCommerce: not running"
    return 0
  fi
  if docker exec nopcommerce-demo-web test -f /app/Plugins/AgentCore.OrderEvents/AgentCore.OrderEvents.dll 2>/dev/null; then
    echo "nopCommerce plugin: AgentCore.OrderEvents dll present"
  else
    echo "nopCommerce plugin: MISSING (run: scripts/nopcommerce-demo.sh start)" >&2
    return 1
  fi
  if docker exec nopcommerce-demo-web grep -q AgentCore.OrderEvents /app/App_Data/plugins.json 2>/dev/null; then
    echo "nopCommerce plugin: listed in plugins.json"
  else
    echo "nopCommerce plugin: not in plugins.json" >&2
    return 1
  fi
  docker exec nopcommerce-demo-web sh -c '
    if [ -n "$AGENTCORE_WEBHOOK_URL" ]; then echo "webhook url: set"; else echo "webhook url: not set"; fi
    if [ -n "$AGENTCORE_WEBHOOK_TOKEN" ]; then echo "webhook token: set"; else echo "webhook token: not set"; fi
  '
}

cmd_start() {
  local with_store=1
  while [[ $# -gt 0 ]]; do
    case "$1" in
      --api-only) with_store=0 ;;
      -h | --help) usage; exit 0 ;;
      *) echo "unknown option: $1" >&2; usage >&2; exit 2 ;;
    esac
    shift
  done

  stop_api_web

  if [[ "$with_store" == 1 ]]; then
    start_nopcommerce
  fi

  start_api
  start_web

  if ! wait_for_url "$api_health" "API" "$api_pid_file" \
    || ! wait_for_url "$web_health" "Vite" "$web_pid_file"; then
    stop_api_web
    return 1
  fi

  echo ""
  echo "Real dev stack is up."
  curl -sS "$api_health"; echo
  curl -sS "$web_health"; echo
  if [[ "$with_store" == 1 ]]; then
    curl -sS -o /dev/null -w "Store: %{http_code}\n" "$store_origin/"
    verify_nopcommerce_plugin || true
    echo "Open http://localhost:5173 (app) and connect the store at $store_origin"
  else
    echo "Open http://localhost:5173"
  fi
  echo "Logs: $api_log , $web_log"
}

cmd_stop() {
  local with_store=0
  while [[ $# -gt 0 ]]; do
    case "$1" in
      --with-store) with_store=1 ;;
      -h | --help) usage; exit 0 ;;
      *) echo "unknown option: $1" >&2; usage >&2; exit 2 ;;
    esac
    shift
  done

  stop_api_web
  if [[ "$with_store" == 1 ]]; then
    (cd "$root" && ./scripts/nopcommerce-demo.sh stop)
  fi
  echo "Stopped API and web."
  if [[ "$with_store" == 1 ]]; then echo "Stopped nopCommerce demo."; fi
  return 0
}

cmd_status() {
  local down=0
  if curl -fsS --max-time 2 "$api_health" >/dev/null 2>&1; then
    echo -n "API: "; curl -sS "$api_health"; echo
  else
    echo "API: down"
    down=1
  fi
  if curl -fsS --max-time 2 "$web_health" >/dev/null 2>&1; then
    echo -n "Web: "; curl -sS "$web_health"; echo
  else
    echo "Web: down"
    down=1
  fi
  if curl -fsS --max-time 2 "$store_origin/" >/dev/null 2>&1; then
    curl -sS -o /dev/null -w "Store: %{http_code}\n" "$store_origin/"
    verify_nopcommerce_plugin || down=1
  else
    echo "Store: down or not started"
  fi
  return "$down"
}

main() {
  local cmd="${1:-}"
  shift || true
  case "$cmd" in
    start) cmd_start "$@" ;;
    restart) cmd_start "$@" ;;
    stop) cmd_stop "$@" ;;
    status) cmd_status ;;
    -h | --help | "") usage; exit 0 ;;
    *)
      echo "unknown command: $cmd" >&2
      usage >&2
      exit 2
      ;;
  esac
}

main "$@"
