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
  // Relative asset URLs so loader.html works when loaded via file:// in
  // the packaged app. Splash and welcome render before Kestrel exists
  // (or while it's being restarted during a catalog swap), so they
  // can't be served over http — main.ts loadFile's them directly off
  // disk. The SPA itself still works fine: relative paths resolve
  // against the document URL whether served by Vite or Kestrel.
  base: './',
  build: {
    outDir: '../wwwroot',
    emptyOutDir: true,
    // Stable filenames (no content hashes) so ASP.NET's static-web-assets
    // manifest doesn't drift between builds. The Electron renderer
    // reloads fresh on each launch, so we don't need hash-based cache
    // busting.
    rollupOptions: {
      // Two entry points: the SPA (index.html) and the pre-SPA loader
      // (loader.html), which renders splash + welcome via React.
      input: {
        main: path.resolve(__dirname, 'index.html'),
        loader: path.resolve(__dirname, 'loader.html'),
      },
      output: {
        entryFileNames: 'assets/[name].js',
        chunkFileNames: 'assets/[name].js',
        assetFileNames: 'assets/[name][extname]',
      },
    },
  },
  server: {
    port: 5173,
    strictPort: true, // fail fast if 5173 is taken — Electron expects it
    // The Documentation tab `import.meta.glob`s repo-root `docs/**/*.md`
    // (eagerly, as ?raw strings). Vite's default fs allow-list is rooted
    // at this workspace, which excludes the repo's `docs/` four levels
    // up; widen it to the repo root so the dev server can serve those
    // markdown sources to the module graph. Production builds bundle
    // the content statically so the restriction doesn't apply there.
    fs: {
      allow: [path.resolve(__dirname, '../../..')],
    },
    // Forward API + SignalR back to Kestrel (pinned to 5050 by
    // electron/main.ts's DATUMV_WEB_URL env). Same-origin from the SPA's
    // POV so generated NSwag clients work with an empty baseUrl in both
    // dev and prod.
    proxy: {
      '/api': { target: 'http://127.0.0.1:5050', changeOrigin: true },
      '/hubs': { target: 'http://127.0.0.1:5050', ws: true, changeOrigin: true },
    },
  },
});
