/// <reference types="vitest" />
import { defineConfig } from "vite";
import react from "@vitejs/plugin-react";

const apiProxyTarget = process.env.VITE_API_PROXY_TARGET ?? "http://127.0.0.1:5080";

export default defineConfig({
  appType: "spa",
  plugins: [react()],
  server: {
    fs: {
      allow: [".."]
    },
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
    exclude: ["e2e/**", "node_modules/**", "dist/**"]
  }
});
