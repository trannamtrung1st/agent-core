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

stop_api_web() {
  if [[ -f "$api_pid_file" ]]; then
    local pid
    pid="$(cat "$api_pid_file" 2>/dev/null || true)"
    if [[ -n "$pid" ]] && kill -0 "$pid" 2>/dev/null; then
      kill "$pid" 2>/dev/null || true
    fi
  fi
  if [[ -f "$web_pid_file" ]]; then
    local pid
    pid="$(cat "$web_pid_file" 2>/dev/null || true)"
    if [[ -n "$pid" ]] && kill -0 "$pid" 2>/dev/null; then
      kill "$pid" 2>/dev/null || true
    fi
  fi
  pkill -f "AgentCore.Api" 2>/dev/null || true
  pkill -f "vite/bin/vite" 2>/dev/null || true
  rm -f "$api_pid_file" "$web_pid_file"
  sleep 1
}

wait_for_url() {
  local url="$1"
  local label="$2"
  local attempt
  for attempt in $(seq 1 120); do
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
  : >"$api_log"
  (
    cd "$root"
    nohup dotnet run --project src/AgentCore.Api --launch-profile http-openrouter \
      >>"$api_log" 2>&1 &
    echo $! >"$api_pid_file"
    disown -h 2>/dev/null || true
  )
}

start_web() {
  : >"$web_log"
  (
    cd "$root/web"
    nohup pnpm run dev >>"$web_log" 2>&1 &
    echo $! >"$web_pid_file"
    disown -h 2>/dev/null || true
  )
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

  wait_for_url "$api_health" "API"
  wait_for_url "$web_health" "Vite"

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
  [[ "$with_store" == 1 ]] && echo "Stopped nopCommerce demo."
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
