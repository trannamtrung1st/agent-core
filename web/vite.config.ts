/// <reference types="vitest" />
import { defineConfig } from "vite";
import react from "@vitejs/plugin-react";

const apiProxyTarget = process.env.VITE_API_PROXY_TARGET ?? "http://127.0.0.1:5080";

export default defineConfig({
  appType: "spa",
  cacheDir: process.env.VITE_CACHE_DIR ?? "node_modules/.vite",
  plugins: [react()],
  worker: { format: "es" },
  css: {
    postcss: { plugins: [{
      postcssPlugin: "scope-fortune-preview",
      Rule(rule: { source?: { input: { file?: string } }; parent?: { type: string; name?: string }; selectors: string[] }) {
        const file = rule.source?.input.file?.replaceAll("\\", "/");
        if (!file?.includes("@fortune-sheet/react/dist/index.css") || rule.parent?.type === "atrule" && rule.parent.name?.endsWith("keyframes")) return;
        rule.selectors = rule.selectors.map(selector => /^html(?=[: .#]|$)/.test(selector)
          ? selector.replace(/^html/, ".file-preview-spreadsheet") : `.file-preview-spreadsheet ${selector}`);
      },
    }] },
  },
  // Worker-only imports are outside Vite's main dependency scan. Prebundle
  // them before serving so the first preview cannot invalidate the page.
  optimizeDeps: {
    include: [
      "@fortune-sheet/react", "papaparse", "shiki/core", "shiki/engine/javascript",
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
    exclude: ["e2e/**", "node_modules/**", "dist/**"],
    // Large Ant Design Admin integration trees exceed 15 seconds on hosted CPU.
    // Keep behavior assertions intact while allowing measured rendering headroom.
    testTimeout: 30_000
  }
});
