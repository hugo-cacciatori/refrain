import { svelte } from '@sveltejs/vite-plugin-svelte';
import { defineConfig } from 'vitest/config';

// The page Tauri shows (SPEC §38): built from ui/ into ui/dist/, served on a fixed port while developing.
export default defineConfig({
  root: 'ui',
  plugins: [svelte()],
  clearScreen: false,
  server: { port: 5173, strictPort: true },
  build: { outDir: 'dist', emptyOutDir: true, target: 'chrome120' },
  test: { include: ['src/**/*.test.ts'] },
});
