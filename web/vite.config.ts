/// <reference types="vitest/config" />
import { defineConfig } from 'vite';
import react from '@vitejs/plugin-react';

const api = process.env.AEZA_API ?? 'http://localhost:5080';

export default defineConfig({
  plugins: [react()],
  build: { outDir: 'dist', sourcemap: false, assetsDir: 'assets' },
  server: {
    port: 5173,
    proxy: {
      '/api': { target: api, changeOrigin: false },
      '/hubs': { target: api, ws: true, changeOrigin: false },
      // Служебные маршруты сервиса: на порту панели ведут себя так же, как на API (а не отдают HTML панели)
      '/health': { target: api, changeOrigin: false },
      '/metrics': { target: api, changeOrigin: false },
    },
  },
  test: { environment: 'jsdom', include: ['src/**/*.test.ts', 'src/**/*.test.tsx'] },
});
