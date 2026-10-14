# nopCommerce restart polish — 2026-10-15

This addresses the remaining optional launcher finding over `ebabbbd1`.
OpenSandbox's accepted trusted-local scope stays closed; no architecture or
milestone acceptance is started. [Operations](../17-observability-and-operations.md#running-after-implementation)
owns startup, refresh and reset commands.

## Behavior

`nopcommerce-demo.sh start` reuses a healthy initialized demo. Its read-only
checks require the named containers to belong to the expected Compose project
and services, a running healthy database, running web service, installed-site
redirect, deployed OrderEvents files and installed catalog entry, and successful
storefront/admin responses whose redirects stay on the loopback origin.

Healthy reuse invokes no Compose startup, container restart, seed write, plugin
build/copy or environment-file preparation. Existing demo orders and stock
changes do not invalidate readiness. Missing or unhealthy prerequisites enter
the existing full initialization path. `start --refresh` explicitly repeats
initialization for seed/plugin/webhook configuration updates; `reset` retains its
explicit project-scoped volume deletion. Invalid arguments fail before Docker.

## Local evidence

The real Bash scripts execute against disposable local HTTP services and
stateful Docker command fixtures. Native full-restart verification launches and
replaces actual detached temporary API/Vite stand-in processes. Docker/provider
calls are stubbed; credentials are fixture-only. The initial restricted run
could not bind loopback ports; the runnable checks use approved local process
and loopback access.

| Scenario | Expected and observed |
| --- | --- |
| Repeated healthy start without an environment file | Both succeed with reuse; no environment file is created and every Docker call is read-only |
| `dev-real.sh start --full`, then `restart --full` | Both invoke the real store script and report reuse; no store Compose/restart/copy or Sandbox stop is requested |
| Missing, unhealthy, stopped or foreign containers | Full initialization is requested; fixture exit 42 is preserved before any actual container mutation |
| Missing plugin files, malformed catalog or absent installation entry | Initialization is requested |
| Installer page, off-origin/installer redirect or failed storefront | Initialization is requested |
| Explicit refresh of a healthy store | Initialization is requested without reuse checks |
| Explicit reset | Only the nopCommerce project receives `down -v`, followed by initialization |
| Invalid options | Exit 2 before any Docker call |

Focused verification passes **38 tests** (21 native launcher, 3 Compose progress,
7 store reuse and 7 OpenSandbox configuration/ownership tests):

```bash
python3 -m unittest scripts.tests.test_dev_real scripts.tests.test_nopcommerce_progress scripts.tests.test_nopcommerce_reuse scripts.tests.test_opensandbox_config -v
```

Bash syntax, changed Markdown relative file links/fence balance and
`git diff --check` also pass.

The hosted Synthetic backend job now runs these launcher regressions alongside
its existing gates. Hosted CI is explicitly requested for the final committed
source; its run outcome is reported separately from this local evidence.

No live nopCommerce containers were running during inspection. The real-store
Docker initialization/recovery path was not rerun; this follow-up uses the
executed HTTP/Docker fixture fallback for the changed selection behavior.
Existing user data and unrelated Docker workloads were left intact. No live
provider, hardware or new milestone acceptance is claimed.
