# Operating instructions width refinement

Date: October 10, 2026. Bounded layout fix; no milestone acceptance claim.

The Definition textarea inherited the 20rem short-field cap, inside a 48rem Settings form. Operating instructions now uses the shared `admin-settings-instructions` variant: its form fills the section and its textarea is excluded from the short-field cap. Definition drafts, immutable version inspection and Instance settings share this policy. Other fields retain existing widths, gaps and Ant Design controls. Existing workspace changes were preserved.

Playwright MCP used a disposable Synthetic/InMemory API on 5096 and Vite on 5196; health reported Synthetic. It created a Definition draft, edited long instructions and saved successfully. The repeated Definition journey separately verified retained drafts through tabs/JSON recovery, save/reload and catalog error recovery. Definition widths were 1374/702/709/332px at 1440/768/767/390px, exactly matching the section width with no page overflow. An Instance was created through visible controls, inherited instructions remained disabled, Customize enabled editing, and long text was saved and retained exactly after reopening. Its editor measured 1374px desktop and 332px mobile. Immutable v1 inspection used 766px desktop and 324px mobile, matching its drawer section while remaining read-only. Final browser inspection had no console errors or failed API requests; initial navigation before API readiness was retried, and early selector/length assertions were corrected to the actual accessible controls and exact original text.

- `definition-configuration-layout.spec.ts` against the disposable host: 2 passed. The existing responsive journey now asserts instruction width matches the section, including more than 768px available on desktop.
- `pnpm run build`: passed. The initial attempt exposed an unsupported Testing Library `exact` option in an existing test helper; removing it preserves the library's default exact string-name matching. Existing SignalR annotation and bundle-size warnings remain.
- Layout detector: no findings. `git diff --check`, affected Markdown links and balanced fences passed.
- Focused Node 22 component check for restricted Core Event authority through editing/JSON round trips passed (1 test, 15 intentionally filtered). The Settings helper now scopes accessible button queries to its own collapse rather than searching cached forms across Admin; string names retain default exact matching. Final TypeScript check passed after that helper change.

The initial two-case component run was stopped after its CPU-bound worker did not complete for over two minutes. A narrower fractional-recurrence save case exceeded its existing 30-second limit before the helper query was scoped. Neither run is counted as passing evidence, and no timeout was increased. The corresponding validation/save flow passes in the Synthetic browser regression above.

The final isolated fractional-recurrence retry passed in 15.15 seconds at the unchanged 30-second limit after the scoped helper change. Together with the Core Event case, 2 focused component tests passed across separate commands; the entire component file was not rerun.

Temporary runner, output and logs live under `/private/tmp/ac-instructions-width`. Only the hosts started for this task were stopped. Full frontend/backend suites, Compose and hosted checks were not run for this layout fix.
