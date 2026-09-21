# P3A — Historical multimodal attachment reread

This report records observed P3A behavior from the implementation through P3A-4 verification. Canonical contracts remain in [Backend Interfaces](../04-backend-interfaces.md) and [Backend Implementation](../12-backend-implementation-spec.md).

## Observed behavior

1. **Typed tool results (P3A-1):** `SessionToolExecutor.ExecuteAsync` returns `ToolExecutionResult` with UTF-8 `Text` counted against the tool-output budget and optional ephemeral `Parts` (for example `ModelImageContent`) that are not embedded as base64 in JSON tool text.
2. **Historical image rehydration (P3A-2):** `attachments.read` on image attachments runs through `IAttachmentProcessor`, returns metadata JSON in `Text` and sanitized image bytes in `Parts`. `ToolResultAdmission.AdmitForModel` removes image `Parts` when `ILanguageModel.Capabilities.Vision` is false and replaces them with `vision_required` JSON in tool text.
3. **Provider projection (P3A-3):** `OpenAICompatibleLanguageModel.MapMessages` emits textual `role=tool` then an adjacent Infrastructure-only multipart `role=user` tool-data frame with mapped `image_url` parts. Non-vision adapters reject image-bearing tool `Parts` before HTTP. `ScriptedLanguageModel` supports `[test:historical-image-reread]` for deterministic Synthetic tools+vision continuation.
4. **User-visible verification (P3A-4):** Playwright `historical-image-reread.spec.ts` covers later-turn Scripted Vision reread and tools-capable Scripted Alpha refusal to claim historical image sight.

## Freeze status

**Not frozen yet.** Plan acceptance requires P3A-1–P3A-3 **focused_output** review findings closed on the same HEAD as the final key-free gate. Focused review is requested against production output revision 4 (`output_digest` `f0246cbb6babdb60636ba4c8cdd81f77be7be78b6f9912c90eec9cd5ac03fa6e`) covering items `item-55860b7a0539`, `item-af8b8cb86adc`, and `item-e70215227ccf`. P3B must not start until review closure and a reconciled gate on the post-closure HEAD.

## Key-free gate (P3A-4 batch, pre-freeze)

Commands match `.github/workflows/synthetic.yml` on HEAD `62ea1d8b524d9d0079d9defb397e7e0473b52903` (`62ea1d8`, 2026-09-21), recorded in TDP production batch for `item-70caa87b782c`. This gate is **non-final** until focused-output review closes and any corrective commits re-run the same suite on the reconciled HEAD.

| Stage | Result |
| --- | --- |
| `tests/realtime-js` `npm ci` | OK |
| Domain tests | 76 passed |
| Infrastructure tests | 202 passed / 12 skipped |
| Application tests (`--blame-hang --blame-hang-timeout 5m`) | 480 passed, no hang |
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
