# Decision Intelligence operator runbook

The service is optional. Host `DecisionIntelligence:Enabled=false`, `ShadowMode=true` and tenant Routing `enabled=false` are independent defaults. Fixed/inherited execution and key-free Synthetic startup continue without Kev. A readiness response establishes transport/model identity only; it does not qualify routing quality.

## Offline preference migration

1. Stop all writers/dispatchers. Settle active Runs and approvals through their existing owners; preserve historical evidence. Record the deployed binary revision and SQLite path.
2. Make a SQLite backup with the host stopped (including any WAL state through SQLite's backup API), copy credential protection keys and existing workspace/artifact roots under the normal backup policy. Verify `PRAGMA integrity_check` and a restore into a disposable location. Never overwrite the only backup.
3. Run `python3 scripts/decision-migration-preflight.py /absolute/store.db --backup /absolute/verified-backup.db`. It opens both read-only and prints counts/schema/integrity only. Matching inventory is a sanity check; the restore test establishes recoverability.
4. Apply the normal offline EF migration to the stopped database using the matching binary. Migration `20261026100000_DecisionIntelligencePreferences` converts supported mutable fixed/inherited intent, never infers Auto, preserves concrete Run pins and updates the frozen preference-bearing approval contract. It removes the retired mutable pair columns and explicit-effort flag.
5. Restart with inference disabled. Read back Settings, Instance unattended preferences, Automations and saved Chat intent; inspect retained Run route IDs/efforts. Then exercise one new fixed Run before any Auto opt-in.

Preflight/migration refuses legacy Definition defaults in draft/publication/Session/Run configuration evidence, prepared Management/Automation approvals and orphan effort values. It does not reserialize immutable approval hashes or historical configuration, delete data, reset a volume or add legacy readers. On refusal, keep the original binary/data together and reconcile through an operator-reviewed plan. Fresh-state replacement needs explicit data-loss approval. Downgrade is deliberately refused; restore the matched binary/database/key backup. Implementation verification used disposable predecessor/fresh databases only.

## Optional local Kev lifecycle

The pinned source is [Kev kev-1.0](https://github.com/jaredpalmer/kev/tree/6b719c3c3f367295f6ef336f4f751cf5ff970abc); the checkpoint is [kev-4b](https://huggingface.co/jaredpalmer/kev-4b/tree/6cfce5c2fa4b4bd64026336ab649c5ca78857d52). Source lockfile resolution stays in the sidecar image. Python/CUDA are not added to the .NET application image. This Docker packaging requires NVIDIA support; 24 GB GPU memory is a planning recommendation, not a measured capacity guarantee. The same checkpoint also runs natively on Apple Silicon through upstream MLX; that verified path is described below.

Put matching private `KEV_API_KEY` and `DecisionIntelligence__ApiKey` values (16–256 printable non-space ASCII characters) in the private operator environment. Never put them in tracked configuration or command output. Set `DecisionIntelligence__Enabled=true`, `Backend=LocalKev`, endpoint `http://127.0.0.1:8009`; retain `ShadowMode=true` during qualification.

```sh
python3 scripts/decision-compose.py preflight
python3 scripts/decision-compose.py build
python3 scripts/decision-compose.py download
python3 scripts/decision-compose.py start
python3 scripts/decision-compose.py status
python3 scripts/decision-compose.py stop
```

Build and weight download are separate explicit network actions. `start` does neither. Download temporarily uses the dedicated egress overlay; inference runs offline on the internal network with a persistent cache. The service may need minutes for cold loading; ordinary bounded calls can time out until ready. Check readiness in Organization → Integrations → AI Models → Routing. No provider API key is needed by Kev. A missing host key reports notConfigured; wrong key, missing cache/checkpoint, unavailable GPU or crashed service refuses Auto unless its explicit eligible-default policy applies.

For the existing native application launcher, `scripts/dev-real.sh start --with-decision` explicitly selects the CUDA Docker sidecar. `--full` selects it only when enabled and skips it with an explanation when that platform's hardware preflight is unavailable. With an already-running native MLX service, use ordinary `scripts/dev-real.sh start` and the configured host endpoint/key; it does not require the sidecar. Stop a managed sidecar with the corresponding `--with-decision` or `--full` option. Do not start the Docker application container alongside the native application merely to obtain inference.

For a Docker application use the optional overlay:

```sh
python3 scripts/compose.py -f docker-compose.yml -f docker-compose.decision.yml config --quiet
python3 scripts/compose.py -f docker-compose.yml -f docker-compose.decision.yml up -d --no-build decision
```

Build/cache provisioning must already have completed for this Compose project/volume. The application talks to `http://decision:8009` over the internal network. The overlay publishes no service port; `docker-compose.decision.native.yml` is the explicit loopback-only override. The container is non-root, read-only with tmpfs, drops capabilities, has bounded resources/PIDs, no Docker socket and no privileged mode. The pinned-source hardening caps server queue/connections and disables cross-request prefix caching and state truncation. Authenticated GET `/v1/models` must witness the exact checkpoint and no truncation before every score request. Never expose the service publicly.

## Native Apple Silicon qualification

An M2 with 24 GiB unified memory successfully ran the exact pinned 4B artifact through MLX/bfloat16. Docker's missing NVIDIA runtime does not prevent this native path. [Real checkpoint evidence](reports/kev-native-verification-2026-10-26.md) records the checks, memory and limits; it supersedes the earlier hardware-only blocker for local checkpoint smoke.

Use a separate checkout at the pinned source SHA above, a dedicated cache, Python 3.13 and the upstream frozen lockfile. Commands below run from that checkout; `AGENT_CORE` and `KEV_CACHE` are absolute operator-selected paths. Set `KEV_API_KEY` privately to a 16–256 character value and keep a protected file containing that same value for the opt-in .NET checks. Do not put a key in command arguments, tracked files or test output.

```sh
uv sync --frozen --extra serve --no-dev --python 3.13
python3 "$AGENT_CORE/deploy/decision/harden.py" --source kev/serve.py
HF_HOME="$KEV_CACHE" HF_HUB_OFFLINE=0 HF_HUB_DISABLE_IMPLICIT_TOKEN=1 uv run --frozen --offline --no-dev --extra serve python "$AGENT_CORE/deploy/decision/download.py"
HF_HOME="$KEV_CACHE" HF_HUB_OFFLINE=1 HF_HUB_DISABLE_IMPLICIT_TOKEN=1 KEV_BACKEND=mlx KEV_DTYPE=bf16 KEV_PREFIX_CACHE=0 KEV_TRUNCATE_STATES=0 uv run --frozen --offline --no-dev --extra serve python -m kev.serve --run jaredpalmer/kev-4b@6cfce5c2fa4b4bd64026336ab649c5ca78857d52 --host 127.0.0.1 --port 8009
```

Apply hardening once to a clean pinned source; drift or reapplication refuses without writing a partial patch. `--no-dev` keeps serving from trying to synchronize development dependencies; the download command allows only the explicit model-download step to use the network. Confirm `/v1/models` reports the exact run, `backend=mlx`, `dtype=bfloat16`, `truncate_states=false` and prefix-cache size zero. Keep the host endpoint at `http://127.0.0.1:8009` and its private key matched to the service. Stop the native process with Ctrl+C; `decision-compose.py` remains the Docker lifecycle owner and does not manage this process.

With that service already running, execute from Agent Core:

```sh
AGENTCORE_LIVE_KEV=1 AGENTCORE_KEV_KEY_FILE=/absolute/private/kev.key dotnet test tests/AgentCore.Infrastructure.Tests/AgentCore.Infrastructure.Tests.csproj --filter FullyQualifiedName~LocalKevLiveTests --nologo
```

Default test runs skip these live checks. The smoke checks choice/yes-no/score transport and authentication; the routing check explicitly warms a public fixture, checks refusal/eligible fallback for its uncertain result, and records whether the 500 ms budget reaches validation or times out. Hardware with faster inference can legitimately change the deadline result; the test accepts either safe refusal and is not a portable performance acceptance gate. It sends only public fixture data and never starts/downloads a model or invokes a conversational provider.

The following coarse-routing numbers are historical `routing-v1` measurements; [routing-v2 evidence](reports/kev-routing-verification-2026-10-26.md) supersedes their routing-quality gap and records the current explicit native test budget. On the tested Mac, first inferences after two separate model loads took 11.87–16.79 s, short warm requests took 397–424 ms, and the longer production-shaped routing fixture took 812–848 ms. A successful readiness response verifies identity, not completed inference warm-up. Perform an explicit operator warm-up outside Run admission with a longer request deadline before judging warm latency; the first-inference samples also exceeded the host’s maximum configurable 10 s admission deadline. The host default 500 ms deadline refused routing; a test-only 2,000 ms budget reached validation, which then correctly rejected the 0.5751 top probability at the unchanged 0.7 threshold. Select a fixed model or explicitly permit an eligible deterministic default while qualifying; retain production defaults until the intended hardware/workload has an approved budget and quality evidence.

## Compose calling an external local Kev service

Use `docker-compose.decision.external.yml` instead of the CUDA sidecar overlay when inference already runs on the Mac. It adds no model container, GPU device, model cache or inference dependency. The same .NET adapter connects through Docker Desktop's `host.docker.internal`. Native MLX can remain bound to host loopback; this bridge was exercised with real inference, including a non-default port. The optional base/Real/HTTPS application configuration remains under its existing owners.

```sh
DecisionIntelligence__Endpoint=http://host.docker.internal:8009 python3 scripts/compose.py -f docker-compose.yml -f docker-compose.decision.external.yml config --quiet
DecisionIntelligence__Endpoint=http://host.docker.internal:8009 python3 scripts/compose.py -f docker-compose.yml -f docker-compose.decision.external.yml -f docker-compose.https.yml up -d --no-build agent-core
```

Set matching `KEV_API_KEY`, host `DecisionIntelligence__ApiKey`, certificate inputs and deliberate `DecisionIntelligence__Enabled` in the private operator environment. Explicit endpoint override is important when the shared native `.env` contains `127.0.0.1`: container loopback refers to the app container. Override the port on both service and endpoint together when needed. `Endpoint` permits operator-selected HTTP ports on literal loopback, `decision`, or `host.docker.internal`; named Docker destinations must resolve to private IPv4/loopback. Redirects, proxies, public/metadata destinations and URL credentials/query/path remain denied. Docker Desktop supplies this DNS name; Linux operators must explicitly provide their own trusted host-gateway mapping if using that route. Neither this overlay nor the Decision Engine checks for NVIDIA/CUDA.

## Frozen held-out quality and calibration check

The checked-in public fixture `scripts/fixtures/kev-quality-v1.json` has 12 calibration and 36 held-out cases across choice/yes-no/score and four languages, including quoted untrusted text. Its split, labels and 0.7 threshold were frozen before inference. Labels/split are never sent to Kev. Run from Agent Core against the already-running native checkpoint:

```sh
python3 scripts/decision-quality.py --key-file /absolute/private/kev.key --output /absolute/evidence/quality-results.json
```

The explicit probe verifies the exact checkpoint, disabled truncation/cache, typed distributions and score consistency; it reports accuracy, constant-first-option baseline, Brier/log-loss, ten-bin reliability/ECE, selective coverage/errors and latency. It has bounded requests and an overall evaluation deadline, never retries inference, never installs/starts a service, and does not run with default tests. `--endpoint` supports a chosen local port. Available-hardware results are in the linked native report: 33/36 held-out labels correct, 28/28 accepted labels correct at 0.7, with all three errors refused below threshold. Noul coverage is lower; no initiative/event integration is enabled by this evaluation. The controlled fixture is not a claim about unseen production distributions or comparative conversational-provider quality.

## Deliberate Auto qualification

Routing `allowConfiguredDefault` defaults false. Enable it only when the configured default is an acceptable deterministic answer; eligibility is still checked for each request. Shadow decisions never replace that answer. Without an allowed eligible default, shadow/unavailable/low-confidence Auto refuses new work with repair guidance; selecting a fixed model restores the deterministic path. Same-Run retries and approvals never rank again.

Before effective LocalKev Auto, freeze a representative held-out task set and record deterministic baseline, eligibility failures, multilingual/vision/tool cases, option-order sensitivity, false positives, calibration and a stated quality/cost tradeoff. Measure incremental warm p50/p95 latency, saturation, cold start and actual GPU/CPU/RAM footprint against an operator-approved budget. The default 0.7 option-probability threshold and 500 ms deadline are uncalibrated acceptance/cancellation controls. Unknown cost, context capacity and latency stay unknown in the UI.

The available-hardware record now includes real MLX inference, calibration/held-out results and measured M2 bounds. Effective Auto remains an explicit operator/tenant decision against those bounds: set host `ShadowMode=false` and enable tenant Auto only for the intended policy/workload. Record model/source/policy/settings revisions and rollback by disabling tenant Auto or selecting fixed; do not retarget admitted Runs. NVIDIA-specific latency/throughput qualification remains separately pending and is not required to complete this local work.

## Test Kev yourself in the Real profile

Real conversation providers and Decision Intelligence are independent. Kev ranks eligible tenant offerings; it is not a tenant chat offering and needs no hosted-provider key. To exercise a complete Real conversation, separately configure a working tenant connection/credential and enabled offerings through AI Models. With only one eligible offering, Auto uses `singleEligible` without model-ranking inference; a model with fewer than two declared reasoning levels also needs no effort ranking.

### 1. Prepare native MLX once

Prerequisites: Apple Silicon, sufficient free unified memory (the verified M2 has 24 GiB), `git`, `uv`, Python 3.13, and the normal .NET 10/pnpm development tools. The verified MLX device allocation peak was about 14.78 GiB; leave room for macOS and the application. Run from the Agent Core repository. Use a durable private directory rather than relying on a temporary test environment:

```sh
export AGENT_CORE="$PWD"
export KEV_HOME="$HOME/.cache/agent-core-kev"
export KEV_CACHE="$KEV_HOME/huggingface"
export KEV_KEY_FILE="$KEV_HOME/service-key"
umask 077
mkdir -p "$KEV_HOME"
python3 - <<'PY'
import os, secrets
p = os.environ['KEV_KEY_FILE']
if not os.path.exists(p):
    fd = os.open(p, os.O_WRONLY | os.O_CREAT | os.O_EXCL, 0o600)
    with os.fdopen(fd, 'w') as f:
        f.write(secrets.token_hex(24))
PY
git clone https://github.com/jaredpalmer/kev.git "$KEV_HOME/source"
git -C "$KEV_HOME/source" checkout --detach 6b719c3c3f367295f6ef336f4f751cf5ff970abc
cd "$KEV_HOME/source"
uv sync --frozen --extra serve --no-dev --python 3.13
python3 "$AGENT_CORE/deploy/decision/harden.py" --source kev/serve.py
HF_HOME="$KEV_CACHE" HF_HUB_OFFLINE=0 HF_HUB_DISABLE_IMPLICIT_TOKEN=1 uv run --frozen --offline --no-dev --extra serve python "$AGENT_CORE/deploy/decision/download.py"
```

Skip clone/sync/hardening/download when reusing that prepared checkout/cache. Hardening deliberately refuses a second application. Download is explicit and may take time; ordinary startup stays offline.

### 2. Start Kev in Terminal A

Use the same private paths as above (exports are local to each terminal). Keep this foreground process running:

```sh
export KEV_HOME="$HOME/.cache/agent-core-kev"
export KEV_CACHE="$KEV_HOME/huggingface"
export KEV_KEY_FILE="$KEV_HOME/service-key"
export KEV_API_KEY="$(cat "$KEV_KEY_FILE")"
cd "$KEV_HOME/source"
HF_HOME="$KEV_CACHE" HF_HUB_OFFLINE=1 HF_HUB_DISABLE_IMPLICIT_TOKEN=1 KEV_BACKEND=mlx KEV_DTYPE=bf16 KEV_PREFIX_CACHE=0 KEV_TRUNCATE_STATES=0 uv run --frozen --offline --no-dev --extra serve python -m kev.serve --run jaredpalmer/kev-4b@6cfce5c2fa4b4bd64026336ab649c5ca78857d52 --host 127.0.0.1 --port 8009
```

### 3. Warm up and verify in Terminal B

From Agent Core, set `KEV_KEY_FILE` to the same protected key file. This opt-in test uses public fixtures, performs an explicit up-to-60-second first-inference warm-up outside admission, then requires actual ranked model/effort outcomes, replay dedupe and SQLite pin preservation. It does not contact a conversational provider or consume hosted-provider credits:

```sh
export KEV_KEY_FILE="$HOME/.cache/agent-core-kev/service-key"
export AGENTCORE_LIVE_KEV=1
export AGENTCORE_KEV_KEY_FILE="$KEV_KEY_FILE"
export AGENTCORE_KEV_ENDPOINT=http://127.0.0.1:8009
AGENTCORE_ROUTING_ADMISSION_RESULTS=/tmp/kev-admission-results.json dotnet test tests/AgentCore.Infrastructure.Tests/AgentCore.Infrastructure.Tests.csproj --filter FullyQualifiedName~RealKev_tenant_Auto_admission --nologo -m:1 -p:UseSharedCompilation=false
```

Expected: `Passed: 1`, with no configured-default success path. A readiness response alone does not warm inference. First-use work on the verified Mac took 12–17 seconds, exceeding any allowed Run-admission deadline. Do not use a new Chat Run as the warm-up. For the frozen 4-calibration/24-held-out routing check:

```sh
AGENTCORE_ROUTING_RESULTS=/tmp/kev-routing-results.json dotnet test tests/AgentCore.Infrastructure.Tests/AgentCore.Infrastructure.Tests.csproj --filter FullyQualifiedName~LocalKevRoutingQualityTests --nologo -m:1 -p:UseSharedCompilation=false
```

Default test runs skip both. The [routing report](reports/kev-routing-verification-2026-10-26.md) records the fixed fixture hash, accepted distributions, calibration assessment and limits.

### 4. Configure and start the Real application

Edit the existing private root `.env`; keep exactly one assignment for each option. If the file does not exist, start from `.env.example`. `.env` is read as literal data, so do not put `$(cat ...)`, `${...}` or a file path in `ApiKey`: enter the actual contents of the protected key file. Keep this key private and do not enable shell tracing.

```dotenv
DecisionIntelligence__Enabled=true
DecisionIntelligence__Backend=LocalKev
DecisionIntelligence__Endpoint=http://127.0.0.1:8009
DecisionIntelligence__ModelId=jaredpalmer/kev-4b@6cfce5c2fa4b4bd64026336ab649c5ca78857d52
DecisionIntelligence__ApiKey=REPLACE_WITH_THE_SAME_PRIVATE_SERVICE_KEY
DecisionIntelligence__ShadowMode=false
DecisionIntelligence__RequestTimeoutMs=2000
DecisionIntelligence__MaxConcurrentRequests=1
```

`2000` is the explicit warmed-M2 **routing** test budget shared by model plus effort selection. Whole tenant admission also includes IAM/catalog/SQLite work and can take longer. Global defaults remain disabled, shadowed and 500 ms; this profile is not a production throughput promise. The launcher loads `.env` automatically; its entries override inherited shell variables. Match endpoint/key and restart after edits:

```sh
scripts/dev-real.sh restart --api-only
```

Use ordinary `restart` instead if the tested task needs the existing OpenSandbox setup. Neither command manages native Kev. Do not select `--with-decision` for native MLX: it explicitly requests the CUDA Docker sidecar. Open `http://localhost:5173`, sign in and select the intended organization. For a fresh installation, follow the existing [explicit identity bootstrap](17-observability-and-operations.md#explicit-identity-bootstrap-foundation) before starting; do not reset an existing database.

### 5. Opt in through the UI and observe

1. Organization → Integrations → AI Models: configure at least two currently available offerings with truthful tools/instruction-hierarchy/reasoning capabilities. To exercise effort routing, the chosen reasoning offering must declare at least two supported levels. Kev cannot compare undeclared cost, latency or actual generated-answer quality.
2. Open Routing, refresh service readiness and confirm Ready with shadow off. Check **Allow Auto for new Runs**, leave **Allow an eligible configured default…** unchecked for strict testing, retain minimum option probability `0.7`, then **Save routing**. Organization model-management permission is required.
3. Open a new Chat and select model **Auto**; choose reasoning **Auto** as well to exercise effort selection. Send `Greet the team.` and then a separate task such as `Diagnose the root cause of a deadlock and prove the proposed fix preserves the invariant.` Compare the observed selected model/effort. Different choices depend on your actual eligible catalog; do not expect the fixture's direct/reasoning results from arbitrary offerings.
4. A successful reply alone does not prove Kev routing. The protected `GET /api/v2/tenants/{tenantId}/sessions/{sessionId}/conversation` returns saved preference and effective model/effort with `observedFromRun=true`; it does not expose private decision evidence. For a reproducible strict proof, inspect `/tmp/kev-admission-results.json` from step 3: its fixture pins require `Basis=ranked`, `EngineId=localKev` and the exact checkpoint (`fixedAutoReasoning` is the internal basis when only effort is Auto). `configuredDefault` is a fallback; `singleEligible` is deterministic. The native Kev terminal should show model-witness GETs and inference POSTs. A normal Chat response also calls its separately configured conversational provider; the opt-in test sends only public data to Kev and makes no conversational-provider call.
5. If Auto refuses, read the reported reason. Timeout: verify warm-up, memory pressure, endpoint and measured workload budget. LowConfidence: select a fixed model or deliberately allow an eligible configured default; do not lower the threshold merely to make the test pass. Unavailable/notConfigured: check matching private key, source/cache/model identity, service process and endpoint. A stopped service must refuse strict Auto, while a fixed choice remains usable.

Rollback: uncheck **Allow Auto for new Runs** or select a fixed Chat model; set host `DecisionIntelligence__Enabled=false` and restart if stopping inference. Stop native Kev with Ctrl+C in Terminal A. Already admitted Runs retain their original pins. Do not delete state, migration evidence or model cache to stop the service. MLX timings do not qualify NVIDIA production latency/throughput.
