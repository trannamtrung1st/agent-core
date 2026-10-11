# Composer mention and chip polish

Local follow-up, 2026-10-11. Retains Ant Design v6, the existing dark composer and atomic Lexical tokens.

Choice names now occupy up to two lines above muted descriptions. Existing category icons and trailing type labels remain. The picker owns its 8px section gaps; rows own 8px inner padding. One product-level `MessageParts` presentation shares chip classes and typed tooltips with the Lexical node, queue and history. Chip padding and line spacing support long wrapped labels; queued previews retain single-line truncation. Historical Skill tooltips describe an invocation in that message instead of suggesting a new activation. Canonical parts and authority are unchanged.

## Verification

- Isolated InMemory Synthetic backend on 5180 and Vite on 5273; no live providers or hardware. Existing user application ports were left alone.
- Playwright MCP: actual keyboard Skill selection, resource selection, Send, history reload and slash-trigger deletion passed. Long labels were inspected at 1280, 768 and 390px. At 390px the draft measured 348px for both client and scroll width; its two chips retained readable labels and canonical typed tooltips after reload. The verified fresh-page journey produced no page errors or failed requests.
- `pnpm run build`: passed strict TypeScript and Vite production build. Existing SignalR annotation and bundle-size warnings remain.
- `NODE_OPTIONS=--no-experimental-webstorage pnpm run test --run src/features/chat/Composer.test.tsx src/features/chat/composerChoices.test.ts src/services/messageParts.test.ts`: **28 passed**. Existing React `act` warnings remain. The scoped flag avoids Node 26's built-in web-storage collision with jsdom.
- `pnpm exec playwright test --config /private/tmp/agent-core-composer-polish/playwright.config.ts e2e/structured-composer.spec.ts --workers=1`: **16 passed** before adding the long-label case. Includes atomic editing, undo/redo, Vietnamese composition, typed clipboard, queue/steering, disabled Skill recovery, popup dismissal, partial-source Retry and empty/paged search.
- Follow-up `--grep 'long mention|typed queued steering'`: queue passed; the initial long-name fixture failed server validation because it exceeded 80 characters. Corrected to a valid 77-character name; `--grep 'long mention'`: **1 passed**. The additional test checks 1440/768/390px bounds, keyboard insertion, no horizontal draft/history overflow, send/reload and historical tooltip semantics. **17 distinct composer browser scenarios passed across these runs.**
- `git diff --check`: passed. Frontend specification, design context and Impeccable sidecar synchronized.

The initial MCP tab retained an old Lexical class after Vite hot replacement, causing node-registration errors and failed insertion attempts. A full page reload restored the registered class before the successful journey. These failed exploratory attempts are not counted as passes.

## Evidence and limits

[Desktop choices](../../.playwright-mcp/token-polish-picker-1280.png), [phone choices](../../.playwright-mcp/token-polish-picker-390.png), [phone draft](../../.playwright-mcp/token-polish-chips-390.png), [reloaded history](../../.playwright-mcp/token-polish-history-390.png) are local, ignored captures.

No backend behavior changed in this visual follow-up. Full backend/frontend suites and hosted CI were not repeated; the user requested no CI wait. Browser checks do not establish physical touch-device or speech behavior. Test hosts were stopped after verification.

## Consistency review follow-up

The subsequent requested review reproduced and fixed three concrete issues:

- A tall draft in a 390×320 viewport put the picker at y=-40px. Placement now chooses above/below, clamps to the viewport and reserves usable room for controls plus a complete choice. If needed, it overlaps the caret. Tall-draft insertion is covered at 1440×900, 390×320 and 320×240.
- Navigating a long list scrolled the focused search field outside the popup. The choice list now owns scrolling; chrome remains visible. Rows do not flex-shrink, preventing wrapped titles/descriptions from overflowing their row. A six-choice mobile regression checks search, active-choice bounds, row containment and keyboard insertion.
- React refresh of the composer replaced the Lexical node constructor while the editor retained the original registration. `ComposerChipNode.ts` now owns that class outside the React refresh boundary. MCP verified the existing draft chip remained and a second chip could be inserted after editing the React component.

Removed a redundant placeholder-position override. The long-label resize test now waits for settled bounds; its first review run sampled the prior 1440px layout immediately after switching to 768px (19 passed, one timing failure). Subsequent checks passed after that correction.

MCP exercised the fixes in the running Synthetic app. At 390×320 the final popup occupied x=18, y=88, width=364, height=224; search and the full 78.85px selected row remained within it. Keyboard selection, Skill-only Send and history reload passed with zero captured page errors or failed requests. The existing idle-pause behavior required Resume before the final journey; paused editing remains disabled.

Final boundary-only run (`--grep 'short viewport|scrolling many|long mention'`): **3 passed**. The same focused Vitest command above: **28 passed**. Final strict TypeScript/Vite build: **passed**, with the existing warnings. Frontend/design/testing documentation and sidecar were synchronized; backend gates and hosted CI remain outside this UI review.

Complete composer/model browser command: `pnpm exec playwright test --config /private/tmp/agent-core-composer-polish/playwright.config.ts e2e/structured-composer.spec.ts e2e/session-model.spec.ts --workers=1`. An intermediate run passed all 21 scenarios. After the final minimum-space correction, 20 passed and the paging test encountered two “Load more” buttons (chat rail and picker) in the accumulated disposable catalog. Scoped that test to the picker; `--grep 'bounded search'` then passed. All **21 distinct scenarios passed across the final run and focused rerun**; no product failure remains from these checks. `git diff --check` passed.

[Desktop review capture](../../.playwright-mcp/composer-review-final-1440.png) and [short phone viewport](../../.playwright-mcp/composer-review-final-390.png) are local ignored evidence.

## Design-system documentation synchronization

October 11, 2026 documentation-only follow-up. Merged the current working-tree presentation into [DESIGN](../../.agents/context/DESIGN.md), the [schema-v2 preview sidecar](../../.impeccable/design.json) and [Chat surface brief](../../.impeccable/surfaces/web-src-features-chat-chatapp-tsx.md). Application source was not edited in this follow-up.

Removed the contradictory primary composer-focus border rule. The guide now consistently distinguishes neutral editor focus, active/focused choice fill and normal Ant Design search/control focus. Recorded the shared chip owners, local editor/history line-height variant, flat icon/type choices, natural row heights, independent list scrolling, viewport placement and mobile targets. Moved the existing permission hierarchy and composer guidance into their canonical component sections. Screens, copy, interaction, identity and activation remain owned by `/docs`.

Added standalone structured-chip and picker examples. Fixed the shell preview's missing Send SVG, secondary-fill fallback, invocation text/border fallbacks, mobile toolbar grouping and a font shorthand that reset the intended line height. Resolved values came from the installed Ant Design `darkAlgorithm` with the repository's primary seed; no parallel palette or UI kit was introduced. Sidecar narrative lists and named rules now come directly from the guide rather than carrying extra stale statements. Preview labels are explicitly illustrative.

Verification for this refresh:

- YAML parsing, schema-v2 JSON structure, all 20 preview component references, token/metadata references and the eight canonical section headings: passed.
- Playwright MCP rendered the three affected snippets in standalone shadow DOMs at 1440/768/390/320px: no horizontal overflow, editor line height 25.9px (14px × 1.85), neutral editor focus, primary search focus, visible Send SVG and mobile choice height above 44px. Long names wrapped within their two-line limit. A populated-list scrolling check retained heading and search visibility. The final fresh-document pass captured zero page errors.
- [320px static preview](../../.playwright-mcp/design-sync-previews-320.png) is local ignored evidence. These checks validate documentation previews, not a fresh application journey. Earlier Synthetic application evidence above remains separate.
- Relative Markdown targets/fragments, referenced Chat source paths, balanced fences, narrative parity and `git diff --check`: passed. README and implementation-plan ownership remain aligned; no milestone status changed.

The documentation-only refresh observed an invocation text contrast gap: `#1668dc` over the chip fill measured **2.20:1**. A subsequent combined implementation review resolved it at the shared chip owner: invocation chips now use the existing readable text role and retain the primary border accent. Playwright MCP measured **8.75:1** in draft/queue and **7.06:1** in history after compositing the actual backgrounds. The preview and design guide were synchronized with this correction. The original documentation-only refresh did not repeat application suites or hosted CI; the combined follow-up does.
