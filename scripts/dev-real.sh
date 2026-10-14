#!/usr/bin/env bash
# Native Real dev stack: http-openrouter API (:5080) + Vite (:5173).
# Optional nopCommerce demo (:5088) with AgentCore.OrderEvents plugin + webhook env from .env.
#
# Usage:
#   scripts/dev-real.sh start                # OpenSandbox + native Real API + web (default)
#   scripts/dev-real.sh start --api-only     # native API + web only, no managed Docker
#   scripts/dev-real.sh start --full         # store + Compose OpenSandbox + API + web
#   scripts/dev-real.sh start --with-store   # optional nopCommerce demo as well
#   scripts/dev-real.sh restart --full       # generation-fenced cleanup and restart
#   scripts/dev-real.sh stop                 # API + web only
#   scripts/dev-real.sh stop --with-store    # also scripts/nopcommerce-demo.sh stop
#   scripts/dev-real.sh status               # health + nopCommerce plugin check
#
# Requires: .env with OPENROUTER_API_KEY (export or user-secrets). For the store demo,
# set NOPCOMMERCE_DB_PASSWORD and optionally AGENTCORE_WEBHOOK_URL / AGENTCORE_WEBHOOK_TOKEN
# (URL should use host.docker.internal:5080 from the nopCommerce container).
set -euo pipefail

root="$(cd "$(dirname "$0")/.." && pwd)"
state_dir="${AGENTCORE_DEV_STATE_DIR:-$root/local/dev}"
api_log="$state_dir/api.log"
web_log="$state_dir/web.log"
api_pid_file="$state_dir/api.pid"
web_pid_file="$state_dir/web.pid"

api_port="${AGENTCORE_DEV_API_PORT:-5080}"
web_port="${AGENTCORE_DEV_WEB_PORT:-5173}"
api_origin="http://127.0.0.1:$api_port"
web_origin="http://localhost:$web_port"
api_health="$api_origin/health"
web_health="$web_origin/health"
sandbox_project="${AGENTCORE_SANDBOX_PROJECT:-agent-core-sandbox}"
sandbox_port="${AGENTCORE_SANDBOX_PORT:-18080}"
sandbox_selected=0
store_selected=0
store_origin="http://127.0.0.1:5088"

usage() {
  echo "usage: scripts/dev-real.sh start|restart [--api-only] [--with-sandbox] [--with-store] | start|restart --full"
  echo "       scripts/dev-real.sh stop [--with-store] [--with-sandbox] | stop --full | status"
  echo "Default start/restart: OpenSandbox infrastructure + native Real API + Vite. --with-store or --full adds the optional nopCommerce demo; --api-only starts API/Vite only. No app container is started."
}

mkdir -p "$state_dir"

load_env() {
  if [[ -f "${AGENTCORE_DEV_ENV_FILE:-$root/.env}" ]]; then
    set -a
    # shellcheck disable=SC1091
    source "${AGENTCORE_DEV_ENV_FILE:-$root/.env}"
    set +a
  fi
}

record_stack() {
  python3 - "$state_dir/stack.json" "$api_port" "$web_port" "$sandbox_project" "$sandbox_port" "$sandbox_selected" "$store_selected" <<'PYTHON'
import json,os,sys
path,api,web,project,port,selected,store=sys.argv[1:]
fd=os.open(path,os.O_WRONLY|os.O_CREAT|os.O_TRUNC,0o600)
with os.fdopen(fd,'w') as output:json.dump(dict(apiPort=int(api),webPort=int(web),sandboxProject=project,sandboxPort=int(port),sandboxSelected=selected=='1',storeSelected=store=='1'),output)
PYTHON
}

load_stack() {
  [[ -f "$state_dir/stack.json" ]] || return 0
  local fields
  fields="$(python3 - "$state_dir/stack.json" <<'PYTHON'
import json,re,sys
value=json.load(open(sys.argv[1]));a=value['apiPort'];w=value['webPort'];p=value['sandboxPort'];name=value['sandboxProject']
assert all(type(port) is int and 1024<=port<=65535 for port in (a,w,p)) and re.fullmatch('agent-core-[a-z0-9-]{1,64}',name)
selected=value.get('sandboxSelected',False);store=value.get('storeSelected',False)
assert type(selected) is bool and type(store) is bool
print(a,w,name,p,int(selected),int(store))
PYTHON
)" || { echo "Invalid stack tracking; retain state and inspect it before cleanup." >&2; return 1; }
  read -r api_port web_port sandbox_project sandbox_port sandbox_selected store_selected <<<"$fields"
  api_origin="http://127.0.0.1:$api_port";web_origin="http://localhost:$web_port"
  api_health="$api_origin/health";web_health="$web_origin/health"
}

process_running() {
  # A denied probe is not evidence that the process exited. Keep its PID file
  # so a later stop from a terminal with process access can still find it.
  python3 - "$1" <<'PYTHON'
import os, subprocess, sys
pid = int(sys.argv[1])
try:
    os.kill(pid, 0)
except ProcessLookupError:
    sys.exit(1)
except PermissionError:
    print(f"Cannot inspect PID {pid}: permission denied.", file=sys.stderr)
    sys.exit(2)
result = subprocess.run(["ps", "-p", str(pid), "-o", "stat="], capture_output=True, text=True)
if result.stderr.strip():
    print(f"Cannot inspect PID {pid}: {result.stderr.strip()}", file=sys.stderr)
    sys.exit(2)
sys.exit(0 if result.stdout.strip() and "Z" not in result.stdout else 1)
PYTHON
}

target_running() {
  local pid="$1" group="$2" processes
  if [[ "$group" == "$pid" ]]; then
    # The launcher can exit before a child releases the listening socket.
    processes="$(ps -axo pgid=,stat=)" || return 2
    awk -v group="$group" '$1 == group && $2 !~ /Z/ { alive=1 } END { exit !alive }' <<<"$processes"
  else
    process_running "$pid"
  fi
}

terminate_tree() {
  local pid="$1" child
  for child in $(pgrep -P "$pid" || true); do terminate_tree "$child"; done
  kill "$pid" 2>/dev/null || true
}

stop_service() {
  local file="$1"
  local pid group attempt cwd state
  [[ -f "$file" ]] || return 0
  pid="$(cat "$file" 2>/dev/null || true)"
  if [[ ! "$pid" =~ ^[0-9]+$ || "$pid" -le 1 ]]; then
    echo "Invalid PID in $file; cannot safely stop service." >&2
    return 1
  fi
  if process_running "$pid"; then
    # Do not signal a reused PID or another project's development server.
    cwd="$( { lsof -a -p "$pid" -d cwd -Fn 2>/dev/null || true; } | sed -n 's/^n//p')"
    if [[ -z "$cwd" ]]; then
      echo "Cannot verify workspace for PID $pid; keeping $file." >&2
      return 1
    fi
    if [[ "$cwd" != "$root" && "$cwd" != "$root/web" && "$cwd" != "$root/src/AgentCore.Api" ]]; then
      echo "Ignoring stale PID $pid in $file (not this workspace)." >&2
      rm -f "$file"
      return 0
    fi
    group="$(ps -p "$pid" -o pgid= | tr -d ' ')"
    if [[ ! "$group" =~ ^[0-9]+$ ]]; then
      echo "Cannot inspect process group for PID $pid; keeping $file." >&2
      return 1
    fi
    if [[ "$group" == "$pid" ]]; then
      kill -TERM -- "-$pid" || return 1
    else
      # Compatibility with PID files created by the older launcher.
      terminate_tree "$pid"
    fi
    for attempt in $(seq 1 30); do
      if target_running "$pid" "$group"; then
        :
      else
        state=$?
        [[ "$state" == 1 ]] || return 1
        break
      fi
      sleep 1
    done
    if target_running "$pid" "$group"; then
      if [[ "$group" == "$pid" ]]; then
        kill -KILL -- "-$pid" || return 1
      else
        kill -KILL "$pid" || return 1
      fi
      # Do not discard tracking before the forced termination has completed.
      for attempt in $(seq 1 5); do
        if target_running "$pid" "$group"; then
          sleep 1
        else
          state=$?
          [[ "$state" == 1 ]] || return 1
          break
        fi
      done
      if target_running "$pid" "$group"; then
        echo "PID $pid still has running processes; keeping $file." >&2
        return 1
      else
        state=$?
        [[ "$state" == 1 ]] || return 1
      fi
    else
      state=$?
      [[ "$state" == 1 ]] || return 1
    fi
  else
    state=$?
    [[ "$state" == 1 ]] || return 1
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
  if [[ "$sandbox_selected" == 1 ]]; then
    set -a
    source "$root/local/opensandbox-compose/$sandbox_project/native.env"
    set +a
  fi
  export Hosting__AllowedOrigins__0="http://localhost:$web_port" Hosting__AllowedOrigins__1="http://127.0.0.1:$web_port"
  local data_args=()
  if [[ -n "${AGENTCORE_DEV_API_DATA_DIR:-}" ]]; then
    mkdir -p "$AGENTCORE_DEV_API_DATA_DIR"
    data_args=("--Persistence:ConnectionString=Data Source=$AGENTCORE_DEV_API_DATA_DIR/core.db"
      "--Persistence:WorkspaceRoot=$AGENTCORE_DEV_API_DATA_DIR/workspaces"
      "--Persistence:ArtifactRoot=$AGENTCORE_DEV_API_DATA_DIR/artifacts"
      "--Persistence:AttachmentRoot=$AGENTCORE_DEV_API_DATA_DIR/attachments"
      "--Persistence:CredentialProtectionKeyRoot=$AGENTCORE_DEV_API_DATA_DIR/keys")
  fi
  launch_service "$root" "$api_log" "$api_pid_file" \
    dotnet run --project src/AgentCore.Api --launch-profile http-openrouter --urls "$api_origin" --AgentCore:Profile=Real ${data_args[@]+"${data_args[@]}"}
}

start_web() {
  export VITE_DEV_PORT="$web_port" VITE_API_PROXY_TARGET="$api_origin"
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

require_dependencies() {
  local name
  for name in python3 dotnet pnpm node curl ps lsof; do
    command -v "$name" >/dev/null || { echo "Required dependency missing: $name" >&2; return 1; }
  done
}

start_sandbox() {
  python3 "$root/scripts/opensandbox-compose.py" up --project "$sandbox_project" --port "$sandbox_port"
}

cmd_start() {
  local with_store=0 full=0 api_only=0 explicit_sandbox=0
  sandbox_selected=1
  while [[ $# -gt 0 ]]; do
    case "$1" in
      --api-only) api_only=1 ;;
      --with-sandbox) explicit_sandbox=1 ;;
      --with-store) with_store=1 ;;
      --full) with_store=1; sandbox_selected=1; full=1 ;;
      -h | --help) usage; return 0 ;;
      *) echo "unknown option: $1" >&2; usage >&2; return 2 ;;
    esac
    shift
  done
  [[ "$full" != 1 || "$api_only" != 1 ]] || { echo "Choose --full or --api-only, not both." >&2; return 2; }
  [[ "$api_only" != 1 || "$with_store" != 1 ]] || { echo "Choose --with-store or --api-only, not both." >&2; return 2; }
  if [[ "$api_only" == 1 && "$explicit_sandbox" != 1 ]]; then sandbox_selected=0; fi
  store_selected="$with_store"
  require_dependencies
  load_env
  api_port="${AGENTCORE_DEV_API_PORT:-5080}";web_port="${AGENTCORE_DEV_WEB_PORT:-5173}"
  api_origin="http://127.0.0.1:$api_port";web_origin="http://localhost:$web_port"
  api_health="$api_origin/health";web_health="$web_origin/health"
  sandbox_project="${AGENTCORE_SANDBOX_PROJECT:-$sandbox_project}"
  sandbox_port="${AGENTCORE_SANDBOX_PORT:-$sandbox_port}"
  if [[ "$with_store" == 1 || "$sandbox_selected" == 1 ]]; then command -v docker >/dev/null || { echo "Docker is required for selected components." >&2; return 1; }; fi
  stop_api_web
  # Refuse occupied ports after stopping only verified tracked processes.
  python3 - "$api_port" "$web_port" <<'PYTHON'
import socket,sys
for port in map(int,sys.argv[1:]):
    with socket.socket() as probe:
        probe.settimeout(1)
        if probe.connect_ex(('127.0.0.1',port)) == 0:
            raise SystemExit(f'Development port {port} is occupied by an untracked process; it was left running.')
PYTHON
  if [[ "$with_store" == 1 ]]; then start_nopcommerce; fi
  if [[ "$sandbox_selected" == 1 ]]; then start_sandbox; fi
  record_stack
  if ! start_api || ! start_web; then stop_api_web; return 1; fi
  if ! wait_for_url "$api_health" "API" "$api_pid_file" || ! wait_for_url "$web_health" "Vite" "$web_pid_file"; then
    stop_api_web
    return 1
  fi
  if ! curl -fsS --max-time 5 "$api_health" | python3 -c 'import json,sys; sys.exit(0 if json.load(sys.stdin).get("profile") == "Real" else 1)'; then
    echo "API did not confirm the Real profile; stopping the tracked native stack." >&2
    stop_api_web
    return 1
  fi
  if [[ "$sandbox_selected" == 1 ]]; then python3 "$root/scripts/opensandbox-compose.py" status --project "$sandbox_project" --port "$sandbox_port"; fi
  echo "Real dev stack is ready: API $api_origin · app $web_origin"
  if [[ "$with_store" == 1 ]]; then
    curl -fsS --max-time 5 "$store_origin/" >/dev/null || { echo "Store readiness failed; existing data retained." >&2; return 1; }
    verify_nopcommerce_plugin
    echo "Store: $store_origin"
  fi
  echo "Logs: $api_log , $web_log"
}

cmd_stop() {
  local with_store=0 with_sandbox=0
  while [[ $# -gt 0 ]]; do
    case "$1" in
      --with-store) with_store=1 ;;
      --with-sandbox) with_sandbox=1 ;;
      --full) with_store=1; with_sandbox=1 ;;
      -h | --help) usage; exit 0 ;;
      *) echo "unknown option: $1" >&2; usage >&2; exit 2 ;;
    esac
    shift
  done

  load_env
  sandbox_project="${AGENTCORE_SANDBOX_PROJECT:-$sandbox_project}"
  sandbox_port="${AGENTCORE_SANDBOX_PORT:-$sandbox_port}"
  load_stack
  local pending=0
  if [[ "$with_sandbox" == 1 ]]; then
    local args=(stop --project "$sandbox_project" --port "$sandbox_port")
    if [[ -f "$api_pid_file" ]] && process_running "$(cat "$api_pid_file")"; then
      local owned_cwd
      owned_cwd="$( { lsof -a -p "$(cat "$api_pid_file")" -d cwd -Fn 2>/dev/null || true; } | sed -n 's/^n//p')"
      if [[ "$owned_cwd" == "$root" || "$owned_cwd" == "$root/src/AgentCore.Api" ]]; then args+=(--api-url "$api_origin"); fi
    fi
    python3 "$root/scripts/opensandbox-compose.py" "${args[@]}" || pending=1
  fi
  stop_api_web
  if [[ "$with_store" == 1 ]]; then
    (cd "$root" && ./scripts/nopcommerce-demo.sh stop)
  fi
  echo "Stopped API and web."
  if [[ "$with_store" == 1 ]]; then echo "Stopped nopCommerce demo."; fi
  [[ "$pending" == 0 ]] || { echo "Selected sandbox cleanup remains pending; server/state retained." >&2; return 1; }
  return 0
}

cmd_status() {
  load_env
  sandbox_project="${AGENTCORE_SANDBOX_PROJECT:-$sandbox_project}"
  sandbox_port="${AGENTCORE_SANDBOX_PORT:-$sandbox_port}"
  load_stack
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
  if [[ "$store_selected" == 1 ]]; then
    if curl -fsS --max-time 2 "$store_origin/" >/dev/null 2>&1; then
      curl -sS -o /dev/null -w "Store: %{http_code}\n" "$store_origin/"
      verify_nopcommerce_plugin || down=1
    else
      echo "Store: selected demo is down"
      down=1
    fi
  else
    echo "Store: optional demo not selected; existing Docker services left unchanged"
  fi
  if [[ "$sandbox_selected" == 1 && -f "$root/scripts/opensandbox-compose.py" ]]; then
    python3 "$root/scripts/opensandbox-compose.py" status --project "$sandbox_project" --port "$sandbox_port" || down=1
  else
    echo "OpenSandbox: not selected for this native stack"
  fi
  return "$down"
}

main() {
  local cmd="${1:-}"
  shift || true
  case "$cmd" in
    start) cmd_start "$@" ;;
    restart)
      local full=0 sandbox=0 api_only=0 with_store=0 option
      for option in "$@"; do
        case "$option" in --full) full=1;; --with-sandbox) sandbox=1;; --with-store) with_store=1;; --api-only) api_only=1;; -h|--help) usage;return 0;; *) echo "unknown option: $option" >&2;return 2;; esac
      done
      [[ "$full" != 1 || "$api_only" != 1 ]] || { echo "Choose --full or --api-only, not both." >&2; return 2; }
      [[ "$with_store" != 1 || "$api_only" != 1 ]] || { echo "Choose --with-store or --api-only, not both." >&2; return 2; }
      if [[ "$full" == 1 ]]; then cmd_stop --full; elif [[ "$sandbox" == 1 ]]; then cmd_stop --with-sandbox; fi
      cmd_start "$@" ;;
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
