import { afterEach } from 'vitest';
import { cleanup } from '@testing-library/react';
import '@testing-library/jest-dom/vitest';

// @testing-library/react's automatic afterEach cleanup only registers itself when it detects a global
// `afterEach` (Vitest's `globals: true`) - this project imports test functions explicitly instead
// (vite.config.ts has no `globals: true`), so without this, rendered DOM from one test carries over
// into the next and produces bogus "found multiple elements" failures.
afterEach(() => {
  cleanup();
});
