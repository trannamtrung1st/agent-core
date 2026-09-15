import { defineConfig, devices } from "@playwright/test";
import path from "node:path";
import { fileURLToPath } from "node:url";

const webDir = path.dirname(fileURLToPath(import.meta.url));
const root = path.resolve(webDir, "..");

export default defineConfig({
  testDir: "./e2e",
  fullyParallel: false,
  retries: 0,
  use: {
    baseURL: "http://127.0.0.1:5173",
    trace: "off",
    ...devices["Desktop Chrome"]
  },
  webServer: [
    {
      command: "dotnet run --project src/AgentCore.Api --launch-profile http",
      cwd: root,
      url: "http://127.0.0.1:5080/health",
      reuseExistingServer: !process.env.CI,
      timeout: 120_000
    },
    {
      command: "npm run dev -- --host 127.0.0.1 --port 5173",
      cwd: webDir,
      url: "http://127.0.0.1:5173",
      reuseExistingServer: !process.env.CI,
      timeout: 120_000
    }
  ]
});
