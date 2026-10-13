# OpenSandbox integration verification — Stage A blocked

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
  started. Domain **195 pass** and Application **1,807 pass / 8 opt-in skips**
  were observed while the remaining suites were running. Final full-suite and
  hosted PR results belong to the PR checks; these interim results are not
  full-suite acceptance.
- Python/JSON/Markdown links/fences, workflow YAML and `git diff --check` pass.

The PR delivers **preparation and offline CI coverage only**. The full
OpenSandbox feature remains unimplemented/unaccepted, with the original
isolated-runner and security requirements intact. Publishing this preparation
does not authorize a privileged test runner or close OSB-01–18.
