# Adaptive visual browser assistance — local verification

Date: 2026-10-10 (Asia/Ho_Chi_Minh).

Initial delivery was an uncommitted working-tree enhancement on `develop/branch-1`, based on `b7a9329e9c0c24a9aeb47001d3dc7ab5d0258c0c` (the local `origin/main` also points there). Existing staged/unstaged browser hardening, operational limits, Admin and documentation edits were retained. That initial pass performed no checkout, reset, commit, push or hosted CI wait. The subsequent user-authorized review commits and pushes all combined changes on this branch; see [combined review](combined-browser-review.md) for corrected gaps and current checks. This is not a historical milestone reopening or a final-SHA hosted acceptance claim.

## Reused implementation and resolved gaps

The native Playwright provider, semantic targets, tool/capability authorization, approval policy, Session/AgentRun runtime, vision feature detection, artifacts, model serializers, checkpoint rehydration, quotas and safe operation telemetry remain the owners. No second provider, observation tool, JavaScript tool, screenshot scheduler, analytics service or Admin observation selector was added. Product frontend behavior/layout was not changed by this enhancement; its HTML page is a backend-only deterministic browser fixture. The Impeccable routing/design context was checked: this backend-only change does not require a product presentation redesign or a new Admin surface.

A single contextual browser strategy now explains semantic sufficiency, proactive visual inspection, semantic action preference, fresh coordinate fallback, independent outcome verification and bounded observation/recovery. Screenshot discovery metadata recognizes visual/layout/dashboard intents. Text-only guidance and receipts explicitly distinguish artifact creation from image understanding.

Screenshots add a bounded redacted semantic observation and existing snapshot/tab IDs, actual encoded dimensions, image-delivery status and coordinate-evidence status. The output budget clips nested semantics before dropping essential artifact identity. Authorized vision gets real image parts; text-only gets no image bytes. Existing Chat Completions and Responses serializers are retained; checkpoints persist artifact references and rehydrate only owned validated bytes.

Native coordinates were previously not tied to a screenshot. Mouse now requires a matching viewport screenshot `snapshotId`; Session/page/generation/viewport/DOM/scroll checks span semantic collection and pixel capture, and reject stale/foreign/ordinary-semantic IDs before an effect. Interaction consumes authority; target/full-page captures never confer viewport authority. The existing settle observer owns visual change counting, and capture waits within its existing bound. Changes across semantic reads and pixel capture mark the observation unsettled and withhold coordinate evidence. Pending requests/fonts and active animations withhold coordinate evidence. SDK transient masking and Core caret suppression do not invalidate the capture itself.

Privacy testing found that explicit `data-sensitive` fields could be masked in pixels but appear in semantic text. Secret collection and semantic target classification now honor that marker too. Caret hiding uses a removable Core masking stylesheet. Existing canvas/SVG/iframe masking and privacy-disable policy remain in force.

Broader native fixtures needed setup corrections without changing policy assertions: permitted frame navigation now uses a shared test-only native-click helper inside its cancellation scope; the initial DNS alias HTTP fixture accepted the proxy Host header using a portable listener prefix; subsequent integration preserves the concurrent hardening commit's loopback TCP alias fixture. Protected-field and signed-wheel tests now supply fresh screenshot IDs so they continue to exercise security and delta validation beyond schema validation. DNS rebinding/redirect denial, protected-point denial, signed scrolling, frame generation fencing and frame interaction denial remain asserted.

## Executed scenarios

| Scenario | Actions and expected outcome | Observed local evidence |
| --- | --- | --- |
| Accessible form, text-only and vision | Navigate, semantic snapshot, fill Notes, click Save, independently verify status; zero screenshots | Owned Synthetic runtime completed; DOM notes status matched |
| Enterprise dashboard, text-only | Navigate, observe, open Cooling project, scope repeated Inspect button to PUMP-1042 row, verify selection | Completed semantically; zero screenshots and image parts |
| Enterprise dashboard, vision transport | Semantic observation first, open project, one capture, scoped semantic click, independent verify | One real PNG reached model continuation; matching snapshot/tab receipt; correct DOM selection |
| Weak visual control | Observe, capture once, authorized viewport coordinate click using its screenshot ID, verify selected pump | Correct PUMP-1042 detail/status; one capture and one coordinate action |
| Freshness | Screenshot then navigation/resize/tab/DOM layout/scroll/action, try old ID, recapture and act; mutate during combined capture | Stale actions refused without effect; mutation during collection withheld authority and marked unsettled; fresh evidence recovered and DOM/verify proved selection |
| Scope/context | Try foreign Session screenshot ID, ordinary semantic snapshot ID, full-page/element capture IDs | No coordinate authority; unchanged semantic refresh remains usable |
| Privacy | Visible `data-sensitive` input, capture and decode pixels, inspect semantic receipt | Black masked pixels; protected string absent from semantics |
| Capture failure/budget | Missing capture target, successful retry in same one-capture scope, over-quota capture, semantic observation | Failure released quota; success stored artifact; quota diagnostic stopped recapture; semantic observation remained usable |
| Text-only capture | Capture artifact with Vision=false, inspect parts, attempt mouse | `imageDelivered=false`, no image parts, truthful diagnostic; mouse denied |
| Output budget | Capture with 512-byte output headroom | Valid bounded JSON, artifact identity retained, clipped semantics explicitly marked |
| Provider wire | Serialize actual captured PNG through real OpenAI-compatible adapter using offline HTTP handler | Decoded wire image URL equaled exact captured bytes; tool JSON held no base64 |
| Checkpoint ownership | Restore screenshot tool receipt and resolve artifacts with vision/text-only/foreign/deleted cases | Existing focused checkpoint suite passed; foreign/deleted images unavailable; text-only received no bytes |

The scripted model selects predetermined fixture actions/pixels. These tests establish real Chromium effects, authorization, transport and runtime mechanics; they do not establish autonomous visual understanding or live-model effectiveness.

## Checks

Focused Application browser/tool-awareness/artifact checks: **75 passed**. Focused adaptive/checkpoint/continuation checks: **29 passed, 3 explicit live-provider checks skipped**. Native freshness/journey/DNS recheck: **13 passed**. These rechecks resolved the initially observed fixture and freshness failures; assertions were preserved.

Broader local checks:

| Check | Outcome |
| --- | --- |
| Full Domain | 185 passed |
| Full Application | 1578 passed, six explicit live-provider skips; the subsequently added accessible/vision case passed with the final focused runtime suite (10 passed, including five Chromium/runtime journeys) |
| Full Infrastructure | 1044 passed, nine explicit live-provider skips with the combined-observation fence; final unsettled-receipt refinement then passed nine freshness tests and ten owned runtime scenarios |
| Full API | Two full runs each passed 406, failed one, skipped two opt-in checks: first event-filter deadline, second unrelated maintenance restart approval (`WaitingForApproval` vs `Cancelled`). Both failing cases passed the matching isolated recheck (three theory/fact cases). Full API gate is not claimed green |
| OrderEvents plugin | Four passed |
| Frontend build | `pnpm -C web run build` passed (existing bundle/deprecation warnings) |
| Supplementary frontend unit suite | 832 passed, five timed out across two Admin files (837 tests, 105 files); all five exact failing tests passed the isolated matching recheck without source/assertion/timeout changes. Full frontend gate is not claimed green. No product frontend source was changed by this enhancement |

Commands used `dotnet test AgentCore.sln --no-restore --nologo -m:1 -nr:false -p:UseSharedCompilation=false` for the initial complete backend pass, then the same build flags with individual Application, Infrastructure and API project paths and TRX loggers for bounded rechecks. Application runtime recheck: `--filter FullyQualifiedName~AdaptiveBrowserRuntimeTests`. API failure recheck: `--filter 'FullyQualifiedName~Protected_Automation_approval_survives_restart_and_late_delete_or_opt_out_wins|FullyQualifiedName~Webhook_true_false_and_coalescing_preserve_receipt_replay_and_owner_projection'` with `--no-build --no-restore`. Frontend unit command: `pnpm -C web run test --run --maxWorkers=1`. Matching frontend failure recheck uses the same worker/timeout settings, both Admin test files and `-t` selecting the five exact failed names; JSON and log evidence are saved in `final/frontend-failure-recheck.*`. Local test execution required sandbox escalation for MSBuild/test sockets and Chromium; automatic review permitted these checks.

The initial full backend run exposed ten Infrastructure fixture/coordinate-consumer failures, all resolved without changing policy assertions (1043-pass full recheck). Earlier failing iterations remain diagnostic evidence, superseded only by matching successful checks. Evidence is under `/private/tmp/agent-core-adaptive-results/` (focused TRX files, `full-backend/` and `final/`). Final focused evidence is `final-evidence-settled.trx` (nine passed) and `final-runtime-settled.trx` (ten passed). The final combined-observation fence is additionally exercised by a deterministic DOM mutation at the existing observation test hook: capture remains available for context, coordinate authority is withheld, and fresh recapture recovers.

Whitespace and our canonical links/anchors/code fences were checked. Subsequent integration retains the concurrently completed [browser hardening report](browser-hardening-verification.md), resolving the canonical link, and adds [current combined checks](combined-browser-review.md). Browser SPA/voice E2E and Compose deployment gates were not rerun because this enhancement changes neither product UI nor deployment/voice behavior; owned runtime/native Chromium journeys exercise the affected backend use cases. Hosted CI is unrun/unawaited as requested; no full milestone acceptance is claimed.

## Limits and optional live acceptance

Default checks use Synthetic/scripted models and offline HTTP handlers. Real vision-model understanding has **not** been exercised. `AdaptiveBrowserLiveTests` provides two bounded configured-model journeys, guarded by explicit `AGENTCORE_ADAPTIVE_BROWSER_LIVE=1`, `OPENROUTER_API_KEY`, and `AGENTCORE_LLM_MODEL` naming a catalog model with Vision and Tools. It never switches models or enables inference just because credentials exist. Run with `dotnet test tests/AgentCore.Application.Tests --filter FullyQualifiedName~AdaptiveBrowserLiveTests` only when that paid opt-in is authorized.

DOM privacy and observation freshness remain best-effort for hostile/dynamic rendering. Existing strict screenshot disablement is the stronger privacy option. Masked canvas/SVG/iframe regions cannot gain visual authority through adaptive guidance. The initial delivery had no implementation commit; the subsequent combined review is committed on the same branch, and its Git commit identifies the candidate. External CI is unawaited as requested. See [canonical regression matrix](../16-testing-strategy.md#adaptive-visual-browser-regression-matrix) and [operational comparison procedure](../17-observability-and-operations.md#comparing-semantic-and-adaptive-browser-workflows) for subsequent effectiveness evaluation.
