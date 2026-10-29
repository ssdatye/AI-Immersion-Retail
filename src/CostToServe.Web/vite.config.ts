import react from "@vitejs/plugin-react";
import { defineConfig } from "vite";

// Builds into the API's wwwroot so one App Service serves the studio and the agent API.
// `npm run dev` proxies /api to the API on http://localhost:5080.
export default defineConfig({
  plugins: [react()],
  build: {
    outDir: "../CostToServe.Api/wwwroot",
    emptyOutDir: true,
  },
  server: {
    port: 5173,
    proxy: { "/api": "http://localhost:5080" },
  },
});
