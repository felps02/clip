import { fileURLToPath } from "node:url";
import react from "@vitejs/plugin-react";
import { defineConfig } from "vitest/config";

export default defineConfig({
  plugins: [react()],
  resolve: {
    alias: {
      "@": fileURLToPath(new URL("./src", import.meta.url)),
    },
  },
  test: {
    environment: "jsdom",
    globals: false,
    setupFiles: ["./vitest.setup.ts"],
    include: ["src/**/*.test.{ts,tsx}"],
    css: false,
    // Interaction tests (Radix menus/dialogs + user-event) take < 1 s alone but can exceed the 5 s
    // default when the whole suite runs in parallel on a busy machine.
    testTimeout: 15_000,
    env: {
      NEXT_PUBLIC_API_BASE_URL: "http://api.test",
      NEXT_PUBLIC_BRAND_NAME: "ClipForge",
    },
  },
});
