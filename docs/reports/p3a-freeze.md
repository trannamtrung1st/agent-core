# P3A — Historical multimodal attachment reread

This report records observed P3A behavior from the implementation through P3A-4 verification. Canonical contracts remain in [Backend Interfaces](../04-backend-interfaces.md) and [Backend Implementation](../12-backend-implementation-spec.md).

## Observed behavior

1. **Typed tool results (P3A-1):** `SessionToolExecutor.ExecuteAsync` returns `ToolExecutionResult` with UTF-8 `Text` counted against the tool-output budget and optional ephemeral `Parts` (for example `ModelImageContent`) that are not embedded as base64 in JSON tool text.
2. **Historical image rehydration (P3A-2):** `attachments.read` on image attachments runs through `IAttachmentProcessor`, returns metadata JSON in `Text` and sanitized image bytes in `Parts` when the remaining text budget allows. `ToolResultAdmission.AdmitForModel` removes image `Parts` when `ILanguageModel.Capabilities.Vision` is false and replaces them with `vision_required` JSON in tool text.
3. **Provider projection (P3A-3):** `OpenAICompatibleLanguageModel.MapMessages` emits textual `role=tool` then an adjacent Infrastructure-only multipart `role=user` tool-data frame with mapped `image_url` parts. Non-vision adapters reject image-bearing tool `Parts` before HTTP. `ScriptedLanguageModel` supports `[test:historical-image-reread]` for deterministic Synthetic tools+vision continuation. Persisted snapshots do not retain wire-only tool-data framing or image base64 from tool continuations.
4. **User-visible verification (P3A-4):** Playwright `historical-image-reread.spec.ts` covers later-turn Scripted Vision reread and tools-capable Scripted Alpha refusal to claim historical image sight.

## Freeze status

**Observed/frozen** (2026-09-21). **Implementation HEAD** `c0f8a8594c509e0841a2cf7c3078926779c0a5ec` (`c0f8a85`) carries P3A runtime behavior and passed the key-free gate below after focused-output review closure. **Freeze documentation** (this report, [Implementation Plan](../18-implementation-plan.md) P3A section, and `TODO.md`) is committed in the TDP P3A-4 production batch on the same branch immediately after that gate. P3A-1–P3A-3 **focused_output** review loop `review-focused-output-01` closed **verified** on production output revision 5 at `c0f8a85`. **P3B** may proceed; public-web work remains blocked until P3B workspace/registry slices complete per proposal §22.

## Key-free gate (implementation HEAD `c0f8a85`)

Commands match `.github/workflows/synthetic.yml` on implementation HEAD `c0f8a85` (2026-09-21), recorded in TDP production batch for `item-70caa87b782c`.

| Stage | Result |
| --- | --- |
| `tests/realtime-js` `npm ci` | OK |
| Domain tests | 76 passed |
| Infrastructure tests | 202 passed / 12 skipped |
| Application tests (`--blame-hang --blame-hang-timeout 5m`) | 489 passed, no hang |
| API tests | 162 passed |
| Web Vitest | 384 passed |
| Web production build | OK |
| `CI=1 pnpm exec playwright test` | 42 passed (includes `historical-image-reread.spec.ts`, `scripted-vision.spec.ts`) |
| `./scripts/compose-sqlite-volume.sh` | `compose sqlite volume check passed` |

## Optional Real probe

GPT-4o-mini historical-reread live probe: **SKIPPED** — `OPENROUTER_API_KEY` / `OPENAI_API_KEY` not used for default verification.

## Persistence and safety (unchanged invariants)

- Wire-only multipart tool-data user messages are not persisted or shown in the browser.
- Cross-session `attachmentId` reads fail closed.
- Runtime epoch / response supersession still rejects late tool results.
- Cancellation and mailbox ownership are unchanged from Phase G typed tools.
