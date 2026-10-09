# Configurable execution budgets and reliable cleanup

Date: 2026-10-09. Baseline behavior: `9ade2bae40ad8ef93e393004813538e666e77758` (includes prior native-browser reliability follow-up). This is a bounded resource/cleanup enhancement; earlier milestone freeze SHAs remain unchanged. Final behavior commit and exact-head hosted result are recorded below after verification.

## Available UAT evidence

The existing eight-trial safe AHI audit confirms several different stopping paths rather than one universal checkpoint failure. One trial hit `tool-step-limit` at 48 steps after protected fill and asset observation. Three trials recorded `finish_required` at 46, 45 and 41 steps; two also recorded `output_limit`. Those receipt codes alone do not distinguish serialized checkpoint headroom from cumulative output usage. The available audit does not include raw checkpoint byte occupancy or elapsed budget for those Runs, so it does not establish an 8 MiB exhaustion or deadline. Another trial failed the provider semantic response at 34 steps, with invalid/repeated calls. A later 38-step trial independently observed logout but made four invalid close calls; browser closure remained unconfirmed. Successfully attempted protected fill, SDK-confirmed actions, independently observed asset/logout, and closure remain separate facts.

The earlier native follow-up already tightened close repair and active instruction validation. This change addresses measured checkpoint pressure, configurable resources and cleanup admission/reservation. No paid model/AHI calls were used, and no existing owner data was reset. Raw private UAT pages/checkpoints were not copied into this report.

## Resource policy and durable execution

Precedence is enforced system ceiling → Definition class default → independent Instance class override → immutable Run admission pin. Standard defaults are 24 steps/180 active seconds, interactive browser 48/300, and authorized background browser 32/240. Extended and Deep Workflow multiply their own class defaults by two and three. Interactive presets are 48/300, 96/600 and 144/900; custom profiles allow integer 8–144 steps, 60–900 seconds and 1–30 seconds per tool. Host ceilings are fixed system policy, not an unrestricted per-Agent resource control. Background/ordinary classes do not inherit an extended interactive profile. A resource setting grants no tool, credential, origin, approval or model capability.

New Run admissions pin class, profile, source and explicit requested-cleanup intent. Existing Session instructions/authority remain pinned while fresh resource defaults are read for the next Run. Running Runs retain their original limits across updates, retry, SQLite reopen and reclaim. Checkpoint counters cannot decrease; a persisted clock timestamp charges active time through the old claim lease, including uncertain-browser direct reclaim. Approval/signal/retry waits remain paused. Historical missing pins/clock fields retain the older context-selected profile and remaining-time interpretation; safe public reads do not fabricate provenance.

Additive migration `20261009160526_ExecutionBudgets` adds one nullable Instance JSON column. Existing Definitions, Instances, Sessions and Runs remain readable. Old-schema fixtures seed using that schema before running forward migrations, rather than asking the current store to insert a nonexistent new column.

## Coordinated cleanup and exhaustion

Interactive requested cleanup reserves 60 active seconds before the existing 30-second tool-free final reply reserve. It preserves 4–12 steps derived from one quarter of the profile, capped at 12; a 48-step workflow enters cleanup at 36. It also preserves 8 KiB of result/checkpoint headroom plus existing completion, recovery and bounded-refusal envelopes. Reserves are inside the admitted total. Cleanup admits already-authorized hover, scroll, wait, verify and key press alongside existing find/snapshot/dialog/click/tabs/close. All current origin, approval, configuration and eligibility fences still apply. Cleanup remains model-guided and tied to the user request.

Work-step, time, checkpoint and cumulative-output exhaustion have distinct canonical safe reasons: `stepLimit`, `runDeadline`, `checkpointCapacity`, `outputLimit`; blocked requested cleanup projects `cleanupBlocked`. Bounded finalization offers no tools and uses durable receipts. Pending calls must fit before dispatch; succeeded recovered effects are acknowledged before cleanup filtering so their facts cannot be relabeled as a refusal or replayed. Completed generation status does not mean the application task was verified. Earlier work verification cannot count as later cleanup verification, and closing a browser does not establish logout.

## Checkpoint measurement

Controlled browser-message fixtures contain alternating large accessibility snapshots and SDK-confirmed action receipts, exact model arguments and one literal pending hover. No protected content is included. Superseded read-only observations are re-compacted as newer evidence arrives; recent observations and the latest useful page excerpt remain. Errors, exact pending identities/arguments, verified/effect flags, recovery authority and uncertain-effect fences survive.

| Steps | Raw checkpoint bytes | Compacted bytes | Exact argument bytes | Compacted + cleanup/completion reserves |
| --- | ---: | ---: | ---: | ---: |
| 48 | 308,004 | 42,517 | 1,420 | 58,438 |
| 96 | 622,284 | 78,733 | 2,836 | 94,654 |
| 144 | 936,736 | 115,037 | 4,252 | 130,958 |

Raw repeated observation bodies dominate these fixtures. Action receipts retain attempted/SDK/application verification fields; discovery remains on its existing bounded compaction path. Pending arguments are never clipped. Existing escaping, error, approval and recovery-envelope tests remain required. The Deep Workflow fixture leaves only 114 bytes under 128 KiB before wider arguments or additional metadata, so the conservative tested system checkpoint ceiling is 256 KiB (262,144 UTF-8 bytes). The independent 8 MiB output and model-context bounds remain unchanged.

An accumulated real workspace-write test reaches actual serialized checkpoint admission pressure while preserving earlier file bytes and durable successful receipts, refuses the next write, and produces tool-free `checkpointCapacity` finalization. Atomic oversized checkpoint rejection independently proves the stored pending identity and confirmed receipt remain unchanged.

## Runtime and UI evidence

Native Chromium with a disposable protected synthetic credential follows login → Data Management → AI Engineering → Pump 002 → Attribute/Table/Media. Meaningful earlier operations consume the ordinary-work allocation. Reserved cleanup performs hover Account → click Log Out → independently verify Sign In visible → observe → close, finishing at 41 steps. Protected values are absent from model arguments, messages and durable receipts. Without hover authorization, Core grants nothing extra: closure succeeds but sign-out remains unverified and the final reply is partial.

Playwright MCP exercised a Definition Extended draft save, an independent Instance Extended/Deep override, unchanged Standard/unattended defaults, revision conflict, draft-preserving Reload and retry, invalid 145-step validation and Discard. At 390/768/1440 widths there was no horizontal overflow; mobile Save/Discard are 40px high. The isolated final UI had no console errors. The intentionally injected stale update returned 409 and displayed actionable conflict text. Disposable UI/API hosts were separate from existing user Real/Synthetic hosts and were stopped afterward.

## Verification ledger

Final local and hosted results are being recorded as gates finish. Interrupted disk-pressure runs and the isolated-copy missing JavaScript dependency setup are not passing evidence. Concurrent model transport/picker and skill maintenance changes were excluded from the stable verification candidate and remain untouched.

## Remaining limits

Finite profiles do not guarantee arbitrary workflows or model competence. Large literal arguments and retained non-replayable receipts can still consume 256 KiB; the boundary finalizes safely. The audit cannot retrospectively prove the exact byte/time exhaustion of Runs whose raw checkpoints are unavailable. This verification uses actual local Chromium and Synthetic model/credentials; it does not prove the current paid provider or real AHI site will finish every task. Existing persistent browser authentication, origin/credential approval restrictions and uncertainty handling remain authoritative. No automatic infinite continuation or workflow engine was introduced.
