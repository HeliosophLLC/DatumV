import { defineConfig } from 'vite';
import react from '@vitejs/plugin-react-swc';
import tailwindcss from '@tailwindcss/vite';
import path from 'node:path';

export default defineConfig({
  plugins: [react(), tailwindcss()],
  resolve: {
    alias: {
      '@': path.resolve(__dirname, './src'),
    },
  },
  build: {
    outDir: '../wwwroot',
    emptyOutDir: true,
    // Stable filenames (no content hashes) so ASP.NET's static-web-assets
    // manifest doesn't drift between builds. Photino fetches fresh anyway,
    // so we don't need hash-based cache busting.
    rollupOptions: {
      output: {
        entryFileNames: 'assets/[name].js',
        chunkFileNames: 'assets/[name].js',
        assetFileNames: 'assets/[name][extname]',
      },
    },
  },
  server: {
    port: 5173,
    // Proxy is wired up next round (Photino host uses an ephemeral Kestrel
    // port; dev-mode standalone hosting will pin a known port for these).
    // proxy: {
    //   '/api':  { target: 'http://127.0.0.1:5000', changeOrigin: true },
    //   '/hubs': { target: 'http://127.0.0.1:5000', ws: true, changeOrigin: true },
    // },
  },
});
