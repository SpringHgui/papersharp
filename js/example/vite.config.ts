import { defineConfig } from "vite";
import react from "@vitejs/plugin-react";

// 站点是纯静态的：引擎产物（public/_framework）由 scripts/sync-engine.mjs 从
// node_modules/papersharp/wasm 拷入，vite 只当普通静态资源搬运，不参与打包。
export default defineConfig({
  plugins: [react()],
  build: {
    target: "esnext",
    outDir: "dist",
    assetsInlineLimit: 0,
  },
  server: { port: 5173 },
  preview: { port: 4173 },
});
