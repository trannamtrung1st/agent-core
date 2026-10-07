# Approval dialog polish verification

Scope: shared Chat approval presentation, 2026-10-07. Existing Ant Design v6 Modal, Alert, Tag and Typography own the controls. `ApprovalModal` owns content classification; shared `.chat-approval-*` rules in `app.css` own 8/12/16px gaps, review overflow and responsive action sizing. No backend approval contract or milestone status changes.

## Result

Future-conversation scope is visible in an info notice. Active version and policy revision are compact labeled tags. Change is a quiet inset with supplied colon-ended headings emphasized and dash-prefixed lines aligned using hanging indentation. Exact text is escaped and preserved, including line breaks. One focusable region contains the remaining complete details within 40vh; title/context/actions remain outside it. Semantic proposals use a centered 720px maximum Modal; ordinary sensitive actions use 520px. Mobile actions retain 40px minimum height.

## Runtime verification

During the initial pass, the worktree backend could not start: ASP.NET endpoint binding rejected the unrelated in-progress `CredentialService.BindAsync` instance method as an invalid HTTP binding method. Those credential changes were preserved. Initial fallback: `git archive HEAD` into `/tmp/agent-core-approval-baseline`, run that committed backend with Synthetic/InMemory on port 5096 and disposable `/tmp` roots; run the edited Vite frontend on 5186 with its proxy targeting 5096. The existing Real host was left untouched. The later review below successfully ran the current worktree backend and supersedes this initial integration limitation.

- Playwright MCP created disposable identities, selected them through Chat, sent requests through the labeled composer, approved a sensitive action using keyboard Enter and observed its completed Synthetic response. It rejected a harness instruction proposal and observed Nothing was saved, then approved a fresh exact proposal and observed the future-conversation receipt.
- Long text was additionally projected as a frontend-only layout fixture, distinct from the actual server-authorized proposal. The real proposal was restored before approval. At 1440×900, 768×900, 390×844 and 390×568 the content wrapped without document horizontal overflow. The 40vh review area retained its opening line, scrolled with keyboard End, and kept actions available. The final info-notice presentation kept scope and footer visible at those widths. Screenshot files are `/tmp/approval-final-{width}-{height}.png`; fixture content is illustrative presentation evidence.
- MCP console inspection returned zero errors after the isolated Synthetic flows. Relevant HTTP requests returned successful statuses; no failed flow requests were observed.

## Repeatable checks

Retained long-content layout fixtures: [desktop](assets/approval-dialog-desktop.png) and [mobile](assets/approval-dialog-mobile.png).

From `web/`:

- `pnpm run test --run src/features/chat/ApprovalModal.test.tsx src/features/chat/ChatApp.test.tsx`: 29 tests passed. Existing ChatApp jsdom NaN-height and Node localStorage warnings remain unrelated to this dialog.
- `pnpm run build`: TypeScript and Vite production build passed after correcting test query typing. Existing bundle-size and SignalR annotation warnings remain.
- `PLAYWRIGHT_API_PORT=5096 PLAYWRIGHT_WEB_PORT=5186 PLAYWRIGHT_FAITHFUL_MANUAL=1 pnpm exec playwright test e2e/approval-flow.spec.ts e2e/email-harness.spec.ts --project=synthetic`: five passed: approve, refresh/same execution, reject, keyboard approve and email preview/send.
- `PLAYWRIGHT_API_PORT=5096 PLAYWRIGHT_WEB_PORT=5186 PLAYWRIGHT_FAITHFUL_MANUAL=1 pnpm exec playwright test e2e/p97-harness-management.spec.ts --project=p97-harness --grep 'Assisted Chat|Managed instruction|stale Chat'`: three passed: reject/fresh approve, instruction rejection and stale-authority refusal/fresh recovery.

Focused verification does not claim milestone acceptance, live provider behavior or full backend/frontend-suite coverage. Native microphone and audio behavior were outside this presentation change.

Scoped Markdown link targets, balanced fences and `git diff --check` passed. The frontend specification, testing strategy, design-context policy and approval surface brief describe the same presentation. README and implementation-plan milestone claims are unchanged.

## Follow-up review before commit

Two defects were reproduced and fixed: at 844×390 the footer bottom was below the viewport, and a replacement approval inherited a 1342px scroll offset. The Modal container now caps to `100dvh - 32px`; its flex body lets the review shrink while the header/footer remain available. Body overflow retains access if context alone is oversized, with at least a 32px review area so the exact proposal cannot collapse out of reach. The review region is keyed by approval identity, so a replacement starts at zero. At 390×568 the dialog now retains 16px clearance instead of reaching the viewport edge.

Current-worktree verification used the same isolated ports, Synthetic/InMemory and `/tmp/agent-core-approval-review-*` storage roots. No committed-backend fallback was needed. MCP checked actual keyboard approve and fresh reject through the composer, completed response and dismissal. Long fixture inspection at 1440×900, 768×900, 390×844, 390×568 and 844×390 confirmed no horizontal overflow, visible footer, no additional body scrollbar for these fixtures, and replacement scroll zero. The landscape footer bottom was 354px in a 390px viewport.

- Focused Vitest: 30 passed (3 ApprovalModal, 27 ChatApp).
- Production build: passed, retaining the existing annotation/bundle warnings.
- Current-worktree Synthetic CLI: ordinary approval/refresh/reject/keyboard and email (5 passed), scratch/home workspace exact approval and rejection (2 passed), and the new `approval-layout.spec.ts` (1 passed). The first layout run measured during Ant Design's opening animation and failed the 40px hit-target assertion; the test now polls the settled geometry. An additional oversized-summary/scope case verifies body scrolling, a nonzero review area, reachable proposal text and a visible footer. Its initial assertion checked the first content line before scrolling the inner review past its Change label; the corrected test exercises both scroll areas to reach that line.
- Current-worktree harness CLI: Assisted reject/fresh approve, instruction rejection and stale-authority recovery (3 passed).
- MCP console/network review recorded one unrelated 404 for Synthetic `fixture-artifact-1` metadata after the scripted response. Approval decisions and their UI transitions succeeded. This review does not claim that unrelated fixture issue is fixed.

The new layout regression uses frontend-only projected proposal data; the other listed browser tests verify real server decisions. The final commit includes only the approval presentation, tests, synchronized docs/design context and retained illustrative screenshots, preserving unrelated credential work.
