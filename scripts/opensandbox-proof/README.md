# OpenSandbox Stage A compatibility probe

This opt-in console program uses the **published** `Alibaba.OpenSandbox` 1.1.1
NuGet package. It is outside `AgentCore.sln` and is not a product executor. It
does not expose sandbox tools or change the running application. Stage A is
**blocked**, not accepted; see the [verification ledger](../../docs/reports/opensandbox-integration-verification.md).

Compile without starting any infrastructure:

```sh
dotnet restore scripts/opensandbox-proof/OpenSandboxProof.csproj --locked-mode
dotnet build scripts/opensandbox-proof/OpenSandboxProof.csproj --no-restore --nologo
dotnet run --project scripts/opensandbox-proof/OpenSandboxProof.csproj --no-build -- --help
```

For live execution, use a disposable isolated Linux runner with authenticated
OpenSandbox 1.1.1 and the digest-pinned components in `images.json`. The runner's
Docker socket must never be mounted into Agent Core, the probe image or an
operator application. A separately provisioned VM is preferred. A privileged
Docker-in-Docker runner requires explicit operator approval; its creation was
rejected by automatic approval review in this session and has **not** run.

`server.toml.example` is an unverified candidate. Its server listener must be
reachable only by the trusted test client. Generate a random temporary key;
inject it through `OPENSANDBOX_SERVER_API_KEY` on the server and supply the
same private value through `AGENTCORE_OSB_PROOF_KEY` to the probe. Do not commit
or print it. The example contains no known placeholder key; missing auth must
block startup. Never set `OPENSANDBOX_INSECURE_SERVER` or confirm key-free
startup. Use `dns+nft`, not
DNS-only enforcement. Preload all images into the isolated daemon before
disconnecting it from outbound networks. Do not mount application data, host
folders, credentials or the outer Docker socket.

Build `Dockerfile.probe` on that runner and push it to a private test registry,
or obtain a daemon-resolvable digest through its image store. Supply that exact
digest as the test image. An image ID is not a registry manifest digest. The
minimal probe image is not the final Python/Node/Office Workbench and has no
packages installed during commands.

Set these environment variables through a private shell/session without
printing their values:

- `AGENTCORE_OSB_PROOF_DISPOSABLE=1`: confirms that the target is disposable.
- `AGENTCORE_OSB_PROOF_URL`: loopback HTTP or isolated-runner HTTPS origin.
- `AGENTCORE_OSB_PROOF_KEY`: temporary server API key.
- `AGENTCORE_OSB_PROOF_IMAGE`: digest-pinned probe image reference.

Then run the same project without `--help`. It creates two bounded sandboxes,
checks auth, non-root execution, exact binary upload/download, nonzero exit
code and separated streams, argv, reconnect/file reuse, file isolation,
background status and confirmed interruption. It attempts to kill all known
owned sandboxes even after failure; remove the entire disposable runner after
the test and verify no unknown/orphan creation remains. Client disposal alone
does not kill an upstream sandbox. TTL is bounded to four minutes as an
additional cleanup limit, not proof of cleanup.

The only network probe targets **its own** sandbox's loopback execd port. It
fails if that port is reachable from command code. Upstream 1.1.1's nftables
rules accept loopback before deny rules; a deny-all policy alone is insufficient
evidence for the approved isolation requirement. Do not weaken this assertion
to make the compatibility test green. A trusted runner-level control that
separates command traffic from control-plane traffic needs real verification.

## Executed fallback

`Dockerfile.execd-fallback` copies the real pinned execd binary into a non-root
Python image. The runner builds it, uses a unique container name and the local
image content ID, and runs with network none, read-only root, all capabilities
dropped, no-new-privileges, 256 MiB / 0.5 CPU / 64 PIDs, no exposed ports or
host mounts, and private 16 MiB tmpfs at `/working` and `/tmp`. It verifies
those restrictions using `docker inspect`, waits for readiness, supplies the
fallback opt-in, pipes test input through stdin and always attempts removal.
The in-container script also checks PID 1 is execd before contacting loopback:

```sh
python3 scripts/opensandbox-proof/run-execd-fallback.py --run
python3 -m unittest discover -s scripts/opensandbox-proof -p 'test_*.py' -v
```

This fallback ran: six functional assertions passed and the workload-to-execd
loopback assertion **failed**, producing exit 1. `network=none` does not disable
loopback. It does not test the lifecycle server, SDK transport or egress
sidecar. The pinned upstream nftables source separately shows an early
loopback accept rule; that is static evidence, not a sidecar runtime test.
The reviewed runner additionally passed its actual restriction and cleanup
checks. Failed, missing or unconfirmed execution receipts cannot count as
successful commands or as blocked loopback traffic. Six receipt/guard
regressions pass offline; those fixtures do not establish SDK/live security
acceptance. Built images remain cached; the runner removes its own container.

Output contains scenario names and booleans, not keys, commands, upstream IDs,
private URLs or exception bodies. Nonzero exit means a failed/unknown probe or
cleanup. Even an exit of zero is only upstream compatibility evidence: it is
**not** OSB-01–18 acceptance. Direct-IP/private-peer/DNS/metadata isolation,
actual container security inspection, restart/orphans, Workspace transactions,
Core authorization, Run fencing, Office artifacts and browser UX remain
separate mandatory gates. No probes may target the operator's live host or an
external network.
