/**
 * Theme preference handling shared by the provider, the settings page and the
 * bootstrap script in index.html.
 */

export type ThemePreference = 'system' | 'light' | 'dark';
export type ResolvedTheme = 'light' | 'dark';

export const THEME_STORAGE_KEY = 'glitchport.theme';
export const DARK_MEDIA_QUERY = '(prefers-color-scheme: dark)';

export function isThemePreference(value: unknown): value is ThemePreference {
  return value === 'system' || value === 'light' || value === 'dark';
}

function getDarkMediaQuery(): MediaQueryList | null {
  if (typeof window === 'undefined' || typeof window.matchMedia !== 'function') {
    return null;
  }

  try {
    return window.matchMedia(DARK_MEDIA_QUERY);
  } catch {
    return null;
  }
}

/** The current OS/browser dark-mode setting; false when unknown. */
export function systemPrefersDark(): boolean {
  return getDarkMediaQuery()?.matches ?? false;
}

/**
 * Calls `onChange` whenever the OS/browser dark-mode setting changes. Returns
 * an unsubscribe function; it is a no-op when `matchMedia` is unavailable.
 */
export function watchSystemTheme(
  onChange: (prefersDark: boolean) => void,
): () => void {
  const mediaQuery = getDarkMediaQuery();

  if (!mediaQuery) {
    return () => {};
  }

  const listener = (event: MediaQueryListEvent) => {
    onChange(event.matches);
  };

  if (typeof mediaQuery.addEventListener === 'function') {
    mediaQuery.addEventListener('change', listener);
    return () => mediaQuery.removeEventListener('change', listener);
  }

  // Older Safari only implements the deprecated listener methods.
  if (typeof mediaQuery.addListener === 'function') {
    mediaQuery.addListener(listener);
    return () => mediaQuery.removeListener(listener);
  }

  return () => {};
}

/** Reads the stored preference, accepting only the three known values. */
export function readThemePreference(): ThemePreference {
  if (typeof window === 'undefined') {
    return 'system';
  }

  try {
    const stored = window.localStorage.getItem(THEME_STORAGE_KEY);
    return isThemePreference(stored) ? stored : 'system';
  } catch {
    // Storage can be unavailable (privacy mode, disabled cookies); the theme
    // still works for the session with the default preference.
    return 'system';
  }
}

/** Persists the preference; failures are ignored for the same reason. */
export function writeThemePreference(preference: ThemePreference): void {
  if (typeof window === 'undefined') {
    return;
  }

  try {
    window.localStorage.setItem(THEME_STORAGE_KEY, preference);
  } catch {
    // Ignore unavailable storage.
  }
}

export function resolveTheme(
  preference: ThemePreference,
  prefersDark: boolean,
): ResolvedTheme {
  if (preference === 'system') {
    return prefersDark ? 'dark' : 'light';
  }

  return preference;
}

/**
 * Applies a resolved theme to the document. `data-theme` selects the CSS token
 * block and `color-scheme` makes native controls and scrollbars match.
 */
export function applyTheme(theme: ResolvedTheme): void {
  document.documentElement.dataset.theme = theme;
  document.documentElement.style.colorScheme = theme;
}
