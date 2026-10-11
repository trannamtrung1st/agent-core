# Single-row composer choices

Local visual follow-up, 2026-10-11. Resource references and Skill invocations reuse the existing `StructuredMessageEditor` choice rows and Ant Design v6 presentation.

The initial single-row implementation shows its type icon, bounded name, muted metadata and category together. It capped names at `min(55%, 24rem)` of the row body without reserving that width. Metadata shrank within the remaining space. The name-width successor below tried a fixed half-width column; the content-width successor records the current natural-width layout. File paths split before their final 12 Unicode code points so middle ellipsis retains the identifying tail and extension; other names and metadata use end ellipsis. Accessible option names and hover titles retain full details.

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

## Name-width successor

The subsequent user feedback requested more readable names beside long metadata. This intermediate revision reserved a fixed 50% of the row body (`flex: 0 0 50%`) rather than using an intrinsic width capped at 55%. Metadata began at a consistent column edge and shrank beside the category in the remaining half. The same owner supplied this layout to Skill and resource choices; ellipsis, spacing, hit areas and canonical identity remained unchanged. The frontend specification, design guide, surface brief and preview were synchronized. The content-width successor below supersedes this fixed column.

Verified on isolated InMemory Synthetic API 5097/Vite 5183:

- MCP inspected populated Skill/resource rows at 1440/768/767/390/320px. Every name occupied half the row body; metadata stayed inline, categories stayed inside the row and no row overflow occurred. Harness authoring and Sample Hello remained fully readable; the 77-character Skill title truncated intentionally. Long file paths retained their final 12 characters.
- Selected and sent Sample Hello; selected a home-file reference with the search keyboard and sent it; exhausted empty search and Escape restored editor focus. A full reload/reselection resolved a development hot-refresh reset, and the search check waited for asynchronous choices before pressing Enter.
- `pnpm run test --run src/features/chat/Composer.test.tsx src/features/chat/composerChoices.test.ts`: **25 passed**. `pnpm run build`: **passed**. Existing React `act`, SignalR annotation and bundle-size warnings remain.
- `pnpm exec playwright test --config=playwright.composer-width.tmp.config.ts e2e/structured-composer.spec.ts --grep 'picker and chips|short viewport|scrolling many|long mention|auto-pages|active option|disabled after selection'`: **14 passed**, covering responsive reachability, long labels, keyboard focus, unavailable Skill recovery and automatic paging/Retry. The temporary configuration was removed after verification.

This is local verification of the revised row width, with the same backend/hosted/device limits as the original pass.

## Content-width successor (2026-10-12)

The latest feedback identifies excess whitespace after short names. Titles now use their natural content width with `flex: none; max-width: 50%`. Metadata follows that actual width with an 8px gap and shrinks first; short names no longer reserve an empty half-row. Long names still cap at half the row body. The frontend specification, design guide, Chat surface brief and schema-v2 preview mirror this rule.

Verified on isolated InMemory Synthetic API 5097/Vite 5183:

- MCP inspected populated Skill and resource rows at 1440/768/767/390/320px. Harness authoring measured approximately 119px rather than the desktop half-row's 292px; the long Skill title used the 292px cap. Metadata followed every measured name with an 8px gap. Short names remained readable, long names truncated, file tails stayed contained, and categories and 40/44px row heights remained intact. Mobile file geometry was rechecked after resize settled, including a 119px title cap inside the 320px viewport.
- Selected and sent Sample Hello; searched for a home file, selected it with Enter and sent it. Both historical chips and Synthetic replies appeared. Exhausted search displayed No matching choices and Escape restored editor focus. An initial assertion used an incorrect `.message-row.assistant` selector; the actual `.chat-message-assistant` and historical chip checks passed.
- `pnpm run test --run src/features/chat/Composer.test.tsx src/features/chat/composerChoices.test.ts`: **25 passed**. `pnpm run build`: **passed**, including strict TypeScript. Existing React `act`, SignalR annotation and bundle-size warnings remain.
- `pnpm exec playwright test --config=playwright.composer-max-width.tmp.config.ts e2e/structured-composer.spec.ts --grep 'picker and chips|short viewport|scrolling many|long mention|auto-pages|active option|disabled after selection'`: **14 passed**. The temporary configuration was removed after verification.
- MCP console inspection found no errors; inspected composer discovery/search requests returned 200. Desktop and mobile screenshots were inspected at `/private/tmp/composer-max-width-1440.png` and `/private/tmp/composer-max-width-390.png`.
- Documentation checks passed for schema v2, exact preview/source title CSS parity, current wording, balanced fences, local links/fragments and `git diff --check`.

Only the disposable test servers were stopped. These checks verify the affected local layout and interaction; the original backend/hosted/device limits still apply.

## Type alignment successor (2026-10-12)

Category labels now align to the row’s trailing edge using `margin-inline-start: auto` on the existing shared type element. Names retain their natural width capped at 50%; metadata remains adjacent with an 8px gap and shrinks first. This also anchors types when metadata is absent. The frontend specification, design guide, Chat surface brief and static preview were synchronized.

Verified on isolated InMemory Synthetic API 5197/Vite 5193, leaving the unrelated frontend on 5183 alone:

- MCP checked populated invocation rows and mixed File, Chat, Run and Skill reference rows at 1440/768/767/390/320px. Every category’s trailing edge matched the row body’s edge; name-to-metadata gaps stayed 8px, row heights remained 40/44px and measured rows had no overflow. Desktop and mobile captures were visually inspected at `/private/tmp/composer-type-align-1440.png`, `/private/tmp/composer-type-align-390.png`, `/private/tmp/composer-type-resources-1440.png` and `/private/tmp/composer-type-resources-390.png`. An initial mixed-category assertion expected “Skill”; the resource picker’s actual category is “Skill reference”, and the corrected check passed.
- Selected and sent Sample Hello, searched and inserted a file with Enter, then sent it. Historical chips and Synthetic responses appeared. Empty search and Escape returned focus to Message. MCP console inspection found no errors and inspected composer requests returned 200.
- `pnpm exec playwright test --config=playwright.composer-type.tmp.config.ts e2e/structured-composer.spec.ts --grep 'picker and chips|short viewport|long mention|active option'`: **6 passed**, covering responsive insertion, short viewports, long names and keyboard focus. The temporary configuration was removed afterward.
- `pnpm run test --run src/features/chat/Composer.test.tsx src/features/chat/composerChoices.test.ts`: **25 passed**. `pnpm run build`: **passed**, including strict TypeScript. Existing React `act`, SignalR annotation and bundle-size warnings remain.

Only the disposable servers were stopped. This local visual refinement carries the same backend/hosted/device verification limits as the original pass.

## Shared keyboard-focus successor (2026-10-12)

The [frontend design review and refinement](frontend-design-system-review.md) records the latest state: native choice buttons now share the product text-control focus outline, using a resolved Ant Design role for readable contrast on selected dark rows. Names, metadata, trailing categories, neutral chips and row geometry retain the rules above. The focused keyboard regression, 95 relevant unit tests, final 27 composer browser scenarios and build passed; the report contains before/after evidence, representative shared-control checks and verification limits.
