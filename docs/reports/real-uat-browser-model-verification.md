# Real UAT browser model verification

Date: 2026-10-09. The user explicitly authorized paid Real-profile trials using the current owner data and the exact task below. This is focused verification and correction of the existing native browser/tool system, not milestone-wide acceptance.

> login into uat ahi, app Data management, then goto project AI Engineering, view data of pump 002 for me. then log out and close browser

## Setup and observed causes

The current SQLite store was backed up before starting the latest Real API and Vite host. Trials used the existing Riley v24 Agent Instance, credential binding, persistent agent browser profile and effective on-demand Skill catalog. Each full task ran in a fresh owned Session, sequentially. Model records confirm `deepseek-v41-flash` / `gpt-5.6-luna` with `medium` reasoning. Protected values stayed in the existing password sink; no credential extraction or ordinary password typing was used.

Three actionable problems were found:

1. `BrowserResultProjection` reserved eight content bytes for every serialized byte of remaining headroom, after checkpoint admission had already applied its conservative escaping reserve. Successful scoped attribute-table observations lost most values even when more safe content would fit. Fitting now searches UTF-8-safe prefixes against the actual serialized envelope size, preserving success/identity and the existing hard limits.
2. The current owner-authored AHI Skill incorrectly told models to fill both Email and Password with `browser.fill_credential`. Its password-only sink correctly rejected Email. The saved procedure now uses non-secret username metadata and ordinary Email entry, with protected fill only for Password.
3. The saved procedure treated the Table tab as measurements, although the observed tab lists configured asset tables. It also omitted the account menu's hover trigger. The corrected procedure starts general view requests with Attribute data, distinguishes blank fields and empty configured-table lists from tool failure, and describes hover → Logout → observed login redirect → browser.close.

The owner Skill was corrected through revision-checked Admin API writes, remains enabled with OnDemand projection, and declares its required hover capability. Its final procedure is 3227 characters. There is no bulk Skill activation, new grant, browser bypass, per-model prompt branch, coordinate script, checkpoint expansion or automatic replay of an uncertain effect. Existing executions retain their pinned procedure; fresh admissions receive the corrected one.

## Trial outcomes

A completed assistant entry alone does not establish full task success. Logout requires a Logout action followed by an independent login-page observation; closure requires the native `closed` receipt with SDK confirmation. A separate cleanup-only turn is diagnostic evidence and is not counted as an exact-prompt success.

| Build / saved procedure | Model | Tool steps | Observed outcome |
| --- | --- | ---: | --- |
| Original build / revision 2 | DeepSeek Medium | 34 | Protected login and asset access worked; invalid dialog choices and malformed final response; cleanup incomplete. |
| Original build / revision 2 | Luna Medium | 46 | Reached asset; observation/capacity limits; explicitly reported incomplete cleanup. |
| Fitting fix / revision 2 | DeepSeek Medium | 48 | Protected login and asset observations worked; further data searches exhausted the step limit; cleanup incomplete. |
| Fitting fix / revision 3 | Luna Medium | 45 | Reported asset attributes; navigation/capacity exhaustion; cleanup incomplete. |
| Fitting fix / revision 4 | DeepSeek Medium | 42 | Existing authenticated profile; asset read, Logout → login-page redirect, confirmed browser closure. |
| Fitting fix / revision 4 | Luna Medium | 41 | Cold protected login and asset read worked; target/menu recovery exhausted capacity; cleanup incomplete. |
| Fitting fix / revision 5 | Luna Medium | 38 | Cold protected login, displayed Pump 002 attributes, Logout → login-page observation, SDK-confirmed closure; completed final response. |
| Fitting fix / revision 5 | DeepSeek Medium | 38 | Cold protected login, asset read and observed logout succeeded. Four malformed close arguments were rejected; browser closure incomplete in the model run. |

After the sixth full task, a separate Luna cleanup-only turn with the observed hover-menu detail completed in 19 tool steps. Its receipts confirm Logout, the login redirect and SDK-confirmed browser closure. This shows that protected login, hover, sign-out and closure are implemented and usable; it does not establish reliable autonomous completion of the whole original task.

## Local checks

- Focused Application fitting/result tests: **14 passed**, no skips or failures. The new executor regression retains a later table value, preserves Unicode and fills the serialized byte budget without overflow.
- Full key-free Application suite: **1455 passed / 3 opt-in skipped**, no failures.
- Native semantic actions, protected credential sink and browser reliability fixtures: **14 passed**, no skips or failures, with actual Chromium and local fixtures.
- Logs: `/tmp/uat-real-observation-tests.log`, `/tmp/uat-real-application-tests.log`, `/tmp/uat-real-native-tests.log`. Minimal private trial audit: `/tmp/uat-real-model-audit.json`. The existing user store retains the owned Session/AgentRun receipts. No private page dumps, credential values or raw measurement data are committed in this report.

## Acceptance limits

Eight exact-prompt Real trials were executed (four per model), plus one separate Luna cleanup diagnostic. The final fresh Luna trial (`59500594-7b5e-4e56-b92b-b244844a7f0c`) completed the whole task from a signed-out state. DeepSeek completed the whole task in its revision-4 authenticated-profile trial (`4c05563c-7841-4c01-ae0f-c237ebdfd679`), but its final revision-5 cold-login trial (`5c6ff6ad-cf8d-4dca-a65c-75c95a220e41`) left browser closure incomplete.

The final DeepSeek close calls were `{"arguments":"{}"}`, `{"target":{}}`, `{"parameters":{}}` and `{"dummy":"x"}`. Its reply inaccurately described these as empty objects. All four violate the offered empty-object schema; the real receipts correctly instruct that close accepts `{}`. An earlier DeepSeek call and the final Luna call used `{}` and received SDK-confirmed `closed`. This remaining intermittent argument-generation failure is model behavior, not an unsupported browser operation. The contract was deliberately left intact rather than accepting invented wrappers/fields or declaring a false full pass.

The final DeepSeek logout was independently observed before cleanup. Ending its test Session retained the live browser context. Gracefully stopping only the Real API/frontend started for these checks closed that context; a read-only process check confirmed zero remaining Chrome processes for the test profile. The current store and corrected on-demand Skill remain saved. The unrelated pre-existing Synthetic host was not stopped or reset.

These outcomes do **not** establish reliable, error-free completion for both models. No overall milestone or full hosted-CI acceptance is claimed for this focused correction. Recovery notices for missing/ambiguous targets and absent native dialogs are tool outcomes, not missing tool implementations. The 48-step interactive limit, 64 KiB checkpoint limit and uncertain-effect protections remain authoritative. Do not infer model reliability or full task success from native fixture passes, provider invocation success or a model's own assertion.
