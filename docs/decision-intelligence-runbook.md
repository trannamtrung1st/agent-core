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

The pinned source is [Kev kev-1.0](https://github.com/jaredpalmer/kev/tree/6b719c3c3f367295f6ef336f4f751cf5ff970abc); the checkpoint is [kev-4b](https://huggingface.co/jaredpalmer/kev-4b/tree/6cfce5c2fa4b4bd64026336ab649c5ca78857d52). Source lockfile resolution stays in the sidecar image. Python/CUDA are not added to the .NET application image. This packaging requires NVIDIA Docker support; 24 GB GPU memory is a planning recommendation, not a measured capacity guarantee. Native Apple Silicon serving is not supplied by this image.

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

For the existing native application launcher, `scripts/dev-real.sh start --with-decision` selects this dependency explicitly. `--full` selects it only when enabled and skips it with an explanation when hardware preflight is unavailable. Ordinary start is unchanged. Stop with the corresponding `--with-decision` or `--full` option. Do not start the Docker application container alongside the native application to obtain inference.

For a Docker application use the optional overlay:

```sh
python3 scripts/compose.py -f docker-compose.yml -f docker-compose.decision.yml config --quiet
python3 scripts/compose.py -f docker-compose.yml -f docker-compose.decision.yml up -d --no-build decision
```

Build/cache provisioning must already have completed for this Compose project/volume. The application talks to `http://decision:8009` over the internal network. The overlay publishes no service port; `docker-compose.decision.native.yml` is the explicit loopback-only override. The container is non-root, read-only with tmpfs, drops capabilities, has bounded resources/PIDs, no Docker socket and no privileged mode. The pinned-source hardening caps server queue/connections and disables cross-request prefix caching and state truncation. Authenticated GET `/v1/models` must witness the exact checkpoint and no truncation before every score request. Never expose the service publicly.

## Deliberate Auto qualification

Routing `allowConfiguredDefault` defaults false. Enable it only when the configured default is an acceptable deterministic answer; eligibility is still checked for each request. Shadow decisions never replace that answer. Without an allowed eligible default, shadow/unavailable/low-confidence Auto refuses new work with repair guidance; selecting a fixed model restores the deterministic path. Same-Run retries and approvals never rank again.

Before effective LocalKev Auto, freeze a representative held-out task set and record deterministic baseline, eligibility failures, multilingual/vision/tool cases, option-order sensitivity, false positives, calibration and a stated quality/cost tradeoff. Measure incremental warm p50/p95 latency, saturation, cold start and actual GPU/CPU/RAM footprint against an operator-approved budget. The default 0.7 option-probability threshold and 500 ms deadline are uncalibrated acceptance/cancellation controls. Unknown cost, context capacity and latency stay unknown in the UI.

Only after that evidence supports the chosen policy should the operator set host `ShadowMode=false` and the tenant manager enable Auto. Record model/source/policy/settings revisions and rollback by disabling tenant Auto or selecting fixed; do not retarget admitted Runs. No real checkpoint/GPU/quality acceptance is claimed by the local Synthetic delivery.
