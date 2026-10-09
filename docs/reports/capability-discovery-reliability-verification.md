# Capability discovery UX and reliability verification

Date: 2026-10-09. Branch: `develop/branch-1`.

Implementation SHA: `bc0a7e940f3c8fa0836d2d20b11e0af14010887d`. The following evidence-only commit records this source SHA without changing executable behavior. Hosted acceptance is deferred at the user's direction; this follow-up is **not frozen**.

## Scope and reviewed lifecycle

The existing registry/matcher, eligible catalog, projection, mailbox load admission, AgentRun IDs/count/checkpoints, prompt builder and Skill loader remain the implementation path. Exact Selected/All Definition grants remain authoritative. Current configuration, owner/trigger/tool/vision readiness and sensitive approvals still apply after loading. The bootstrap is unchanged; advanced tools are not automatically projected. Skill procedures use the existing pinned catalog/loader and grant no authority. No provider, persistence schema, model-specific routing or P10/P11 change is introduced.

Concrete corrections:

- Explicit registered names narrow matching; names are case-insensitive. Feature-owned form/dialog synonyms and canonical action/name/description metadata supply concrete intents. Explicit category scope prevents workspace searches loading email search. Strong name/tag evidence suppresses incidental prose matches; ordinal name ties remain deterministic.
- One limit bounds loaded, already offered and safely unavailable entries together. Structured outcomes and next-step guidance distinguish success, already offered, unavailable, no-match, invalid, budget, stale/cancelled and repeated-strategy refusal. Unavailable can identify only explicitly requested Definition-authorized names; restricted inventories and configuration details remain hidden.
- Loaded schemas appear on the next model request. Compact receipts retain readiness/next-step and shared recovery counters near the cumulative output bound, with explicit truncation.
- The existing checkpointed recovery circuit bounds equivalent ineffective discovery. No-match/unavailable/budget queries ignore case, whitespace and limit changes; already-projected queries retain the limit so a wider search remains possible. Corrected invalid limits and different goals remain allowed. No arbitrary effect retry or authorization recovery is introduced.

## Executed behavior

The offline owned native Chromium journey starts with `browser.fill_form` authorized but absent from projection. A scripted ordinary AgentRun asks “fill a form”; the next request receives the actual schema. It finds the generic loopback Record field, fills `Discovery verified`, takes a targeted native snapshot and publishes a completed grounded reply. An independent Playwright input-value read verifies the real DOM. No website, credentials, paid model or test-only product endpoint is required.

The no-match journey repeats the same impossible goal with changed case, whitespace and limits. Only two loads are admitted; the third/fourth equivalent attempts are refused and the fifth request has no tools and completes with a truthful limitation. Invalid JSON/non-object/invalid-limit journeys receive `load_invalid`, complete without claiming a load and leave the load count at zero.

SQLite reopen/reclaim retains loaded IDs and the original load count, restores ineffective-discovery counters from existing receipt checkpoints, and projects the loaded workspace/email schemas on the recovered Run. Existing runtime coverage proves next-request execution and fresh independent Run state. Stale revision/generation and cancelled admissions leave state unchanged.

## Local checks

- `dotnet test AgentCore.sln --nologo`: 2,909 passed / 12 live opt-in skipped on the initial implementation (Domain 173, Application 1,422, Infrastructure 920, API 390, order-event plugin 4). No failures.
- Final isolated focused discovery/runtime/recovery: 61 passed, including widened already-projected search versus equivalent no-match, malformed JSON/non-object/invalid-limit admission, compact receipt counters and the real native form journey. Strengthened isolated SQLite fixture: four passed. Complete isolated Application suite: 1,426 passed / three live opt-in skips. Together with the unchanged Domain/API/plugin and full Infrastructure checks, 2,913 backend cases were exercised successfully; the four SQLite cases are a focused rerun, not extra unique cases.
- Frontend: 99 files / 770 tests passed under explicit Node 22. Production build passed (existing large-chunk warning). No frontend product changes were required.
- `git diff --check` passed. Canonical interface, wire, runtime, testing, implementation-plan and README owners are synchronized; historical verification reports remain historical.

The first broad frontend command (`pnpm run test --run`) selected Node 26.7.0 from `web/` despite the repository-root shell selecting Node 22. It finished with 697 passing / 73 failing tests: missing jsdom localStorage caused service failures, and one editor case exceeded its existing timeout under high concurrency. This setup attempt is not acceptance evidence. The rerun uses `/Users/trungtran/.nvm/versions/node/v22.18.0/bin/node node_modules/vitest/vitest.mjs run --maxWorkers=2`; no product storage shim or relaxed assertions were added.

An initial native test used the control-state fixture, which deliberately contains a visible verification-code control. The existing intervention detector correctly stopped observation; the ordinary generic Record form is the intended discovery acceptance fixture. Its successful fill plus targeted snapshot and independent DOM read are the exercised evidence.

## Remaining gates

All five hosted Synthetic jobs, full core/acceptance Playwright and Compose on the final candidate SHA remain unverified for this follow-up. CI was not awaited, as requested. No new paid real-model comparison, external AHI login/sign-out or owner-profile journey was run. Previous migration freezes and historical live-model results are not moved or promoted into current acceptance.

Concurrent browser dialog/cleanup edits appeared during verification. The last shared-checkout Application run passed 1,429 tests / three opt-in skips but included three concurrent dialog cases and is not used as exact-patch evidence. The final staged capability patch was copied over a Git archive of the original HEAD into `/tmp/agent-core-capability-review-20261009` and tested independently. The concurrent edits are excluded from this commit and preserved in the shared checkout.
