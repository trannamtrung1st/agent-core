# Browser Run finalization verification

Authorized on `develop/branch-1` after browser compatibility implementation `27a98478`. The user explicitly requested all reply-failure enhancements and asked not to wait for CI; hosted reconciliation is deferred. This is a focused follow-up on the existing Session/AgentRun architecture.

## Findings and implementation

Three owned-runtime regressions failed before correction: total provider timeout after closure received no independent final response, the work cutoff left no finalization reserve, and hard deadline expiry could leave Streaming state. The latter also exposed the five-minute claim expiry boundary: ordinary terminal failure could not settle an expired claim, blocking follow-up chat.

Interactive browser work now reserves the final 30 seconds of its unchanged 300-second active budget, refuses new tool admission when the remaining time cannot cover the per-tool bound plus reserve, and finalizes with no tools and at most 2,048 output tokens. Approval waits pause both clocks. Core checkpoints the phase and answers unexecuted pending calls before it, preserving receipts and preventing replay after recovery. Provider timeout/cancellation after recorded work can enter one bounded finalization phase; caller cancellation and visible partial output do not start a new reply. Existing protocol repair remains bounded within the phase.

Hard Run expiry is `Timeout/runDeadline`; per-tool expiry is `toolTimeout`; provider setup/idle/total timeouts retain their existing reasons; provider cancellation is `providerCancelled`; user stop/steer/disconnect retain their interruption causes. The explicit terminal deadline command preserves revision/generation/CAS fences and cannot renew work or settle a reclaimed generation. Failed replies retain successful action receipts. Field/credential fill and click receipts use Core labels without values and explicitly do not prove authentication/submission. Browser closure does not prove sign-out. Prior-Run facts now carry allowlisted failure identity/reason, phase and bounded outcomes for grounded follow-up explanations.

The supplied review had no diagnostic ID or underlying trace. The original screenshot's cancellation source therefore remains unconfirmed; code-path reproduction is independent evidence.

## Local verification

- Full local backend passed: Domain 173, Infrastructure 916, Application 1,403, API 390 and OrderEvents 4 (**2,886 passes / 12 opt-in skips**). All five suites passed; Application was rerun in full after the final boundary changes.
- Focused finalization/protocol, generation-fence, effect-receipt and prior SSO regressions passed. The near-cutoff batch case proves one closure and a durable refusal for the unexecuted second call; user stop retains Interrupted/userStop without another model request. A model requesting tools during finalization fails closed with one committed closure.
- Frontend diagnostic/history checks passed **68/68**, and the production build passed.
- Synthetic Playwright diagnostic journey passed **8/8**, including native navigation/closure, recovered reply, failed reply with a retained closure receipt, safe timeout details and reload persistence.
- Playwright MCP independently exercised the failed finalization UI on a disposable SQLite Synthetic host; native navigation/closure produced “Browser closed” independently of “Reply failed”. The `totalTimeout` diagnostic ID remained `ca2c0fe3-dc2b-41b9-b783-d7bb3325f528` after reload, and a subsequent “Hello” completed normally. There were zero browser console errors and observed API requests returned 200.

No paid models or external AHI mutations were run for this follow-up. Default benchmark tests skip without explicit opt-in. The earlier six-trial comparison retains its 6/6 authenticated-state and 5/6 completed-report result; this work does not replace that failed provider sample with a claim of live-model acceptance. Full hosted CI, specific screenshot attribution and PR conflicts with main remain deferred.
