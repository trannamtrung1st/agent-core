# Task-aware Kev routing verification — 26 October 2026

The bounded S1/S2 follow-up is complete against code commit `90c358c2b015ef53d98cec0a12fa179cdf120e7e` (parent `5580ed56b4501c32594e2cc67c30ca3b8296fa09`). This report qualifies the actual ModelRouting and ReasoningEffort paths on available hardware. It supplements the [original native primitive/transport evaluation](kev-native-verification-2026-10-26.md), whose ContextRelevance/InitiativeRelevance/WorkPriority labels did not establish routing quality. No threshold was lowered and configured fallback is not counted as a successful ranked result.

## Completion audit

| Requested correction | Implemented behavior | Observed evidence |
| --- | --- | --- |
| Meaningful private task inputs | Shared `RoutingTaskSignals` emits task kind, reasoning demand and code presence from at most 4096 UTF-16 characters of already-authorized task text, plus original count/mode/attachment/vision metadata. No raw text, names, snippets, URLs, keys or code is transmitted in these signals. | Same-length `Greet the team.` / `Debug the code.` (15 characters each) produce light/greeting versus substantial/analysis. Both select their expected declared capabilities in real held-out inference. Privacy/boundedness and multilingual projection tests pass. |
| Real routing and effort quality | Frozen public fixture invokes the production projection, router, validator and actual pinned HTTP adapter. Model tests use Auto/modelDefault; effort tests use fixed reasoning model/Auto effort. Both disallow configured fallback and assert their exact ranked basis. | 4/4 calibration and **24/24 held-out** accepted/correct at 0.7; 12 real model choices and 12 real effort choices. Real tenant test requires a ranked reasoning/high pin for complex work and a ranked direct/no-effort pin for a greeting; replay performs no extra inference and SQLite reopening preserves the pin. |
| Appropriate bounded routing latency | Router snapshots local host policy with no status I/O, shares one deadline across model and effort stages and preserves typed Timeout, explicit eligible defaults and caller cancellation. The mandatory authenticated checkpoint witness before each inference remains. | Unit test proves two 100 ms stages cannot each renew a 150 ms budget. Timeout default test makes one inference only and records Timeout/configuredDefault; caller cancellation propagates. Real native profile uses an explicit 2000 ms routing budget, concurrency 1; measured two-stage inference totals 1666.77 ms. Global 500 ms/default-off/shadow settings remain unchanged. |

The same projection is used for foreground text/voice, configured unattended Automation instructions, existing-conversation Automations, already-authorized continuation tasks and parsed background objectives. Completion reports without a task supply unknown/general rather than borrowing private conversation or inventing semantics. These are small local heuristics with multilingual vocabulary and an explicit unknown case, not a semantic classifier or a guarantee about every language/task.

IAM, catalog eligibility/current-authority rechecks, immutable Run pins, replay and approval boundaries retain their existing owners. This follow-up adds no migration, model registry, inference framework, compatibility reader, UI redesign or Docker infrastructure. Existing migration evidence remains in the original delivery. S3–S5 purpose-specific consumers remain explicitly deferred. No tenant/customer data was migrated or reset.

## Frozen routing-specific evaluation

[Fixture](../../scripts/fixtures/kev-routing-v2.json) SHA256: `29fd15e499dd8e45f7b67ff5c132c915c5d5f459ba4a29a3788445691e45f501`. The 4 calibration / 24 held-out split, labels, policy `routing-v2`, acceptance threshold 0.7 and budget 2000 ms were frozen before real inference. A calibration-only validation preceded the full run; no hints, labels, threshold or instructions were tuned after looking at these held-out outputs. Budget-exhaustion handling was subsequently corrected to preserve allowed deterministic fallback, with no change to the accepted-result path. Final strict tenant admission and refusal/fallback regressions pass with that correction.

Each purpose has six light and six substantial held-out tasks across English, Vietnamese, Spanish and French, with text/unattended modes and the equal-length pair. The configured direct/no-reasoning model is compared with a declared reasoning-capable model; the effort options are low/high. IDs, split and labels are not sent to Kev. Neither attached content nor new sources are fetched for hint extraction. Warm-up is explicit and outside evaluated admission. There are no inference retries or fallback successes.

| Set / purpose | Accepted / correct | Constant direct / low baseline | Multiclass Brier | Log loss | ECE, 10 fixed bins |
| --- | --- | --- | --- | --- | --- |
| Calibration model, n=2 | 2/2 | 50% | 0.03412 | 0.12408 | 0.11445 |
| Calibration effort, n=2 | 2/2 | 50% | 0.00125 | 0.02522 | 0.02490 |
| Held-out model, n=12 | 12/12 | 50% | 0.03403 | 0.12561 | 0.11603 |
| Held-out effort, n=12 | 12/12 | 50% | 0.00170 | 0.02899 | 0.02856 |

Brier is mean summed squared categorical error, log loss uses the labeled class, and ECE compares top confidence against correctness in fixed bins. The [raw results](assets/kev-routing-2026-10-26/quality-results.json) retain every expected/selected value, basis, probability distribution, hint and elapsed time; the [summary](assets/kev-routing-2026-10-26/summary.json) retains reliability bins. Minimum accepted top probability was 0.8176 for model selection and 0.9599 for effort. Coverage was 100% in each held-out purpose with no accepted errors. This is a small calibration assessment, not fitted calibration or a population-wide correctness probability.

The labels test declared capability fit given bounded hints. They do not compare generated answers from actual providers, establish cost savings, qualify arbitrary catalog choices, or certify unknown cost/context/latency metadata. Model-label sensitivity, reordered options, saturation under concurrent load, unseen languages and production-distribution calibration are not established by this fixture. The existing deterministic filters, threshold/refusal and explicit default policy remain necessary.

## Exact runtime and measured limits

- Machine: Apple M2 / Mac14,7, 24 GiB unified memory, 10 GPU cores; macOS 27.0.1 arm64.
- Upstream source: `6b719c3c3f367295f6ef336f4f751cf5ff970abc` (kev-1.0), with the checked-in source hardening (bounded queue/connections; truncation and prefix cache disabled).
- Checkpoint: `jaredpalmer/kev-4b@6cfce5c2fa4b4bd64026336ab649c5ca78857d52`; base `Qwen/Qwen3.5-4B-Base@1001bb4d826a52d1f399e183466143f4da7b741b`.
- Native backend/precision: MLX / bfloat16; no quantization. Python 3.13, Torch 2.8.0, MLX 0.32.2, mlx-lm 0.31.3, Transformers 5.17.0, FastAPI 0.141.1, uvicorn 0.53.0, frozen upstream lockfile.
- Authenticated offline service: loopback port 18009, no prefix cache/state truncation. Every inference retains the exact checkpoint witness; the router makes zero extra `StatusAsync` calls. No provider DTO enters Application and no CUDA/NVIDIA check enters the Decision Engine.

| Measurement | Result | Interpretation |
| --- | --- | --- |
| Held-out model routing, n=12 | p50 1033.64 ms; nearest-rank p95 1039.45 ms | Complete shared-router call for model stage, warmed sequential public fixture. |
| Held-out effort routing, n=12 | p50 605.77 ms; nearest-rank p95 637.25 ms | Complete shared-router call for fixed model / Auto effort. |
| Strict real tenant complex admission, one final sample | Model 1050.80 ms + effort 615.97 ms = 1666.77 ms inference stages; whole tenant admission 2118.58 ms | Both decisions share a 2000 ms routing budget. Whole admission includes additional existing IAM/catalog/SQLite work; the budget is not a deadline for those operations. |
| Strict real tenant greeting, one final sample | Inference 1034.41 ms; whole tenant admission 1056.23 ms | No effort call because chosen direct model declares no reasoning levels. |
| Native MLX allocation peak this run | 15,867,964,940 bytes / 14.78 GiB | Device allocations, not total process/macOS memory. Active 8,411,510,280 bytes and cache 1,074,821,783 bytes at shutdown. |

The [final admission result](assets/kev-routing-2026-10-26/admission-results.json) contains exact fixture pins, stage times and total admission times. A prior warmed sample was 2195.19 ms whole complex admission and 1068.24 ms whole greeting admission; there are too few tenant samples for percentile or throughput claims. The previous native report retains the two cold-first-inference measurements of 11.87–16.79 s. The first attempted strict admission here timed out without warm-up; the fixture now warms with a separate 60 s diagnostic cancellation bound before starting admission. It never renews a Run's budget or silently retries. A test-setup attempt also correctly hit the immutable preset identity guard; the fixture was repaired to preserve that identity, without changing catalog rules.

The longer routing-v2 instructions/hints improve this controlled capability-fit result but increase request size relative to old coarse routing. Removing redundant status calls is not a claim of net speedup. Use the explicit 2000 ms/concurrency-1 profile only after warm-up and workload checks on this machine. Service unavailability, low confidence, saturation or exhausted budget still refuses strict Auto. Only deliberately enabled eligible defaults can proceed. No NVIDIA production latency, throughput, CUDA reservation or crash/restart qualification is claimed; that platform-specific performance qualification remains pending.

## Focused verification and operation

Commands ran locally with `--no-restore --nologo -m:1 -p:UseSharedCompilation=false` (regression runs used `--no-build` when applicable):

| Project / filter | Result | Exercised behavior |
| --- | --- | --- |
| Application: `AutoModelRouterTests\|RoutingTaskSignalsTests\|ExecutionModelPolicyTests` | **28 passed** | Fixed/inherited/Auto policy, filtering, privacy/bounded hints, multilingual/equal-length cases, one shared deadline, explicit timeout default, caller cancellation, no status I/O. |
| Infrastructure, explicit live: `LocalKevRoutingQualityTests` | **1 passed**, 28 real decisions | Frozen routing-specific calibration/held-out model/effort tasks; exact ranked bases; no fallback success. |
| Infrastructure, explicit live: `RealKev_tenant_Auto_admission` | **1 passed**, repeated after stage instrumentation | Real model plus high effort selection, direct greeting selection, no duplicate inference on replay, SQLite pin reopening. |
| Infrastructure: `LocalKevLiveTests\|DecisionEngineTests\|DecisionIntelligence_actual_Auto` | **31 passed** | Real typed choice/yes-no/score, wrong-key denial, separate uncertainty/refusal/default path, 500 ms bound, Synthetic admission/replay/cancellation/reopen and current authority revocation during ranking. |
| API: `DecisionIntelligence_HTTP\|Tenant_background_model_starts_detached_owned_work_and_reports_once_to_original_conversation` | **5 passed** | Actual Synthetic HTTP Routing CAS and protected status, Auto Chat/manual Automation, memory/SQLite fixed and Auto background child/report completion once through existing owners. |
| Infrastructure, live opt-out and owned service stopped: `LocalKevLiveTests\|LocalKevRoutingQualityTests\|RealKev_tenant\|DecisionEngineTests` | **27 passed / 4 skipped** | Default tests require no Kev process, checkpoint download or provider credentials. |

JSON/hash, changed Markdown links/fences, private-key exclusion and `git diff --check` were checked. No frontend source changed, so existing UI evidence was retained; no new browser-journey claim is made. No CI was requested, dispatched or awaited. The owned native service is stopped; its private cache/environment remains available for operator reuse, with no key tracked.

The [Real-profile self-test procedure](../decision-intelligence-runbook.md#test-kev-yourself-in-the-real-profile) covers durable native preparation, private key matching, explicit warm-up, strict opt-in tests, `.env` settings, application start, tenant Routing/Chat opt-in, observation and rollback. Real profile alone does not enable Kev. S1/S2 closes within these local verified limits; S3–S5 remain future integrations rather than new work started by this follow-up.
