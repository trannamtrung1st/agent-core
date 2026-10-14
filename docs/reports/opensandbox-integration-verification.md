# OpenSandbox integration verification

## Current main integration — 2026-10-14

The owner selected **main `ed943429c619d1e28f0878222328a8c6af4b43b9`** as the final
synchronization snapshot. It adds public binary downloads and responsive exact
Drive approval tables. Conflicts preserve those contracts/UI and sandbox
ownership/recovery. Main's immutable General Assistant v26–v29 are byte-identical;
new **v30** combines main v29's public-download/Drive/Office grants with the five
modern sandbox tools and its approved profile policy. Both the real Workbench
browser runner and the combined Drive Office journey use v30.

Focused selected-snapshot checks pass: **43 Infrastructure**, **10 API**,
**19 Application** and **29 frontend cases / three files**. They cover owned
binary download/commit/retry, catalog and immutable versions, sandbox store,
Drive exact approvals and sandbox inspection. The credential fixture's AntD
field-validation callbacks could survive unmount; its stateful owner and real
Cancel flow now also cancel fixture-owned timers at teardown. The focused rerun
has zero unhandled errors. TypeScript/Vite builds pass. Real SDK acceptance passes **31 cases / zero skips**. Both v30 browser phases
pass (36.2 seconds before / approximately 1.5 minutes after actual Core/server
recreation), including owned coding/Office artifacts, independent background
work, output streaming, cancellation and reset/recovery. MCP separately opens
Add credential, switches purpose, cancels and confirms closed dialog/focus
returned to Add credential; zero console errors. The required exact-source hosted results for the corrected source are published
in [PR #15 checks](https://github.com/trannamtrung1st/agent-core/pull/15/checks).

The first selected-snapshot hosted candidate `6c03a1fc` in
[workflow 38531482334](https://github.com/trannamtrung1st/agent-core/actions/runs/38531482334)
failed: fresh NuGet advisories reject existing SignalR MessagePack 10.0.12 and
transitive PKCS 10.0.0; core browser discovery also rejects main's import of an
absent spreadsheet helper. SignalR protocol references and the Infrastructure
PKCS override now use Microsoft's patched **10.0.13** versions, retaining
warning-as-error/audit enforcement. See the
[SignalR advisory](https://github.com/dotnet/aspnetcore/security/advisories/GHSA-86xp-94pg-8r6r)
and [PKCS advisories](https://github.com/dotnet/runtime/security/advisories/GHSA-2fmv-rv2c-59x7).
Fresh restore and the whole solution vulnerability audit pass; **53 API**
(MessagePack wire/download/sandbox) and **17 email/MIME** cases pass. The Drive
fixture uses its existing actual spreadsheet cell control. A local responsive
check also measured the closing preview modal immediately after Escape; the
trace shows resize only 11 ms later. A subsequent check pressed Escape while
the approval entrance animation remained active. The fixture now awaits actual
preview closure and settled approval entrance before layout/keyboard assertions,
and polls the same no-overflow condition while React applies breakpoint changes.
The complete v30 Drive/Office/browser journey passes (**38.1 seconds**), including
exact seven-target Reject/Escape/approval and desktop/mobile version wrapping,
with the existing assertions and timeout bounds. No user
database reset or unrelated service cleanup is authorized.

A later hosted candidate `4bb5a790` in
[workflow 38533358603](https://github.com/trannamtrung1st/agent-core/actions/runs/38533358603)
reported **30 native passes / one teardown cleanup failure** in the deadline
case. The underlying provisioning cause was masked and is not recoverable from
its retained TRX. Fixture teardown now uses the existing metadata reconciliation
path when create did not return an identity, confirms Stop and retains its
SQLite evidence if cleanup remains pending. Provisioning failures log only
Core Session/generation, normalized category/status and whether an identity was
linked; no SDK body/message/provider identity/key is logged. A real SDK/Kestrel
regression returns HTTP 500 with a secret sentinel, asserts one create POST,
NeedsReconciliation plus safe diagnostic evidence, then metadata-only cleanup
to Terminated without replay. **All seven offline recovery cases pass**; the
real full rerun passes **31 cases / zero skips** and both browser restart phases
(20.6 seconds before / approximately 1.4 minutes after). The prior hosted failure
remains recorded; the final corrected source still requires the PR's native gate.

On `f9bf52bd`, **seven hosted gates pass**, including 4,023 backend passes/32
opt-in skips, 1,028 frontend passes/128 files, both builds, Compose and native
31 cases/zero skips plus both browser restart phases (32.8 seconds / approximately
1.5 minutes). Core shard 2 passes; shard 1 has 114 passes/one Drive Escape failure
in [workflow 38535978220](https://github.com/trannamtrung1st/agent-core/actions/runs/38535978220).
The retained frame shows a Stop tooltip behind the approval: its portal registers
after the modal and consumes the first Escape. The prior entrance wait alone
could not fix that ownership bug. The shared composer now unmounts Send/Stop
help while approval is pending, preserving the existing controls and resuming
normal tooltips afterwards. The browser regression deliberately opens Send help
before submitting both exact Drive requests, checks that action popups disappear,
and verifies single-Escape rejection without mutation followed by exact approval.
The complete strengthened v30 journey passes (**45.9 seconds**) at all existing
viewport bounds; 45 composer/approval cases and TypeScript/Vite builds pass. The
new tooltip regression advances only its logical timers inside act; existing
Composer fixtures retain their React act warnings, with zero unhandled errors.
The final corrected source requires all eight PR checks.

The preceding combined source **`b26291d249a19fb194bb825389419f396ba3ea08`**
(main `d2bada99`, combined v29) passed all eight
[hosted gates](https://github.com/trannamtrung1st/agent-core/actions/runs/38526559158).
Backend: Domain 196, Infrastructure 1,380, Application 1,807, API 607 —
**3,990 passes / 32 optional live skips**. Frontend: **1,027 passes / 128 files**,
both builds passed. Mandatory native acceptance: **31 cases / zero skips**,
both browser phases passed (28.8 seconds before / approximately 1.3 minutes after
actual Core/server recreation). This remains evidence for that exact source,
not a substitute for the selected-snapshot gates.

## Earlier accepted trusted-local cutover — 2026-10-14

Branch `develop/branch-1`; main `a63194ba` was merged as `e9089734`, followed by `719ac6c5` as `928b8ccd` and `32f5f244` as `81a39b87`. Office/Drive/file-preview code and immutable General Assistant v26/v27 are preserved. New v28 combines their capabilities with modern sandbox tools. **Accepted behavior: `4c8f2507e04e7bd4e7a3ecf1af4d89a2d806f582`**, with all eight jobs in [workflow 38518370016](https://github.com/trannamtrung1st/agent-core/actions/runs/38518370016) passed. This closes Stages A–G and OSB-01–18 for the expressly revised trusted-local scope. Subsequent closure edits change documentation only. Earlier candidates and failures below remain historical evidence.

The owner explicitly approved trusted single-user local development/demo on the existing Docker daemon. Only the trusted OpenSandbox server may mount its socket; neither Core nor workloads do. DinD, an extra VM and other execution platforms are excluded. Own execd loopback is an acknowledged local-demo exception; public/multi-tenant isolation, writable-layer disk quota and signed supply-chain attestation remain deferred. Earlier stronger failed/rejected evidence below remains historical.

Actual environment: macOS arm64, .NET 10.0.401, supported Node 22.18.0 and Docker Desktop 29.8.2. Exact SDK/server/execd/egress release 1.1.1 pins are in `scripts/opensandbox-proof/images.json`. The authenticated server uses default Docker bridge, private loopback publication, deny-default dns+nft, no workload host binds, non-root commands, dropped capabilities, no-new-privileges, CPU/memory/64-PID/TTL limits. Only one server per daemon is supported: upstream sidecar reconciliation interfered when two disposable servers ran together. The runner now refuses another server or orphan workload topology rather than touching unrelated resources.

## Executed current checks

| Gate / action | Expected versus observed |
| --- | --- |
| Real SDK/topology proof | Pass: auth rejection, non-root, enforced CPU/memory/PIDs/no privileges, exact 256-byte transfer, separated streams/exit 7, argv, reconnect, separate files, tracked background stop, absent socket/server key/config, blocked external DNS/IP/metadata/private lifecycle/gateway; own execd loopback explicitly reachable; both workloads killed |
| Real SDK + durable migration/store + atomic Workspace acceptance | **31 passed, 0 failed, 0 skipped**: actual Python fail/fix, Node, XLSX/DOCX library validation; pinned initialization/reconnect/expiry; terminal receipt preservation; generation CAS; global disable; legacy SDK path; UTF-8 output bounds; traversal/protected/case/parent/link/FIFO/socket/quota/conflict guards; authority revoked during staging; SDK deadline; abandoned provisioning; clipped uncertainty metadata |
| Original affected Playwright MCP journey | Pass: save/verify profile, create owned Instance/Session, run five real coding/Office commands, publish/download both authenticated Artifacts; inspect/collapse command output, actual >30-second command, cancel/reset/recreate, retained-file read; 0 application console errors during working flows |
| Actual Core process restart | Pass: same SQLite root retained exact generation/provider identity/initialization timestamp and init-marker bytes; subsequent real command read retained files. Idle disconnected/shutdown Ready leases now persist until TTL; manual deactivate still terminates |
| Bounded Impeccable runtime review | Admin + Chat inspected at 1440/1024/768/390/320px after transitions settled; no horizontal overflow. Profile Discard and navigation Stay preserve baseline/draft; keyboard Escape/focus correction and output regions added through existing AntD patterns. DESIGN/context/sidecar synchronized without replacing main's file preview |
| Full merged frontend Vitest | **1,021 passed / 129 files**, supported Node 22. Host pnpm had selected Node 26, producing undefined localStorage failures; the supported runtime fixes all 73 affected storage/transport tests. Dedicated Playwright suite is excluded from Vitest |
| Frontend TypeScript/Vite build | Pass; existing chunk-size and Rollup annotation warnings retained |
| Full Application suite | **1,807 passed, 8 optional live skips** after catalog/constraint fixes; 33 focused tool/runtime/configuration checks also pass |
| Full initial merged backend | Domain 195 pass; API 585 pass / 3 optional live skips; plugin 4 pass. Four stale catalog/choice assertions were corrected; final Infrastructure rerun **1,362 pass / 21 explicit optional skips**, with focused recheck **30 pass** |
| Focused frontend final checks | 23 pass across profile/drawer/budget/Instance settings; drawer keyboard/output follow-up recheck **4 pass** |
| Reproducible Workbench inventory | Pass: owned runner build/start; installed SPDX 2.3 inventory contains 498 package identities and 277 notice hashes; license bundle retains 547 files. Exact local image content ID recorded alongside inventory. Inventory is not signed attestation/legal conclusion/vulnerability acceptance |
| Complete browser/process-recreation gate | Pass. Before phase verifies actual coding/artifacts/copy/responsive/Escape/focus. After phase verifies unchanged generation/init/artifact hashes across Core + server recreation, actual partial output while Running, 31-second completion, cancellation/reset/recovery. Independent Background Session fixture wrapping corrected; **both phases passed** (22.5 seconds before / approximately 1.3 minutes after), including child coding/Office completion, separate generation and child-only init marker. Cleanup removed owned workloads/server |

The migration is additive and preserves a populated pre-cutover Session record and Drive schema; no user database reset was performed. Testing uses isolated `local/opensandbox-runners/.../ui-*` databases/roots, private 0600 keys and project-owned resources. Unrelated local API and nopCommerce services are left running. Failed acceptance cleanup confirmed removal of its owned workloads/server, retaining only its test image/state volume.

## Corrections observed through execution

- Use the exact published SDK namespace/status API and restore the solution's transitive assets. Background logs are authenticated plain text, bounded through an Infrastructure shim; SDK accumulation is disabled.
- Docker Desktop rejects binding its VM bridge gateway on the workstation. Use host.docker.internal/loopback there; native Linux uses its inspected gateway. A narrow port range collided with a host allocation; use the wider upstream range and record the allocator's cross-namespace limitation.
- Workbench disables pytest's optional `.pytest_cache/.gitignore` producer, while retaining actual pytest execution and all protected-path guards.
- Production SQLite uses WAL/busy-timeout interception. The real integration fixture now uses that same configuration after lock errors in cancellation/polling; all concurrency assertions are retained.
- Idle shutdown/disconnection must retain a Ready environment; explicit deactivation remains cleanup. Supervision runs when profiles/readiness are disabled, reconciles abandoned provisioning and checks Instance lifecycle. Finished receipts are not rewritten during reset.
- File imports recheck live authority inside the filesystem gate immediately before swap and reject inconsistent parent casing. Unknown dispatch evidence survives text-output clipping; persistence follow-up failures cannot mask an uncertain effect, and post-commit errors report committed scratch honestly.
- Browser tests wait for actual terminal/output evidence and settled drawer geometry. Exact dialog names distinguish reset confirmation. Save/Verify names remain stable while busy. Cold server readiness is separate from ordinary Core health.

## Additional combined regression evidence

Eight ordinary Synthetic browser cases passed on isolated 6480/6473 ports and disposable roots: guarded scratch/home filesystem, exact Artifact download/reload/error recovery, shared Office/Drive OAuth/binding/preview/epoch behavior, and Instance settings Save/Reset/CAS/recovery. No sandbox service was required for these existing journeys. The isolated Compose project on port 6580 passed SQLite volume recreation, exact home/scratch isolation, Workspace source deletion, Admin/resources, protected credential/binding, Synthetic OAuth and Drive remote bytes/same operation identity. Its container/network were removed and unrelated services preserved. The historical sandbox.run real-SDK export recheck passed, preserving ok/exit/output/truncation/artifact/message fields and refusing to present indeterminate execution as success. Six offline probe receipt/guard tests passed. Markdown file-target audit found zero missing targets across 22 canonical/overview/deployment documents.

## Exact-source acceptance closure

All eight jobs pass on the accepted behavior SHA: backend, frontend shards 1/2,
core browser shards 1/2, acceptance browser, Synthetic Compose and mandatory
Trusted local OpenSandbox acceptance. Hosted backend results are Domain **195**,
Infrastructure **1,368**, Application **1,807** and API **586** passed:
**3,956 passes / 32 explicit optional live skips / zero failures**. The six
SDK/loopback cleanup regressions execute within Infrastructure; the separate
mandatory Docker job executes **31 cases / zero skips** and both browser phases
(27.6 seconds before / approximately 1.3 minutes after actual Core/server recreation).
Both frontend shards/builds pass; local full Vitest records **1,021 tests / 129 files**.

The retained [final native acceptance artifact](https://github.com/trannamtrung1st/agent-core/actions/runs/38518370016/artifacts/11872863337)
contains the TRX, browser report, image identity, installed SPDX inventory
(498 packages) and license bundle. The downloaded TRX independently confirms
31 executed/31 passed/zero failed, skipped, aborted or timed out. Private keys
and databases are excluded. Owned runtime processes/server/workloads are stopped;
images/state and local/hosted receipts remain for inspection. No user database
reset or unrelated resource removal was performed.

The changed Markdown audit passes: 24 documents, zero missing file targets or
unbalanced fences, and all 253 local fragment links resolve. PR
[#15](https://github.com/trannamtrung1st/agent-core/pull/15) delivers the cutover
with current main through `32f5f244` integrated. Production isolation, writable-layer
quota, signed attestation and other explicitly deferred targets are not accepted.

### Combined hosted correction and latest-main review

The earlier [51fb6fd9 workflow](https://github.com/trannamtrung1st/agent-core/actions/runs/38512857065) passed backend (Domain 195, Infrastructure 1,362, Application 1,807, API 586; 32 explicit optional live skips), frontend (589 + 432 tests / 129 files, both builds), acceptance browser and Compose. Core browser shard 1 passed 114 cases and failed the 390px Gmail access-check assertion. The trace contains no POST to `/connection/check`: the click occurred during the reopened centered modal's zoom animation, with a scaled 21.5px button. The test now waits for the existing settled AntD motion boundary and asserts the actual successful access-check response before the unchanged visible receipt assertion. No email product behavior, authorization or timeout is altered.

The correction passes all five isolated Synthetic email cases (1440/768/390/320px plus Custom endpoint defaults), including authorization/check/disconnect/reconnect, draft restoration, durable chooser reload and cancel. Existing pointer, keyboard, responsive and exact-owner assertions remain. A Vite proxy EPIPE during server teardown did not fail these journeys.

Latest main `32f5f244` only removes obsolete border/padding on focused Instance assignments. It merged without conflicts. All three isolated Synthetic Instance settings cases pass after that merge, exercising Save/Reset, conflict and failed-refresh recovery, resource ownership and responsive containment. Unrelated processes/data were untouched.

The [8ddedb58 workflow](https://github.com/trannamtrung1st/agent-core/actions/runs/38515274908) passed both frontend shards, backend, acceptance browser, Compose and core browser shard 1 (including the corrected Gmail flow). Core shard 2 passed 112 cases and failed nested Event management: the subscription's pointer click occurred during Collapse layout motion and no Global Events drawer opened. That journey now explicitly verifies focus and uses real Enter actions to open nested management; the same draft/sample/subscription identity/save/reload assertions pass locally (one case, 26.8 seconds). No Automation product behavior or test timeout changed.

That candidate's mandatory SDK job passed 30 cases and failed UTF-8/adversarial-tree teardown with a durable cleanup-pending receipt; browser phases were not run there. The receipt did not establish a confirmed removal and the upstream cause was not exposed. Cleanup now performs only bounded idempotent DELETE retries for the same durable provider identity, with five-second request limits and the unchanged 20-second total budget. 404 confirms absence; denied cleanup is not retried; exhausted cleanup remains pending for supervision. Commands are never replayed. Safe warning fields exclude upstream errors, bodies, routes, provider IDs and secrets. Six actual SDK/loopback Kestrel regressions pass for transient HTTP recovery, lost acknowledgement, forbidden cleanup, persistent failure and unresponsive bounded cleanup. The owner sandbox API/production-DI recheck passes (one case).

After that correction, the complete local runner passes the topology proof, **31 real SDK/store/Workspace cases with zero skips**, and **both browser phases** (21.0 seconds before / approximately 1.3 minutes after). Actual Core/server recreation, exact Artifacts, live 31-second output, cancellation/reset, separate Background Session completion and owned cleanup remain asserted. The rebuilt Workbench and inventory are retained under the owned gitignored runner state. The accepted exact-source hosted run above subsequently repeats and passes this gate.

## OSB-01–18 evidence map for the approved scope

These rows map the executed local and mandatory Linux evidence above to the proposal. All combined exact-source CI jobs have passed as recorded above; the rows identify the exercised behavior rather than inferring it from CI status alone.

| Criterion | Executed evidence |
| --- | --- |
| OSB-01 — optional startup | Default Synthetic and Real API startup/auth/availability cases pass without OpenSandbox; unavailable execution fails closed with Admin status |
| OSB-02 — pinned deployment | Owned native runner on macOS and Ubuntu uses exact authenticated server/execd/egress digests, verified non-root/resource restrictions and no Core/workload socket |
| OSB-03 — actual coding | Python/Node versions, failing then corrected Python tests, Node test, separated exit-7 streams and bounded UTF-8 output pass through the real SDK |
| OSB-04 — Session ownership | Same-generation file reuse, distinct Session files, durable ownership/operation uniqueness and owner-scoped background status/stop pass |
| OSB-05 — revisions/init | Profile history/CAS and malformed-script validation pass; real initialization is generation-pinned, once-only on reconnect and deterministic after expiration |
| OSB-06 — persistence | Exact canonical bytes survive adapter reopen, actual Core/server recreation and expiration/rehydration; `/home` remains independently tested |
| OSB-07 — guarded import | Atomic stale-authority/tree/quota/conflict checks reject traversal, links, protected/case-colliding paths, FIFO/socket and untracked children without partial canonical writes |
| OSB-08 — Office delivery | Real openpyxl/python-docx create and reread XLSX/DOCX; authenticated browser downloads retain exact hashes across restart |
| OSB-09 — authority/disable | Definition/Instance constraints, unavailable capability filtering, profile disable and generation fences pass; disable reconciles owned environments |
| OSB-10 — revised local isolation | Actual auth/resource/DNS/external-IP/metadata/private lifecycle/gateway/socket probes pass on both topologies. **Own execd loopback remains reachable**, expressly accepted for this trusted-local scope; no production isolation claim |
| OSB-11 — command budget | Real one-second deadline quarantines without importing partial files; browser observes live partial output and successful 31-second completion under the separate sandbox allowance |
| OSB-12 — recovery/no replay | Cancellation/manual deactivate, reset CAS, terminal receipt preservation, disabled-profile cleanup, actual restart and abandoned provisioning reconciliation pass; unknown output survives clipping and is not replayed |
| OSB-13 — background | Real start/inspect/log/owned stop, untracked-child guards and teardown fences pass; independent existing Background Session completes coding/Office work with separate generation and init marker |
| OSB-14 — Admin | Profile Save/Discard/Verify/navigation guard, owner HTTP revision CAS, Instance Save/Reset/conflict/refresh recovery and redacted projections pass |
| OSB-15 — compatibility | Full local backend/frontend, eight ordinary Workspace/Artifact/Office/Drive/Instance browser cases and Compose pass; historical `sandbox.run` executes through the real SDK with its original result fields. v26/v27 remain byte-identical to main |
| OSB-16 — browser UX | Real command stream, collapse/copy, cancellation/reset guidance, protected downloads, profile edits and keyboard Escape/focus pass at 1440/1024/768/390/320px with no application page errors |
| OSB-17 — recreation | Runner actually restarts Core and server using retained isolated SQLite; same generation/init/artifact hashes and canonical recovery pass, then owned server/workloads/processes are cleaned up |
| OSB-18 — build/docs | Pinned Workbench builds on arm64 and Ubuntu; installed SPDX/license/image evidence retained. Architecture, ports, protocol, persistence, operations and shared DESIGN/context are synchronized; no signed provenance or legal/vulnerability conclusion |

The executable native cases live in [SDK/lifecycle acceptance](../../tests/AgentCore.Infrastructure.Tests/OpenSandboxIntegrationTests.cs), [store/migration parity](../../tests/AgentCore.Infrastructure.Tests/SandboxStoreTests.cs), [atomic Workspace checks](../../tests/AgentCore.Infrastructure.Tests/WorkspaceStructureTests.cs), [owner API](../../tests/AgentCore.Api.Tests/SandboxApiTests.cs) and [real browser/restart journey](../../web/opensandbox-e2e/workbench.spec.ts). [The runner](../../scripts/opensandbox-local.py) invokes these gates rather than replacing them with health checks or scripted result text.

## Historical Stage A evidence — 2026-10-13

Date: 2026-10-13 (Asia/Ho_Chi_Minh). Branch: `develop/branch-1`.
Request: implement `local/proposals/agent-core-opensandbox-final-proposal.md` as
one complete cutover. **The feature is not implemented or accepted.** The
proposal requires Stage A compatibility and threat-model proof before its
dependent stages and explicitly says to stop if this cannot be made safe.

## Source and environment

- Reviewed starting behavior: `5b2d42d43da48646971fd79d907b49dc63d72e2b`; clean checkout.
- Fetched current main and fast-forwarded the current branch to
  `d6472746d73ed62d6eccaed57e7709432c9c6265`. The sole newer change was one TODO
  line; no unrelated edits were overwritten.
- This ledger and probe are preparation on that base. The owner requested a
  review/fix and commit/push follow-up; publication does not establish an
  exact-commit feature acceptance record.
- macOS arm64; .NET SDK `10.0.401`; Docker Desktop Linux daemon `29.8.2`.
  No containers were running at the initial inventory.
- Upstream candidate: [`release-1.1.1`](https://github.com/opensandbox-group/OpenSandbox/releases/tag/release-1.1.1)
  at `531700ca18a7fa5fab6a2f25d6dc68a76eddab12`. The proposal's 1.1.0 candidate
  has an upstream-documented server-wheel packaging failure fixed in 1.1.1.
- Published NuGet `Alibaba.OpenSandbox` **1.1.1** restored successfully, with
  an exact version and transitive hashes in
  [packages.lock.json](../../scripts/opensandbox-proof/packages.lock.json).
- Registry image digests were resolved, and server/execd/egress images pulled.
  [images.json](../../scripts/opensandbox-proof/images.json) records candidates.
  Attestation verification, SBOM/license acceptance and final Workbench build
  have not run. These are not accepted production pins.

## Changes prepared

[The standalone SDK probe](../../scripts/opensandbox-proof/README.md) compiles
against the published package and prepares create/connect, exact binary
transfer, separated command streams/nonzero exit, duration, native argv,
two-sandbox file isolation, background inspection/interruption and owned kill
checks. It requires an explicit disposable-target opt-in, uses a private
server key and server proxy, suppresses raw SDK exception details, caps
execution duration and attempts cleanup. It is outside `AgentCore.sln`.

Candidate server configuration and a non-root Python test image are prepared.
They are unverified fixtures, not a partially accepted Compose deployment or
the final Python/Node/Office Workbench. Production ports, DI, persistence,
tools, Definitions, Workspace, API and frontend code have not been changed.

## Executed checks

| Command/action | Expected | Observed |
| --- | --- | --- |
| `dotnet restore scripts/opensandbox-proof/OpenSandboxProof.csproj --packages /private/tmp/agent-core-osb-nuget` | Resolve exact SDK | Pass: published 1.1.1 restored |
| Same restore with `--locked-mode --ignore-failed-sources --nologo` | Verify locked graph | Pass |
| `dotnet build scripts/opensandbox-proof/OpenSandboxProof.csproj --no-restore --nologo` | Compile all SDK call sites | Pass: 0 warnings, 0 errors after correcting two await/span compile errors |
| Probe `--help` | No infrastructure calls | Pass: usage printed |
| Probe without disposable opt-in after final build | Refuse before infrastructure calls | Pass: actionable message, exit 2 |
| `docker build --network none -f scripts/opensandbox-proof/Dockerfile.execd-fallback -t agent-core-osb-execd-fallback scripts/opensandbox-proof` | Build pinned upstream execd fallback | Pass |
| Real fallback command protocol and file tests | Non-root, separated streams, exact exit 7, exact bytes retained by the next command | Six assertions pass |
| Real fallback workload-to-execd loopback probe | Command cannot connect to control endpoint | **Fail**: command connected to `127.0.0.1:44772`; test exits 1 |
| Actual fallback `docker inspect` | No privileges/network/host mounts | Observed user `1000:1000`, network `none`, `Privileged=false`, read-only root, cap drop `ALL`, no-new-privileges, mounts `[]`; limits were configured at creation |
| Remove owned fallback and inspect labeled containers/networks | No test resources remain running | Pass: fallback removed; labeled container/network queries empty |
| Python AST / fixture JSON / changed Markdown links and fences / `git diff --check` | Valid preparation artifacts | Pass |

Fallback image local ID:
`sha256:4064f08e603a8f1cec32dc8ebb7b04dcda30fe50cc5bbf910036f9452986f782`.
The initial attempt used its config digest rather than Docker's local image
ID and was rejected before container creation; the resolved local ID ran.

The fallback used no host mounts, exposed ports, Docker socket, credentials or
external network targets. `/working` and `/tmp` were small private tmpfs
mounts. [execd-fallback.py](../../scripts/opensandbox-proof/execd-fallback.py)
was piped through `docker exec -i ... python`. It called the real execd service
inside that same container; it did not use the C# SDK or lifecycle server.
Its output intentionally records booleans only. The failed assertion is not
converted into a success or a skipped security test.

## Isolation issue and blocked action

Upstream's [pinned nftables implementation](https://github.com/opensandbox-group/OpenSandbox/blob/531700ca18a7fa5fab6a2f25d6dc68a76eddab12/components/egress/pkg/nftables/manager.go#L282)
accepts loopback before static deny rules. This is source evidence of a gap
with the proposal's command/control-plane isolation requirement; the egress
sidecar itself has **not** been exercised. The fallback proves separately
that network-none alone does not separate commands from local execd. A
trusted runner-level enforcement mechanism must be designed and tested;
do not weaken OSB-10 or declare a deny-all egress profile sufficient.

Automatic approval review rejected starting the proposed **privileged outer
Docker-in-Docker runner**, citing a significant host security boundary and no
explicit authorization for that implementation. The rejected command would
have created an internal test network and a separately managed daemon, with
no outer socket/host mounts and only a loopback lifecycle port. Neither the
runner nor that network was created. The unprivileged fallback above was
subsequently authorized and completed.

Continuation requires an operator-approved disposable runner method: either
an isolated Linux VM/runner, or explicit permission for the privileged outer
DinD test runner. The latter can affect the Docker Desktop VM through its
privileges despite having no host mounts; it is not a hardened production
isolation claim. Approval would allow the live proof to proceed, not waive
the security assertion or the remaining acceptance gates.

## Gate status and remaining scope

| Stage | Status |
| --- | --- |
| A — upstream compatibility and threat model | **Blocked/not accepted**: SDK compilation and execd fallback only; full authenticated create/connect/execute/file/kill and direct-IP/private-host isolation proof unrun |
| B — normalized contracts, policy, persistence | Not started; requires InMemory/SQLite CAS, owner isolation, data-preserving migration and disabled startup |
| C — environment/lifecycle | Not started; requires Workbench, image/script pins, lazy Session reuse, initialization once, TTL/restart/reconciliation and orphan evidence |
| D — Workspace bridge | Not started; requires bounded snapshot/hydration, untrusted tree validation and atomic fenced import, failure/conflict preservation and exact Artifact bytes |
| E — tools/Run safety | Not started; requires exact authority, non-replayable receipts/unknown outcomes, claim fencing, command budget composition, owned process controls and historical `sandbox.run` compatibility |
| F — Admin/Chat/docs | Not started; requires profile/selection/effective configuration owners, command UI, responsive keyboard/runtime evidence and normative contracts |
| G — final cutover/acceptance | Not started; requires legacy runtime retirement, new immutable General Assistant version, regression/Compose/runner gates and exact-source acceptance |

**OSB-01–18 remain unaccepted.** The loopback observation is a security failure
relevant to OSB-10, not a full OSB-10 topology test. Product Synthetic/Real
startup, Core authorization, Workspace adversarial/atomic import, migrations,
command deadlines, cancellation/crash replay safety, emergency disable,
background teardown, Office artifact delivery, Admin/Chat journeys, Compose
recreation and all full regression/hosted acceptance suites have not run for
this feature. They cannot be inferred from compilation or fallback success.

No application acceptance is claimed. No user data was reset, historic
Definition or verification report modified, or legacy runtime retired.

## Review and publication follow-up

The owner requested a consistency/functional review with corrections and
commit/push to `develop/branch-1`. This did not explicitly approve the
previously rejected privileged runner. Stage A remains blocked.

Review corrections enforce settled execution receipts and expected exit codes
before interpreting any fallback output; reject missing, malformed, failed or
still-running commands; require an explicit fallback opt-in and PID 1 execd
identity before contacting loopback; and add a reproducible unprivileged
runner that verifies actual container restrictions and cleans up in `finally`.
The SDK probe now exits 2 for invalid configuration without printing values,
halts after unconfirmed/root execution, rejects auth-check redirects and
enforces its output cap while consuming events. Candidate digest status and
README historical status wording were corrected; generated Python bytecode
and unrelated build context are excluded from publication.
The candidate server fixture no longer supplies a known placeholder API key;
it requires private environment injection and prohibits insecure startup.

The observed local execd reachability is **not** evidence of reaching the
operator host, the lifecycle API or another Session. It fails the proposal's
literal localhost isolation requirement and motivates real runner-level
verification. No such broader isolation test is claimed.

Review results:

- `python3 -m unittest discover -s scripts/opensandbox-proof -p 'test_*.py' -v`:
  **6 pass**, covering missing/oversized receipts, wrong/unknown/running
  command outcomes, separated exit-7 output and no-contact guard behavior.
- `python3 scripts/opensandbox-proof/run-execd-fallback.py --run`: actual
  restriction check + six functional assertions + cleanup **pass**; local
  workload-to-execd reachability assertion **fails**, exit 1. The failure is
  preserved, and the runner cleans up even on errors. No test was skipped.
- SDK build: **0 warnings/errors**. Seven CLI guard/help scenarios pass:
  help, absent opt-in, malformed origin, non-loopback HTTP, embedded URL
  credentials, origin path and unpinned image. Invalid inputs exit 2 without
  printing the test secret marker or throwing an unhandled exception.
- Candidate TOML validates against `AppConfig` in the digest-pinned server
  image, with no lifecycle service, Docker socket, host mount or network.
  The first validation used Python 3.11's `tomllib` import on the server's
  Python 3.10 image; rerunning with its own `tomli` fallback parser passed.
  Schema validation does not establish service startup or security.
- After removing the known placeholder key, the pinned server's real
  `api_key_confirm` guard rejects the candidate's missing authentication in
  noninteractive mode. The fallback script also exits 2 on the host before
  making requests. Locked restore and Python/JSON/Markdown link/anchor/fence
  validation pass; `git diff --check` is clean.
- Main advanced during review to `3f46ce1ec119adde55c5cdc163dd25c6f73d593e`
  (unrelated frontend approval recovery). The current branch was
  fast-forwarded without altering those eight files or overwriting this work.
  The OpenSandbox preparation has no application or frontend behavior changes;
  product-wide suites and hosted feature acceptance remain unrun.

## Main synchronization and PR preparation

The owner explicitly authorized current-main synchronization, careful conflict
resolution, combined verification and a PR to main. The fetched main is
`3f46ce1ec119adde55c5cdc163dd25c6f73d593e`, already an ancestor of branch
`61897029b4d870728bba2bb6739083733298fb2c`. Git found no new commits or conflicts;
no merge or conflict-resolution changes were necessary. Main's approval
recovery behavior and the standalone proof remain separate modules.

The existing Synthetic backend CI job now restores/builds the locked proof,
runs its no-infrastructure help path and executes the six offline receipt
regressions alongside the existing application suites. It starts no
OpenSandbox service and does not waive the failing live isolation assertion.
The existing CI jobs/matrix are preserved. This closes the preparation's
missing automated regression coverage, not Stage A acceptance.

Combined local verification on this main/branch source:

- Proof build: zero warnings/errors; locked restore and no-network help pass.
  Offline Python receipt/guard regressions: **6 pass**.
- `pnpm run test --run src/features/chat/ApprovalModal.test.tsx src/services/realtime.approval.test.ts`:
  **13 pass**; `pnpm run build` passes.
- Synthetic Playwright `e2e/approval-flow.spec.ts --project=synthetic`:
  **6 pass** in 46.6 s. Actions cover approve, reject, keyboard decisions,
  refresh retaining the same Run/Response, and disconnect rejection/retry.
  Approval executes once; rejected decisions never execute; recovery retains
  the exact pending decision and clears the modal after confirmation.
  The CLI runner used API `6080`, web `6173`, fixture `6091` and disposable
  `/private/tmp/agent-core-osb-sync-browser` SQLite/blob/key roots; the existing
  host on `5080` was untouched. Playwright MCP separately inspected the
  Synthetic UI; a later navigation failed after the CLI runner tore down its
  server. MCP inspection is not counted as functional verification.
- The real unprivileged execd fallback again passes six functional assertions,
  actual restriction checks and cleanup, and **fails** its localhost execd
  isolation assertion (exit 1). No broader host/peer/network access is claimed.
- Full backend regression was attempted locally with provider spending
  disabled. The initial sandboxed run could not open MSBuild named pipes;
  that owned failed process was stopped, and a permitted `-m:1` rerun was
  completed. Domain **195 pass**, Application **1,807 pass / 8 opt-in skips**,
  Infrastructure **1,327 pass / 10 opt-in skips**, API **575 pass / 3 opt-in
  skips**, and OrderEvents **4 pass**: **3,908 pass / 21 opt-in skips / zero
  failures**. Hosted results belong to [PR #15](https://github.com/trannamtrung1st/agent-core/pull/15)
  and its exact-head checks; product regression success does not accept Stage A.
- Python/JSON/Markdown links/fences, workflow YAML and `git diff --check` pass.

The PR delivers **preparation and offline CI coverage only**. The full
OpenSandbox feature remains unimplemented/unaccepted, with the original
isolated-runner and security requirements intact. Publishing this preparation
does not authorize a privileged test runner or close OSB-01–18.

### Hosted readiness-test correction

The first [PR workflow](https://github.com/trannamtrung1st/agent-core/actions/runs/38430290967)
on `c23b7bef` failed frontend shard 1: **569 pass / 1 fail**. The existing
`InstanceSkillsSection` test found its panel wrapper before AntD committed
the Core Skill row, then synchronously asserted `Core v1`. This was a test
readiness race, not evidence of altered capability authority.

The corrected fixture deliberately holds the inventory response, verifies
loading/no Core row, releases it, then awaits the actual Core row. Read-only,
missing-authority, suppression, enabled-choice and forbidden-action assertions
remain intact. An initial local correction waited for the absent panel while
holding the response; that ordering failed locally and was corrected before
publication. Product UI/services have no behavior changes.

Local combined Skills/approval/realtime regressions now pass **21 tests**, and
the TypeScript/Vite build passes. The PR must rerun all seven hosted gates on
the corrected head; the failed first candidate and prior successful jobs do
not establish final acceptance. Stage A remains blocked throughout.
