/// <reference types="vitest" />
import { defineConfig } from "vite";
import react from "@vitejs/plugin-react";

const apiProxyTarget = process.env.VITE_API_PROXY_TARGET ?? "http://127.0.0.1:5080";

export default defineConfig({
  appType: "spa",
  plugins: [react()],
  worker: { format: "es" },
  // Worker-only imports are outside Vite's main dependency scan. Prebundle
  // them before serving so the first preview cannot invalidate the page.
  optimizeDeps: {
    include: [
      "papaparse", "shiki/core", "shiki/engine/javascript",
      "shiki/themes/github-dark.mjs",
      ...["javascript", "typescript", "jsx", "tsx", "json", "yaml", "toml", "xml", "csharp", "python", "sql", "css"]
        .map(language => `shiki/langs/${language}.mjs`)
    ]
  },
  server: {
    port: Number(process.env.VITE_DEV_PORT ?? 5173),
    proxy: {
      "/api": { target: apiProxyTarget, changeOrigin: true },
      "/health": { target: apiProxyTarget, changeOrigin: true },
      "/hubs": { target: apiProxyTarget, changeOrigin: true, ws: true }
    }
  },
  preview: {
    port: Number(process.env.VITE_PREVIEW_PORT ?? 4173),
    proxy: {
      "/api": { target: apiProxyTarget, changeOrigin: true },
      "/health": { target: apiProxyTarget, changeOrigin: true },
      "/hubs": { target: apiProxyTarget, changeOrigin: true, ws: true }
    }
  },
  test: {
    environment: "jsdom",
    globals: true,
    setupFiles: "./src/test/setup.ts",
    exclude: ["e2e/**", "opensandbox-e2e/**", "node_modules/**", "dist/**"],
    // Large Ant Design Admin integration trees exceed 15 seconds on hosted CPU.
    // Keep behavior assertions intact while allowing measured rendering headroom.
    testTimeout: 30_000
  }
});
