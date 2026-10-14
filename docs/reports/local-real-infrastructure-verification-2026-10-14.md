# Native Real infrastructure separation — 2026-10-14

The native launcher already targeted only the OpenSandbox service, but its Compose
input merged the default Synthetic application definition. The stopped acceptance
app on port 6880 also retained the image tag `agent-core:synthetic`, although its
last configured provider profile was Real. Both made the topology misleading.

The normal local path now has one infrastructure Compose file containing only
OpenSandbox. nopCommerce and its database remain a separate optional demo project.
The API and Vite run natively in Real mode:

```bash
scripts/dev-real.sh start
scripts/dev-real.sh restart
scripts/dev-real.sh status
scripts/dev-real.sh stop                   # native processes only
scripts/dev-real.sh start --with-store     # optional store demo
scripts/dev-real.sh stop --full            # owned infrastructure too, data retained
scripts/dev-real.sh start --api-only       # no managed Docker startup
```

`docker-compose.sandbox.yml` defines no Agent Core service or app-data volume.
`opensandbox-compose.py` loads only that file. Explicit packaged app integration
uses `docker-compose.app-sandbox.yml` alongside the existing app files; Real
packaging now uses `agent-core:real`. The ordinary key-free Synthetic Compose path
remains available separately. Native startup explicitly supplies Real configuration
and checks the health profile before reporting success. Recorded component
selections make status truthful for optional infrastructure; a selected failed
store returns failure. Plain restart reuses infrastructure. Private key projection,
Workbench pins, one-server ownership and generation-fenced cleanup are unchanged.

Focused local verification:

- 15 portable launcher regressions passed, including default/explicit selections,
  order-independent flag conflicts, Synthetic health rejection, tracked process
  cleanup, foreign process preservation, selected-store status and pending cleanup.
- 7 sandbox configuration/ownership regressions passed, including single-file
  infrastructure selection and private key/foreign-server protections.
- Actual Docker Compose config validated four topologies: infrastructure-only,
  explicit Synthetic app with Sandbox, explicit Real app with Sandbox, and the
  unchanged ordinary Synthetic app. Resolved credentials were never printed.
- Actual `dev-real.sh start` reused the pinned Workbench/server, confirmed native
  Real API/Vite readiness and requested no store startup or Docker app service.
- Authenticated image readiness executed non-root Python in a real disposable
  sandbox, cleaned it up and preserved the Global Profile revision. Initial provisioning checks did not confirm success. An independent server
  restart was observed during verification; retry after readiness succeeded. No product execution was automatically replayed.
- Removed only our stopped acceptance app on port 6880 after checking project,
  service, stopped state, image tag and actual profile. Its data volume remains.
  Existing nopCommerce containers, their data and unrelated resources were left
  untouched. Final inspection shows no Docker Agent Core app. Another live Sandbox workload
  was observed and left untouched; our image-check workload was cleaned up.
- Final native API/Vite health reports Real and OpenSandbox status is Ready.
  The shared checkout and normal stack were independently restarted during this
  work; the existing store is still running. No attempt was made to override
  another startup's component selections.
- Bash syntax, Python compilation and scoped whitespace checks passed. README,
  Operations, deployment/configuration guidance, architecture and plan summaries
  were synchronized with these launch defaults.

This is a local operations correction, not milestone acceptance. No hosted model
call, external CI, full backend/frontend suite, app image build or coding/Office
end-to-end demo was rerun. Existing acceptance evidence remains separate. The
live check used Docker Desktop; native Linux hardware was not exercised. The
trusted single-user Docker threat model and deferred production isolation remain
unchanged. Normal shutdown retains application data, volumes and images.
