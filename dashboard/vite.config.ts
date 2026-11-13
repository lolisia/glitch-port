import react from '@vitejs/plugin-react';
import { defineConfig } from 'vitest/config';

// The Processor allows the http://localhost:5173 origin via CORS, which is the
// Vite dev server default (pinned here so the two stay in sync).
export default defineConfig({
  plugins: [react()],
  server: {
    port: 5173,
  },
  test: {
    environment: 'jsdom',
    setupFiles: './src/setupTests.ts',
  },
});
