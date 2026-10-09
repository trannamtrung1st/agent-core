# Model catalog refresh verification

Verified 2026-10-09/10. User-authorized focused catalog/provider/selection enhancement; P10/P11 remain unopened. This report accompanies the model-catalog implementation commit. Earlier milestone and browser reports remain historical. Exact-head hosted CI has not been run for this local change.

## Delivered behavior

The [catalog/configuration owner](../15-persistence-and-configuration.md#real-model-catalog) records all five entries, exact IDs, efforts, defaults, limits and source links. DeepSeek Medium stays the system default; Luna Low, Haiku Medium and Sol Medium are explicit choices. OpenRouter Free remains experimental. Retired built-ins are unavailable for new selection, but existing concrete Session pins are not rewritten. Definition/Automation/unattended admission rejects unavailable pins; an operator can deliberately restore a verified catalog entry. There is no migration, reset, automatic routing or cost-based substitution.

One factory owns the built-in Real catalog; launch and Compose no longer duplicate its entries. Explicit complete catalog overrides remain supported, including transport/semantic response policy. Unknown primary defaults fail visibly. Synthetic catalog and fixtures are preserved.

Luna and Sol use stateless OpenRouter Responses with full history, `store:false`, exact `call_id`, encrypted reasoning continuation, image input and native structured finals. HTTP/SSE/cancellation/timeout/circuit handling stays in the existing adapter. Completed provider output is validated before any tool call is published; partial calls cannot execute. Opaque model/transport-bound continuation survives durable checkpoints, with adapter and existing checkpoint byte limits. Chat reasoning details are retained for continuation and excluded from visible answers.

Haiku uses Chat Completions and native structured finals. Its verified OpenRouter routes accept automatic tool choice; forced named/required choice fails rather than silently changing the request. Nullable schemas use equivalent unions, preserving enum and existing union constraints.

DeepSeek advertises verified image/native schema capability. Broader native-schema-plus-tool trials regressed: browser runs skipped tools and a transient-error lookup reached the output limit. Its explicit `PreferResponseFunction:true` catalog setting preserves the established schema-bearing semantic response-function channel. Reasoning, native capability metadata, tool identifiers and validated final semantics remain intact; there is no model-name conditional or automatic fallback.

The shared frontend utility orders only supported intensity values as none → minimal → low → medium → high → xhigh → max. Unknown/nonlinear modes remain discrete choices. Chat slider bounds, labels, screen-reader value text and Right/Up versus Left/Down agree. Admin Definition, Instance, unattended and Automation selectors share the ordering. Switching retains valid efforts and otherwise uses the selected model default. Sorting does not revise saved values or wire pins. Existing Ant Design v6 controls, layouts and tokens are reused; [design guidance](../../.agents/context/DESIGN.md) is synchronized.

## Local verification

| Check | Observed result |
| --- | --- |
| Infrastructure catalog/Responses/schema/OpenAI adapter/semantic wrapper suites | 160 passed, 5 explicitly opt-in historical live tests skipped |
| Application binder/execution model policy/opaque checkpoint/generic SSO suites | 21 passed, paid comparison skipped |
| Application Session selection/generation retry/browser lifecycle/Admin Automation suites | 43 passed |
| Real-profile host journey on final settings | Passed; validates catalog/defaults, supported selection and actual API Session pins without paid inference |
| Earlier affected API selection group | 8 passed |
| Shared effort utility/Chat picker/ExecutionModelFields/ChatApp | 50 passed |
| Definition candidate editor full suite | 14 passed with isolated worker and command-only 90-second timeout; original 30-second runs timed out in existing heavy form tests, and isolated failed cases subsequently passed |
| Production API Release build | Passed, zero warnings/errors |
| Frontend production build | Passed; existing Rollup annotation/chunk-size warnings |
| Focused Synthetic session-model Playwright E2E | 2 passed |
| Whitespace and changed-document link/fragment consistency | Passed; 453 changed-document local paths/heading fragments resolve, and `git diff --check` is clean |

The backend checks execute owned runtime requests, generic browser authentication and actual tool effects; provider fixtures inspect exact serialized contracts and normalized error outcomes. Checkpoint tests retain opaque state rather than parsing provider DTOs in Application. Retry/lifecycle tests are deterministic Synthetic evidence, not live long-running provider evidence.

The Playwright MCP Synthetic journey submitted and reloaded a Beta conversation, switched to Alpha High and reloaded the persisted pin, exercised Home/End and all four arrow directions with descending catalog input, saved/reloaded an Instance model/effort override, and observed ascending Admin choices. Expected effort/pin values matched API/reloaded UI. Chat and Admin controls were reviewed at 1440, 768 and 390 pixels; mobile controls fit without horizontal overflow. Current console/network checks were clean. API catalog tests separately verify the five Real names/capabilities; MCP presentation evidence used Synthetic models.

## Paid fixed-model checks

`AGENTCORE_MODEL_CATALOG_LIVE=1` plus the configured OpenRouter key enables eight tests. Final execution passed **8/8**. Each fixed model called a real inference tool, received an injected transient error, retried and returned the independently known answer through the validated semantic schema. Each also identified a locally generated red image in a structured conversational reply. Defaults were preserved during these tests; other advertised effort levels were verified from current documentation/metadata rather than exhaustively invoked.

| Model / effort | Transport | Executed lookup calls | Input/output tokens across recovery | Recovery elapsed |
| --- | --- | --- | --- | --- |
| Luna / low | Responses | 2 | 2,428 / 76 | 5.75 s |
| Haiku / medium | Chat Completions | 2 | 7,255 / 139 | 8.60 s |
| Sol / medium | Responses | 2 | 2,428 / 76 | 6.23 s |
| DeepSeek / medium | Chat Completions + response function | 2 | 5,553 / 262 | 2.97 s |

An earlier native DeepSeek recovery trial failed `outputLimit`; it is retained here as evidence for the explicit response-function policy. The original simpler lookup/image run passed 8/8, which did not establish broad workflow reliability.

## Paid browser comparison

`AGENTCORE_BROWSER_BENCHMARK_LIVE=1` runs three natural-instruction trials for each fixed model. All sites, email, password and record contents come from `GenericSsoFixture`: fresh loopback origins, invented demo records, isolated in-memory ownership and temporary browser profiles. No user site, record or profile is opened. Safe metrics contain model/effort, elapsed time, token/cost estimates, call names/counts and error codes, never credentials or raw prompts/pages/arguments.

Final execution passed **12/12 independently verified completions**, including authenticated DOM/server state and completed final replies. All four model theories passed. Each trial used a fresh browser profile; the inference client was reused after its first trial.

| Model / effort | Verified completions | Valid / malformed calls | Median latency | Total input/output tokens (3 trials) | Estimated cost per verified completion |
| --- | --- | --- | --- | --- | --- |
| Luna / low | 3/3 | 22 / 0 | 18.7 s | 254,651 / 861 | $0.008632 |
| Haiku / medium | 3/3 | 20 / 0 | 24.4 s | 362,963 / 3,842 | $0.012739 |
| Sol / medium | 3/3 | 21 / 0 | 28.9 s | 228,662 / 791 | $0.155078 |
| DeepSeek / medium | 3/3 | 23 / 0 | 13.4 s | 280,759 / 3,848 | $0.029615 |

No final trial recorded blocked/repeated tool failures or timeouts. Browser recovery success therefore has no failure denominator in this sample; injected transient-error recovery was separately verified for all four models above. Base-rate estimates per million input/output tokens are Luna and Haiku $0.10/$0.50, Sol $2/$10, DeepSeek $0.30/$1.20, checked against OpenRouter on 2026-10-09. Estimates use recorded completed-request usage and exclude caching, long-context tiers and routing/provider variation; they are not invoices.


The earlier pre-correction comparison returned Haiku 3/3, Luna 3/3, Sol 2/3 (one final `responseTooLarge` after authenticated DOM success), and DeepSeek 0/3 (no browser calls under simultaneous native schema). These measured failures are not erased by subsequent retries. Small samples are neither a ranking nor a reason to change the system default.

## Remaining verification limits

No exhaustive effort-by-effort live matrix, Free-router quality guarantee, private-site UAT, paid long-running/background restart/recovery trial, full solution/frontend suite, Compose acceptance or exact-head hosted CI is claimed. Deterministic affected selection, Automation/background lifecycle, retry and checkpoint coverage passed. The fixed models' paid fixtures verify concrete inference paths, not universal reliability across arbitrary agent workflows. Earlier milestone freezes and their CI evidence remain unchanged.
