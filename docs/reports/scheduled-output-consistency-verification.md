# Scheduled output consistency verification

Date: 2026-10-12. Baseline: main `6acfc8bd9bf1115d08199c8a770ac9f6c1ea625a`. This is a bounded unified-Automation prompt correction; historical freezes and P10/P11 remain unchanged. CI waiting is explicitly waived for this request.

## Finding and correction

The existing-session Automation path already appends persisted instructions as the last task. However, prior chat is also replayed as native User/Assistant messages. The historical Real result in the [greeting report](greeting-scheduling-authorization-verification.md) demonstrates the model answering intervening negative chat instead of greeting. Five pre-fix prompt regressions reproduced the missing history boundary; they are structural evidence, not a claim that every model deterministically fails.

Authored Schedule, Event, Core Event and manual occurrences now receive selected chat as one serialized historical-evidence block, with a fixed instruction that history cannot replace or suppress the admitted task. The persisted instructions remain the current task. Summary boundaries, delivered/heard prefixes, newest-20 selection and the existing 24,000-character history budget are preserved, including serialization overhead. Current Chat and native initiative retain their original message roles. No owner, permission, trigger, approval, timeout or retry boundary changed. Legitimate edits and cancellation continue through existing authorized Automation commands.

## Deterministic verification

Initial prompt regression: **17 passed** after five failures before correction. The SQLite task-isolation journey: **1 passed**; creates/persists a greeting Automation, reopens storage, executes three slots with contradictory and unrelated Chat between them, observes two actual greetings and then a welcome after an authorized owner edit, and observes no fourth execution after disable. Repeated scans do not create duplicate occurrences. The model fixture reproduces historical-instruction dominance when the contradictory turns appear as native conversation messages; live acceptance separately checks actual provider behavior.

Broader regressions on the isolated baseline: full Application **1,741 passed / 8 opt-in skips**; API reliability/destination/continuity/readiness filter **76 passed**. Bounded Real-profile results are recorded in the delivery follow-up after execution.

## Verification isolation

Concurrent unrelated Email edits arrived in the shared checkout and temporarily failed compilation (`EmailAccountService` symbol resolution and an Email test syntax error). They were left intact. A managed isolated worktree at the committed baseline plus only this fix supplies the runtime evidence. The scheduling commit excludes those outside edits.
