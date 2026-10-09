# Low reasoning defaults — 2026-10-10

All four shipped Real reasoning-capable models and Synthetic Alpha now default to `low`. Native launch, Synthetic appsettings, Compose fallback and `.env.example` agree. Unspecified catalog/primary defaults use `low`; explicit configured defaults and saved Session/Run pins retain their chosen values. Free and non-reasoning Synthetic models retain null effort. DeepSeek remains the default model; transports and supported effort sets are unchanged.

`SessionModelApiTests` passed 8 tests: authenticated catalog exposes Low, creating a default Session persists Low on history reload, explicit Host High survives, non-reasoning selection remains null, mutation/ended/owner protection remain valid. This is HTTP integration evidence using Synthetic, without provider credentials.

Catalog and Chat Completions/Responses adapter regressions passed 105 tests, with 5 live opt-in skips. The final catalog consistency rerun passed all 17 tests, including shipped launch/Compose/environment/appsettings checks and omitted-default/explicit-override behavior. `SessionModelBinderTests`, `SessionModelSelectionTests` and `ExecutionModelPolicyTests` passed all 24 tests, covering runtime selection, persisted provenance and unattended overrides.

Canonical decisions, configuration and operating examples are synchronized. Edited Markdown local links/fragments/fences and configuration JSON parsing passed. This is a focused default change; full solution, frontend/Playwright, Compose execution and live inference were not rerun. Hosted CI is not awaited, as requested. Existing sessions need an explicit effort change to adopt Low.
