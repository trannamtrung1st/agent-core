# Milestone 4 verification

Branch: `codex/tdp-mvp-run-20260914T184310-67b372`

Recorded 2026-09-15. Synthetic/offline only. No microphone, speaker, GPU, or hosted keys.

## Commands and results

| Command | Exit status |
| --- | --- |
| `dotnet test AgentCore.sln --nologo` | 0 — Domain 3, Application 16, Infrastructure 25 passed + 1 skipped live smoke, Api 6 |

Observed:

- `mhm` during a live gated response Continue: no extra user turn; R1 remains live until released.
- Partial `wait` marks R1 Interrupted before cancellation; late `R1b` after R2 starts is unpublished.
- Low `activityScore` noise is Ignore while the model remains gated (`WaitUntilMailboxDrainedAsync`).
- `SpeechFinal` before `SpeechEnded` commits one user entry; later Ended does not reopen the utterance.
- Classifier replies that miss candidate revision are ignored; `RequestInterruptionClassification` does not increment `IAgentBrain` calls; idle `TimerElapsed` does not call `IInterruptionClassifier`.
- Stale timer generation is ignored. `PartialTranscripts=false` interrupts at the degraded 250 ms candidate timer without a classifier call.
