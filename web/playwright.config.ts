import { defineConfig, devices } from "@playwright/test";
import fs from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";

const webDir = path.dirname(fileURLToPath(import.meta.url));
const root = path.resolve(webDir, "..");
const playwrightData = path.join(root, "data", "playwright");
const sqlitePath = process.env.PLAYWRIGHT_SQLITE_PATH
  ?? path.join(playwrightData, "synthetic.db");
process.env.PLAYWRIGHT_SQLITE_PATH = sqlitePath;
fs.mkdirSync(path.dirname(sqlitePath), { recursive: true });
const apiPort = process.env.PLAYWRIGHT_API_PORT ?? "5080";
const webPort = process.env.PLAYWRIGHT_WEB_PORT ?? "5173";
const apiUrl = `http://127.0.0.1:${apiPort}`;
const webUrl = `http://127.0.0.1:${webPort}`;
const browserSttApiPort = process.env.PLAYWRIGHT_BROWSER_STT_API_PORT ?? "5081";
const browserSttWebPort = process.env.PLAYWRIGHT_BROWSER_STT_WEB_PORT ?? "5174";
const browserSttApiUrl = `http://127.0.0.1:${browserSttApiPort}`;
const browserSttWebUrl = `http://127.0.0.1:${browserSttWebPort}`;
const browserBrowserApiPort = process.env.PLAYWRIGHT_BROWSER_BROWSER_API_PORT ?? "5082";
const browserBrowserWebPort = process.env.PLAYWRIGHT_BROWSER_BROWSER_WEB_PORT ?? "5175";
const browserBrowserApiUrl = `http://127.0.0.1:${browserBrowserApiPort}`;
const browserBrowserWebUrl = `http://127.0.0.1:${browserBrowserWebPort}`;

const chromium = devices["Desktop Chrome"];

export default defineConfig({
  testDir: "./e2e",
  fullyParallel: false,
  workers: 1,
  retries: 0,
  use: {
    ...chromium,
    baseURL: webUrl,
    trace: "off",
    permissions: ["microphone"],
    launchOptions: {
      ...chromium.launchOptions,
      args: [
        ...(chromium.launchOptions?.args ?? []),
        "--use-fake-device-for-media-stream",
        "--use-fake-ui-for-media-stream",
        "--autoplay-policy=no-user-gesture-required"
      ]
    }
  },
  projects: [
    { name: "synthetic", testIgnore: /browser-stt\.spec\.ts|browser-browser\.spec\.ts/ },
    { name: "browser-stt", testMatch: /browser-stt\.spec\.ts/, use: { baseURL: browserSttWebUrl } },
    { name: "browser-browser", testMatch: /browser-browser\.spec\.ts/, use: { baseURL: browserBrowserWebUrl } }
  ],
  webServer: [
    {
      command: `dotnet run --project src/AgentCore.Api --no-launch-profile --urls ${apiUrl}`,
      cwd: root,
      env: {
        ASPNETCORE_ENVIRONMENT: "Development",
        AgentCore__Profile: "Synthetic",
        Providers__Speech__Recognition__Adapter: "Synthetic",
        Providers__Speech__Synthesis__Adapter: "Synthetic",
        AGENTCORE_LIVE_PROVIDER_TESTS: process.env.AGENTCORE_LIVE_PROVIDER_TESTS ?? "0",
        AGENTCORE_LIVE_OPENAI_STT: process.env.AGENTCORE_LIVE_OPENAI_STT ?? "0",
        AGENTCORE_LIVE_OPENAI_TTS: process.env.AGENTCORE_LIVE_OPENAI_TTS ?? "0",
        Persistence__Provider: process.env.Persistence__Provider ?? "Sqlite",
        Persistence__ConnectionString: process.env.Persistence__ConnectionString
          ?? `Data Source=${sqlitePath}`,
        Persistence__AttachmentRoot: process.env.Persistence__AttachmentRoot
          ?? path.join(playwrightData, "attachments"),
        Persistence__WorkspaceRoot: process.env.Persistence__WorkspaceRoot
          ?? path.join(playwrightData, "workspaces"),
        Persistence__ArtifactRoot: process.env.Persistence__ArtifactRoot
          ?? path.join(playwrightData, "artifacts")
      },
      url: `${apiUrl}/health`,
      reuseExistingServer: !process.env.CI,
      timeout: 120_000
    },
    {
      command: `pnpm exec vite --host 127.0.0.1 --port ${webPort}`,
      cwd: webDir,
      env: {
        VITE_API_PROXY_TARGET: apiUrl,
        VITE_DEV_PORT: webPort
      },
      url: webUrl,
      reuseExistingServer: !process.env.CI,
      timeout: 120_000
    },
    {
      command: `dotnet run --project src/AgentCore.Api --no-launch-profile --urls ${browserSttApiUrl}`,
      cwd: root,
      env: {
        ASPNETCORE_ENVIRONMENT: "Development",
        AgentCore__Profile: "Synthetic",
        Persistence__Provider: "InMemory",
        Providers__Speech__Recognition__Adapter: "Browser",
        AGENTCORE_LIVE_PROVIDER_TESTS: "0",
        AGENTCORE_LIVE_OPENAI_STT: "0",
        AGENTCORE_LIVE_OPENAI_TTS: "0"
      },
      url: `${browserSttApiUrl}/health`,
      reuseExistingServer: false,
      timeout: 120_000
    },
    {
      command: `pnpm exec vite --host 127.0.0.1 --port ${browserSttWebPort}`,
      cwd: webDir,
      env: {
        VITE_API_PROXY_TARGET: browserSttApiUrl,
        VITE_DEV_PORT: browserSttWebPort
      },
      url: browserSttWebUrl,
      reuseExistingServer: false,
      timeout: 120_000
    },
    {
      command: `dotnet run --project src/AgentCore.Api --no-launch-profile --urls ${browserBrowserApiUrl}`,
      cwd: root,
      env: {
        ASPNETCORE_ENVIRONMENT: "Development",
        AgentCore__Profile: "Synthetic",
        Persistence__Provider: "InMemory",
        Providers__Speech__Recognition__Adapter: "Browser",
        Providers__Speech__Synthesis__Adapter: "Browser",
        AGENTCORE_LIVE_PROVIDER_TESTS: "0",
        AGENTCORE_LIVE_OPENAI_STT: "0",
        AGENTCORE_LIVE_OPENAI_TTS: "0"
      },
      url: `${browserBrowserApiUrl}/health`,
      reuseExistingServer: false,
      timeout: 120_000
    },
    {
      command: `pnpm exec vite --host 127.0.0.1 --port ${browserBrowserWebPort}`,
      cwd: webDir,
      env: {
        VITE_API_PROXY_TARGET: browserBrowserApiUrl,
        VITE_DEV_PORT: browserBrowserWebPort
      },
      url: browserBrowserWebUrl,
      reuseExistingServer: false,
      timeout: 120_000
    }
  ]
});
