# Voice probes (`general-assistant`)

`general-assistant` is a **neutral identity** (goals and system instructions only). It must not contain `[[speech:]]`, TTS, or UI-format instructions. Speech/display are runtime response capabilities exposed as a validated semantic envelope (`speech.mode` same/custom/none). Deterministic coverage includes `PromptContextBuilderTests` (framework Voice guidance only), `RichEnvelopeRuntimeTests` (long display with short spoken text), and `VoiceRealtimeRegressionTests` (projection ordering, no display-to-TTS leakage, completion fallback).

Manual Real-profile checks, if needed, confirm runtime behavior rather than composition style:

| Check | Expected |
| --- | --- |
| Voice turn with `speech.mode=same` (no custom projection) | Display streams after completion fallback; TTS uses display-derived speech, not speculative streaming narration. |
| Voice turn with `speech.mode=custom` first | Live `agent.speech.projection`; TTS uses that text; later display does not enter TTS. |
| Equivalent speech and display | No duplicate Spoken section. |
| Meaningfully different `speechText` | Spoken section appears above display. |

Keep agent definitions free of speech/display composition rules. `VoiceModeOutputGuidance` may explain that speech should sound conversational, briefly convey the point, and direct the listener to useful on-screen detail rather than narrating a long display answer. Avoid GOOD/BAD examples, stock lead-ins, or a fixed spoken length.
