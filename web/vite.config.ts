/// <reference types="vitest" />
import { defineConfig } from "vite";
import react from "@vitejs/plugin-react";

export default defineConfig({
  plugins: [react()],
  server: {
    port: 5173,
    proxy: {
      "/api": { target: "http://127.0.0.1:5080", changeOrigin: true },
      "/health": { target: "http://127.0.0.1:5080", changeOrigin: true },
      "/hubs": { target: "http://127.0.0.1:5080", changeOrigin: true, ws: true }
    }
  },
  test: {
    environment: "jsdom",
    globals: true,
    setupFiles: "./src/test/setup.ts"
  }
});
