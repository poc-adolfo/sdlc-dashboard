/// <reference types="vitest/config" />
import { defineConfig } from 'vite';
import react from '@vitejs/plugin-react';

// The backend port isn't fixed by a launchSettings.json (src/Backend.Api has none) - override via
// VITE_BACKEND_PORT if `dotnet run` picks a different one for you locally.
const backendTarget = `http://localhost:${process.env.VITE_BACKEND_PORT ?? '5000'}`;

// https://vite.dev/config/
export default defineConfig({
  plugins: [react()],
  server: {
    // Explicit list matching the endpoint contract (seção 14) - not a catch-all, so the dev server
    // still serves its own asset/HTML routes under every other path.
    proxy: Object.fromEntries(
      ['/auth', '/clients', '/workspaces', '/pipeline-instances'].map((path) => [path, backendTarget]),
    ),
  },
  test: {
    environment: 'jsdom',
    setupFiles: ['./src/test/setup.ts'],
    // Vitest's default "forks" pool spawns child processes, which some sandboxed dev/CI environments
    // block outright (observed here as a worker-start timeout with no other error). Worker threads
    // don't need process-spawn permission and work everywhere forks does.
    pool: 'threads',
  },
});
