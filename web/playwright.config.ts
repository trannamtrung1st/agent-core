import { defineConfig, devices } from "@playwright/test";
import path from "node:path";
import { fileURLToPath } from "node:url";

const webDir = path.dirname(fileURLToPath(import.meta.url));
const root = path.resolve(webDir, "..");
const apiPort = process.env.PLAYWRIGHT_API_PORT ?? "5080";
const webPort = process.env.PLAYWRIGHT_WEB_PORT ?? "5173";
const apiUrl = `http://127.0.0.1:${apiPort}`;
const webUrl = `http://127.0.0.1:${webPort}`;

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
  webServer: [
    {
      command: `dotnet run --project src/AgentCore.Api --no-launch-profile --urls ${apiUrl}`,
      cwd: root,
      env: {
        ASPNETCORE_ENVIRONMENT: "Development",
        AgentCore__Profile: "Synthetic",
        AGENTCORE_LIVE_PROVIDER_TESTS: process.env.AGENTCORE_LIVE_PROVIDER_TESTS ?? "0",
        AGENTCORE_LIVE_OPENAI_STT: process.env.AGENTCORE_LIVE_OPENAI_STT ?? "0",
        AGENTCORE_LIVE_OPENAI_TTS: process.env.AGENTCORE_LIVE_OPENAI_TTS ?? "0",
        ...(process.env.Persistence__Provider
          ? { Persistence__Provider: process.env.Persistence__Provider }
          : {}),
        ...(process.env.Persistence__ConnectionString
          ? { Persistence__ConnectionString: process.env.Persistence__ConnectionString }
          : {}),
        ...(process.env.Persistence__AttachmentRoot
          ? { Persistence__AttachmentRoot: process.env.Persistence__AttachmentRoot }
          : {}),
        ...(process.env.Persistence__WorkspaceRoot
          ? { Persistence__WorkspaceRoot: process.env.Persistence__WorkspaceRoot }
          : {}),
        ...(process.env.Persistence__ArtifactRoot
          ? { Persistence__ArtifactRoot: process.env.Persistence__ArtifactRoot }
          : {})
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
    }
  ]
});
