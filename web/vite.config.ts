import { defineConfig } from 'vite'
import react from '@vitejs/plugin-react'
import tailwindcss from '@tailwindcss/vite'

// https://vite.dev/config/
export default defineConfig({
  plugins: [react(), tailwindcss()],
  resolve: {
    tsconfigPaths: true,
  },
  server: {
    proxy: {
      // Phase 0 uses an in-memory mock client, so this proxy is unused today.
      // It is here so a later phase can point the typed data client at the
      // real .NET broker read-model without reworking the dev setup.
      '/tessera-api': {
        target: process.env.VITE_TESSERA_API_TARGET ?? 'http://127.0.0.1:8080',
        changeOrigin: true,
        rewrite: (path) => path.replace(/^\/tessera-api/, ''),
      },
    },
  },
})
