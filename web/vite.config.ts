import { defineConfig } from "vitest/config"
import react from "@vitejs/plugin-react"

export default defineConfig({
  plugins: [react()],
  preview: {
    allowedHosts: process.env.AMP_ORB ? true : undefined,
    proxy: {
      "/api": "http://127.0.0.1:4317",
      "/health": "http://127.0.0.1:4317",
    },
  },
  server: {
    allowedHosts: process.env.AMP_ORB ? true : undefined,
    proxy: {
      "/api": "http://127.0.0.1:4317",
      "/health": "http://127.0.0.1:4317",
    },
  },
  test: {
    environment: "jsdom",
    include: ["src/**/*.test.{ts,tsx}"],
    setupFiles: "./src/test-setup.ts",
  },
})
