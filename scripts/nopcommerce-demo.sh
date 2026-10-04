#!/usr/bin/env bash
# Start, stop, or reset the nopCommerce demo Compose project only.
# Does not target the Agent Core compose project or the agent-core-data volume.
set -euo pipefail

root="$(cd "$(dirname "$0")/.." && pwd)"
cd "$root"

project="nopcommerce-demo"
compose=(docker compose -p "$project" -f "$root/docker-compose.nopcommerce.yml")
origin="http://127.0.0.1:5088"
seed="$root/deploy/nopcommerce/seed/demo-conditions.sql"
env_file="$root/.env"

usage() {
  echo "usage: scripts/nopcommerce-demo.sh start|stop|reset" >&2
}

require_docker() {
  if ! docker info >/dev/null 2>&1; then
    echo "Docker is not available." >&2
    exit 1
  fi
}

prepare_env() {
  local generated
  generated="$(python3 - "$env_file" <<'PY'
import pathlib, re, secrets, string, sys
path = pathlib.Path(sys.argv[1])
text = path.read_text() if path.exists() else ""
values = {}
for line in text.splitlines():
    if not line or line.lstrip().startswith("#") or "=" not in line:
        continue
    key, value = line.split("=", 1)
    values[key.strip()] = value
email = values.get("NOPCOMMERCE_ADMIN_EMAIL", "").strip() or "demo-owner@example.com"
db = values.get("NOPCOMMERCE_DB_PASSWORD", "")
admin = values.get("NOPCOMMERCE_ADMIN_PASSWORD", "")
if not db.strip():
    raise SystemExit("Set NOPCOMMERCE_DB_PASSWORD in .env (see .env.example).")
if any(ch in db for ch in "\"'\n\r$"):
    raise SystemExit("NOPCOMMERCE_DB_PASSWORD cannot contain quotes, dollar signs, or line breaks.")
generated = "0"
if not admin.strip():
    alphabet = string.ascii_letters + string.digits
    admin = "".join(secrets.choice(alphabet) for _ in range(24)) + "aA1!"
    generated = "1"

def upsert(key, value):
    global text
    pattern = re.compile(rf"^{re.escape(key)}=.*$", re.M)
    line = f"{key}={value}"
    text = pattern.sub(line, text, count=1) if pattern.search(text) else (text.rstrip() + ("\n" if text else "") + line + "\n")

upsert("NOPCOMMERCE_DB_PASSWORD", db)
upsert("NOPCOMMERCE_ADMIN_EMAIL", email)
upsert("NOPCOMMERCE_ADMIN_PASSWORD", admin)
path.write_text(text if text.endswith("\n") else text + "\n")
print(generated)
PY
)"
  # Export only the demo keys. Do not print the passwords.
  local export_file
  export_file="$(mktemp)"
  python3 - "$env_file" "$export_file" <<'PY'
import pathlib, shlex, sys
values = {}
for line in pathlib.Path(sys.argv[1]).read_text().splitlines():
    if not line or line.lstrip().startswith("#") or "=" not in line:
        continue
    key, value = line.split("=", 1)
    values[key.strip()] = value
lines = [
    f"export {key}={shlex.quote(values.get(key, ''))}"
    for key in ("NOPCOMMERCE_DB_PASSWORD", "NOPCOMMERCE_ADMIN_PASSWORD", "NOPCOMMERCE_ADMIN_EMAIL")
]
pathlib.Path(sys.argv[2]).write_text("\n".join(lines) + "\n")
PY
  # shellcheck disable=SC1090
  source "$export_file"
  rm -f "$export_file"
  if [[ "$generated" == "1" ]]; then
    if ! printf 'nopCommerce admin email: %s\nnopCommerce admin password: %s\n' \
      "$NOPCOMMERCE_ADMIN_EMAIL" "$NOPCOMMERCE_ADMIN_PASSWORD" >/dev/tty 2>/dev/null; then
      echo "Generated NOPCOMMERCE_ADMIN_PASSWORD and saved it in .env." >&2
    fi
  fi
}

snapshot_volumes() {
  docker volume ls -q | sort
}

assert_unrelated_volumes() {
  local before="$1"
  local after removed
  after="$(snapshot_volumes)"
  removed="$(comm -23 <(printf '%s\n' "$before") <(printf '%s\n' "$after") || true)"
  if [[ -n "$removed" ]]; then
    local name
    while IFS= read -r name; do
      [[ -z "$name" ]] && continue
      if [[ "$name" != ${project}_* ]]; then
        echo "reset removed unrelated volume: $name" >&2
        exit 1
      fi
    done <<<"$removed"
  fi
}

wait_for_http() {
  local attempt
  for attempt in $(seq 1 90); do
    if curl -fsS -o /dev/null --max-time 5 "$origin/install" 2>/dev/null || curl -fsS -o /dev/null --max-time 5 "$origin/" 2>/dev/null; then
      return 0
    fi
    sleep 2
  done
  echo "nopCommerce did not accept HTTP on $origin" >&2
  "${compose[@]}" logs --no-color >&2 || true
  exit 1
}

apply_seed() {
  docker exec -i nopcommerce-demo-db bash -lc \
    '/opt/mssql-tools18/bin/sqlcmd -C -S localhost -U sa -P "$MSSQL_SA_PASSWORD" -d nopCommerce -b' \
    <"$seed"
}

assert_seed() {
  local report
  report="$(docker exec nopcommerce-demo-db bash -lc \
    '/opt/mssql-tools18/bin/sqlcmd -C -S localhost -U sa -P "$MSSQL_SA_PASSWORD" -d nopCommerce -h -1 -W -b -Q "
SET NOCOUNT ON;
SELECT CASE WHEN EXISTS (
  SELECT 1 FROM [Product]
  WHERE [Deleted] = 0 AND [ManageInventoryMethodId] = 1 AND [StockQuantity] < [MinStockQuantity]
) THEN 1 ELSE 0 END;
SELECT CASE WHEN EXISTS (
  SELECT 1 FROM [Order] WHERE [Deleted] = 0 AND [OrderStatusId] = 10
) THEN 1 ELSE 0 END;
SELECT [Url] FROM [Store];
"')"
  python3 -c 'import sys; text=sys.stdin.read().split();
assert text[0]=="1", text; assert text[1]=="1", text
urls=[item for item in text[2:] if item]
assert urls and all(item.startswith("http://127.0.0.1:5088") for item in urls), urls' <<<"$report"
}

install_if_needed() {
  NOPCOMMERCE_ORIGIN="$origin" python3 - <<'PY'
import os, re, urllib.error, urllib.parse, urllib.request
origin = os.environ["NOPCOMMERCE_ORIGIN"]
email = os.environ["NOPCOMMERCE_ADMIN_EMAIL"]
admin = os.environ["NOPCOMMERCE_ADMIN_PASSWORD"]
db = os.environ["NOPCOMMERCE_DB_PASSWORD"]
class StopRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, req, fp, code, msg, headers, newurl):
        return None

direct = urllib.request.build_opener(StopRedirect, urllib.request.HTTPCookieProcessor())

def fetch(url, data=None, timeout=30):
    request = urllib.request.Request(url, data=data, method="POST" if data else "GET")
    try:
        with direct.open(request, timeout=timeout) as response:
            return response.status, response.headers, response.read()
    except urllib.error.HTTPError as error:
        return error.code, error.headers, error.read()

def origin_of(url):
    parts = urllib.parse.urlsplit(url)
    return f"{parts.scheme}://{parts.netloc}"

status, headers, body = fetch(origin + "/install")
location = headers.get("Location")
if status in (301, 302, 303, 307, 308) and location:
    target = urllib.parse.urljoin(origin + "/install", location)
    if origin_of(target) != origin:
        raise SystemExit(f"install redirect left {origin}: {target}")
    if urllib.parse.urlsplit(target).path.rstrip("/") not in ("", "/"):
        raise SystemExit(f"unexpected install redirect: {target}")
    print("already-installed")
    raise SystemExit(0)
html = body.decode("utf-8", "replace")
if "AdminPassword" not in html:
    raise SystemExit("install page did not contain the installer form")
match = re.search(r'name="__RequestVerificationToken"[^>]*value="([^"]+)"', html) or re.search(
    r'value="([^"]+)"[^>]*name="__RequestVerificationToken"', html)
if not match:
    raise SystemExit("install page did not contain an antiforgery token")
fields = {
    "__RequestVerificationToken": match.group(1),
    "AdminEmail": email,
    "AdminPassword": admin,
    "ConfirmPassword": admin,
    "DataProvider": "SqlServer",
    "ServerName": "nopcommerce-db",
    "DatabaseName": "nopCommerce",
    "Username": "sa",
    "Password": db,
    "CreateDatabaseIfNotExists": "true",
    "InstallSampleData": "true",
    "ConnectionStringRaw": "false",
    "IntegratedSecurity": "false",
    "InstallRegionalResources": "false",
    "SubscribeNewsletters": "false",
    "UseCustomCollation": "false",
}
payload = urllib.parse.urlencode(fields).encode()
status, headers, body = fetch(origin + "/install", payload, timeout=900)
html = body.decode("utf-8", "replace")
redacted = html.replace(admin, "[redacted]").replace(db, "[redacted]")
if status >= 400 or "window.location.replace" not in html:
    snippet = re.sub(r"\s+", " ", redacted)[:500]
    raise SystemExit(f"install failed ({status}): {snippet}")
restart = re.search(r'url:\s*"([^"]+)"', html)
if restart:
    target = urllib.parse.urljoin(origin + "/install", restart.group(1))
    if origin_of(target) != origin:
        raise SystemExit(f"restart URL left {origin}: {target}")
    try:
        fetch(target, timeout=60)
    except Exception:
        # The site recycles as part of restart. Readiness is checked by the caller.
        pass
print("installed")
PY
}

assert_origins() {
  NOPCOMMERCE_ORIGIN="$origin" python3 - <<'PY'
import os, urllib.error, urllib.parse, urllib.request
origin = os.environ["NOPCOMMERCE_ORIGIN"]

class StopRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, req, fp, code, msg, headers, newurl):
        return None

opener = urllib.request.build_opener(StopRedirect)

def origin_of(url):
    parts = urllib.parse.urlsplit(url)
    if parts.scheme != "http" or parts.hostname != "127.0.0.1" or parts.port != 5088:
        return None
    return origin

def check(path):
    url = origin + path
    for _ in range(8):
        if origin_of(url) != origin:
            raise SystemExit(f"{path} left {origin}: {url}")
        request = urllib.request.Request(url, method="GET")
        try:
            with opener.open(request, timeout=30) as response:
                status = response.status
                location = response.headers.get("Location")
        except urllib.error.HTTPError as error:
            status = error.code
            location = error.headers.get("Location")
        if location:
            nxt = urllib.parse.urljoin(url, location)
            if origin_of(nxt) != origin:
                raise SystemExit(f"{path} redirected off {origin}: {url} -> {nxt}")
            url = nxt
            continue
        if status >= 400:
            raise SystemExit(f"{path} returned {status}")
        return
    raise SystemExit(f"{path} redirected too many times")

check("/")
check("/admin")
print("origins-ok")
PY
}

install_order_events_plugin() {
  local plugin="$root/deploy/nopcommerce/plugin/AgentCore.OrderEvents"
  local refs="$plugin/refs"
  mkdir -p "$refs"
  docker cp nopcommerce-demo-web:/app/Nop.Core.dll "$refs/Nop.Core.dll"
  docker cp nopcommerce-demo-web:/app/Nop.Services.dll "$refs/Nop.Services.dll"
  dotnet build "$plugin/AgentCore.OrderEvents.csproj" -c Release --nologo
  local out="$plugin/bin/Release/net9.0"
  docker exec nopcommerce-demo-web mkdir -p /app/Plugins/AgentCore.OrderEvents
  docker cp "$out/AgentCore.OrderEvents.dll" nopcommerce-demo-web:/app/Plugins/AgentCore.OrderEvents/AgentCore.OrderEvents.dll
  docker cp "$out/AgentCore.OrderEvents.deps.json" nopcommerce-demo-web:/app/Plugins/AgentCore.OrderEvents/AgentCore.OrderEvents.deps.json
  docker cp "$plugin/plugin.json" nopcommerce-demo-web:/app/Plugins/AgentCore.OrderEvents/plugin.json
  local catalog
  catalog="$(mktemp)"
  if ! docker cp nopcommerce-demo-web:/app/App_Data/plugins.json "$catalog" 2>/dev/null; then
    printf '%s\n' '{"InstalledPlugins":[]}' >"$catalog"
  fi
  python3 - "$catalog" <<'PY'
import json, pathlib, sys
path = pathlib.Path(sys.argv[1])
raw = path.read_text(encoding="utf-8-sig") or "{}"
data = json.loads(raw)
installed = data.setdefault("InstalledPlugins", [])
if not any(item.get("SystemName") == "AgentCore.OrderEvents" for item in installed if isinstance(item, dict)):
    installed.append({"SystemName": "AgentCore.OrderEvents"})
path.write_text(json.dumps(data, indent=2) + "\n")
PY
  docker cp "$catalog" nopcommerce-demo-web:/app/App_Data/plugins.json
  rm -f "$catalog"
}

bring_up() {
  prepare_env
  "${compose[@]}" up -d
  wait_for_http
  install_if_needed
  apply_seed
  install_order_events_plugin
  docker restart nopcommerce-demo-web >/dev/null
  wait_for_http
  assert_origins
  assert_seed
  echo "nopCommerce demo is ready at ${origin} (admin ${NOPCOMMERCE_ADMIN_EMAIL})."
}

cmd="${1:-}"
require_docker
case "$cmd" in
  start)
    bring_up
    ;;
  stop)
    "${compose[@]}" stop
    ;;
  reset)
    before="$(snapshot_volumes)"
    "${compose[@]}" down -v
    assert_unrelated_volumes "$before"
    bring_up
    ;;
  *)
    usage
    exit 2
    ;;
esac
