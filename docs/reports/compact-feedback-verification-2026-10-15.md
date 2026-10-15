# Compact message feedback — 2026-10-15

The screenshot showed a long verification error spanning the header. Chat and Admin now share native Ant Design message configuration through `web/src/app/appFeedback.ts`: 40rem maximum width, full wrapping, 16px inline clearance, 8px stack gaps and bottom placement above a 96px action clearance plus the device safe area. The native surface, severity icons, hover pause and diagnostic controls remain. Normal-flow notices avoid stale measured-height offsets when existing messages wrap after resizing. This is a focused UI refinement; [Frontend Implementation](../13-frontend-implementation-spec.md) owns behavior and [DESIGN.md](../../.agents/context/DESIGN.md#admin-forms-creation-dialogs-and-validation) owns presentation guidance.

## Runtime evidence

Playwright MCP exercised an isolated Vite server on 5188 and a disposable InMemory Synthetic API on 5088. `/health` reported profile `Synthetic`, build `aba8cd94`. The user's existing Real host and database were not used. Presentation source fingerprints are recorded in the [preview metadata](../../.impeccable/design.json).

A profile was created through Add prebuilt image. Blank Image showed a warning and moved focus to the persistent invalid field. An invalid image produced an actual API 400, retained the draft and displayed the bounded Save failed message. Correcting to a local image ID saved with API 200, closed the Modal and selected the saved profile. Verify profile then returned an actual `passed: false` provisioning observation, reproducing the long error without inventing successful readiness; retained verification evidence stayed available.

The long verification message remained fully wrapped with no horizontal page/text overflow:

| Viewport | Toast width × height | Inline position | Bottom clearance |
| --- | --- | --- | --- |
| 1440×900 | 640×84 | x=400 | 96px |
| 768×900 | 640×84 | x=64 | 96px |
| 390×844 | 358×128 | x=16 | 96px |
| 320×600 | 288×172 | x=16 | 96px |
| 720×500 | 640×84 | x=40 | 96px |

The confirmation reused the same message while resizing, verifying that wrapping no longer shifts the stack toward the footer. Local captures: `.playwright-mcp/feedback-bottom-1440.png` and `.playwright-mcp/feedback-bottom-390.png` (not committed artifacts).

At 390×844, blank Sandbox Image and nested Registry credential submission used one deduplicated warning. Focus moved to Image and then Display name. The warning occupied y=686…748; Save began at y=768, so footer actions remained reachable. Closing the nested dialog returned to the profile editor.

A controlled API 400 fixture supplied a long unbroken reference and diagnostic ID. Its message remained inside x=16…374 with no horizontal overflow (358×190); Error details opened the safe ID and exposed Copy details. The draft retained its image. Removing the fixture and retrying saved successfully against the Synthetic API. This fixture verifies feedback/diagnostics presentation, not a new backend failure contract.

At 320×600, an additional controlled 1,600-character unbroken diagnostic exercised vertical overflow. The stack stayed 300px tall with 1,180px of scrollable content. Opening Error details scrolled locally to 880px and kept the diagnostic/Copy control reachable.

A non-destructive Chat catalog/rename response fixture exercised the actual Session rail Rename → Save flow. The saved row changed and native Session renamed feedback used the same bottom layout (162.83×40, x=113.59, y=708 at 390×844). Fixtures were removed afterward. Console inspection found no errors in this final Chat flow. The Sandbox checks produced the expected actual/controlled 400 responses; earlier startup connection errors were resolved before the use cases ran.

## Focused checks

- `pnpm exec vitest run src/features/admin/adminFormFeedback.test.ts src/features/admin/adminFailure.test.tsx src/features/admin/SandboxProfilesSection.test.tsx src/app/AppShell.test.tsx --maxWorkers=1`: 22 tests passed. The existing jsdom Sandbox geometry warning did not fail the suite.
- `pnpm run build`: passed; existing bundle-size and SignalR annotation notices remain.
- Impeccable detector: passed.
- Documentation JSON/YAML, token references, links/anchors, guide/preview narrative and source fingerprints are checked together with the final diff.

CI was not requested or run. These checks establish local UI behavior; they do not establish successful image provisioning, private-registry/provider acceptance, native browser zoom, physical touch or manual screen-reader behavior. Persistent field/local guidance remains the recovery source after transient feedback disappears.
