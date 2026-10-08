# Browser v2 full-cutover verification

Date: 2026-10-08. Status: implementation candidate; behavioral closure is pending.

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
| L — real model | An opt-in test drives the owned runtime through find, contextual capability loading and multi-field SPA actions using OpenRouter. | Pending explicit authorization for the external fixture payload |

## Runtime UI verification

Playwright MCP operated the Synthetic application on isolated API/Vite ports 5290/5390 with an ephemeral browser context. A General Assistant v17 chat looked up AC-1042 through the loopback browser fixture. The observed output was an intermediate application message followed by exactly one completed final response: AC-1042 is In review. Admin provider diagnostics rendered the advertised feature list, readiness and limits. At 390 pixels, document width was 390 pixels and the diagnostics exposed no private path.

These interactions complement the CLI suites; they do not replace the full regression gates or real-model journey.

## Dependency and environment verification

NuGet security audit rejected the previous ImageSharp 3.1.11 dependency. ImageSharp 4.1.2 then blocked Release compilation without a SixLabors license. The candidate replaces it with MIT-licensed SkiaSharp 4.153.1 and its Linux native package, without audit suppression or license bypass. Image sanitation decodes bounded PNG/JPEG/WebP/GIF pixels and re-encodes metadata-free PNG/JPEG; the processor cache version advances to `attachment-processors/3`. Header/pixel bounds and EXIF-stripping tests pass. Browser PNG/JPEG/WebP screenshot tests pass. The initial replacement build had zero warnings and errors.

Shared tracked application settings, launch profiles, UserSecretsId, Vite configuration, `.env.example` and hosted workflow remain unchanged. Test ports, databases and profiles use local/environment overrides. No original checkout database, saved browser profile or running host was reset. Historical report screenshots generated by existing E2E tests were restored to their original bytes.

A later full regression attempt encountered a full host disk and Docker build-cache I/O errors. SQLite write failures from that attempt are environment failures and are not counted as passing evidence. Generated Linux/Windows runtime copies in this checkout were removed to recover space; macOS runtime assets and source files were retained. Gates are rerun after recovery.

## Gate ledger

Final local and hosted results will be recorded here after execution. Current evidence includes a clean audited build, Domain 152 passed, Infrastructure 830 passed/9 opt-in skips, API 371 passed/3 opt-in skips, plugin 4 passed, frontend 739 passed before the final diagnostics addition, and the final Admin file's 42 tests passed. The first complete Playwright core run passed 110/116 and exposed stale built-in pins plus two timing/noise failures; this is not a passing core gate. Corrections and complete reruns are in progress.

Full closure requires all current local gates, the real-model generic-fixture proof, and all five hosted Synthetic jobs green on the final behavior SHA. The implementation is not behavior-frozen while any required gate remains pending.
