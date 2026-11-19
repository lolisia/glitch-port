import { act, renderHook } from '@testing-library/react';
import { createElement, type ReactNode } from 'react';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { ThemeProvider, useTheme } from './ThemeProvider';
import {
  DARK_MEDIA_QUERY,
  THEME_STORAGE_KEY,
  applyTheme,
  readThemePreference,
  resolveTheme,
  systemPrefersDark,
  watchSystemTheme,
} from './theme';

type ChangeListener = (event: MediaQueryListEvent) => void;

interface MatchMediaHarness {
  matchMedia: ReturnType<typeof vi.fn>;
  addEventListener: ReturnType<typeof vi.fn>;
  removeEventListener: ReturnType<typeof vi.fn>;
  setMatches: (matches: boolean) => void;
  listenerCount: () => number;
}

/** Installs a controllable `window.matchMedia` for the dark-mode query. */
function stubMatchMedia(initialMatches: boolean): MatchMediaHarness {
  const listeners = new Set<ChangeListener>();
  let matches = initialMatches;

  const addEventListener = vi.fn(
    (type: string, listener: ChangeListener) => {
      if (type === 'change') {
        listeners.add(listener);
      }
    },
  );
  const removeEventListener = vi.fn(
    (type: string, listener: ChangeListener) => {
      if (type === 'change') {
        listeners.delete(listener);
      }
    },
  );

  const mediaQuery = {
    get matches() {
      return matches;
    },
    media: DARK_MEDIA_QUERY,
    onchange: null,
    addEventListener,
    removeEventListener,
    addListener: vi.fn(),
    removeListener: vi.fn(),
    dispatchEvent: vi.fn(() => false),
  };

  const matchMedia = vi.fn(
    (_query: string) => mediaQuery as unknown as MediaQueryList,
  );
  vi.stubGlobal('matchMedia', matchMedia);

  return {
    matchMedia,
    addEventListener,
    removeEventListener,
    setMatches(next: boolean) {
      matches = next;
      for (const listener of listeners) {
        listener({ matches: next } as MediaQueryListEvent);
      }
    },
    listenerCount: () => listeners.size,
  };
}

function renderTheme() {
  return renderHook(() => useTheme(), {
    wrapper: ({ children }: { children: ReactNode }) =>
      createElement(ThemeProvider, null, children),
  });
}

beforeEach(() => {
  window.localStorage.clear();
  document.documentElement.removeAttribute('data-theme');
  document.documentElement.style.removeProperty('color-scheme');
});

describe('readThemePreference', () => {
  it('defaults to system when nothing is stored', () => {
    expect(readThemePreference()).toBe('system');
  });

  it.each(['system', 'light', 'dark'] as const)(
    'reads the stored %s preference',
    (preference) => {
      window.localStorage.setItem(THEME_STORAGE_KEY, preference);

      expect(readThemePreference()).toBe(preference);
    },
  );

  it.each(['', 'blue', 'Dark', 'true', 'null'])(
    'falls back to system for the invalid value %j',
    (stored) => {
      window.localStorage.setItem(THEME_STORAGE_KEY, stored);

      expect(readThemePreference()).toBe('system');
    },
  );

  it('falls back to system when storage is unavailable', () => {
    vi.spyOn(Storage.prototype, 'getItem').mockImplementation(() => {
      throw new Error('storage blocked');
    });

    expect(readThemePreference()).toBe('system');
  });
});

describe('resolveTheme', () => {
  it.each([
    ['system', true, 'dark'],
    ['system', false, 'light'],
    ['light', true, 'light'],
    ['light', false, 'light'],
    ['dark', true, 'dark'],
    ['dark', false, 'dark'],
  ] as const)(
    'resolves %s with prefersDark=%s to %s',
    (preference, prefersDark, expected) => {
      expect(resolveTheme(preference, prefersDark)).toBe(expected);
    },
  );
});

describe('applyTheme', () => {
  it('sets data-theme and color-scheme for dark', () => {
    applyTheme('dark');

    expect(document.documentElement.dataset.theme).toBe('dark');
    expect(document.documentElement.style.colorScheme).toBe('dark');
  });

  it('sets data-theme and color-scheme for light', () => {
    applyTheme('light');

    expect(document.documentElement.dataset.theme).toBe('light');
    expect(document.documentElement.style.colorScheme).toBe('light');
  });
});

describe('systemPrefersDark', () => {
  it('returns false when matchMedia is unavailable', () => {
    vi.stubGlobal('matchMedia', undefined);
    expect(typeof window.matchMedia).toBe('undefined');
    expect(systemPrefersDark()).toBe(false);
  });

  it('follows the dark media query', () => {
    stubMatchMedia(true);
    expect(systemPrefersDark()).toBe(true);

    stubMatchMedia(false);
    expect(systemPrefersDark()).toBe(false);
  });
});

describe('watchSystemTheme', () => {
  it('returns a no-op unsubscribe when matchMedia is unavailable', () => {
    vi.stubGlobal('matchMedia', undefined);
    const unsubscribe = watchSystemTheme(() => {});

    expect(() => unsubscribe()).not.toThrow();
  });

  it('notifies on changes and removes the listener on cleanup', () => {
    const harness = stubMatchMedia(false);
    const onChange = vi.fn();

    const unsubscribe = watchSystemTheme(onChange);

    expect(harness.matchMedia).toHaveBeenCalledWith(DARK_MEDIA_QUERY);
    expect(harness.listenerCount()).toBe(1);

    harness.setMatches(true);
    expect(onChange).toHaveBeenLastCalledWith(true);

    unsubscribe();
    expect(harness.listenerCount()).toBe(0);
    expect(harness.removeEventListener).toHaveBeenCalledTimes(1);
  });
});

describe('ThemeProvider', () => {
  it('applies and persists the stored dark preference on mount', () => {
    window.localStorage.setItem(THEME_STORAGE_KEY, 'dark');

    const { result } = renderTheme();

    expect(result.current.preference).toBe('dark');
    expect(result.current.resolvedTheme).toBe('dark');
    expect(document.documentElement.dataset.theme).toBe('dark');
    expect(document.documentElement.style.colorScheme).toBe('dark');
    expect(window.localStorage.getItem(THEME_STORAGE_KEY)).toBe('dark');
  });

  it('defaults to system and light when matchMedia is unavailable', () => {
    vi.stubGlobal('matchMedia', undefined);
    const { result } = renderTheme();

    expect(result.current.preference).toBe('system');
    expect(result.current.resolvedTheme).toBe('light');
    expect(document.documentElement.dataset.theme).toBe('light');
    expect(document.documentElement.style.colorScheme).toBe('light');
    expect(window.localStorage.getItem(THEME_STORAGE_KEY)).toBe('system');
  });

  it('applies and persists explicit preferences immediately', () => {
    const { result } = renderTheme();

    act(() => result.current.setPreference('dark'));

    expect(result.current.preference).toBe('dark');
    expect(document.documentElement.dataset.theme).toBe('dark');
    expect(window.localStorage.getItem(THEME_STORAGE_KEY)).toBe('dark');

    act(() => result.current.setPreference('light'));

    expect(result.current.preference).toBe('light');
    expect(result.current.resolvedTheme).toBe('light');
    expect(document.documentElement.dataset.theme).toBe('light');
    expect(window.localStorage.getItem(THEME_STORAGE_KEY)).toBe('light');
  });

  it('follows media-query changes while the preference is system', () => {
    const harness = stubMatchMedia(false);
    const { result } = renderTheme();

    expect(result.current.resolvedTheme).toBe('light');
    expect(harness.listenerCount()).toBe(1);

    act(() => harness.setMatches(true));

    expect(result.current.resolvedTheme).toBe('dark');
    expect(document.documentElement.dataset.theme).toBe('dark');

    act(() => result.current.setPreference('light'));

    // An explicit preference stops following the OS setting.
    expect(harness.listenerCount()).toBe(0);
    expect(result.current.resolvedTheme).toBe('light');
    expect(document.documentElement.dataset.theme).toBe('light');
  });

  it('re-subscribes and adopts the current OS setting when returning to system', () => {
    const harness = stubMatchMedia(false);
    const { result } = renderTheme();

    act(() => result.current.setPreference('dark'));
    expect(harness.listenerCount()).toBe(0);

    act(() => harness.setMatches(true));
    expect(result.current.resolvedTheme).toBe('dark');

    act(() => result.current.setPreference('system'));

    expect(harness.listenerCount()).toBe(1);
    expect(result.current.resolvedTheme).toBe('dark');
    expect(document.documentElement.dataset.theme).toBe('dark');
  });
});
