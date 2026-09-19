---
name: providers
description: "Implement or review Agent Core LLM, STT and TTS ports, synthetic adapters, HTTP/SSE handling and provider selection."
---

# Providers

Read [Backend Interfaces](../../../docs/04-backend-interfaces.md), adapter sections of [Backend Implementation](../../../docs/12-backend-implementation-spec.md), and options/DI in [Persistence and Configuration](../../../docs/15-persistence-and-configuration.md).

- Keep ILanguageModel, ISpeechRecognizer and ISpeechSynthesizer independently replaceable. HTTP, vendor DTOs, codecs and credentials belong in Infrastructure; runtime/controller consume normalized events/capabilities.
- OpenAICompatibleLanguageModel uses direct HTTP/SSE. OpenRouter is initial hosted text configuration. `openrouter/free` is **not** the Real/demo DefaultModel (random routing); opt-in adapter smoke still uses it, and the shipped Real catalog may offer it plus `openai/gpt-4o-mini-2024-07-18` as explicit session choices. Local/direct-hosted endpoints use the same adapter. Text compatibility implies no speech capability. Initial OpenAiSpeechRecognizer is OpenAI realtime transcription (speech-to-text only; recommended `gpt-live-transcribe`), not native speech-to-speech. Optional vendor fields such as transcription `delay` are adapter-local and omitted unless the selected API documents them. OpenAiSpeechSynthesizer is a separate TTS registration. Batch `/audio/transcriptions` is a separate degraded adapter. Bind `OPENROUTER_API_KEY` / `OPENAI_API_KEY` from environment or `dotnet user-secrets` only.
- Use IHttpClientFactory, explicit cancellation/deadlines and canonical safe failures. Parse SSE across arbitrary read/UTF-8 boundaries with bounded payloads, termination and disposal semantics.
- Generation POST retries are disabled by default. Never replay after normalized partial output or uncertain provider acceptance. Midstream failure leaves partial output failed and cancels downstream TTS; a user retry gets a new response ID.
- Advertise **effective** speech capabilities from the adapter. Configuration may require a subset or disable optional features; it must not raise unsupported flags. Apply the specified degraded policy when partial STT is absent; do not fabricate transcripts or disable continuous capture.
- Synthetic DI must not resolve real adapters or attempt outbound provider HTTP. Preserve production contracts, timing, cancellation and failures through scripts. Do not block default tests or the text-conversation path on `OPENAI_API_KEY`.
- Apply [testing](../testing/SKILL.md) for offline SSE/error/cancellation and synthetic parity. Live smoke tests require explicit opt-in plus operator credentials, skip cleanly when a key is missing, and never run because a key happens to be in the environment. Do not add extra external services solely for testing.
