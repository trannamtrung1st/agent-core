# Background Work review and projection copy — October 9, 2026

The supplied review leaves one UI concern: Admin should acknowledge Core-managed Browser v2 bootstrap independently of explicit Always selections. The current policy is preserved. Admin now explains authorized/eligible bootstrap and active Skill requirements, and labels the readout **Always selected** rather than implying it counts the complete initial projection. The architecture’s existing bootstrap union names the seven implemented browser tools; frontend documentation records the authoring explanation. No runtime, authorization, provider or persistence behavior changed.

## Background Work review

The existing layout passed the bounded review; no further visual change was warranted. Original title headings, initial Run status/result, quiet outcomes and completion accounting remain distinct. Catalog actions wrap; detail Continue remains the intentional full-width primary action. Back restores the exact View original result trigger. Conversation run history remains collapsed and original task files retain their ownership guidance. Ant Design v6, resolved tokens and the 8/12/16px rhythm are preserved.

Playwright MCP used isolated Synthetic API `127.0.0.1:5128` and Vite `127.0.0.1:5218`, with `/private/tmp/agent-core-background-polish.db`. Public DTO fixtures exercised long original titles, attention and quiet outcomes, keyboard original-result inspection, Back/focus restoration and reopening. Settled captures at 1440/768/390px confirmed document widths equal to viewport widths, drawer client/scroll widths 640/640, 640/640 and 390/390, and 16px body inset. Screenshots were visually reviewed at desktop/mobile. No layout defect remained, so BackgroundWorkDrawer and its styles were unchanged.

The first resize captures were taken during Ant Design drawer animation and were superseded by settled geometry assertions. Script setup was corrected for the MCP VM’s available globals and file roots; initial startup also waited for local owner bootstrap. These exploratory harness errors are not application failures or passing evidence. Ignored settled captures are `local/verification/background-polish/{catalog,details}-final-{1440,768,390}.png`.

## Authoring and regression evidence

Playwright MCP forked General Assistant v17, opened its draft through the visible Drafts collection and selected Capabilities. The Core-managed explanation was visible beside Authorized / Always selected counts (69 / 49). At 390px, document width remained 390px. The final navigation had zero console errors. A first draft probe used an unsupported URL; visible collection navigation corrected it.

| Check | Result |
| --- | --- |
| Focused Vitest: `definitionCandidateEditor.test.tsx -t 'preserves capability authorization\|permits more than 32' --maxWorkers=1`, Node 22 | 2 passed; 11 unrelated tests skipped. Form save retains exact grants and Always selection; large-catalog counts remain accurate |
| Synthetic Playwright: `background-work.spec.ts` | 3 passed: attention/approval separation, exact source navigation, and bulk read/recovery/persistence |
| TypeScript `tsc --noEmit` and Vite production build | Passed; inherited large-chunk warning remains |
| Design/docs validation | YAML/JSON, unchanged token primitives, canonical headings, narrative/metadata/reference consistency, local links/source targets and whitespace checks passed |

Commands ran from `web/` with Node `/Users/trungtran/.nvm/versions/node/v22.18.0/bin/node`. Browser command uses the existing temporary `../local/verification/admin-ui-feedback/playwright.config.mts`, `PLAYWRIGHT_API_PORT=5128`, `PLAYWRIGHT_WEB_PORT=5218` and `PLAYWRIGHT_SQLITE_PATH=/private/tmp/agent-core-background-polish.db`. Logs remain ignored under `local/verification/admin-ui-feedback/logs/`: `bootstrap-copy-unit-final.log`, `background-polish-e2e.log`, `bootstrap-copy-build.log`.

DESIGN.md, PRODUCT.md, Chat/Admin surface briefs and the Impeccable sidecar were synchronized with current source. The sidecar retains the same token primitives and updates the capability preview count/explanation. CI was neither polled nor awaited, as requested. This is local UI verification, not a hosted acceptance or freeze claim; earlier evidence remains historical.

## Follow-up: other authorized capabilities

The remaining terminology concern is resolved by renaming the collapsed disclosure to **Other authorized capabilities**. Its count now says **Not selected as Always available**, describing explicit selection membership without implying those tools are absent from initial model context. The frontend specification, Admin surface brief, design guidance and preview use the same terminology. Projection and authorization behavior remain unchanged.

The two focused Vitest cases passed (11 unrelated tests skipped). The save case includes authorized `browser.navigate` outside the Always selection, verifies disclosure expansion and its count, and preserves the exact saved grants. Playwright MCP exercised a Synthetic General Assistant draft, expanded the disclosure with the keyboard, confirmed the Core-managed `browser.close` tag, and collapsed it again. Document width matched viewport width at 1440, 768 and 390px; the browser reported zero console errors.

TypeScript and the Vite production build passed in an isolated snapshot of `6c88b53f` with this follow-up's frontend changes. The shared checkout's initial compilation encountered unrelated concurrent Activity/navigation changes, so that build does not establish correctness of those changes. The initial unit probe's missing test import and browser probes' owner-bootstrap/locator assumptions were corrected before the passing checks. Final logs are `other-authorized-unit-final.log` and `other-authorized-build-isolated.log` under the ignored verification log directory. Design reference, token, metadata, local-link and whitespace validation passed. CI was not awaited.
