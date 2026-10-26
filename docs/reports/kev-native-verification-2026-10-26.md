# Real Kev native and held-out verification — 26 October 2026

## Overall verdict

**The requested available-hardware Decision Intelligence implementation and verification are complete.** Real pinned Kev-4B runs natively through MLX/bfloat16 on this Apple M2 Mac. Authenticated choice/yes-no/score, the shared routing policy, actual IAM/Run admission/replay/SQLite reopen, migration/Synthetic integration and Docker-to-native inference all pass. The frozen held-out evaluation records 33/36 correct overall and 28/28 correct among decisions accepted at the unchanged 0.7 threshold; all three errors are below threshold.

**Only NVIDIA-specific production latency/throughput qualification remains pending.** It is not a blocker for this completion. M2 results make no NVIDIA performance claim. The optional engine, disabled/shadow defaults, explicit eligible-default policy, deadlines and tenant opt-in remain unchanged. No application milestone-wide or unseen-production-distribution acceptance is implied. S3–S5 consumers remain the proposal's explicitly deferred subsequent slices; exercising generic purposes does not install those consumers.

Exact implementation code SHA: **d640f882f4c7cbfbdbae2d5dee09e0d00cba978f**. This report and its delivery-record note are a subsequent documentation-only commit. The original implementation was `33d2e8857b2b2fbec4a661f1b9d96c2a8d75307a`, with delivery baseline `68235d667e6e1e51fefc77784939aa1a89c3577f`.

## Exact environment and exercised paths

| Item | Recorded value |
| --- | --- |
| Hardware / OS | Apple M2, 10-core integrated GPU, 24 GiB unified memory, Mac14,7; macOS 27.0.1 / arm64 |
| Kev source | `6b719c3c3f367295f6ef336f4f751cf5ff970abc` (`kev-1.0`), frozen upstream uv lock |
| Artifact | `jaredpalmer/kev-4b@6cfce5c2fa4b4bd64026336ab649c5ca78857d52` (resolved v1.0) |
| Base | `Qwen/Qwen3.5-4B-Base@1001bb4d826a52d1f399e183466143f4da7b741b`; LoRA rank 16 |
| Serving | Native MLX 0.32.2 / mlx-lm 0.31.3, bfloat16; upstream device reports `mps` with backend `mlx` |
| Other packages | Python 3.13; torch 2.8.0, transformers 5.17.0, FastAPI 0.141.1, uvicorn 0.53.0 |
| Calibration | Checkpoint temperature `2.406050072164233`, unchanged; caller threshold 0.7 frozen before evaluation |
| Isolation | Private ephemeral bearer, loopback listener; prefix cache and state truncation disabled; model download disabled while serving |
| Native source hardening | SHA256 `16d67b616cd75d6206b6bf88075d94519d70b60633bd1d98083b76f987e295c8`; bounded queue/connections, fail-on-drift patch |

Used a separate temporary runtime checkout/environment/cache; no application database, root `.env`, tenant preference or hosted provider account was changed. The repository's explicit download step cached the exact adapter/base. A 0600 key file authenticated only the local test service. Readback verified the immutable run, MLX/bfloat16, disabled truncation and prefix cache. Unauthenticated requests returned **401**; wrong-key .NET readiness refused.

Actual requests covered English/Vietnamese/Spanish/French evidence selection, reversed choice order, yes-no, ordered score and the production-shaped coarse model-routing payload. The .NET tests crossed the real LocalKevDecisionAdapter and shared AutoModelRouter. An additional real Kev test created an authorized Auto Session, admitted one Run through the existing IAM/model owners, replayed its input without another decision call and reopened the immutable pin from SQLite. No conversational provider was contacted by those live admission checks.

Docker Desktop Compose was exercised with the new external-service overlay and the actual compiled .NET adapter mounted into the existing application runtime image as a temporary diagnostic entrypoint. It reached the Mac's loopback-bound MLX service through `host.docker.internal` at **8009 and 18009**, validated the pinned model, and selected the relevant Paris option with p=0.9963. The temporary project/volume/network was removed. This proves the container transport and resolved Compose endpoint; it does not claim a new production deployment or UI acceptance run.

The [runbook](../decision-intelligence-runbook.md) contains native setup, source hardening, external-service Compose and opt-in commands. [Raw smoke/bridge measurements](assets/kev-native-2026-10-26/results.json), [frozen fixtures](../../scripts/fixtures/kev-quality-v1.json), [evaluation tool](../../scripts/decision-quality.py) and [full held-out results](assets/kev-native-2026-10-26/quality-results.json) are retained.

## Held-out quality and calibration

The 48 public curated cases were frozen **before inference**: 12 calibration and 36 held-out, with four languages, three decision kinds and quoted untrusted text. Fixture SHA256: `543b77799330cedd0d569f9acee16a2fddc89ffc3c3c485a03f3ae33e403e115`. Labels, case IDs and split never enter the model request. Neither labels nor the 0.7 threshold were adjusted after observing held-out outputs. Prefix caching is off and requests are independent. Explicit warm-up is outside the evaluated sets; no inference POST is retried.

| Set | Accuracy | Constant-first-option baseline | Accepted at 0.7 | Accepted accuracy | Multiclass Brier | Log loss | ECE (10 bins) |
| --- | --- | --- | --- | --- | --- | --- | --- |
| Calibration, n=12 | 12/12 (100%) | 50.0% | 10/12 (83.3%) | 10/10 | 0.05159 | 0.12558 | 0.10920 |
| Held-out, n=36 | 33/36 (91.7%) | 44.4% | 28/36 (77.8%) | 28/28 | 0.10618 | 0.18565 | 0.10699 |

Brier here is the mean of `sum_k((p_k - y_k)^2)` over the complete categorical distribution, including the two-class yes-no case. Log loss uses the probability of the labeled class. ECE compares top-probability confidence with observed correctness in fixed bins; every bin and case distribution is in the results file. This is a calibration **assessment**, not temperature fitting on the held-out data.

| Held-out kind | Raw accuracy | Accepted coverage at 0.7 | Accepted accuracy |
| --- | --- | --- | --- |
| Choice | 12/12 | 12/12 | 12/12 |
| Yes-no | 9/12 | 4/12 | 4/4 |
| Score, argmax level | 12/12 | 12/12 | 12/12 |

The three errors are preserved: `noul-04` (Vietnamese silence request, confidence 0.5069), `noul-11` (Spanish requested reminder, 0.6783) and `noul-13` (French requested reminder, 0.5341). Each is below 0.7, so the existing shared validation refuses it. Lower yes-no coverage matters for any future initiative/event consumer; this evaluation does not enable one. No threshold was lowered to obtain an accepted result.

These controlled primitive tasks demonstrate useful selective decisions against the stated constant baseline. They are not a population-wide calibration guarantee, an evaluation of actual conversational-provider outputs, or proof that the current coarse routing signals identify the best provider for every semantic task. S1/S2 remains safe through explicit refusal/default policy when the available evidence is weak; cost/context/per-provider quality stays unknown rather than invented.

## Latency, memory and operational findings

| Measurement | Observed result | Scope |
| --- | --- | --- |
| First inference after two separate loads | **11.87 s / 16.79 s** | Includes first-use work; no cold-start percentile claim. |
| Short warm smoke | n=6; p50 **406.55 ms**, nearest-rank p95 **423.96 ms** | One short question, two options; raw sequential HTTP. |
| Held-out requests | n=36; p50 **413.331 ms**, nearest-rank p95 **554.078 ms** | Mixed choice/yes-no/score public cases. |
| Repeated production-shaped routing | n=4; p50 **812.59 ms**, nearest-rank p95 **847.74 ms** | Longer GUID/capability strings and coarse task features. |
| Shared router | Uncertainty refused in **835.2 ms** with a 2,000 ms budget; a 500 ms budget refused in **504.9 ms** | Real policy path, not just raw endpoint latency. |
| MLX device allocations | Peak **14.78 GiB**, active **7.83 GiB** at shutdown | Across load/merge/inference; not total macOS/process memory. Buffer-cache usage varies, about 0.5–1.0 GiB. |
| Sampled CPU process RSS | 691,728 KiB | Separate snapshot; excludes some Metal allocations. |

No wire/schema/authentication defect was found. The following concrete issues were addressed or documented:

- `DecisionIntelligenceOptions` and `LocalKevDecisionAdapter` previously excluded the Docker host bridge and forced port 8009. They now accept configurable local ports and the explicitly named bridge, retaining private IPv4/loopback verification, deny-public/metadata behavior, no redirect/proxy, strict URL shape and the model witness. `docker-compose.decision.external.yml` adds no inference container or GPU reservation. Both default/non-default bridge ports passed real inference.
- `deploy/decision/harden.py` previously accepted only the container source path. `--source` now supports a native pinned checkout; output is byte-identical to the exercised service and source drift/reapplication refuses without partial mutation.
- The default **500 ms** deadline is too short for this M2 routing fixture. A test-only **2,000 ms** warm budget reached validation, which then correctly refused the **0.5751** top probability below 0.7. Preserve the global default; deliberately configure a measured native budget where effective Auto is desired. Fixed selection or the explicitly allowed eligible default remains deterministic.
- `TenantRunService` sends bounded length/mode/image/count features rather than raw private conversation. Equal-length tasks can have identical projected evidence; no semantic-provider superiority is claimed. The tested uncertain payload takes the existing refusal/default path. Richer bounded task features or provider comparison belong to a separately evaluated policy improvement, not a fabricated success in this delivery.
- Readiness establishes checkpoint identity, not completed inference warm-up. First-use samples exceed even the host's maximum 10 s admission deadline; explicit operator warm-up uses a longer diagnostic deadline **outside Run admission**. No automatic retry or unbounded admission timeout was introduced.

The native profile and evaluation are qualified within these recorded bounds. There is no NVIDIA latency, throughput, CUDA crash/restart or GPU reservation acceptance claim; that platform-specific performance qualification alone remains pending and does not block this work.

## Focused checks

- Infrastructure filter `LocalKevLiveTests|RealKev_tenant|DecisionEngineTests|DecisionPreferenceMigrationTests|DecisionIntelligence_actual_Auto|DynamicModels_actual`, with explicit live flag/private key: **42 passed**. Includes three real-service tests, authenticated typed adapter, timeout/uncertainty/explicit fallback, real tenant admission/replay/reopen, supported migration/refusal and Synthetic/provider-wire admission/dispatch fences. Used `--no-restore --nologo -m:1 -p:UseSharedCompilation=false`; no full local CI suite.
- Three live tests repeated with `AGENTCORE_KEV_ENDPOINT=http://127.0.0.1:18009`: **3 passed**. Test code honors the configured port; no special success path or alternate model is used.
- Default opt-out filter `LocalKevLiveTests|RealKev_tenant|DecisionEngineTests`: **27 passed / 3 skipped**, with service stopped. Default tests require no model download, service or provider key.
- Synthetic API `DecisionIntelligence_HTTP` journey: **1 passed**. Preserves protected status, Routing CAS, saved Auto intent and manual Automation admission through existing owners.
- Frozen quality tool: **48 real responses** with valid distributions, typed shape/score consistency and matching model identity. Quality/calibration measurements above include errors and abstentions without editing labels.
- Compose external overlay: configuration/default and custom endpoint checks **passed**; actual .NET container-to-native inference **passed** at both ports; disposable project cleaned up.
- Five decision-specific Python checks, Python compilation, native hardening parity/drift check, JSON/hash/link/fence/private-key/diff checks: **passed**. Earlier delivery retains the unchanged frontend/browser and broader Synthetic evidence; no frontend source changed here.

No installed/customer database was migrated or reset. The existing canonical migration was verified only against disposable supported/unsupported predecessor fixtures, including reopen/guard preservation. No CI was dispatched/rerun/awaited. All owned native test services are stopped. The dedicated environment/cache remains at `/private/tmp/agent-core-kev-native-4yt8cw3v` (about 10 GiB including dependencies) for repeat tests; no private key is tracked.

Smallest final recommendation: retain disabled/shadow defaults and deterministic refusal/fallback; opt in deliberately using the measured native deadline and explicit warm-up when appropriate. Continue future policy/consumer work against its own representative data. NVIDIA performance testing can wait for that hardware without reopening this completion.
