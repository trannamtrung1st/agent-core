# OpenSandbox SDK and topology proof

This opt-in console program uses exact `Alibaba.OpenSandbox` 1.1.1. It is outside the application solution and complements the real Core integration/browser gates. The supported target is the owner-approved **trusted single-user local demo** using one authenticated OpenSandbox server on the existing Docker daemon. Only that server may mount the daemon socket. No DinD or extra VM is required or started.

[Operations](../../docs/17-observability-and-operations.md#trusted-local-opensandbox) owns setup. `python3 scripts/opensandbox-local.py accept --ui --build-workbench` supplies private configuration, builds the Workbench, runs this proof plus integrated tests, recreates Core/server, and cleans its owned resources. Do not start two servers on one daemon: upstream sidecar cleanup can interfere across them. Never use arbitrary live/operator resources as probe targets.

Offline checks:

```sh
dotnet restore scripts/opensandbox-proof/OpenSandboxProof.csproj --locked-mode
dotnet build scripts/opensandbox-proof/OpenSandboxProof.csproj --no-restore --nologo
dotnet run --project scripts/opensandbox-proof/OpenSandboxProof.csproj --no-build -- --help
python3 -m unittest discover -s scripts/opensandbox-proof -p 'test_*.py' -v
```

Manual live execution requires `AGENTCORE_OSB_PROOF_DISPOSABLE=1`, a loopback HTTP/private HTTPS origin in `AGENTCORE_OSB_PROOF_URL`, private `AGENTCORE_OSB_PROOF_KEY` and immutable `AGENTCORE_OSB_PROOF_IMAGE`. The optional private/gateway targets must come from inspection of the owned server. Do not print keys or SDK exception bodies. The runner supplies these values without writing application secrets into source or frontend payloads.

The proof checks required auth, non-root identity, enforced cgroup CPU/memory/PIDs, capabilities/no-new-privileges, binary transfer, argv, separated stdout/stderr and nonzero exit, reconnect, separate files, tracked background status/stop, absent socket/server credentials, offline DNS/IP/metadata and private lifecycle/gateway connectivity. It explicitly observes reachable own-container execd loopback and reports `productionIsolationAccepted=false`. That observation is an acknowledged local-demo limitation, not a production security acceptance. All owned sandboxes are killed in `finally`; unconfirmed cleanup fails the gate.

`images.json` and `server.toml.example` pin the tested umbrella release and topology. Docker Desktop uses workstation loopback/host.docker.internal; native Linux uses the inspected bridge gateway for workload port routing. Core still has no socket or canonical writable mount. Workbench/license details are in [deployment sources](../../deploy/sandbox/README.md), and actual evidence is in [verification](../../docs/reports/opensandbox-integration-verification.md).

The standalone execd fallback and its offline tests remain **historical diagnostic fixtures**. Its stronger loopback-isolation assertion deliberately fails against upstream behavior; it is neither the product executor nor a replacement acceptance path. The earlier rejected privileged-DinD attempt remains recorded historically and must not be retried.
