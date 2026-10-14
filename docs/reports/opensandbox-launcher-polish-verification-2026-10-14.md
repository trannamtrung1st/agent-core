# OpenSandbox launcher polish — 2026-10-14

This follow-up fixes the two launcher findings in the owner's OpenSandbox review.
It changes local operations only and does not reopen P10 or start P11 acceptance.
The existing trusted single-user Docker scope and deferred isolation work remain
unchanged. [Operations](../17-observability-and-operations.md#compose-sandbox-and-full-native-real-demo)
owns the commands.

## Result

- `start`/`restart --api-only` explicitly export `OpenSandbox__Enabled=false`
  after loading private environment configuration. `--with-sandbox` retains the
  explicit authenticated handoff and enables Sandbox.
- Ordinary restart, including `--full` and `--with-sandbox`, reuses selected
  infrastructure without requesting cleanup of Session sandboxes.
- `restart --recreate-sandbox` explicitly requests the existing generation-fenced
  cleanup and owned-server stop/start. It warns before cleanup that container-local
  packages, processes and unsynchronized files may be lost, while canonical files
  are preserved. Invalid selection flags fail before stopping services; pending
  cleanup prevents replacement startup.

## Focused local evidence

The two regression cases were run before the fix: API-only passed `true` to the
API despite its selection, and full restart issued `sandbox:stop` and `store:stop`.
Both failed their intended assertions. After the fix:

`python3 -m unittest scripts/tests/test_dev_real.py scripts/tests/test_opensandbox_config.py -v`
passes **27 tests** (20 launcher, 7 configuration/ownership). The launcher executes
real detached temporary processes with stubbed provider/Docker/readiness commands;
the topology tests use fixtures. Checks include environment precedence, explicit
enablement, server/store preservation, warned recreation, pending cleanup without
replacement startup, invalid flags, private handoff and process ownership/shutdown.
An initial loopback bind was denied by the execution sandbox; the runnable tests
were executed with approved local process/loopback access. One new fixture was
corrected to wait for the first stub API to record its environment before restart;
this did not require a product change.

Live execution uses disposable Core data and process tracking in
`/private/tmp/agent-core-launcher-polish.t8OXEc`, API `6580`, Vite `6573`, and the
dedicated `agent-core-launcher-polish` Compose server on `18081`. The private
environment deliberately enables OpenSandbox; the later API-only check also
loads the running server's valid private credentials. No hosted model request is
made and no user Session/database is reset.

| Scenario | Expected and observed |
| --- | --- |
| API-only startup with enablement in private environment | Native Real API/Vite start; protected platform status is exactly `Disabled` |
| Default startup with the Compose server | Authenticated `Ready`; actual non-root Python/Node generate and reopen DOCX/XLSX in disposable setup verification |
| Existing setup acceptance script | Passes image-only versus failing setup, exit 7 and separate stderr, durable exact-revision observation, stale CAS 409 and anonymous-owner 401 |
| `restart --with-sandbox` | Same running server ID and start timestamp; persisted setup-failure observation survives native API recreation |
| `restart --api-only` with a healthy server and valid credentials | Platform status is exactly `Disabled`; server retains its running state and original start timestamp |
| `restart --api-only --with-sandbox --recreate-sandbox` | Warning precedes cleanup; owned server stops/starts and its start timestamp changes; persisted configuration/failure observation survives |
| Execution after explicit recreation | The same Python/Node/DOCX/XLSX, setup failure, persisted output, owner and stale-CAS acceptance passes again |

The live acceptance command is
`python3 scripts/tests/opensandbox_compose_acceptance.py --api-url http://127.0.0.1:6580 --expect-profile Real`.
Readiness alone is not counted as execution: its positive case creates and reads
actual Office files; its negative case executes failing setup and inspects the
persisted failure through the owned API.

Final `stop --with-sandbox` stopped only the disposable API/Vite/server. Its three
ports are closed, PID tracking is removed, and no running test workload remains;
data, images, private handoff and volumes are retained. Final execution output is
saved in `/private/tmp/agent-core-launcher-polish.t8OXEc/acceptance-after-recreation.log`.
Bash syntax, Python compilation, changed Markdown relative file links/fence
balance and `git diff --check` pass. The verified launcher SHA-256 is
`05901cd8426ee8d4fb95478d89e5e8395ae8e43cb28ef5d06570f237608c74ce`.

## Verification boundaries

This is focused verification of the current local launcher changes over source
`2766d08e`, not full milestone acceptance. The optional nopCommerce full-mode
selection/preservation has portable launcher evidence; live store startup was not
rerun. Browser journeys, full backend/frontend suites, packaged app overlays and
native Linux hardware were not rerun for this operations-only change. Historical
Sandbox acceptance remains evidence for its original sources. Hosted CI was not
dispatched, rerun or awaited.
