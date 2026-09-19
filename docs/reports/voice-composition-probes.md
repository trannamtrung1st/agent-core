# Voice probes (`general-assistant`)

`general-assistant` is a **neutral identity** (goals and system instructions only). It must not contain `[[speech:]]`, TTS, or UI-format instructions. Speech/display are runtime response capabilities; P2B will expose them as a validated envelope. Deterministic coverage is `PromptContextBuilderTests` (short Voice compatibility text only) plus `VoiceRealtimeRegressionTests` (projection ordering, no display-to-TTS leakage, completion fallback).

Manual Real-profile checks, if needed, confirm runtime behavior rather than composition style:

| Check | Expected |
| --- | --- |
| Voice turn with no speech marker | Display streams after completion fallback; TTS uses display-derived speech, not speculative streaming narration. |
| Voice turn with complete `[[speech:...]]` first | Live `agent.speech.projection`; TTS uses that text; later display does not enter TTS. |
| Equivalent speech and display | No duplicate Spoken section. |
| Meaningfully different `speechText` | Spoken section appears above display. |

Do not add agent-definition heuristics or expand `VoiceModeOutputGuidance` with GOOD/BAD composition examples.
