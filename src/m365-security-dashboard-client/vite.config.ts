import { fileURLToPath, URL } from "node:url";
import react from "@vitejs/plugin-react";
import { defineConfig } from "vite";

const root = fileURLToPath(new URL(".", import.meta.url));

export default defineConfig({
  root,
  plugins: [react()],
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
      "/api": { target: "http://localhost:5001", changeOrigin: true }
    }
  },
  server: {
    port: 5000,
    strictPort: true,
    proxy: {
      "/api": { target: "http://localhost:5001", changeOrigin: true }
    }
  }
});
