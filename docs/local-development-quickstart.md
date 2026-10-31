# Local development and self-hosting quickstart

Use a new, isolated data root for first use. For an existing installation, start
with the [migration and recovery runbook](17-observability-and-operations.md#controlled-single-host-deployment-preflight).
The [readiness ledger](reports/self-host-local-readiness-2026-10-31.md) records
what has actually been exercised; this guide does not certify optional services.

| Choose | Start with | Available scope |
| --- | --- | --- |
| Key-free local verification | Native launcher with `AgentCore__Profile=Synthetic`, or packaged Synthetic HTTPS | Deterministic Chat and current tenant workflows; no provider credentials |
| Workstation development / native execution | Native Real API + Vite; selected trusted-local Sandbox | Browser, Workbench/Office and native effects require their own configured, authorized dependencies |
| Packaged self-host | Base + optional Real + HTTPS | Authenticated API/SPA, durable tenant configuration and text workflows; Browser and Sandbox execution excluded by the Production profile |

## Prerequisites

Install Git, Python 3, .NET 10 SDK, Node 22 and pnpm 9; use the repository's
`global.json` and `web/package.json` for tool constraints. Check both installed
.NET runtimes against the [servicing floor](17-observability-and-operations.md#serviced-net-runtime-and-safe-redeployment).
Native launcher commands also need Bash, curl, ps and lsof. Docker with Compose
2.24.4+ is needed for packaged or managed Sandbox use; OpenSSL is used by packaged
qualification. Native Browser needs a compatible installed browser. Linux and
macOS mount/identity behavior have distinct qualification boundaries.

```sh
dotnet --version
dotnet --list-runtimes
node --version
pnpm --version
python3 --version
pnpm --dir web install --frozen-lockfile
```

## Native first use

From the checkout root, create `.env` **only if absent**:

```sh
(umask 077; set -C; cat .env.example > .env)
```

Edit that private file. Set all five `AGENTCORE_BOOTSTRAP_*` values, a password
of 12–256 characters, and an unused `AGENTCORE_DEV_API_DATA_DIR` and
`AGENTCORE_DEV_STATE_DIR`. Default ports are API 5080 and Vite 5173; choose free
ports when another app is running. Keep all durable roots and browser profiles
with their selected installation. For a key-free path set
`AgentCore__Profile=Synthetic` and `Browser__Enabled=false`; the template otherwise
selects Real. Do not source dotenv: the launcher parses values literally and
resolves relative paths against the checkout. See [input precedence and
names](host-configuration-reference.md).

```sh
./scripts/dev-real.sh bootstrap-identity
# Remove/blank the one-use bootstrap password in .env after success.
./scripts/dev-real.sh start --api-only
./scripts/dev-real.sh status
```

`--api-only` starts **API and Vite**, with managed Sandbox disabled. Open
`http://localhost:5173/sign-in` (substitute your Vite port), sign in with the
explicit owner, open the organization and send a Chat message. Synthetic should
complete with `Hello from synthetic.`; reload to check saved history. Real can
boot before inference is ready: configure **AI models → protected credential →
connection → metadata test/discovery → adopt offering → explicit default** before
expecting a provider reply. Missing configuration is not a fallback model.

After checking Chat, use these lifecycle commands when needed:

```sh
./scripts/dev-real.sh restart --api-only
./scripts/dev-real.sh stop
```

Normal `start` also prepares/reuses managed Sandbox infrastructure; read its
[trusted-local setup](17-observability-and-operations.md#trusted-local-opensandbox)
first. One server is allowed per Docker daemon. Register/verify an exact selected
image and grant the Definition/Instance/Session the required profile and IAM;
server reachability alone does not authorize execution. Build Workbench images
explicitly, separately from startup. `--with-store`/`--full` adds the independent
nopCommerce demo; it does not enable every integration. Decision remains
disabled/shadow by default; explicit `--with-decision` requires its own
[hardware/service preflight](decision-intelligence-runbook.md).

When sharing that server with another isolated installation, set an explicit
`Persistence__WorkspaceRoot` in private `.env` **before first use**. It must be
the managed canonical root or a single 32-character lowercase hexadecimal child
of `local/opensandbox-compose/<project>/canonical-workspaces`. Generate a unique
namespace once with `python3 -c 'import uuid; print(uuid.uuid4().hex)'`, save the
result in the path, and keep it stable across bootstrap/start/restart. The launcher
retains that selection and derives the matching daemon path. Outside roots and
arbitrary nested names are refused; never reset an installed shared root to make
a fresh setup pass. Profile verification and Instance setup verification are
separate; complete both before requesting a native command.

Ordinary stop/restart retains infrastructure and files. `stop --with-sandbox`
and `restart --recreate-sandbox` request generation-fenced cleanup and can lose
container-local state; reconcile unknown/pending effects first. Logs and PID
tracking live in `AGENTCORE_DEV_STATE_DIR` (default `local/dev`). Never kill an
untracked port owner or delete a database lock to make startup succeed.

## Packaged HTTPS first use

Use a distinct Compose project and loopback port, private operator configuration,
and an operator-provisioned PFX trusted by the browser, readable by container UID
1654. Set `AGENTCORE_TLS_CERTIFICATE_PATH` and `AGENTCORE_TLS_PASSWORD` in the
private inputs. Base HTTP serves health/static inspection only. Follow the
[canonical HTTPS bootstrap commands](17-observability-and-operations.md#docker-compose-integration-and-demo),
including the five one-use bootstrap values; normal startup never creates an
owner. Keep the same `-p`, env file and overlays on **every** command:

```sh
python3 scripts/compose.py --env-file /absolute/private/selfhost.env -p agent-core-local \
  -f docker-compose.yml -f docker-compose.https.yml config --quiet
# After the documented build and one-use bootstrap:
python3 scripts/compose.py --env-file /absolute/private/selfhost.env -p agent-core-local \
  -f docker-compose.yml -f docker-compose.https.yml up -d
```

Sign in at `https://127.0.0.1:5080/sign-in` (or the selected port). Add
`-f docker-compose.real.yml` **before** HTTPS on every command for packaged Real,
then configure tenant AI models. Keep Browser/OpenSandbox disabled: HTTPS selects
Production, whose preflight refuses native execution. The image also omits
Chromium/its driver. Use the native/hybrid path for those capabilities; the
app-sandbox overlay is not a supported authenticated Production combination.

`stop` retains containers and data; `down` removes this project's containers and
network while retaining its named volume. `down -v` destroys data and is never
an installed-state maintenance step. The disposable proof below creates its own
certificate, credentials and new volume, then removes only that fixture:

```sh
AGENTCORE_COMPOSE_PORT=15443 ./scripts/compose-sqlite-volume.sh
```

## Diagnose and recover

| Observation | Next action |
| --- | --- |
| Identity is not initialized | Confirm the intended **new** database/root, fill five inputs and explicitly bootstrap; never rebootstrap installed data |
| Port occupied / ownership unavailable | Stop only its known owner, or choose isolated ports/roots; keep untracked processes and lock files |
| Invalid/ambiguous dotenv input | Use one documented spelling/assignment; values are intentionally redacted |
| Real Chat has no usable model | Configure and test the tenant connection/offering/default; preserve the reported provider failure |
| Browser/Sandbox unavailable | Check binary, protected connection, selected image verification, profile and current IAM separately |
| `unqualified_native_effects` | Keep native execution in Development; do not weaken Production guards |
| Certificate/schema/key refusal | Restore the matching certificate, binaries, database and original keys/roots; never substitute fresh keys or InMemory |

Stop all writers before storage maintenance, including native Browser/Workbench,
independent worker hosts and external integrations. Preserve SQLite plus keys,
Workspace, Attachment, Artifact, DefinitionResource, browser profiles and Sandbox
metadata/backing roots as applicable. The existing [cold backup/restore
tool](17-observability-and-operations.md#cold-backup-restore-and-rollback) is qualified
only for the controlled **Synthetic aggregate-root** profile with Browser/Sandbox
disabled. Native/Real topology requires coordinated stopped-state preservation
of every external writer/root; do not infer the tool covers it. An uncertain
effect requires factual readback, never automatic replay.

Use [focused verification](16-testing-strategy.md#runtime-evidence-for-development-and-review)
and the ledger's remaining gates before claiming readiness. Owner manual UI/UX
validation remains Order 8; Group Chat remains Order 9.
