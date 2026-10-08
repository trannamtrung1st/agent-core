# Browser v2 full-cutover verification

Date: 2026-10-08. Status: implementation, authorized real-model Journey L and all five hosted gates passed on corrected candidate `ac1cba0b16edaf17b86ee62066fe3aa7b83410ba` ([run 37751900338](https://github.com/trannamtrung1st/agent-core/actions/runs/37751900338)). Main integration is locally verified on `df29ec85`; the [merge report](branch-1-main-merge-verification.md) owns the combined-tree evidence and current hosted status.

The authorized migration starts from `bd44046896df6f3e0fc2e7d15d60dd479a5349cd` and its five-job [Synthetic baseline run 37677917533](https://github.com/trannamtrung1st/agent-core/actions/runs/37677917533). That run proves the pre-migration tree, not Browser v2. Historical P9/P9.5/P9.6 and Instance Skills reports and freeze SHAs remain unchanged. P10/P11 remain unopened.

## Implemented contract

Application owns `IBrowser`, provider descriptors, neutral requests/results and a canonical Browser tool/feature registry. Infrastructure owns the in-process Playwright provider. Model-facing v1 aliases and the old port/provider names have been removed from active source, built-ins and tests. No persisted-name translator or parallel v1 runtime was introduced.

General Assistant v17 and Secretary v5 use exact capability authorization with a seven-tool browser bootstrap and contextual discovery of specialized tools. Existing non-browser capabilities remain projected. Definition and Instance Skill ownership, stable ids and immutable execution catalogs retain their existing semantics.

The Playwright provider supports navigation/history, native ARIA snapshot/search, Locator interactions, forms, keyboard, uploads/downloads, tabs/popups, dialogs, waits, resize, masked screenshots, console/network diagnostics, bounded network controls, cookies/local/session storage, verification/informational locator generation, vision mouse, highlighting and media emulation. Compact snapshot output is bounded independently of the full search index; generic fixtures exercise 160 tree items and actions after DOM replacement.

Evaluation, raw storage-state export/import, PDF, tracing and video remain explicitly unsupported by this provider. These tools have canonical contract entries but are filtered from discovery and return `unsupported_operation` if an authorized call reaches execution. Artifact-backed file drop is also unsupported; structured text drop is supported. This subset is deliberate: raw export/evaluation/trace/video do not yet provide the host's required secret masking. No alternate provider, selector, JavaScript or web-search fallback is used.

Privileged mutations retain exact approval hashes and execution-time checks. Frame interactions recheck the frame's current origin. Password injection remains an attached-user-only secure sink. Non-vision models cannot discover or execute coordinate actions. Pending native actions are fenced on cancellation before the browser gate is reused.

Admin effective configuration shows provider identity, readiness, supported features, policy/profile modes and output limits. It exposes no profile filesystem path or protected value.

## Acceptance evidence

| Journey | Expected and observed evidence | Status |
| --- | --- | --- |
| A — snapshot/rerender | Native ARIA refs re-resolve after replacing the selected DOM node; clicking Asset 159 produces the expected selected state. | Passed, Infrastructure integration |
| B — large-page discovery | Bounded compact output retains a searchable full index; Asset 159 is found and actionable beyond the former 40-node cap. | Passed, Infrastructure integration |
| C — forms/keyboard | Multi-field fill, checkbox, custom combobox/listbox, multiselect, contenteditable, Home and Shift+Tab produce the expected state. | Passed, Infrastructure integration |
| D — tabs/popups | New/list/select/close, allowed popup adoption and denied popup targets preserve opaque refs and policy. | Passed, provider regressions |
| E — dialogs | Alert, confirm and prompt are explicit pending states; accept/dismiss completes the native action without deadlock. | Passed, Infrastructure integration |
| F — frames | Allowed iframe controls work; a readable frame on another navigation origin cannot bypass the narrower interaction origin. | Passed, Infrastructure integration |
| G — files/artifacts | Approved resource upload, accepted/rejected downloads, limits and screenshot artifacts preserve session ownership and hide provider paths. | Passed, provider/Application regressions |
| H — OnDemand diagnostics | Initial projection omits specialized diagnostics; network/console discovery finds relevant tools; output is bounded and redacted. | Passed, capability/provider tests |
| I — subset provider | Unadvertised tracing is absent from discovery; an authorized forced call returns `unsupported_operation`; unauthorized calls remain forbidden. | Passed, Application contract |
| J — System Credentials | Password-target validation and secure sink tests retain redaction, detached injection denial, profile leases and owner-attention boundaries. | Passed, credential/profile regressions; live authenticated-site opt-ins not run |
| K — vision | Non-vision discovery/execution rejects mouse; screenshot plus coordinate click reaches the visual-only fixture target under a vision admission. | Passed, Application/provider integration |
| L — real model | An opt-in test drives the owned runtime through find, contextual capability loading and multi-field SPA actions using OpenRouter. | Passed: authorized OpenRouter / DeepSeek v4.1 Flash, actual Chromium assertions, 47 seconds; see follow-up below |

## Runtime UI verification

Playwright MCP operated the Synthetic application on isolated API/Vite ports 5290/5390 with an ephemeral browser context. A General Assistant v17 chat looked up AC-1042 through the loopback browser fixture. The observed output was an intermediate application message followed by exactly one completed final response: AC-1042 is In review. Admin provider diagnostics rendered the advertised feature list, readiness and limits. At 390 pixels, document width was 390 pixels and the diagnostics exposed no private path.

Reloading the tested deep link after the readiness correction restored Riley, Ready, exactly one final response and one separate application progress message; the composer became available only on the restored chat.

These interactions complement the CLI suites; they do not replace the full regression gates or real-model journey.

## Dependency and environment verification

NuGet security audit rejected the previous ImageSharp 3.1.11 dependency. ImageSharp 4.1.2 then blocked Release compilation without a SixLabors license. The candidate replaces it with MIT-licensed SkiaSharp 4.153.1 and its Linux native package, without audit suppression or license bypass. Image sanitation decodes bounded PNG/JPEG/WebP/GIF pixels and re-encodes metadata-free PNG/JPEG; the processor cache version advances to `attachment-processors/3`. Header/pixel bounds and EXIF-stripping tests pass. Browser PNG/JPEG/WebP screenshot tests pass. The initial replacement build had zero warnings and errors.

Shared tracked application settings, launch profiles, UserSecretsId, Vite configuration, `.env.example` and hosted workflow remain unchanged. Test ports, databases and profiles use local/environment overrides. No original checkout database, saved browser profile or running host was reset. Historical report screenshots generated by existing E2E tests were restored to their original bytes.

A later full regression attempt encountered a full host disk and Docker build-cache I/O errors. SQLite write failures from that attempt are environment failures and are not counted as passing evidence. Generated Linux/Windows runtime copies in this checkout were removed to recover space; macOS runtime assets and source files were retained. Gates are rerun after recovery.

## Gate ledger

Local checks use native Synthetic hosts, isolated SQLite files, ephemeral profiles and distinct ports. `NODE_OPTIONS=--no-experimental-webstorage` avoids the local Node 22.18 storage-global mismatch; it is an execution override, not tracked configuration.

| Gate / command | Current evidence |
| --- | --- |
| Audited `dotnet build` | Passed, zero warnings/errors |
| `dotnet test tests/AgentCore.Domain.Tests/AgentCore.Domain.Tests.csproj` | 152 passed |
| `dotnet test tests/AgentCore.Application.Tests/AgentCore.Application.Tests.csproj --nologo -m:1` | 1317 passed, 2 explicit live opt-in skips, after final telemetry/privacy corrections |
| `dotnet test tests/AgentCore.Infrastructure.Tests/AgentCore.Infrastructure.Tests.csproj` | 830 passed, 9 explicit live opt-in skips |
| `dotnet test tests/AgentCore.Api.Tests/AgentCore.Api.Tests.csproj` | 371 passed, 3 opt-in skips after final Application corrections |
| OrderEvents plugin tests | 4 passed |
| `pnpm --dir web run test --run --maxWorkers=2` | 739 passed across 96 files after bootstrap correction |
| Five realtime unit files | 81 passed after bootstrap correction |
| `pnpm --dir web run build` | Passed after bootstrap correction; existing bundle-size warning |
| Playwright core (Synthetic, Browser STT and Browser/Browser) | 116 passed in the complete final run, including deterministic held-models deep-link/workspace/Artifact restoration and all active-response reconnect, streaming and Stop scenarios. |
| Seven Playwright acceptance projects | All seven projects passed, 16 tests total. Secretary passed all four scenarios after its complete v2 grants, final-response scoping and bounded count wait corrections. |
| `COMPOSE_PROJECT_NAME=agent-core-browser-v2-final AGENTCORE_COMPOSE_PORT=5188 ./scripts/compose-sqlite-volume.sh` | Release build, owner-capability path, SQLite/keyring/profile/workspace/Skill-copy survival across recreation passed |

The final core command is `pnpm exec playwright test --project=synthetic --project=browser-stt --project=browser-browser`, with `CI=true`, API/Vite ports 5280/5373, Browser STT ports 5281/5374, Browser/Browser ports 5282/5375 and `PLAYWRIGHT_SQLITE_PATH=../data/browser-v2-core-final5.db`. Browser profile mode is EphemeralSession. Acceptance runs `pnpm exec playwright test --project=<name>` sequentially for faithful-manual, admin-lifecycle, p76-admin, p97-harness, p9899-continuity, p910-continuity-maintenance and secretary-demo, with a separate ignored SQLite file per project. Faithful-manual, p97-harness, p910-continuity-maintenance and secretary-demo use `PLAYWRIGHT_FAITHFUL_MANUAL=1`. The Compose test containers, networks and two task-owned volumes were removed after verification. All temporary native test and preview hosts were stopped; the original checkout hosts on 5180/5190 remained running.

Earlier core attempts exposed stale built-in pins and a deep-link bootstrap race. The browser acceptance journey's custom Definition still had the former multiplexed action's click-only migration; Browser v2 additionally requires its explicit typing grant. Corrections retain all scenario assertions. Deep links stay Connecting with Message and Send disabled until attachment to the requested identity. Browser logs record provider/feature/operation/outcome durations while omitting accessible names and private URL components.

Verified gate candidate is `162594346ebc869ff1197f2ca102c15e0c7c5a45`; all five jobs in [hosted run 37722988290](https://github.com/trannamtrung1st/agent-core/actions/runs/37722988290) completed successfully on that exact SHA. Production behavior is unchanged from `ff325a662153d23dc44553a32c41d5972f039664`. Active-response tests inspect actual hub attachment instead of waiting for an idle header label. All seven focused approval/streaming/Stop scenarios pass. The Secretary helper retains exact response count/text and uses its existing 30-second answer budget; a prior hosted snapshot showed the correct final answer arriving just after its default five-second count assertion expired.

Hosted backend on the final candidate passed Domain 152, Infrastructure 824/15 skips, Application 1317/2 skips and API 371/3 skips. Six Docker sandbox cases had unavailable prerequisites on that backend runner; those six passed in the local Infrastructure gate. The remaining skips are explicit live-provider/site opt-ins. The hosted backend job does not include the separately verified four OrderEvents plugin cases. Hosted core (116 tests), acceptance (16 tests), frontend unit/build and Compose are also green. This final evidence update changes documentation only and does not move the tested behavior/test SHA.

Authorized branch `develop/branch-1` is published in [draft PR #3](https://github.com/trannamtrung1st/agent-core/pull/3). [Hosted candidate run 37720003182](https://github.com/trannamtrung1st/agent-core/actions/runs/37720003182) tests behavior SHA `11333f2fa3a4392c41e74b794bb293010b0e72fc`: backend and Compose passed; acceptance exposed the same omitted typing grant. That candidate predates the final bootstrap/telemetry corrections and cannot establish final closure.

At that checkpoint, key-free gates passed and real-model Journey L remained unverified after automatic approval review required destination/payload authorization. The later authorized proof below supersedes that pending status. Historical freeze SHAs remain unchanged.

## Authorized real-model Journey L follow-up

On 2026-10-08, the owner specifically approved `https://openrouter.ai/api/v1/chat/completions`, model `deepseek/deepseek-v4.1-flash`, General Assistant instructions, browser schemas, the generic SPA task and disposable fixture observations/results. The existing API key was read from user-secrets into the test process; local configuration, persistent profiles and user data were unchanged. No main integration was retained.

The first attempts exposed two live-harness issues: unconditional native structured-output advertisement differed from the configured DeepSeek catalog (`StructuredOutput=false`), and inspecting a missing browser context hid the failure behind a null-reference exception. Use the existing tool-response channel and check runtime errors/context first, reporting only error categories/codes, request counts and tool names. Initial structured-mode attempts ended without browser calls; no product authorization, provider adapter, timeout or expected state assertion was relaxed.

The corrected opt-in test passed in 47 seconds with no skips. Through the owned Session Runtime, the real model selected Asset 159, filled Title with `Browser v2 proof` and Notes with `Generic SPA verified`, checked Enabled, inspected resulting state and emitted a nonempty final response. Actual Chromium state assertions passed, and recorded calls included `browser.find`, `capabilities.load` and `browser.fill_form`; no runtime error output occurred. This verifies the generic SPA workflow, not arbitrary model reliability or integrated behavior with main.

Command: `AGENTCORE_BROWSER_V2_LIVE=1 AGENTCORE_LLM_MODEL=deepseek/deepseek-v4.1-flash AGENTCORE_LLM_REASONING_EFFORT=medium dotnet test tests/AgentCore.Application.Tests/AgentCore.Application.Tests.csproj --no-restore --nologo --filter FullyQualifiedName~BrowserV2LiveJourneyTests -v minimal`, with the approved key supplied privately to the child environment. Result log: `/private/tmp/browser-v2-journey-l-tool-channel.log`. Earlier failure logs remain `/private/tmp/browser-v2-journey-l-first.log`, `/private/tmp/browser-v2-journey-l-diagnostic.log` and `/private/tmp/browser-v2-journey-l-offering.log`.

The corrected candidate `ac1cba0b16edaf17b86ee62066fe3aa7b83410ba` passed all five existing hosted key-free gates in [run 37751900338](https://github.com/trannamtrung1st/agent-core/actions/runs/37751900338). The preceding source/fixture candidate `f8e4179b` also passed all five in [run 37746737917](https://github.com/trannamtrung1st/agent-core/actions/runs/37746737917). The owner subsequently authorized main integration and PR completion; [combined-tree verification](branch-1-main-merge-verification.md) supersedes the earlier deferral and draft instruction.
