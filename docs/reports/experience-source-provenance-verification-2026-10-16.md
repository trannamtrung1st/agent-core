# Experience source provenance verification — 2026-10-16

## Finding and change

The Experience table treated every `AgentRun` source as Background work. Read-only inspection of the reported record confirmed that its source and generation Runs were conversational UserTurn Runs, so the label was incorrect.

The owner HTTP review now projects the original source Run's output contract independently of the generation Run. Table labels, search, Source filters and expanded provenance use Conversation, Background work, Completion report, Consolidated or the unknown-metadata fallback Run. Separate actions inspect source and generation Runs when their IDs differ. These labels describe execution context, not who requested learning.

Existing Experience records use retained source metadata immediately after updating the API and frontend. No migration or modification of stored observations is needed. The user's running application and database were not changed for runtime verification.

## Focused verification

- API: `TMPDIR=/private/tmp DOTNET_CLI_USE_MSBUILD_SERVER=0 dotnet test tests/AgentCore.Api.Tests --no-restore -m:1 -p:UseSharedCompilation=false --nologo --filter 'FullyQualifiedName~ExperienceSourceProvenanceTests|FullyQualifiedName~ExperienceJourneyTests|FullyQualifiedName~ContinuityBoundaryTests'` — 21 passed. Conversational and Automation background sources execute separate background Experience generation, return their original source contract over owner HTTP and retain provenance after SQLite reopen. Missing/foreign source metadata returns null without borrowing generation metadata.
- Frontend, from `web`: `pnpm run test --run src/features/admin/InstanceContinuitySection.test.tsx` — 15 passed. Covers all source labels in rows/search/details, existing owner controls and distinct source/generation navigation.
- Frontend, from `web`: `pnpm run build` — passed; existing SignalR annotation and bundle-size warnings remain.
- Playwright MCP against an isolated Synthetic API on port 5680 and Vite on 5683, using a copied disposable test database: searched Conversation and expanded the completed Experience. Expected and observed Conversation in the row and Source type; the retained source ID matched the conversation Run. View source run opened the completed Chat turn; View generation run opened the distinct completed background Run with the Experience receipt. The Conversation filter retained the row; Background work excluded it. Related HTTP requests returned 200 and the browser reported no console errors.

Canonical frontend, HTTP contract and testing documents were synchronized. This is a focused regression fix, not milestone acceptance. CI and live-provider execution were not requested or run. Deploying the change requires the updated API and frontend; the existing application was not restarted.
