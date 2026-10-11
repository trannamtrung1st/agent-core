# Single-row composer choices

Local visual follow-up, 2026-10-11. Resource references and Skill invocations reuse the existing `StructuredMessageEditor` choice rows and Ant Design v6 presentation.

The row shows its type icon, bounded name, muted metadata and category together. Names cap at `min(55%, 24rem)` of the row body and do not grow to push metadata away. Metadata shrinks within the remaining space. File paths split before their final 12 Unicode code points so middle ellipsis retains the identifying tail and extension; other names and metadata use end ellipsis. Accessible option names and hover titles retain full details.

The picker owns its compact 8px shell and section gaps; rows own their 8px inset, and the inline body owns the 8px sibling gap. Existing 40px desktop and 44px mobile minimum row heights, selection fill, independent scrolling, automatic pagination and Retry remain. Draft, queue and history chips retain their existing wrapping behavior.

## Implementation verification

These checks ran during the implementation pass preceding the documentation synchronization:

- Playwright MCP against isolated InMemory Synthetic on API 5097 and Vite 5183: selected and sent a real home-file reference, reloaded retained reference history, selected a Skill with the keyboard and sent it, and exercised exhausted empty search plus Escape focus return. These outcomes passed. The initial Skill history assertion used an editor-only data attribute; checking the rendered historical chip confirmed the sent Skill label.
- Rendered row geometry at 1440/768/767/390/320px: single-line names, metadata and categories, bounded file tails, no row overflow, and 40/44px minimum row heights passed. Representative content included long file paths and Skill names, ordinary Chats and Runs.
- `pnpm run test --run src/features/chat/Composer.test.tsx src/features/chat/composerChoices.test.ts`: **25 passed**. Existing React `act` warnings remain.
- `pnpm run build`: **passed**, including strict TypeScript. Existing SignalR annotation and bundle-size warnings remain.
- `pnpm exec playwright test --config=playwright.composer-polish.tmp.config.ts e2e/structured-composer.spec.ts`: **25 passed** after configuring the isolated host with the runner's `MaxActiveSessions=32` and `DetachGracePeriodSeconds=2`. The first run passed 22 and stopped three scenarios during setup at the host's default active-session limit; the complete corrected run passed all 25, including responsive insertion, paging/Retry, unavailable Skill recovery, clipboard, queue, keyboard and dismissal cases. The temporary configuration reused only the isolated Synthetic servers and was removed afterward.
- Console/network inspection found no errors or failed requests in the successful affected journeys. Restarting the disposable backend afterward produced expected disconnect/reconnect errors in the old tab; these were test-host lifecycle effects. A fresh navigation was clean.

The existing Real app on 5080/5173 was left running. Only the temporary test servers were stopped. Full backend suites and hosted CI were not repeated for this visual change. Browser evidence does not establish physical touch-device or speech behavior.

## Design-system synchronization

The [frontend specification](../13-frontend-implementation-spec.md#unified-structured-composer), [design guide](../../.agents/context/DESIGN.md), [schema-v2 preview](../../.impeccable/design.json) and [Chat surface brief](../../.impeccable/surfaces/web-src-features-chat-chatapp-tsx.md) describe the same single-row presentation. The static preview demonstrates a long path with the same middle-ellipsis split, full accessible/hover details and no obsolete Load more button. Existing token primitives and product behavior ownership remain unchanged. Earlier [two-line polish evidence](composer-mention-polish-verification.md) remains historical.

Documentation checks passed for schema-v2/component references, unchanged design token primitives, preview/source layout parity, local links and fragments, balanced fences, and whitespace. Playwright MCP rendered the static preview at 1440/768/390/320px: inline metadata/type placement, row containment, full accessible/hover labels and 40/44px minimum heights passed without horizontal overflow. The long-path example retained its identifying tail, and the obsolete Load more control was absent. Desktop/mobile rendered inspection confirmed the result. This synchronization adds no application or milestone acceptance claim.
