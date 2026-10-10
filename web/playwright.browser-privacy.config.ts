import { defineConfig, devices } from "@playwright/test";
import { mkdtempSync } from "node:fs";
import { tmpdir } from "node:os";
import path from "node:path";
import { fileURLToPath } from "node:url";

const web = path.dirname(fileURLToPath(import.meta.url));
const external = process.env.BROWSER_PRIVACY_BASE_URL;
const data = mkdtempSync(path.join(tmpdir(), "agent-core-browser-privacy-e2e-"));
const api = "http://127.0.0.1:5198";
const baseURL = external ?? "http://127.0.0.1:5298";
export default defineConfig({
  testDir: "./e2e", testMatch: "browser-privacy.spec.ts", workers: 1, retries: 0,
  reporter: "list", outputDir: path.join(data, "results"),
  use: { ...devices["Desktop Chrome"], baseURL, trace: "retain-on-failure" },
  webServer: external ? undefined : [
    { command: "dotnet run --no-build --project src/AgentCore.Api --no-launch-profile --urls " + api,
      cwd: path.resolve(web, ".."), url: api + "/health", timeout: 60_000, reuseExistingServer: false,
      env: { AgentCore__Profile: "Synthetic", Browser__Enabled: "true", Browser__Headless: "true", Browser__FixturePort: "0",
        Browser__AllowUnmaskedCaptures: "true", Browser__UnmaskedCaptureOrigins__0: "https://example.test",
        Browser__TrustedVisualCaptureOrigins__0: "https://example.test", Persistence__Provider: "Sqlite",
        Persistence__ConnectionString: "Data Source=" + path.join(data,"app.db"),
        Persistence__WorkspaceRoot: path.join(data,"workspace"), Persistence__ArtifactRoot: path.join(data,"artifacts") } },
    { command: "pnpm exec vite --host 127.0.0.1 --port 5298", cwd: web, url: baseURL, timeout: 60_000,
      reuseExistingServer: false, env: { VITE_API_PROXY_TARGET: api } }
  ]
});
