import { createRequire } from "node:module";
import { fileURLToPath, URL } from "node:url";
import react from "@vitejs/plugin-react";
import { defineConfig } from "vite";

const root = fileURLToPath(new URL(".", import.meta.url));

// Single source of truth for the client version. Hardcoding it in main.tsx meant
// the sidebar could claim a version the build was not — this reads package.json
// at build time, and CI checks it still matches the API's <Version>.
const { version } = createRequire(import.meta.url)("./package.json");

export default defineConfig({
  root,
  plugins: [react()],
  define: { __APP_VERSION__: JSON.stringify(version) },
  build: {
    outDir: "../M365SecurityDashboard.Api/wwwroot",
    emptyOutDir: true,
    rollupOptions: {
      input: fileURLToPath(new URL("index.html", import.meta.url))
    }
  },
  preview: {
    port: 5000,
    strictPort: true,
    proxy: {
      "/api": { target: "https://vigil365.local:5001", changeOrigin: true, secure: false }
    }
  },
  server: {
    port: 5173,
    strictPort: true,
    proxy: {
      "/api": { target: "https://vigil365.local:5001", changeOrigin: true, secure: false }
    }
  },
  test: {
    exclude: ['node_modules', 'dist', 'e2e/**']
  }
});
