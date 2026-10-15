# OCI image tag onboarding fix — 2026-10-15

The later owner-authorized [ordinary-image successor](ordinary-sandbox-images-verification-2026-10-15.md) supersedes this report’s former Python/non-root/image-owned-scratch compatibility boundary. The results below remain historical evidence for onboarding and its earlier contract.

Scope: fix owner prebuilt-image saves for ordinary tags and allow common public OCI registries. This is focused maintenance; no milestone acceptance or CI was requested.

## Behavior

Owner input accepts tags, Docker Hub short names and existing immutable digests. Tags resolve through a bounded registry HEAD/auth exchange before persistence; only canonical sha256 digest identities enter saved profile revisions, Run pins and leases. Names without a registry use Docker Hub, official single-component names gain `library/`, and omitted tags use `latest` for the lookup. No layers are pulled during Save. Resolution follows the [Registry HTTP API HEAD/digest contract](https://distribution.github.io/distribution/spec/api/).

Default approved hosts are `docker.io`, `ghcr.io`, `mcr.microsoft.com`, `quay.io` and `public.ecr.aws`. Explicit configured image prefixes replace defaults, retaining exact host/path boundaries. Scope is checked before lookup or credential disclosure. Token-origin policy, redirect restrictions and protected credential ownership remain in force. Failed resolution appends no revision, and stale revisions fail before lookup.

The form explains accepted input and digest pinning, retains failed drafts, and uses normalized hosts for registry credential choices. Saved revisions still require explicit compatibility verification before command use. Existing profile history is preserved; no migration or database reset is needed.

## Observed browser scenarios

Playwright MCP used an isolated native API on `127.0.0.1:5280` and Vite on `127.0.0.1:5373`, with `/health` confirming Synthetic and disposable InMemory/temp-root state. The existing user app was left running.

1. Add prebuilt image → name `.NET Workbench`, description `.NET SDK workbench`, source `mcr.microsoft.com/dotnet/sdk:10.0`, no credential → Save. Expected/observed: HTTP 200, modal closed, revision 1, `NeedsVerification`, digest displayed as `mcr.microsoft.com/dotnet/sdk@sha256:0d99ebf1ccad15037b5c98c9fe8979d6b8dbac23812feae2277a0b277b5c5ffa`.
2. Edit source to a deliberately missing MCR tag → Save. Expected/observed: actionable HTTP 400, draft retained and no revision appended. Correct to `:10.0` → Save. Expected/observed: HTTP 200, revision 2, same immutable digest; reload displayed the persisted digest.
3. Add `Public Docker Hub` with `busybox:latest`, no credential → Save. Expected/observed: HTTP 200, normalized `docker.io/library/busybox@sha256:fd7dc98638c8e305f4dc34e979f1c0fdfdcaeb0fbf8fcff77ae834b6da3d7e6e`, no credential ID, `NeedsVerification`, digest displayed.

Final browser console inspection reported zero errors; network inspection showed successful catalog/save requests and the intentionally rejected missing-tag request. Early Vite transform errors from concurrent Admin edits were corrected with minimal syntax/type fixes before exercising the flows.

## Focused checks

- `dotnet test tests/AgentCore.Api.Tests/AgentCore.Api.Tests.csproj --no-restore --filter FullyQualifiedName~SandboxApiTests --nologo --verbosity quiet -m:1 -nodeReuse:false -p:UseSharedCompilation=false`: **12 passed**. Includes real loopback registry → production tag transport → owner PUT → readback/history, failed lookup preservation, stale revision and narrowed configuration policy.
- `dotnet test tests/AgentCore.Infrastructure.Tests/AgentCore.Infrastructure.Tests.csproj --no-restore --filter 'FullyQualifiedName~RegistryAuthorizationProbeTests|FullyQualifiedName~OciSandboxAuthorityTests' --nologo --verbosity quiet -m:1 -nodeReuse:false -p:UseSharedCompilation=false`: **69 passed**. Covers parser boundaries, defaults, credential epochs, tag digest headers and anonymous/authenticated Hub challenges. Live cases remain inert without explicit opt-in.
- `AGENTCORE_LIVE_REGISTRY_TESTS=1 dotnet test tests/AgentCore.Infrastructure.Tests/AgentCore.Infrastructure.Tests.csproj --no-build --no-restore --filter FullyQualifiedName~Live_public_tag_resolution_uses_production_transport --nologo --verbosity quiet -m:1 -nodeReuse:false`: **5 passed**. Anonymous production HTTPS tag resolution plus digest-access checks for MCR .NET SDK 10.0, Docker Hub BusyBox, GHCR uv, Quay Prometheus and ECR Public BusyBox. No credentials or layer pulls.
- `cd web && pnpm run test --run src/services/sandboxes.test.ts`: **12 passed**, including credential host matching for short names.
- `cd web && pnpm run test --run src/features/admin/SandboxProfilesSection.test.tsx --testTimeout=60000`: **14 passed**. Includes modal creation, failed tag save/draft retention/retry and display of the returned digest.
- `cd web && pnpm run build`: **passed**, with existing bundle-size/dependency annotation warnings.
- `git diff --check`: **passed**.

The combined UI run initially timed out in the modal cancel/discard test at its 30-second limit; the tag save/failure/retry test passed in that run and separately. The final file rerun with the longer timeout passed all 14 tests; its cancel/discard scenario completed in 22 seconds. Existing JSDOM/Ant Design textarea-height warnings remain non-failing.

## Boundaries

No Docker workload provisioning or compatibility acceptance was performed. A public image may save successfully yet fail Python, non-root, scratch or other sandbox compatibility checks; use Verify profile before enabling execution. No live private-registry credentials, model providers, hosted CI, production restart or milestone acceptance were involved. Existing in-progress workspace changes were retained. The disposable API/Vite processes were stopped and their ports confirmed free. Restart the existing backend to load the change; the running user app was not restarted.


## Follow-up review and corrections — 2026-10-15

The requested second review found and fixed two edge cases:

- A single-component Docker Hub repository containing a dot, such as `my.image:v1` or `my.image@sha256:…`, was classified as a registry host by the backend while credential selection correctly classified it as Docker Hub. Two new tag parser cases failed before the fix. Backend onboarding now treats every single-component name as a Docker Hub name; explicit registry detection requires a slash. Added API save/readback and frontend credential-host regressions cover the same normalization.
- Explicit JSON `AllowedImagePrefixes: null` restored all five public defaults because `section.Exists()` was false. A new owner API test reproduced the unintended public inventory. Configuration now checks the presence of the child key and binds an explicit empty or null value to an empty allowlist. Both JSON `[]` and `null` are covered through catalog inspection and rejected remote profile saves. Omitted configuration still uses defaults; configured nonempty lists retain replacement semantics.

Canonical port, configuration and testing documents were synchronized. The existing source authorization, token-origin restrictions, immutable saved profiles, history and compatibility verification requirements remain in force.

Fresh focused checks after these fixes:

- Same Sandbox API command above: **15 passed**.
- Same Infrastructure filter above: **71 passed**.
- `cd web && pnpm run test --run src/services/sandboxes.test.ts src/features/admin/SandboxProfilesSection.test.tsx --testTimeout=60000 --no-file-parallelism`: **28 passed**, including the existing save/failure/retry journey. The existing JSDOM height warning remains non-failing.
- `cd web && pnpm run build`: **passed**, with the existing bundle/dependency warnings.
- `git diff --check`: **passed**.

A fresh Playwright MCP check against disposable Synthetic API/Vite hosts repeated MCR SDK `:10.0` → successful pinned save, deliberately missing tag → HTTP 400 with retained input, correction → revision 2 and persisted digest after reload. A second form submission of the fixture identity `my.image@sha256:<64 a characters>` saved and displayed `docker.io/library/my.image@sha256:<same digest>` without credentials and remained NeedsVerification. This dotted fixture checks registration/normalization only; its image existence was not asserted. Browser console errors: **zero**; the only rejected API request was the intentionally missing tag.

Total fresh focused tests: **114 passed** (86 backend, 28 frontend). The unchanged registry transport's earlier five live registry checks remain recorded above and were not repeated wholesale. No Docker provisioning, private-registry acceptance, hosted CI or milestone acceptance was performed. Only the disposable review hosts and their temporary data were cleaned up; the existing user app was left running.
