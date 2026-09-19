# Voice composition manual probes (`general-assistant`)

Deterministic prompt tests in `PromptContextBuilderTests` assert the Voice system guidance text. These manual probes exercise **model composition** on a Real or hosted profile with agent **`general-assistant`**. They do not replace runtime speech-contract tests.

Record: session in **Voice** mode; whether **Spoken** appears; whether speech and display are equivalent or meaningfully split; any meta-only display lines.

| # | User prompt | Expected composition |
| --- | --- | --- |
| 1 | What is a samurai? | Ordinary conversational answer; speech and display same or near-equivalent; no useless “Here’s the summary” display-only line. |
| 2 | Compare SQLite, PostgreSQL, and MySQL in a table. | Concise spoken takeaway; detailed comparison table on screen; table body not narrated in speech. |
| 3 | Tell me a short story. | Story spoken in full; do not put the story only on screen with a short spoken summary. |
| 4 | Explain this code. (with a small snippet attached or pasted) | Conversational spoken explanation; code and fine detail remain visual. |
| 5 | Give me a detailed weekly schedule. | Brief spoken overview; schedule detail stays in structured/visual display. |

Pass criteria per probe: TTS uses explicit `[[speech:...]]` (or completion fallback only if the model omits the marker); no speculative display narration while streaming; composition matches the table above. Failures are prompt/model issues, not transport bugs.
