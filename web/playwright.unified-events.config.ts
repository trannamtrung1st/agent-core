import { defineConfig, devices } from "@playwright/test";
export default defineConfig({
  testDir: "./e2e", outputDir: process.env.UNIFIED_EVENTS_OUTPUT_DIR ?? "/tmp/agent-core-unified-playwright", workers: 1, retries: 0, reporter: "list", timeout: 90000,
  use: { ...devices["Desktop Chrome"], baseURL: process.env.UNIFIED_EVENTS_BASE_URL ?? "http://127.0.0.1:5273", trace: "retain-on-failure" },
});
