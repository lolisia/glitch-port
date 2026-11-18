import '@testing-library/jest-dom/vitest';
import { cleanup } from '@testing-library/react';
import { afterEach, vi } from 'vitest';

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
  vi.unstubAllEnvs();
  vi.restoreAllMocks();
  // The active project and the locale are persisted here; tests must not leak
  // a selection into the next one.
  window.localStorage.clear();
  // The locale provider sets this on <html>; reset it so a Korean test cannot
  // change the language of the next one.
  document.documentElement.lang = 'en';
});
