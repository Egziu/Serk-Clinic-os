import { defineConfig } from "vitest/config";
import react from "@vitejs/plugin-react";

// The production build is served by the ASP.NET Core host (same origin => SameSite=Strict session cookie + CSP 'self').
export default defineConfig({
  plugins: [react()],
  build: {
    outDir: "../backend/Serk.Clinic.Host/wwwroot",
    emptyOutDir: true,
    sourcemap: false,
  },
  server: {
    port: 5173,
    proxy: {
      "/api": "http://localhost:5080",
      "/health": "http://localhost:5080",
    },
  },
  test: {
    include: ["src/**/*.test.ts"],
    environment: "node",
  },
});
